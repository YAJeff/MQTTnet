// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections;
using System.Reflection;
using MQTTnet.Diagnostics.Logger;
using MQTTnet.Formatter;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using MQTTnet.Server;
using MQTTnet.Server.Internal;
using MQTTnet.Adapter;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using MQTTnet.Tests.Mockups;
using MQTTnet.LowLevelClient;

namespace MQTTnet.Tests.Server;

[TestClass]
public sealed class DurableSessionPersistence_Tests
{
    [TestMethod]
    [DataRow(MqttQualityOfServiceLevel.AtLeastOnce, false)]
    [DataRow(MqttQualityOfServiceLevel.ExactlyOnce, false)]
    [DataRow(MqttQualityOfServiceLevel.ExactlyOnce, true)]
    public async Task Another_Live_Server_Resumes_Original_Transaction_And_Fences_Old_Wire(MqttQualityOfServiceLevel qos, bool pubRel)
    {
        var storage = new Store();
        using var firstEnvironment = new TestEnvironment { IgnoreServerLogErrors = true };
        using var secondEnvironment = new TestEnvironment();
        var firstServer = await Start(firstEnvironment, storage);
        var secondServer = await Start(secondEnvironment, storage);
        using var first = await ConnectWire(firstEnvironment);
        var firstSession = (await firstServer.GetSessionsAsync()).Single();
        Assert.IsTrue(firstSession.TryEnqueueApplicationMessage(new MqttApplicationMessageBuilder().WithTopic("durable/wire").WithQualityOfServiceLevel(qos).Build(), out var admission, false, new Token("wire")));
        var original = await Receive<MqttPublishPacket>(first);
        Assert.AreEqual(admission.PacketIdentifier, original.PacketIdentifier);
        if (pubRel)
        {
            await first.SendAsync(new MqttPubRecPacket { PacketIdentifier = original.PacketIdentifier });
            Assert.AreEqual(original.PacketIdentifier, (await Receive<MqttPubRelPacket>(first)).PacketIdentifier);
        }
        using var second = await ConnectWire(secondEnvironment, true);
        Assert.AreNotSame(firstSession.Items, (await secondServer.GetSessionsAsync()).Single().Items);
        if (pubRel)
        {
            Assert.AreEqual(original.PacketIdentifier, (await Receive<MqttPubRelPacket>(second)).PacketIdentifier);
            var oldDisconnected = Signal();
            firstServer.ClientDisconnectedAsync += _ => { oldDisconnected.TrySetResult(); return Task.CompletedTask; };
            await first.SendAsync(new MqttPubRecPacket { PacketIdentifier = original.PacketIdentifier });
            await oldDisconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsGreaterThan(0, storage.FencedTransitions);
            await second.SendAsync(new MqttPubCompPacket { PacketIdentifier = original.PacketIdentifier });
        }
        else
        {
            var resumed = await Receive<MqttPublishPacket>(second);
            Assert.AreEqual(original.PacketIdentifier, resumed.PacketIdentifier);
            Assert.IsTrue(resumed.Dup);
            if (qos == MqttQualityOfServiceLevel.ExactlyOnce)
            {
                await second.SendAsync(new MqttPubRecPacket { PacketIdentifier = resumed.PacketIdentifier });
                Assert.AreEqual(original.PacketIdentifier, (await Receive<MqttPubRelPacket>(second)).PacketIdentifier);
                await second.SendAsync(new MqttPubCompPacket { PacketIdentifier = resumed.PacketIdentifier });
            }
            else await second.SendAsync(new MqttPubAckPacket { PacketIdentifier = resumed.PacketIdentifier });
        }
        using var completed = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await storage.Terminal.Task.WaitAsync(completed.Token);
        Assert.HasCount(0, storage.Records);
        await first.DisconnectAsync();
        await second.DisconnectAsync();
    }

    static async Task<MqttServer> Start(TestEnvironment environment, Store storage)
    {
        var options = new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(0).WithPersistentSessions().WithMaxPendingMessagesPerClient(8).Build();
        options.SessionPersistence = storage;
        var server = environment.CreateServer(options);
        server.PreparingSessionRecoveryAsync += args => { Assert.IsNotNull(args.Connection); args.Recovery.Commit(); return Task.CompletedTask; };
        await server.StartAsync();
        environment.ServerPort = options.DefaultEndpointOptions.Port;
        return server;
    }

    static async Task<ILowLevelMqttClient> ConnectWire(TestEnvironment environment, bool sessionPresent = false)
    {
        var client = new MqttClientFactory().CreateLowLevelMqttClient();
        await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", environment.ServerPort).WithProtocolVersion(MqttProtocolVersion.V500).Build());
        await client.SendAsync(new MqttConnectPacket { ClientId = "durable", CleanSession = false, SessionExpiryInterval = uint.MaxValue, ReceiveMaximum = 1 });
        Assert.AreEqual(sessionPresent, (await Receive<MqttConnAckPacket>(client)).IsSessionPresent);
        return client;
    }

    static async Task<T> Receive<T>(ILowLevelMqttClient client) where T : MqttPacket
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var packet = await client.ReceiveAsync(timeout.Token);
        Assert.IsInstanceOfType<T>(packet);
        return (T)packet;
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PubComp_Waits_For_PubRel_Send_Completion_Instead_Of_Being_Lost(bool durable)
    {
        var storage = new Store();
        using var context = new Context(storage, durable);
        var channel = new HeldPubRelChannel();
        using var connection = context.Connect(channel);
        await context.Restore();
        var packet = context.Admit("pubrel-race", MqttQualityOfServiceLevel.ExactlyOnce);
        await context.Prepare(packet);
        context.Call("MarkPublishSent", packet, context.Generation);
        if (durable) await context.Acknowledge(packet, true, 0);
        else context.Call("ProcessPubRec", packet.PacketIdentifier, false, context.Generation, null);
        var send = connection.SendPacketAsync(new MqttPubRelPacket { PacketIdentifier = packet.PacketIdentifier }, CancellationToken.None);
        await channel.Visible.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var acknowledge = (Task)typeof(MqttConnectedClient).GetMethod("HandleIncomingPubCompPacket", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(connection, [new MqttPubCompPacket { PacketIdentifier = packet.PacketIdentifier }]);
        Assert.IsFalse(acknowledge.IsCompleted);
        Assert.AreSame(packet, context.Session.PeekAcknowledgePublishPacket(packet.PacketIdentifier));
        channel.Release.SetResult();
        await Task.WhenAll(send, acknowledge).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsNull(context.Session.PeekAcknowledgePublishPacket(packet.PacketIdentifier));
    }

    [TestMethod]
    public async Task Prewire_And_Terminal_Commits_Are_Awaited_And_Identifier_Remains_Reserved()
    {
        var storage = new Store();
        using var context = new Context(storage);
        await context.Restore();
        var packet = context.Admit("delivery");
        var entered = Signal();
        var release = Signal();
        storage.BeforeCommit = async _ => { entered.TrySetResult(); await release.Task; };
        var prepare = context.Prepare(packet);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(prepare.IsCompleted);
        Assert.AreSame(packet, context.Session.PeekAcknowledgePublishPacket(packet.PacketIdentifier));
        release.SetResult();
        await prepare;
        Assert.AreEqual(MqttOutgoingTransactionPhase.AwaitPubAck, storage.Records.Single().Value.Phase);
        Assert.IsTrue((bool)context.Call("MarkPublishSent", packet, context.Generation));
        entered = Signal(); release = Signal();
        var terminal = context.Acknowledge(packet, false, 0);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(terminal.IsCompleted);
        Assert.AreSame(packet, context.Session.PeekAcknowledgePublishPacket(packet.PacketIdentifier));
        release.SetResult();
        Assert.AreSame(packet, await terminal);
        Assert.IsNull(context.Session.PeekAcknowledgePublishPacket(packet.PacketIdentifier));
        Assert.HasCount(0, storage.Records);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Already_Retired_Only_Authorizes_Terminal_Release(bool terminal)
    {
        var storage = new Store();
        using var context = new Context(storage);
        await context.Restore();
        var packet = context.Admit("retired");
        if (terminal) { await context.Prepare(packet); context.Call("MarkPublishSent", packet, context.Generation); }
        storage.Override = transition => new MqttPersistenceCommitResult(Guid.Empty, transition.SessionGeneration, transition.OwnerFence, 0,
            MqttPersistenceCommitStatus.AlreadyRetired, transition.DeliveryHandle);
        if (terminal)
        {
            Assert.AreSame(packet, await context.Acknowledge(packet, false, 0));
            Assert.IsNull(context.Session.PeekAcknowledgePublishPacket(packet.PacketIdentifier));
            Assert.AreEqual(MqttPersistenceCommitStatus.AlreadyRetired, context.Call("GetDurableCompletionStatus", packet));
        }
        else
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => context.Prepare(packet));
            Assert.AreSame(packet, context.Session.PeekAcknowledgePublishPacket(packet.PacketIdentifier));
        }
    }

    [TestMethod]
    [DataRow((byte)0)]
    [DataRow((byte)128)]
    public async Task PubRec_Commits_Phase_Or_Negative_Terminal_Outcome(byte reason)
    {
        var storage = new Store();
        using var context = new Context(storage);
        await context.Restore();
        var packet = context.Admit("qos2", MqttQualityOfServiceLevel.ExactlyOnce);
        await context.Prepare(packet);
        context.Call("MarkPublishSent", packet, context.Generation);
        Assert.AreSame(packet, await context.Acknowledge(packet, true, reason));
        Assert.AreEqual(reason >= 128, new ClientAcknowledgedPublishPacketEventArgs("durable", null, context.Session.Items, packet,
            new MqttPubRecPacket { PacketIdentifier = packet.PacketIdentifier, ReasonCode = (MqttPubRecReasonCode)reason }).IsCompleted);
        if (reason < 128)
        {
            Assert.AreEqual(MqttOutgoingTransactionPhase.AwaitPubComp, storage.Records.Single().Value.Phase);
            Assert.IsTrue((bool)context.Call("CanSendPubRel", packet.PacketIdentifier, context.Generation));
        }
        else
        {
            Assert.HasCount(0, storage.Records);
            Assert.IsNull(storage.Transitions.Last().NextPhase);
            Assert.AreEqual(reason, storage.Transitions.Last().TerminalReasonCode);
            Assert.IsNull(context.Session.PeekAcknowledgePublishPacket(packet.PacketIdentifier));
        }
    }

    [TestMethod]
    public async Task New_Session_Imports_Original_Identifier_Phase_And_Subscriptions_Without_Subscribe_Events()
    {
        var storage = new Store();
        using var first = new Context(storage);
        await first.Restore();
        var packet = first.Admit("restore", MqttQualityOfServiceLevel.ExactlyOnce);
        await first.Prepare(packet);
        first.Call("MarkPublishSent", packet, first.Generation);
        await first.Acknowledge(packet, true, 0);
        storage.Subscriptions = [new MqttPersistedSubscription("restore/#", MqttQualityOfServiceLevel.ExactlyOnce, true, true, MqttRetainHandling.SendAtSubscribe, 7)];
        using var second = new Context(storage);
        var events = 0;
        second.Events.InterceptingSubscriptionEvent.AddHandler(_ => events++);
        second.Events.ClientSubscribedTopicEvent.AddHandler(_ => events++);
        await second.Restore();
        Assert.AreEqual(0, events);
        Assert.IsTrue(second.Session.HasSubscribedTopics);
        var restored = (MqttPubRelPacket)(await second.Session.DequeuePacketAsync(CancellationToken.None)).Packet;
        Assert.AreEqual(packet.PacketIdentifier, restored.PacketIdentifier);
        Assert.IsNotNull(second.Session.PeekAcknowledgePublishPacket(packet.PacketIdentifier));
    }

    static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [TestMethod]
    public async Task Delayed_Disconnection_Commit_Cannot_Remove_Replacement_Connection_Or_Session()
    {
        var storage = new Store();
        var entered = Signal();
        var release = Signal();
        var disconnects = 0;
        storage.BeforeDisconnected = async _ => { if (Interlocked.Increment(ref disconnects) == 1) { entered.TrySetResult(); await release.Task; } };
        var options = new MqttServerOptionsBuilder().WithPersistentSessions().Build();
        options.SessionPersistence = storage;
        var events = new MqttServerEventContainer();
        var captures = new List<MqttClientStatus>();
        events.AddSessionRecoveryHandler(args => { captures.Add(args.Connection); args.Recovery.Commit(); return Task.CompletedTask; });
        var logger = new MqttNetNullLogger();
        using var retained = new MqttRetainedMessagesManager(events, logger);
        using var manager = new MqttClientSessionsManager(options, retained, events, logger);
        var first = new ScriptedConnectionChannel();
        var second = new ScriptedConnectionChannel();
        var firstRun = manager.HandleClientConnectionAsync(first, CancellationToken.None);
        Task secondRun = Task.CompletedTask;
        try
        {
            await first.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            first.Close.TrySetResult();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            secondRun = manager.HandleClientConnectionAsync(second, CancellationToken.None);
            await second.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            release.TrySetResult();
            await firstRun.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreSame(second, manager.GetClients().Single().ChannelAdapter);
            Assert.HasCount(1, await manager.GetSessionsStatus());
            Assert.AreNotEqual(captures[0].ConnectionAttemptId, captures[1].ConnectionAttemptId);
            await captures[0].DisconnectAsync(new MqttServerClientDisconnectOptions());
            Assert.AreSame(second, manager.GetClients().Single().ChannelAdapter);
            Assert.IsTrue(manager.GetClients().Single().IsRunning);
        }
        finally
        {
            release.TrySetResult(); first.Close.TrySetResult(); second.Close.TrySetResult();
            await Task.WhenAll(firstRun, secondRun).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    [DataRow("transition")]
    [DataRow("generation")]
    [DataRow("owner")]
    [DataRow("revision")]
    [DataRow("handle")]
    [DataRow("uncertain")]
    public async Task Unconfirmed_Prewire_Result_Produces_No_Channel_Write_Or_Identifier_Release(string mismatch)
    {
        var storage = new Store();
        using var context = new Context(storage);
        var channel = new HeldPubRelChannel();
        using var connection = context.Connect(channel);
        await context.Restore();
        var packet = context.Admit("unconfirmed");
        storage.Override = transition =>
        {
            var result = new MqttPersistenceCommitResult(transition.TransitionId, transition.SessionGeneration, transition.OwnerFence,
                transition.ExpectedRevision + 1, MqttPersistenceCommitStatus.Applied, transition.DeliveryHandle);
            return mismatch switch
            {
                "transition" => result with { TransitionId = Guid.NewGuid() },
                "generation" => result with { SessionGeneration = Guid.NewGuid() },
                "owner" => result with { OwnerFence = result.OwnerFence + 1 },
                "revision" => result with { Revision = result.Revision + 1 },
                "handle" => result with { DeliveryHandle = "another" },
                _ => result with { Status = MqttPersistenceCommitStatus.Uncertain }
            };
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => connection.SendPacketAsync(packet, CancellationToken.None));
        Assert.AreEqual(0, channel.Writes);
        Assert.AreSame(packet, context.Session.PeekAcknowledgePublishPacket(packet.PacketIdentifier));
    }

    [TestMethod]
    public async Task Duplicate_Durable_Handle_Is_Backpressure_Without_Changing_Original_Admission()
    {
        using var context = new Context(new Store());
        await context.Restore();
        var original = context.Admit("unique");
        Assert.IsFalse(new MqttSessionStatus(context.Session).TryEnqueueApplicationMessage(new MqttApplicationMessageBuilder().WithTopic("duplicate")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), out var rejected, false, new Token("unique")));
        Assert.IsNull(rejected);
        Assert.AreSame(original, context.Session.PeekAcknowledgePublishPacket(original.PacketIdentifier));
        Assert.AreEqual(1L, context.Session.PendingDataPacketsCount);
    }

    [TestMethod]
    public async Task Wire_Request_Intent_And_Attempt_Identity_Survive_Shared_Filter_Rewrite()
    {
        var storage = new Store();
        using var environment = new TestEnvironment();
        var server = await Start(environment, storage);
        var connected = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ClientConnectedAsync += args => { connected.TrySetResult(args.ConnectionAttemptId); return Task.CompletedTask; };
        server.ClientDisconnectedAsync += args => { disconnected.TrySetResult(args.ConnectionAttemptId); return Task.CompletedTask; };
        var state = new object();
        var requestId = Guid.Empty;
        server.InterceptingInboundPacketAsync += args =>
        {
            if (args.Packet is MqttSubscribePacket subscribe)
            {
                requestId = args.RequestId;
                args.RequestState = state;
                foreach (var filter in subscribe.TopicFilters) filter.Topic = "effective/topic";
            }
            return Task.CompletedTask;
        };
        server.InterceptingSubscriptionAsync += args =>
        {
            Assert.AreSame(state, args.Request.RequestState);
            Assert.AreEqual(requestId, args.Request.RequestId);
            Assert.AreEqual(args.RequestFilterIndex == 0 ? "$share/first/topic" : "$share/second/topic", args.Request.Filters[args.RequestFilterIndex].Topic);
            return Task.CompletedTask;
        };
        using var client = await ConnectWire(environment);
        var attempt = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreNotEqual(Guid.Empty, attempt);
        Assert.AreEqual(attempt, storage.LastLoadRequest.ConnectionAttemptId);
        await client.SendAsync(new MqttSubscribePacket { PacketIdentifier = 9, SubscriptionIdentifier = 42, TopicFilters =
            [new MqttTopicFilter { Topic = "$share/first/topic", QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce },
             new MqttTopicFilter { Topic = "$share/second/topic", QualityOfServiceLevel = MqttQualityOfServiceLevel.ExactlyOnce }] });
        Assert.AreEqual((ushort)9, (await Receive<MqttSubAckPacket>(client)).PacketIdentifier);
        var transition = storage.LastSubscriptionTransition;
        Assert.AreEqual(attempt, transition.Request.ConnectionAttemptId);
        Assert.AreEqual(requestId, transition.Request.RequestId);
        Assert.AreNotEqual(Guid.Empty, requestId);
        Assert.AreSame(state, transition.Request.RequestState);
        Assert.HasCount(2, transition.Results);
        Assert.HasCount(1, transition.Subscriptions);
        Assert.AreEqual("$share/first/topic", transition.Request.Filters[0].Topic);
        Assert.AreEqual("$share/second/topic", transition.Request.Filters[1].Topic);
        Assert.IsTrue(transition.Results.All(result => result.EffectiveTopic == "effective/topic" && result.ProcessNatively));
        await client.DisconnectAsync();
        Assert.AreEqual(attempt, await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task New_Empty_Volatile_Session_Can_Opt_Out_Without_Missing_Context_Fallback()
    {
        var storage = new Store { UsePersistence = false };
        using var context = new Context(storage);
        await context.Restore(0);
        Assert.IsFalse((bool)context.Call("get_HasDurablePersistence"));
        Assert.IsTrue(new MqttSessionStatus(context.Session).TryEnqueueApplicationMessage(new MqttApplicationMessageBuilder().WithTopic("volatile").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), out var result, false));
        var packet = context.Session.PeekAcknowledgePublishPacket(result.PacketIdentifier);
        await context.Prepare(packet);
        Assert.HasCount(0, storage.Transitions);
        Assert.AreSame(packet, context.Session.AcknowledgePublishPacket(packet.PacketIdentifier));
    }

    [TestMethod]
    public async Task Resumed_Session_With_Zero_Expiry_Cannot_Opt_Out()
    {
        var storage = new Store { UsePersistence = false, SessionPresentOverride = true };
        using var context = new Context(storage);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => context.Restore(0));
        Assert.IsTrue((bool)context.Call("get_HasDurablePersistence"));
    }

    [TestMethod]
    [DataRow("token")]
    [DataRow("identifier")]
    [DataRow("sequence")]
    [DataRow("alias")]
    [DataRow("phase")]
    public async Task Invalid_Import_Fails_Before_Tracking_Is_Installed(string failure)
    {
        var storage = new Store { SessionPresentOverride = true };
        var packet = new MqttPublishPacket { PacketIdentifier = 17, Topic = "import", QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce, TopicAlias = failure == "alias" ? (ushort)1 : (ushort)0 };
        storage.Records.Add("first", new MqttPersistedOutgoingTransaction("first", 1, Guid.NewGuid(), failure == "phase" ? MqttOutgoingTransactionPhase.AwaitPubComp : MqttOutgoingTransactionPhase.AwaitPubAck,
            1, packet, new Token(failure == "token" ? "wrong" : "first")));
        if (failure is "identifier" or "sequence")
        {
            var second = new MqttPublishPacket { PacketIdentifier = failure == "identifier" ? (ushort)17 : (ushort)18, Topic = "second", QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce };
            storage.Records.Add("second", new MqttPersistedOutgoingTransaction("second", 1, Guid.NewGuid(), MqttOutgoingTransactionPhase.AwaitPubAck,
                failure == "sequence" ? 1 : 2, second, new Token("second")));
        }
        using var context = new Context(storage);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => context.Restore());
        Assert.IsNull(context.Session.PeekAcknowledgePublishPacket(17));
        Assert.IsNull(context.Session.PeekAcknowledgePublishPacket(18));
        Assert.AreEqual(0L, context.Session.PendingDataPacketsCount);
    }

    [TestMethod]
    public async Task Durable_Retained_Matches_Keep_Subscription_Identifiers_Without_Duplicate_Delivery()
    {
        var storage = new Store();
        using var context = new Context(storage);
        await context.Restore();
        await context.Retain(new MqttApplicationMessageBuilder().WithTopic("retained/data").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.ExactlyOnce).WithRetainFlag().WithPayload("value").Build());
        var result = await (Task<SubscribeResult>)context.Call("SubscribeForConnectionAsync", new MqttSubscribePacket
        {
            SubscriptionIdentifier = 42,
            TopicFilters = [new MqttTopicFilter { Topic = "retained/#", QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce },
                new MqttTopicFilter { Topic = "retained/+", QualityOfServiceLevel = MqttQualityOfServiceLevel.ExactlyOnce }]
        }, context.Generation, CancellationToken.None);
        Assert.HasCount(1, result.RetainedMessages);
        var wire = MQTTnet.Server.Internal.Formatter.MqttPublishPacketFactory.Create(result.RetainedMessages[0]);
        Assert.AreEqual(MqttQualityOfServiceLevel.ExactlyOnce, wire.QualityOfServiceLevel);
        CollectionAssert.AreEqual(new uint[] { 42 }, wire.SubscriptionIdentifiers);
    }

    [TestMethod]
    public async Task Shared_Filter_Extension_Can_Handle_Subscription_Without_Native_Installation()
    {
        var storage = new Store();
        using var context = new Context(storage);
        await context.Restore();
        var invoked = false;
        context.Events.InterceptingSubscriptionEvent.AddHandler(args =>
        {
            invoked = true;
            args.ProcessSubscription = false;
            args.Response.ReasonCode = MqttSubscribeReasonCode.GrantedQoS1;
        });
        var result = await (Task<SubscribeResult>)context.Call("SubscribeForConnectionAsync", new MqttSubscribePacket
        { TopicFilters = [new MqttTopicFilter { Topic = "$share/group/filter", QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce }] }, context.Generation, CancellationToken.None);
        Assert.IsTrue(invoked);
        Assert.AreEqual(MqttSubscribeReasonCode.GrantedQoS1, result.ReasonCodes.Single());
        Assert.HasCount(0, storage.Subscriptions);
        Assert.IsFalse(context.Session.HasSubscribedTopics);
    }

    [TestMethod]
    public async Task Lost_Terminal_Reply_Resolves_Bounded_Retirement_On_Restore()
    {
        var storage = new Store();
        using var context = new Context(storage);
        await context.Restore();
        var packet = context.Admit("lost-reply");
        await context.Prepare(packet);
        context.Call("MarkPublishSent", packet, context.Generation);
        storage.AfterCommit = transition => { if (transition.NextPhase == null) throw new IOException("response lost after retirement"); };
        await Assert.ThrowsExactlyAsync<IOException>(() => context.Acknowledge(packet, false, 0));
        Assert.IsNotNull(context.Session.PeekAcknowledgePublishPacket(packet.PacketIdentifier));
        SessionApplicationMessagesInvalidatedEventArgs invalidation = null;
        context.Events.SessionApplicationMessagesInvalidatedEvent.AddHandler(args => invalidation = args);
        await context.Reconnect();
        Assert.IsNull(context.Session.PeekAcknowledgePublishPacket(packet.PacketIdentifier));
        Assert.AreEqual(MqttSessionApplicationMessagesInvalidationReason.DurableTransactionRetired, invalidation.Reason);
        Assert.AreSame(packet, invalidation.Messages.Single().PublishPacket);
    }

    [TestMethod]
    public async Task Expired_Durable_Generation_Does_Not_Restage_Old_Native_Queued_Receipt()
    {
        var storage = new Store();
        using var context = new Context(storage);
        await context.Restore();
        var packet = context.Admit("old-generation");
        storage.Generation = Guid.NewGuid();
        storage.ForceNewSession = true;
        SessionApplicationMessagesInvalidatedEventArgs invalidation = null;
        context.Events.SessionApplicationMessagesInvalidatedEvent.AddHandler(args => invalidation = args);
        await context.Reconnect();
        Assert.AreEqual(0L, context.Session.PendingDataPacketsCount);
        Assert.IsNull(context.Session.PeekAcknowledgePublishPacket(packet.PacketIdentifier));
        Assert.AreEqual(MqttSessionApplicationMessagesInvalidationReason.DurableGenerationReplaced, invalidation.Reason);
        Assert.AreSame(packet, invalidation.Messages.Single().PublishPacket);
    }

    sealed record Token(string DeliveryHandle) : IMqttDurableDeliveryContext;

    sealed class Store : IMqttServerSessionPersistence
    {
        public Guid Generation { get; set; } = Guid.NewGuid();
        public bool ForceNewSession { get; set; }
        public bool? SessionPresentOverride { get; set; }
        public bool UsePersistence { get; set; } = true;
        readonly HashSet<string> _retired = new(StringComparer.Ordinal);
        long _subscriptionRevision;
        public TaskCompletionSource Terminal { get; } = Signal();
        public int FencedTransitions { get; private set; }
        long _fence;
        public Dictionary<string, MqttPersistedOutgoingTransaction> Records { get; } = new(StringComparer.Ordinal);
        public List<MqttOutgoingTransactionTransition> Transitions { get; } = new();
        public IReadOnlyList<MqttPersistedSubscription> Subscriptions { get; set; } = Array.Empty<MqttPersistedSubscription>();
        public Func<MqttOutgoingTransactionTransition, Task> BeforeCommit { get; set; }
        public Action<MqttOutgoingTransactionTransition> AfterCommit { get; set; }
        public Func<MqttSessionDisconnectedTransition, Task> BeforeDisconnected { get; set; }
        public Func<MqttOutgoingTransactionTransition, MqttPersistenceCommitResult> Override { get; set; }
        public MqttSessionPersistenceRequest LastLoadRequest { get; private set; }
        public MqttSubscriptionTransition LastSubscriptionTransition { get; private set; }
        public Task<MqttPersistedSessionSnapshot> LoadAsync(MqttSessionPersistenceRequest request, CancellationToken cancellationToken)
        {
            LastLoadRequest = request;
            return Task.FromResult(new MqttPersistedSessionSnapshot(1, Generation, Interlocked.Increment(ref _fence), _subscriptionRevision, SessionPresentOverride ?? (_fence > 1 && !ForceNewSession),
                Records.Values.ToList(), Subscriptions, request.PendingDeliveryHandles.Where(_retired.Contains).ToList(), UsePersistence));
        }
        public async Task<MqttPersistenceCommitResult> CommitTransitionAsync(MqttOutgoingTransactionTransition transition, CancellationToken cancellationToken)
        {
            Transitions.Add(transition);
            if (BeforeCommit != null) await BeforeCommit(transition);
            if (Override != null) return Override(transition);
            if (transition.OwnerFence != _fence)
            {
                FencedTransitions++;
                return new(transition.TransitionId, transition.SessionGeneration, transition.OwnerFence, 0, MqttPersistenceCommitStatus.Fenced, transition.DeliveryHandle);
            }
            if (transition.NextPhase.HasValue)
                Records[transition.DeliveryHandle] = new MqttPersistedOutgoingTransaction(transition.DeliveryHandle, transition.ExpectedRevision + 1, transition.TransitionId,
                    transition.NextPhase.Value, transition.SendSequence, transition.PublishPacket, new Token(transition.DeliveryHandle));
            else { Records.Remove(transition.DeliveryHandle); _retired.Add(transition.DeliveryHandle); Terminal.TrySetResult(); }
            AfterCommit?.Invoke(transition);
            return new(transition.TransitionId, transition.SessionGeneration, transition.OwnerFence, transition.ExpectedRevision + 1, MqttPersistenceCommitStatus.Applied, transition.DeliveryHandle);
        }
        public Task<MqttPersistenceCommitResult> CommitSubscriptionsAsync(MqttSubscriptionTransition transition, CancellationToken cancellationToken)
        {
            LastSubscriptionTransition = transition;
            Subscriptions = transition.Subscriptions;
            _subscriptionRevision = transition.ExpectedRevision + 1;
            return Task.FromResult(new MqttPersistenceCommitResult(transition.TransitionId, transition.SessionGeneration, transition.OwnerFence, _subscriptionRevision, MqttPersistenceCommitStatus.Applied, null));
        }
        public async Task<MqttPersistenceCommitResult> CommitDisconnectedAsync(MqttSessionDisconnectedTransition transition, CancellationToken cancellationToken)
        {
            if (BeforeDisconnected != null) await BeforeDisconnected(transition);
            if (transition.OwnerFence != _fence) return new MqttPersistenceCommitResult(transition.TransitionId, transition.SessionGeneration, transition.OwnerFence, 0, MqttPersistenceCommitStatus.Fenced, null);
            _subscriptionRevision = transition.ExpectedRevision + 1;
            return new MqttPersistenceCommitResult(transition.TransitionId, transition.SessionGeneration, transition.OwnerFence, _subscriptionRevision, MqttPersistenceCommitStatus.Applied, null);
        }
        public Task<MqttPersistenceCommitResult> DeleteSessionAsync(MqttSessionPersistenceDeletion deletion, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    sealed class Context : IDisposable
    {
        readonly MqttRetainedMessagesManager _retained;
        readonly MqttClientSessionsManager _sessions;
        readonly MqttServerOptions _options;
        public MqttServerEventContainer Events { get; } = new();
        public MqttSession Session { get; }
        public long Generation { get; private set; }
        public Context(Store storage, bool durable = true)
        {
            Events.AddSessionRecoveryHandler(_ => Task.CompletedTask);
            var options = new MqttServerOptionsBuilder().WithMaxPendingMessagesPerClient(8).Build();
            options.SessionPersistence = durable ? storage : null;
            _options = options;
            var logger = new MqttNetNullLogger();
            _retained = new MqttRetainedMessagesManager(Events, logger);
            _sessions = new MqttClientSessionsManager(options, _retained, Events, logger);
            Session = new MqttSession(new MqttConnectPacket { ClientId = "durable" }, new Hashtable(), options, Events, _retained, _sessions);
            Generation = (long)Call("ActivateConnection");
        }
        public object Call(string name, params object[] args) => typeof(MqttSession).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(Session, args);
        public async Task Restore(uint expiry = uint.MaxValue)
        {
            if (_options.SessionPersistence != null)
                await (Task<bool>)Call("RestoreDurableStateAsync", new MqttSessionPersistenceRequest("durable", Guid.NewGuid(), new Hashtable(), false, MqttProtocolVersion.V500, expiry), Generation, CancellationToken.None);
            var lease = (MqttSessionRecoveryLease)Call("BeginRecovery", CancellationToken.None);
            lease.Commit();
            await (Task)Call("FinalizeRecoveryAsync", lease, true);
        }
        public async Task Reconnect() { Generation = (long)Call("ActivateConnection"); await Restore(); }
        public Task Retain(MqttApplicationMessage message) => _retained.UpdateMessage("publisher", message);
        public MqttConnectedClient Connect(IMqttChannelAdapter adapter)
        {
            var connection = new MqttConnectedClient(new MqttConnectPacket { ClientId = "durable", ReceiveMaximum = 8 }, adapter, Session, _options, Events, _sessions, new MqttNetNullLogger());
            Generation = (long)typeof(MqttConnectedClient).GetProperty("ConnectionGeneration", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(connection);
            return connection;
        }
        public MqttPublishPacket Admit(string handle, MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtLeastOnce)
        {
            Assert.IsTrue(new MqttSessionStatus(Session).TryEnqueueApplicationMessage(new MqttApplicationMessageBuilder().WithTopic("durable/data").WithQualityOfServiceLevel(qos).Build(), out var result, false, new Token(handle)));
            return Session.PeekAcknowledgePublishPacket(result.PacketIdentifier);
        }
        public Task Prepare(MqttPublishPacket packet) => (Task)Call("PrepareDurablePublishAsync", packet, packet, Generation, CancellationToken.None);
        public Task<MqttPublishPacket> Acknowledge(MqttPublishPacket packet, bool pubRec, byte reason) =>
            (Task<MqttPublishPacket>)Call("CompleteDurableAcknowledgementAsync", packet.PacketIdentifier, packet.QualityOfServiceLevel, reason, pubRec, Generation, CancellationToken.None);
        public void Dispose() { Session.Dispose(); _sessions.Dispose(); _retained.Dispose(); }
    }

    class HeldPubRelChannel : IMqttChannelAdapter
    {
        public int Writes { get; private set; }
        public TaskCompletionSource Visible { get; } = Signal();
        public TaskCompletionSource Release { get; } = Signal();
        public MqttPacketFormatterAdapter PacketFormatterAdapter { get; } = new(MqttProtocolVersion.V500, new MqttBufferWriter(4096, 65536));
        public long BytesReceived => 0;
        public long BytesSent => 0;
        public X509Certificate2 ClientCertificate => null;
        public EndPoint RemoteEndPoint => null;
        public EndPoint LocalEndPoint => null;
        public bool IsSecureConnection => false;
        public Task ConnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken token) => Task.CompletedTask;
        public void ResetStatistics() { }
        public void Dispose() { }
        public virtual Task<MqttPacket> ReceivePacketAsync(CancellationToken token) => throw new NotSupportedException();
        public virtual async Task SendPacketAsync(MqttPacket packet, CancellationToken token)
        {
            Writes++;
            if (packet is MqttPubRelPacket) { Visible.TrySetResult(); await Release.Task.WaitAsync(token); }
        }
    }

    sealed class ScriptedConnectionChannel : HeldPubRelChannel
    {
        int _reads;
        public TaskCompletionSource Connected { get; } = Signal();
        public TaskCompletionSource Close { get; } = Signal();
        public override async Task<MqttPacket> ReceivePacketAsync(CancellationToken token)
        {
            if (Interlocked.Increment(ref _reads) == 1) return new MqttConnectPacket { ClientId = "durable", CleanSession = false, SessionExpiryInterval = uint.MaxValue, ReceiveMaximum = 8 };
            await Close.Task.WaitAsync(token);
            return null;
        }
        public override Task SendPacketAsync(MqttPacket packet, CancellationToken token)
        {
            if (packet is MqttConnAckPacket) Connected.TrySetResult();
            return base.SendPacketAsync(packet, token);
        }
    }
}

