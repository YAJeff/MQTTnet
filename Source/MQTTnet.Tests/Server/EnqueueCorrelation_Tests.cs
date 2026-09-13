// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections;
using MQTTnet.Diagnostics.Logger;
using MQTTnet.Internal;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using MQTTnet.Server;
using MQTTnet.Server.Internal;

namespace MQTTnet.Tests.Server;

[TestClass]
public sealed class EnqueueCorrelation_Tests : BaseTestClass
{
    [TestMethod]
    public async Task Overlapping_Admissions_Of_Same_Message_Preserve_Exact_State()
    {
        using var context = new Context();
        var message = Message();
        var entered = Signal();
        var release = Signal();
        var firstState = new object();
        var secondState = new object();
        var calls = 0;
        var outcomes = new List<ApplicationMessageEnqueuedEventArgs>();
        context.Events.InterceptingClientEnqueueEvent.AddHandler(async args =>
        {
            Assert.AreSame(context.Session.Items, args.ReceiverSessionItems);
            Assert.AreSame(message, args.ApplicationMessage);
            args.EnqueueState = Interlocked.Increment(ref calls) == 1 ? firstState : secondState;
            if (ReferenceEquals(args.EnqueueState, firstState))
            {
                entered.SetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
        context.Events.ApplicationMessageEnqueuedOrDroppedEvent.AddHandler(args => outcomes.Add(args));
        var first = context.Enqueue(message);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(await context.Enqueue(message));
        release.SetResult();
        Assert.IsTrue(await first);
        Assert.HasCount(2, outcomes);
        Assert.AreSame(secondState, outcomes[0].EnqueueState);
        Assert.AreSame(firstState, outcomes[1].EnqueueState);
        Assert.IsTrue(outcomes.All(e => ReferenceEquals(e.ReceiverSessionItems, context.Session.Items) && !e.IsDropped && e.Exception == null));
    }

    [TestMethod]
    public async Task Disposed_Captured_Session_Does_Not_Become_Replacement_Session()
    {
        using var old = new Context();
        using var replacement = new Context();
        Assert.AreEqual(old.Session.Id, replacement.Session.Id);
        var entered = Signal();
        var release = Signal();
        var state = new object();
        ApplicationMessageEnqueuedEventArgs outcome = null;
        old.Events.InterceptingClientEnqueueEvent.AddHandler(async args =>
        {
            args.EnqueueState = state;
            entered.SetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
        });
        old.Events.ApplicationMessageEnqueuedOrDroppedEvent.AddHandler(args => outcome = args);
        var attempt = old.Enqueue(Message());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        old.Session.Dispose();
        Assert.IsTrue(await replacement.Enqueue(Message()));
        release.SetResult();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => attempt);
        Assert.AreSame(old.Session.Items, outcome.ReceiverSessionItems);
        Assert.AreNotSame(replacement.Session.Items, outcome.ReceiverSessionItems);
        Assert.AreSame(state, outcome.EnqueueState);
        Assert.IsTrue(outcome.IsDropped);
        Assert.IsInstanceOfType<ObjectDisposedException>(outcome.Exception);
        Assert.AreEqual(1L, replacement.Session.PendingDataPacketsCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Rejected_Or_Throwing_Interceptor_Has_Correlated_Dropped_Outcome(bool throws)
    {
        using var context = new Context();
        var state = new object();
        var error = new InvalidOperationException("interceptor control");
        var count = 0;
        context.Events.InterceptingClientEnqueueEvent.AddHandler(args =>
        {
            args.EnqueueState = state;
            args.AcceptEnqueue = false;
            if (throws) throw error;
        });
        context.Events.ApplicationMessageEnqueuedOrDroppedEvent.AddHandler(args =>
        {
            count++;
            Assert.IsTrue(args.IsDropped);
            Assert.AreSame(state, args.EnqueueState);
            Assert.AreSame(context.Session.Items, args.ReceiverSessionItems);
            Assert.AreSame(throws ? error : null, args.Exception);
        });
        if (throws) Assert.AreSame(error, await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => context.Enqueue(Message())));
        else Assert.IsFalse(await context.Enqueue(Message()));
        Assert.AreEqual(1, count);
        Assert.AreEqual(0L, context.Session.PendingDataPacketsCount);
    }

    [TestMethod]
    public async Task Outcome_Callback_Is_Outside_Gate_And_Can_Follow_Eviction()
    {
        using var context = new Context(1);
        var firstEntered = Signal();
        var release = Signal();
        QueueMessageOverwrittenEventArgs overwritten = null;
        context.Events.QueuedApplicationMessageOverwrittenEvent.AddHandler(args => overwritten = args);
        context.Events.ApplicationMessageEnqueuedOrDroppedEvent.AddHandler(async args =>
        {
            if (args.ApplicationMessage.Topic != "first") return;
            firstEntered.SetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(args.IsDropped);
            Assert.AreSame(context.Session.Items, args.ReceiverSessionItems);
        });
        var first = context.Enqueue(Message("first"));
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // This second thread would block if the first callback held the admission gate.
        Assert.IsTrue(await Task.Run(() => context.Enqueue(Message("second"))).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsNotNull(overwritten);
        Assert.AreSame(context.Session.Items, overwritten.ReceiverSessionItems);
        Assert.AreEqual("first", ((MqttPublishPacket)overwritten.Packet).Topic);
        release.SetResult();
        Assert.IsTrue(await first);
    }

    [TestMethod]
    public void Existing_Event_Constructors_Retain_Null_Metadata()
    {
        var message = Message();
        var pre = new InterceptingClientApplicationMessageEnqueueEventArgs("s", "r", message);
        var post = new ApplicationMessageEnqueuedEventArgs("s", "r", message, false);
        var overwritten = new QueueMessageOverwrittenEventArgs("r", new MqttPublishPacket());
        Assert.IsNull(pre.ReceiverSessionItems);
        Assert.IsNull(pre.EnqueueState);
        Assert.IsNull(post.ReceiverSessionItems);
        Assert.IsNull(post.EnqueueState);
        Assert.IsNull(post.Exception);
        Assert.IsNull(overwritten.ReceiverSessionItems);
    }

    [TestMethod]
    [DataRow(false, MqttQualityOfServiceLevel.AtMostOnce)]
    [DataRow(false, MqttQualityOfServiceLevel.AtLeastOnce)]
    [DataRow(false, MqttQualityOfServiceLevel.ExactlyOnce)]
    [DataRow(true, MqttQualityOfServiceLevel.AtMostOnce)]
    [DataRow(true, MqttQualityOfServiceLevel.AtLeastOnce)]
    [DataRow(true, MqttQualityOfServiceLevel.ExactlyOnce)]
    public async Task Retained_Subscription_Paths_Report_Exact_Session_And_State(bool serverSubscription, MqttQualityOfServiceLevel qos)
    {
        using var environment = CreateTestEnvironment();
        var server = await environment.StartServer();
        var publisher = await environment.ConnectClient();
        await publisher.PublishAsync(new MqttApplicationMessageBuilder().WithTopic("correlation/retained").WithPayload("value")
            .WithRetainFlag().WithQualityOfServiceLevel(qos).Build());
        var receiver = await environment.ConnectClient(builder => builder.WithClientId("correlation-receiver"));
        var session = (await server.GetSessionsAsync()).Single(s => s.Id == receiver.Options.ClientId);
        var state = new object();
        var received = Signal();
        var posted = new TaskCompletionSource<ApplicationMessageEnqueuedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = Signal();
        receiver.ApplicationMessageReceivedAsync += args => { received.TrySetResult(); return Task.CompletedTask; };
        server.InterceptingClientEnqueueAsync += args =>
        {
            Assert.AreSame(session.Items, args.ReceiverSessionItems);
            args.EnqueueState = state;
            return Task.CompletedTask;
        };
        server.ApplicationMessageEnqueuedOrDroppedAsync += args => { posted.TrySetResult(args); return Task.CompletedTask; };
        server.ClientAcknowledgedPublishPacketAsync += args =>
        {
            Assert.AreSame(session.Items, args.SessionItems);
            Assert.AreSame(state, args.EnqueueState);
            if (args.IsCompleted) acknowledged.TrySetResult();
            return Task.CompletedTask;
        };
        var filter = new MqttTopicFilter { Topic = "correlation/#", QualityOfServiceLevel = qos };
        if (serverSubscription) await server.SubscribeAsync(receiver.Options.ClientId, new[] { filter });
        else await receiver.SubscribeAsync(filter);
        var outcome = await posted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (qos != MqttQualityOfServiceLevel.AtMostOnce) await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreSame(session.Items, outcome.ReceiverSessionItems);
        Assert.AreSame(state, outcome.EnqueueState);
        Assert.IsFalse(outcome.IsDropped);
        Assert.IsNull(outcome.Exception);
        await receiver.DisconnectAsync();
        await publisher.DisconnectAsync();
    }

    [TestMethod]
    public async Task Native_Dispatch_Outcome_Can_Complete_After_Ack_With_Exact_Context()
    {
        using var environment = CreateTestEnvironment();
        var server = await environment.StartServer();
        var receiver = await environment.ConnectClient();
        await receiver.SubscribeAsync(new MqttTopicFilter { Topic = "correlation/native", QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce });
        var session = (await server.GetSessionsAsync()).Single(s => s.Id == receiver.Options.ClientId);
        var state = new object();
        var acknowledged = Signal();
        var posted = Signal();
        server.InterceptingClientEnqueueAsync += args =>
        {
            Assert.AreSame(session.Items, args.ReceiverSessionItems);
            args.EnqueueState = state;
            return Task.CompletedTask;
        };
        server.ClientAcknowledgedPublishPacketAsync += args =>
        {
            Assert.AreSame(session.Items, args.SessionItems);
            Assert.AreSame(state, args.EnqueueState);
            acknowledged.TrySetResult();
            return Task.CompletedTask;
        };
        server.ApplicationMessageEnqueuedOrDroppedAsync += async args =>
        {
            await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreSame(session.Items, args.ReceiverSessionItems);
            Assert.AreSame(state, args.EnqueueState);
            Assert.IsFalse(args.IsDropped);
            posted.TrySetResult();
        };
        await server.InjectApplicationMessage(new InjectedMqttApplicationMessage(new MqttApplicationMessageBuilder()
            .WithTopic("correlation/native").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build()));
        await posted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await receiver.DisconnectAsync();
    }

    [TestMethod]
    [DataRow(MqttPendingMessagesOverflowStrategy.DropNewMessage, MqttQualityOfServiceLevel.AtLeastOnce)]
    [DataRow(MqttPendingMessagesOverflowStrategy.DropNewMessage, MqttQualityOfServiceLevel.ExactlyOnce)]
    [DataRow(MqttPendingMessagesOverflowStrategy.DropOldestQueuedMessage, MqttQualityOfServiceLevel.AtLeastOnce)]
    [DataRow(MqttPendingMessagesOverflowStrategy.DropOldestQueuedMessage, MqttQualityOfServiceLevel.ExactlyOnce)]
    public async Task Recovery_Overflow_Reports_Exact_Removed_Publish_And_Retains_Others(
        MqttPendingMessagesOverflowStrategy strategy, MqttQualityOfServiceLevel qos)
    {
        using var context = new Context(2, strategy);
        var packets = Enumerable.Range(0, 3).Select(i => new MqttPublishPacket { Topic = "recovery/" + i, QualityOfServiceLevel = qos }).ToArray();
        context.Session.EnqueueDataPacket(new MqttPacketBusItem(packets[0]));
        var inFlight = await context.Session.DequeuePacketAsync(CancellationToken.None);
        inFlight.Complete();
        context.Session.EnqueueDataPacket(new MqttPacketBusItem(packets[1]));
        context.Session.EnqueueDataPacket(new MqttPacketBusItem(packets[2]));
        var invalidations = new List<SessionApplicationMessagesInvalidatedEventArgs>();
        var overwrites = new List<QueueMessageOverwrittenEventArgs>();
        var callbackAcquiredGate = false;
        context.Events.SessionApplicationMessagesInvalidatedEvent.AddHandler(args =>
        {
            invalidations.Add(args);
            if (args.Reason == MqttSessionApplicationMessagesInvalidationReason.RecoveryOverflow)
            {
                var admitted = Task.Run(() => new MqttSessionStatus(context.Session).TryEnqueueApplicationMessage(Message(), out _, false))
                    .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                Assert.IsFalse(admitted);
                callbackAcquiredGate = true;
            }
        });
        context.Events.QueuedApplicationMessageOverwrittenEvent.AddHandler(args => overwrites.Add(args));
        context.Session.Recover();
        Assert.IsTrue(callbackAcquiredGate);
        Assert.HasCount(1, invalidations);
        var invalidation = invalidations[0];
        Assert.AreSame(context.Session.Items, invalidation.ReceiverSessionItems);
        Assert.AreEqual(MqttSessionApplicationMessagesInvalidationReason.RecoveryOverflow, invalidation.Reason);
        Assert.HasCount(1, invalidation.PublishPackets);
        var removed = strategy == MqttPendingMessagesOverflowStrategy.DropNewMessage ? packets[2] : packets[0];
        Assert.AreSame(removed, invalidation.PublishPackets[0]);
        if (strategy == MqttPendingMessagesOverflowStrategy.DropOldestQueuedMessage)
        {
            Assert.HasCount(1, overwrites);
            Assert.AreSame(removed, overwrites[0].Packet);
            Assert.AreSame(context.Session.Items, overwrites[0].ReceiverSessionItems);
        }
        else Assert.IsEmpty(overwrites);
        foreach (var expected in packets.Where(packet => !ReferenceEquals(packet, removed)))
        {
            var item = await context.Session.DequeuePacketAsync(CancellationToken.None);
            Assert.AreSame(expected, item.Packet);
            item.Complete();
            Assert.AreSame(expected, context.Session.AcknowledgePublishPacket(expected.PacketIdentifier));
        }
        context.Session.Recover();
        Assert.HasCount(1, invalidations);
        Assert.AreEqual(0L, context.Session.PendingDataPacketsCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Disposal_Invalidates_Exact_Session_Once_Including_Empty_Session(bool hasPackets)
    {
        using var context = new Context();
        var first = new MqttPublishPacket { Topic = "inflight", QualityOfServiceLevel = MqttQualityOfServiceLevel.ExactlyOnce };
        var second = new MqttPublishPacket { Topic = "queued", QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce };
        if (hasPackets)
        {
            context.Session.EnqueueDataPacket(new MqttPacketBusItem(first));
            (await context.Session.DequeuePacketAsync(CancellationToken.None)).Complete();
            context.Session.EnqueueDataPacket(new MqttPacketBusItem(second));
        }
        var invalidations = new List<SessionApplicationMessagesInvalidatedEventArgs>();
        var callbackAcquiredGate = false;
        context.Events.SessionApplicationMessagesInvalidatedEvent.AddHandler(args =>
        {
            invalidations.Add(args);
            Task.Run(() => Assert.ThrowsExactly<ObjectDisposedException>(() =>
                    new MqttSessionStatus(context.Session).TryEnqueueApplicationMessage(Message(), out _, false)))
                .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            callbackAcquiredGate = true;
        });
        context.Session.Dispose();
        context.Session.Dispose();
        Assert.IsTrue(callbackAcquiredGate);
        Assert.HasCount(1, invalidations);
        var invalidation = invalidations[0];
        Assert.AreSame(context.Session.Items, invalidation.ReceiverSessionItems);
        Assert.AreEqual(MqttSessionApplicationMessagesInvalidationReason.SessionDisposed, invalidation.Reason);
        Assert.HasCount(hasPackets ? 2 : 0, invalidation.PublishPackets);
        if (hasPackets)
        {
            Assert.AreSame(first, invalidation.PublishPackets[0]);
            Assert.AreSame(second, invalidation.PublishPackets[1]);
        }
    }

    [TestMethod]
    public async Task Recovery_Invalidation_Then_Late_Overwrite_Keep_Original_Admission_State()
    {
        using var context = new Context(2);
        var status = new MqttSessionStatus(context.Session);
        var message = new MqttApplicationMessageBuilder().WithTopic("same-receipt").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build();
        var oldState = new object();
        var newState = new object();
        object current = oldState;
        Assert.IsTrue(status.TryEnqueueApplicationMessage(message, out _, false, oldState));
        var oldItem = await context.Session.DequeuePacketAsync(CancellationToken.None);
        oldItem.Complete();
        Assert.IsTrue(status.TryEnqueueApplicationMessage(message, out _, false, new object()));
        Assert.IsTrue(status.TryEnqueueApplicationMessage(message, out _, false, new object()));
        var reAdmitted = false;
        var lateOverwriteObserved = false;
        context.Events.SessionApplicationMessagesInvalidatedEvent.AddHandler(args =>
        {
            if (args.Reason != MqttSessionApplicationMessagesInvalidationReason.RecoveryOverflow) return;
            Assert.HasCount(1, args.Messages);
            Assert.AreSame(oldState, args.Messages[0].EnqueueState);
            Assert.AreSame(oldItem.Packet, args.Messages[0].PublishPacket);
            if (ReferenceEquals(current, args.Messages[0].EnqueueState)) current = null;
            var completed = context.Session.DequeuePacketAsync(CancellationToken.None).GetAwaiter().GetResult();
            completed.Complete();
            context.Session.AcknowledgePublishPacket(((MqttPublishPacket)completed.Packet).PacketIdentifier);
            Assert.IsTrue(status.TryEnqueueApplicationMessage(message, out _, false, newState));
            current = newState;
            reAdmitted = true;
        });
        context.Events.QueuedApplicationMessageOverwrittenEvent.AddHandler(args =>
        {
            Assert.IsTrue(reAdmitted);
            Assert.AreSame(oldState, args.EnqueueState);
            Assert.AreSame(oldItem.Packet, args.Packet);
            if (ReferenceEquals(current, args.EnqueueState)) current = null;
            lateOverwriteObserved = true;
        });
        context.Session.Recover();
        Assert.IsTrue(reAdmitted);
        Assert.IsTrue(lateOverwriteObserved);
        Assert.AreSame(newState, current);
    }

    [TestMethod]
    public async Task Late_Ack_Callback_For_Same_Receipt_Does_Not_Terminate_New_Admission()
    {
        using var environment = CreateTestEnvironment();
        var server = await environment.StartServer();
        var receiver = await environment.ConnectClient();
        var session = (await server.GetSessionsAsync()).Single(s => s.Id == receiver.Options.ClientId);
        var message = new MqttApplicationMessageBuilder().WithTopic("same-receipt").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build();
        var oldState = new object();
        var newState = new object();
        object current = oldState;
        var oldAckEntered = Signal();
        var releaseOldAck = Signal();
        var oldAckFinished = Signal();
        var newAck = Signal();
        server.ClientAcknowledgedPublishPacketAsync += async args =>
        {
            Assert.AreSame(session.Items, args.SessionItems);
            if (ReferenceEquals(args.EnqueueState, oldState))
            {
                oldAckEntered.TrySetResult();
                await releaseOldAck.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (ReferenceEquals(current, args.EnqueueState)) current = null;
                Assert.AreSame(newState, current);
                oldAckFinished.TrySetResult();
            }
            else
            {
                Assert.AreSame(newState, args.EnqueueState);
                newAck.TrySetResult();
            }
        };
        Assert.IsTrue(session.TryEnqueueApplicationMessage(message, out _, false, oldState));
        await oldAckEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(session.TryEnqueueApplicationMessage(message, out _, false, newState));
        current = newState;
        releaseOldAck.SetResult();
        await oldAckFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await newAck.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await receiver.DisconnectAsync();
    }

    [TestMethod]
    [DataRow(MqttQualityOfServiceLevel.AtLeastOnce)]
    [DataRow(MqttQualityOfServiceLevel.ExactlyOnce)]
    public async Task Direct_State_Survives_Recovery_And_Rejected_Retry_Does_Not_Replace_It(MqttQualityOfServiceLevel qos)
    {
        using var context = new Context(1);
        var status = new MqttSessionStatus(context.Session);
        var message = new MqttApplicationMessageBuilder().WithTopic("state").WithQualityOfServiceLevel(qos).Build();
        var acceptedState = new object();
        var rejectedState = new object();
        Assert.IsTrue(status.TryEnqueueApplicationMessage(message, out var initial, false, acceptedState));
        var original = context.Session.PeekAcknowledgePublishPacket(initial.PacketIdentifier);
        Assert.IsFalse(status.TryEnqueueApplicationMessage(message, out var rejected, false, rejectedState));
        Assert.IsNull(rejected);
        context.Session.Recover();
        var recovered = await context.Session.DequeuePacketAsync(CancellationToken.None);
        Assert.AreSame(original, recovered.Packet);
        recovered.Complete();
        SessionApplicationMessagesInvalidatedEventArgs disposed = null;
        context.Events.SessionApplicationMessagesInvalidatedEvent.AddHandler(args => disposed = args);
        context.Session.Dispose();
        Assert.IsNotNull(disposed);
        Assert.HasCount(1, disposed.Messages);
        Assert.AreSame(original, disposed.Messages[0].PublishPacket);
        Assert.AreSame(acceptedState, disposed.Messages[0].EnqueueState);
    }

    static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    static MqttApplicationMessage Message(string topic = "correlation") => new MqttApplicationMessageBuilder().WithTopic(topic).Build();

    sealed class Context : IDisposable
    {
        readonly MqttRetainedMessagesManager _retained;
        readonly MqttClientSessionsManager _sessions;
        public MqttServerEventContainer Events { get; } = new();
        public MqttSession Session { get; }
        public Context(int capacity = 16, MqttPendingMessagesOverflowStrategy strategy = MqttPendingMessagesOverflowStrategy.DropOldestQueuedMessage)
        {
            var options = new MqttServerOptionsBuilder().WithMaxPendingMessagesPerClient(capacity).WithPendingMessagesOverflowStrategy(strategy).Build();
            var logger = new MqttNetNullLogger();
            _retained = new MqttRetainedMessagesManager(Events, logger);
            _sessions = new MqttClientSessionsManager(options, _retained, Events, logger);
            Session = new MqttSession(new MqttConnectPacket { ClientId = "same-client" }, new Hashtable(), options, Events, _retained, _sessions);
        }
        public Task<bool> Enqueue(MqttApplicationMessage message) => typeof(MqttSession)
            .GetMethod("EnqueueApplicationMessageAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .CreateDelegate<Func<string, MqttApplicationMessage, Func<MqttPublishPacket>, Task<bool>>>(Session)
            .Invoke("sender", message, () => new MqttPublishPacket { Topic = message.Topic });
        public void Dispose() { Session.Dispose(); _sessions.Dispose(); _retained.Dispose(); }
    }
}
