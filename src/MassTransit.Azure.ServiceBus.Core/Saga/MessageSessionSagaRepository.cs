﻿// Copyright 2007-2018 Chris Patterson, Dru Sellers, Travis Smith, et. al.
//  
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use
// this file except in compliance with the License. You may obtain a copy of the 
// License at 
// 
//     http://www.apache.org/licenses/LICENSE-2.0 
// 
// Unless required by applicable law or agreed to in writing, software distributed
// under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR 
// CONDITIONS OF ANY KIND, either express or implied. See the License for the 
// specific language governing permissions and limitations under the License.
namespace MassTransit.Azure.ServiceBus.Core.Saga
{
    using System;
    using System.IO;
    using System.Text;
    using System.Threading.Tasks;
    using Context;
    using GreenPipes;
    using Logging;
    using MassTransit.Saga;
    using Newtonsoft.Json;
    using Serialization;
    using Util;


    /// <summary>
    /// A saga repository that uses the message session in Azure Service Bus to store the state 
    /// of the saga.
    /// </summary>
    /// <typeparam name="TSaga">The saga state type</typeparam>
    public class MessageSessionSagaRepository<TSaga> :
        ISagaRepository<TSaga>
        where TSaga : class, ISaga
    {
        static readonly ILog _log = Logger.Get(typeof(MessageSessionSagaRepository<TSaga>));

        public void Probe(ProbeContext context)
        {
            var scope = context.CreateScope("sagaRepository");
            scope.Set(new
            {
                Persistence = "messageSession"
            });
        }

        async Task ISagaRepository<TSaga>.Send<T>(ConsumeContext<T> context, ISagaPolicy<TSaga, T> policy, IPipe<SagaConsumeContext<TSaga, T>> next)
        {
            if (!context.TryGetPayload(out MessageSessionContext sessionContext))
                throw new SagaException($"The session-based saga repository requires an active message session: {TypeMetadataCache<TSaga>.ShortName}",
                    typeof(TSaga), typeof(T));

            if (Guid.TryParse(sessionContext.SessionId, out var sessionId))
                context = new CorrelationIdConsumeContextProxy<T>(context, sessionId);

            var saga = await ReadSagaState(sessionContext).ConfigureAwait(false);
            if (saga == null)
            {
                var missingSagaPipe = new MissingPipe<T>(next, WriteSagaState);

                await policy.Missing(context, missingSagaPipe).ConfigureAwait(false);
            }
            else
            {
                SagaConsumeContext<TSaga, T> sagaConsumeContext = new MessageSessionSagaConsumeContext<TSaga, T>(context, sessionContext, saga);

                if (_log.IsDebugEnabled)
                    _log.DebugFormat("SAGA:{0}:{1} Existing {2}", TypeMetadataCache<TSaga>.ShortName, sessionContext.SessionId, TypeMetadataCache<T>.ShortName);

                await policy.Existing(sagaConsumeContext, next).ConfigureAwait(false);

                if (!sagaConsumeContext.IsCompleted)
                {
                    await WriteSagaState(sessionContext, saga).ConfigureAwait(false);

                    if (_log.IsDebugEnabled)
                        _log.DebugFormat("SAGA:{0}:{1} Updated {2}", TypeMetadataCache<TSaga>.ShortName, sessionContext.SessionId,
                            TypeMetadataCache<T>.ShortName);
                }
            }
        }

        Task ISagaRepository<TSaga>.SendQuery<T>(SagaQueryConsumeContext<TSaga, T> context, ISagaPolicy<TSaga, T> policy,
            IPipe<SagaConsumeContext<TSaga, T>> next)
        {
            throw new NotImplementedException(
                $"Query-based saga correlation is not available when using the MessageSession-based saga repository: {TypeMetadataCache<TSaga>.ShortName}");
        }

        /// <summary>
        /// Writes the saga state to the message session
        /// </summary>
        /// <param name="context">The message session context</param>
        /// <param name="saga">The saga state</param>
        /// <returns>An awaitable task, of course</returns>
        async Task WriteSagaState(MessageSessionContext context, TSaga saga)
        {
            using (var serializeStream = new MemoryStream())
            using (var writer = new StreamWriter(serializeStream, Encoding.UTF8, 1024, true))
            using (var bsonWriter = new JsonTextWriter(writer))
            {
                JsonMessageSerializer.Serializer.Serialize(bsonWriter, saga);

                bsonWriter.Flush();
                await serializeStream.FlushAsync().ConfigureAwait(false);

                await context.SetStateAsync(serializeStream.ToArray()).ConfigureAwait(false);
            }
        }

        async Task<TSaga> ReadSagaState(MessageSessionContext context)
        {
            var state = await context.GetStateAsync().ConfigureAwait(false);
            if (state == null)
                return default;

            using (var stateStream = new MemoryStream(state))
            {
                if (stateStream.Length == 0)
                    return default;

                using (var reader = new StreamReader(stateStream, Encoding.UTF8, false, 1024, true))
                using (var bsonReader = new JsonTextReader(reader))
                {
                    return JsonMessageSerializer.Deserializer.Deserialize<TSaga>(bsonReader);
                }
            }
        }


        /// <summary>
        /// Once the message pipe has processed the saga instance, add it to the saga repository
        /// </summary>
        /// <typeparam name="TMessage"></typeparam>
        class MissingPipe<TMessage> :
            IPipe<SagaConsumeContext<TSaga, TMessage>>
            where TMessage : class
        {
            readonly IPipe<SagaConsumeContext<TSaga, TMessage>> _next;
            readonly Func<MessageSessionContext, TSaga, Task> _writeSagaState;

            public MissingPipe(IPipe<SagaConsumeContext<TSaga, TMessage>> next, Func<MessageSessionContext, TSaga, Task> writeSagaState)
            {
                _next = next;
                _writeSagaState = writeSagaState;
            }

            void IProbeSite.Probe(ProbeContext context)
            {
                _next.Probe(context);
            }

            public async Task Send(SagaConsumeContext<TSaga, TMessage> context)
            {
                var sessionContext = context.GetPayload<MessageSessionContext>();

                var proxy = new MessageSessionSagaConsumeContext<TSaga, TMessage>(context, sessionContext, context.Saga);

                if (_log.IsDebugEnabled)
                    _log.DebugFormat("SAGA:{0}:{1} Created {2}", TypeMetadataCache<TSaga>.ShortName, sessionContext.SessionId,
                        TypeMetadataCache<TMessage>.ShortName);

                try
                {
                    await _next.Send(proxy).ConfigureAwait(false);

                    if (!proxy.IsCompleted)
                    {
                        await _writeSagaState(sessionContext, proxy.Saga).ConfigureAwait(false);
                        if (_log.IsDebugEnabled)
                            _log.DebugFormat("SAGA:{0}:{1} Saved {2}", TypeMetadataCache<TSaga>.ShortName, sessionContext.SessionId,
                                TypeMetadataCache<TMessage>.ShortName);
                    }
                }
                catch (Exception)
                {
                    if (_log.IsDebugEnabled)
                        _log.DebugFormat("SAGA:{0}:{1} Unsaved(Fault) {2}", TypeMetadataCache<TSaga>.ShortName, sessionContext.SessionId,
                            TypeMetadataCache<TMessage>.ShortName);

                    throw;
                }
            }
        }
    }
}