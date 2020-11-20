﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// ------------------------------------------------------------

namespace Dapr.Actors.Runtime
{
    using System;
    using System.Collections.Concurrent;
    using System.IO;
    using System.Reflection;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Dapr.Actors;
    using Dapr.Actors.Communication;
    using Microsoft.Extensions.Logging;

    // The ActorManager serves as a cache for a variety of different concerns related to an Actor type
    // as well as the runtime managment for Actor instances of that type.
    internal sealed class ActorManager
    {
        private const string ReceiveReminderMethodName = "ReceiveReminderAsync";
        private const string TimerMethodName = "FireTimerAsync";
        private readonly ActorRegistration registration;
        private readonly ActorActivator activator;
        private readonly ILoggerFactory loggerFactory;
        private readonly ConcurrentDictionary<ActorId, ActorActivatorState> activeActors;
        private readonly ActorMethodContext reminderMethodContext;
        private readonly ActorMethodContext timerMethodContext;
        private readonly ActorMessageSerializersManager serializersManager;
        private readonly IActorMessageBodyFactory messageBodyFactory;

        // method dispatchermap used by remoting calls.
        private readonly ActorMethodDispatcherMap methodDispatcherMap;

        // method info map used by non-remoting calls.
        private readonly ActorMethodInfoMap actorMethodInfoMap;

        private readonly ILogger logger;

        internal ActorManager(ActorRegistration registration, ActorActivator activator, ILoggerFactory loggerFactory)
        {
            this.registration = registration;
            this.activator = activator;
            this.loggerFactory = loggerFactory;

            // map for remoting calls.
            this.methodDispatcherMap = new ActorMethodDispatcherMap(this.registration.Type.InterfaceTypes);

            // map for non-remoting calls.
            this.actorMethodInfoMap = new ActorMethodInfoMap(this.registration.Type.InterfaceTypes);
            this.activeActors = new ConcurrentDictionary<ActorId, ActorActivatorState>();
            this.reminderMethodContext = ActorMethodContext.CreateForReminder(ReceiveReminderMethodName);
            this.timerMethodContext = ActorMethodContext.CreateForReminder(TimerMethodName);
            this.serializersManager = IntializeSerializationManager(null);
            this.messageBodyFactory = new WrappedRequestMessageFactory();

            this.logger = loggerFactory.CreateLogger(this.GetType());
        }

        internal ActorTypeInformation ActorTypeInfo => this.registration.Type;

        internal async Task<Tuple<string, byte[]>> DispatchWithRemotingAsync(ActorId actorId, string actorMethodName, string daprActorheader, Stream data, CancellationToken cancellationToken)
        {
            var actorMethodContext = ActorMethodContext.CreateForActor(actorMethodName);

            // Get the serialized header
            var actorMessageHeader = this.serializersManager.GetHeaderSerializer()
                .DeserializeRequestHeaders(new MemoryStream(Encoding.ASCII.GetBytes(daprActorheader)));

            var interfaceId = actorMessageHeader.InterfaceId;

            // Get the deserialized Body.
            var msgBodySerializer = this.serializersManager.GetRequestMessageBodySerializer(actorMessageHeader.InterfaceId);

            IActorRequestMessageBody actorMessageBody;
            using (var stream = new MemoryStream())
            {
                await data.CopyToAsync(stream);
                actorMessageBody = msgBodySerializer.Deserialize(stream);
            }

            // Call the method on the method dispatcher using the Func below.
            var methodDispatcher = this.methodDispatcherMap.GetDispatcher(actorMessageHeader.InterfaceId, actorMessageHeader.MethodId);

            // Create a Func to be invoked by common method.
            async Task<Tuple<string, byte[]>> RequestFunc(Actor actor, CancellationToken ct)
            {
                IActorResponseMessageBody responseMsgBody = null;

                try
                {
                    responseMsgBody = (IActorResponseMessageBody)await methodDispatcher.DispatchAsync(
                        actor,
                        actorMessageHeader.MethodId,
                        actorMessageBody,
                        this.messageBodyFactory,
                        ct);

                    return this.CreateResponseMessage(responseMsgBody, interfaceId);
                }
                catch (Exception exception)
                {
                    // return exception response message
                    return this.CreateExceptionResponseMessage(exception);
                }
            }

            return await this.DispatchInternalAsync(actorId, actorMethodContext, RequestFunc, cancellationToken);
        }

        internal async Task DispatchWithoutRemotingAsync(ActorId actorId, string actorMethodName, Stream requestBodyStream, Stream responseBodyStream, CancellationToken cancellationToken)
        {
            var actorMethodContext = ActorMethodContext.CreateForActor(actorMethodName);

            // Create a Func to be invoked by common method.
            var methodInfo = this.actorMethodInfoMap.LookupActorMethodInfo(actorMethodName);

            async Task<object> RequestFunc(Actor actor, CancellationToken ct)
            {
                var parameters = methodInfo.GetParameters();
                dynamic awaitable;

                if (parameters.Length == 0)
                {
                    awaitable = methodInfo.Invoke(actor, null);
                }
                else if (parameters.Length == 1)
                {
                    // deserialize using stream.
                    var type = parameters[0].ParameterType;
                    var deserializedType = await JsonSerializer.DeserializeAsync(requestBodyStream, type);
                    awaitable = methodInfo.Invoke(actor, new object[] { deserializedType });
                }
                else
                {
                    var errorMsg = $"Method {string.Concat(methodInfo.DeclaringType.Name, ".", methodInfo.Name)} has more than one parameter and can't be invoked through http";
                    throw new ArgumentException(errorMsg);
                }

                await awaitable;

                // Handle the return type of method correctly.
                if (methodInfo.ReturnType.Name != typeof(Task).Name)
                {
                    // already await, Getting result will be non blocking.
                    var x = awaitable.GetAwaiter().GetResult();
                    return x;
                }
                else
                {
                    return default;
                }
            }

            var result = await this.DispatchInternalAsync(actorId, actorMethodContext, RequestFunc, cancellationToken);

            // Write Response back if method's return type is other than Task.
            // Serialize result if it has result (return type was not just Task.)
            if (methodInfo.ReturnType.Name != typeof(Task).Name)
            {
                await JsonSerializer.SerializeAsync(responseBodyStream, result, result.GetType());
            }
        }

        internal async Task FireReminderAsync(ActorId actorId, string reminderName, Stream requestBodyStream, CancellationToken cancellationToken = default)
        {
            // Only FireReminder if its IRemindable, else ignore it.
            if (this.ActorTypeInfo.IsRemindable)
            {
                var reminderdata = await ReminderInfo.DeserializeAsync(requestBodyStream);

                // Create a Func to be invoked by common method.
                async Task<byte[]> RequestFunc(Actor actor, CancellationToken ct)
                {
                    await
                        (actor as IRemindable).ReceiveReminderAsync(
                            reminderName,
                            reminderdata.Data,
                            reminderdata.DueTime,
                            reminderdata.Period);

                    return null;
                }

                await this.DispatchInternalAsync(actorId, this.reminderMethodContext, RequestFunc, cancellationToken);
            }
        }

        internal async Task FireTimerAsync(ActorId actorId, string timerName, Stream requestBodyStream, CancellationToken cancellationToken = default)
        {
            var timerData = await TimerInfo.DeserializeAsync(requestBodyStream);

            dynamic awaitable;
            // Create a Func to be invoked by common method.
            async Task<byte[]> RequestFunc(Actor actor, CancellationToken ct)
            {
                var actorTypeName = this.actorService.ActorTypeInfo.ActorTypeName;
                var actorType = this.actorService.ActorTypeInfo.ImplementationType;
                MethodInfo[] methods = actorType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
                var methodInfo = actorType.GetMethod(timerData.Callback);

                var parameters = methodInfo.GetParameters();
                if (parameters.Length == 0)
                {
                    awaitable = methodInfo.Invoke(actor, null);
                }
                else
                {
                    var deserializedType = timerData.Data;
                    awaitable = methodInfo.Invoke(actor, new object[] { deserializedType });
                }
                await awaitable;

                return default;
            }

            var result = await this.DispatchInternalAsync(actorId, this.timerMethodContext, RequestFunc, cancellationToken);
        }

        internal async Task ActivateActorAsync(ActorId actorId)
        {
            // An actor is activated by "Dapr" runtime when a call is to be made for an actor.
            var state = await this.CreateActorAsync(actorId);

            try
            {
                await state.Actor.OnActivateInternalAsync();
            }
            catch
            {
                // Ensure we don't leak resources if user-code throws during activation.
                await DeleteActorAsync(state);
                throw;
            }

            // Add actor to activeActors only after OnActivate succeeds (user code can throw error from its override of Activate method.)
            //
            // In theory the Dapr runtime protects us from double-activation - there's no case
            // where we *expect* to see the *update* code path taken. However it's a possiblity and
            // we should handle it.
            //
            // The policy we have chosen is to always keep the registered instance if we hit a double-activation
            // so that means we have to destroy the 'new' instance.
            var current = this.activeActors.AddOrUpdate(actorId, state, (key, oldValue) => oldValue);
            if (object.ReferenceEquals(state, current))
            {
                // On this code path it was an *Add*. Nothing left to do.
                return;
            }

            // On this code path it was an *Update*. We need to destroy the new instance and clean up.
            await DeactivateActorCore(state);
        }

        private async Task<ActorActivatorState> CreateActorAsync(ActorId actorId)
        {
            this.logger.LogDebug("Creating Actor of type {ActorType} with ActorId {ActorId}", this.ActorTypeInfo.ImplementationType, actorId);
            var host = new ActorHost(this.ActorTypeInfo, actorId, this.loggerFactory);
            var state =  await this.activator.CreateAsync(host);
            this.logger.LogDebug("Finished creating Actor of type {ActorType} with ActorId {ActorId}", this.ActorTypeInfo.ImplementationType, actorId);
            return state;
        }

        internal async ValueTask DeactivateActorAsync(ActorId actorId)
        {
            if (this.activeActors.TryRemove(actorId, out var deactivatedActor))
            {
                await DeactivateActorCore(deactivatedActor);
            }
        }

        private async ValueTask DeactivateActorCore(ActorActivatorState state)
        {
            try
            {
                await state.Actor.OnDeactivateInternalAsync();
            }
            finally
            {
                // Ensure we don't leak resources if user-code throws during deactivation.
                await DeleteActorAsync(state);
            }
        }

        private async ValueTask DeleteActorAsync(ActorActivatorState state)
        {
            this.logger.LogDebug("Deleting Actor of type {ActorType} with ActorId {ActorId}", this.ActorTypeInfo.ImplementationType, state.Actor.Id);
            await this.activator.DeleteAsync(state);
            this.logger.LogDebug("Finished deleting Actor of type {ActorType} with ActorId {ActorId}", this.ActorTypeInfo.ImplementationType, state.Actor.Id);
        }

        // Used for testing - do not leak the actor instances outside of this method in library code.
        public bool TryGetActorAsync(ActorId id, out Actor actor)
        {
            var found = this.activeActors.TryGetValue(id, out var state);
            actor = found ? state.Actor : default;
            return found;
        } 

        private static ActorMessageSerializersManager IntializeSerializationManager(
            IActorMessageBodySerializationProvider serializationProvider)
        {
            // TODO serializer settings
            return new ActorMessageSerializersManager(
                serializationProvider,
                new ActorMessageHeaderSerializer());
        }

        private async Task<T> DispatchInternalAsync<T>(ActorId actorId, ActorMethodContext actorMethodContext, Func<Actor, CancellationToken, Task<T>> actorFunc, CancellationToken cancellationToken)
        {
            if (!this.activeActors.ContainsKey(actorId))
            {
                await this.ActivateActorAsync(actorId);
            }

            if (!this.activeActors.TryGetValue(actorId, out var state))
            {             
                var errorMsg = $"Actor {actorId} is not yet activated.";
                throw new InvalidOperationException(errorMsg);
            }

            var actor = state.Actor;

            T retval;
            try
            {
                // invoke the function of the actor
                await actor.OnPreActorMethodAsyncInternal(actorMethodContext);
                retval = await actorFunc.Invoke(actor, cancellationToken);

                // PostActivate will save the state, its not invoked when actorFunc invocation throws.
                await actor.OnPostActorMethodAsyncInternal(actorMethodContext);
            }
            catch (Exception)
            {
                await actor.OnInvokeFailedAsync();
                throw;
            }

            return retval;
        }

        private Tuple<string, byte[]> CreateResponseMessage(IActorResponseMessageBody msgBody, int interfaceId)
        {
            var responseMsgBodyBytes = Array.Empty<byte>();
            if (msgBody != null)
            {
                var responseSerializer = this.serializersManager.GetResponseMessageBodySerializer(interfaceId);
                responseMsgBodyBytes = responseSerializer.Serialize(msgBody);
            }

            return new Tuple<string, byte[]>(string.Empty, responseMsgBodyBytes);
        }

        private Tuple<string, byte[]> CreateExceptionResponseMessage(Exception ex)
        {
            var responseHeader = new ActorResponseMessageHeader();
            responseHeader.AddHeader("HasRemoteException", Array.Empty<byte>());
            var responseHeaderBytes = this.serializersManager.GetHeaderSerializer().SerializeResponseHeader(responseHeader);
            var serializedHeader = Encoding.UTF8.GetString(responseHeaderBytes, 0, responseHeaderBytes.Length);

            var responseMsgBody = ActorInvokeException.FromException(ex);
            
            return new Tuple<string, byte[]>(serializedHeader, responseMsgBody);
        }
    }
}
