// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections;
using System.Reflection;
using MQTTnet.Diagnostics.Logger;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using MQTTnet.Server;
using MQTTnet.Server.Internal;
using MQTTnet.LowLevelClient;
using MQTTnet.Formatter;
using MQTTnet.Tests.Mockups;

namespace MQTTnet.Tests.Server;

[TestClass]
public sealed class OrderedSessionRecovery_Tests
{
    [TestMethod]
    [DataRow(8)]
    [DataRow(1000)]
    public async Task Persisted_Sequence_Precedes_Later_Native_Suffix_On_Wire(int capacity)
    {
        using var environment = new TestEnvironment();
        var options = new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(0)
            .WithPersistentSessions().WithMaxPendingMessagesPerClient(capacity).Build();
        var server = environment.CreateServer(options);
        var reconnect = false;
        var next = 0;
        const int total = 1156;
        var schedulingGate = new object();
        MqttSessionStatus session = null;
        IDictionary currentAttempt = null;
        IDictionary originalSessionItems = null;
        server.ValidatingConnectionAsync += args => { currentAttempt = args.SessionItems; return Task.CompletedTask; };
        server.PreparingSessionRecoveryAsync += args =>
        {
            Assert.AreSame(currentAttempt, args.ConnectionAttemptItems);
            if (reconnect) { Assert.AreSame(originalSessionItems, args.Session.Items); Assert.AreNotSame(args.Session.Items, args.ConnectionAttemptItems); }
            else originalSessionItems = args.Session.Items;
            session = args.Session;
            if (reconnect)
            {
                Assert.HasCount(capacity, args.Recovery.NeverSentMessages);
                while (next < total && args.Recovery.TryStageApplicationMessage(Message($"sequence/{next}"), next)) next++;
            }
            args.Recovery.Commit();
            return Task.CompletedTask;
        };
        server.ClientAcknowledgedPublishPacketAsync += acknowledged =>
        {
            lock (schedulingGate)
            {
                while (next < total && session.TryEnqueueApplicationMessage(Message($"sequence/{next}"), out _, false, next)) next++;
            }
            return Task.CompletedTask;
        };
        await server.StartAsync();
        environment.ServerPort = options.DefaultEndpointOptions.Port;
        using (var first = await Connect(environment))
        {
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task Closed(ClientDisconnectedEventArgs _) { disconnected.TrySetResult(); return Task.CompletedTask; }
            server.ClientDisconnectedAsync += Closed;
            await first.DisconnectAsync();
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            server.ClientDisconnectedAsync -= Closed;
        }
        for (var i = total - capacity; i < total; i++)
            Assert.IsTrue(session.TryEnqueueApplicationMessage(Message($"sequence/{i}"), out _, false, i));
        reconnect = true;
        using var resumed = await Connect(environment);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        for (var i = 0; i < total; i++)
        {
            var packet = await resumed.ReceiveAsync(deadline.Token);
            Assert.IsInstanceOfType<MqttPublishPacket>(packet);
            var publish = (MqttPublishPacket)packet;
            Assert.AreEqual($"sequence/{i}", publish.Topic);
            Assert.IsFalse(publish.Dup);
            await resumed.SendAsync(new MqttPubAckPacket { PacketIdentifier = publish.PacketIdentifier }, deadline.Token);
        }
        await resumed.DisconnectAsync();
    }

    [TestMethod]
    public async Task Awaiting_Recovery_Owner_Does_Not_Block_Another_Client_Connection()
    {
        using var environment = new TestEnvironment();
        var options = new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(0).Build();
        var server = environment.CreateServer(options);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.PreparingSessionRecoveryAsync += async args =>
        {
            if (args.Session.Id == "blocked") { entered.SetResult(); await release.Task; }
            args.Recovery.Commit();
        };
        await server.StartAsync();
        environment.ServerPort = options.DefaultEndpointOptions.Port;
        var first = Connect(environment, "blocked");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var second = await Connect(environment, "independent").WaitAsync(TimeSpan.FromSeconds(5));
            await second.DisconnectAsync();
        }
        finally { release.TrySetResult(); }
        using var connected = await first.WaitAsync(TimeSpan.FromSeconds(5));
        await connected.DisconnectAsync();
    }

    static async Task<ILowLevelMqttClient> Connect(TestEnvironment environment, string id = "ordered-wire")
    {
        var client = new MqttClientFactory().CreateLowLevelMqttClient();
        await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", environment.ServerPort)
            .WithClientId(id).WithCleanSession(false).WithProtocolVersion(MqttProtocolVersion.V311).Build());
        await client.SendAsync(new MqttConnectPacket { ClientId = id, CleanSession = false });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.IsInstanceOfType<MqttConnAckPacket>(await client.ReceiveAsync(timeout.Token));
        return client;
    }

    [TestMethod]
    [DataRow(MqttQualityOfServiceLevel.AtMostOnce)]
    [DataRow(MqttQualityOfServiceLevel.AtLeastOnce)]
    [DataRow(MqttQualityOfServiceLevel.ExactlyOnce)]
    public async Task Commit_Is_Callback_Free_And_Reclaims_Exact_Original_Token_After_Handler(MqttQualityOfServiceLevel qos)
    {
        using var context = new Context(2);
        var originalToken = new object();
        var successorToken = new object();
        context.Admit("old", qos, originalToken);
        // Dequeued but still awaiting an interceptor is also a never-attempted publication.
        var originalItem = await context.Session.DequeuePacketAsync(CancellationToken.None);
        var lease = context.Begin();
        Assert.HasCount(1, lease.NeverSentMessages);
        Assert.AreSame(originalToken, lease.NeverSentMessages[0].EnqueueState);
        lease.NeverSentMessages[0].PublishPacket.Topic = "mutated-snapshot";
        var outcomes = new List<SessionApplicationMessagesInvalidatedEventArgs>();
        context.Events.SessionApplicationMessagesInvalidatedEvent.AddHandler(args => outcomes.Add(args));
        var message = Message("successor", qos);
        Assert.IsTrue(lease.TryStageApplicationMessage(message, successorToken));
        message.Topic = "mutated-input";
        var accepted = lease.Commit();
        lease.Dispose();
        Assert.IsFalse(lease.CancellationToken.IsCancellationRequested);
        Assert.HasCount(0, outcomes);
        Assert.IsTrue(lease.IsCommitted);
        Assert.AreSame(successorToken, accepted[0].EnqueueState);
        accepted[0].PublishPacket.Topic = "mutated-result";
        Assert.IsFalse(new MqttSessionStatus(context.Session).TryEnqueueApplicationMessage(Message("bypass", qos), out _, false));
        await context.Finish(lease, true);
        Assert.IsTrue(originalItem.WaitAsync().IsCanceled);
        Assert.HasCount(1, outcomes);
        Assert.AreEqual(MqttSessionApplicationMessagesInvalidationReason.ApplicationMessageReclaimed, outcomes[0].Reason);
        Assert.AreSame(originalItem.Packet, outcomes[0].Messages[0].PublishPacket);
        Assert.AreEqual("old", outcomes[0].Messages[0].PublishPacket.Topic);
        Assert.AreSame(originalToken, outcomes[0].Messages[0].EnqueueState);
        var queued = (MqttPublishPacket)(await context.Session.DequeuePacketAsync(CancellationToken.None)).Packet;
        Assert.AreEqual("successor", queued.Topic);
    }

    [TestMethod]
    public async Task Failed_Owner_After_Commit_Preserves_Installed_State_And_Stale_Abort_Cannot_Unpause_Successor()
    {
        using var context = new Context(2);
        context.Admit("old");
        var old = context.Begin();
        Assert.IsTrue(old.TryStageApplicationMessage(Message("committed"), new object()));
        old.Commit();
        old.Abort();
        await context.Finish(old, false);
        context.Call("ActivateConnection");
        var successor = context.Begin();
        Assert.AreEqual("committed", successor.NeverSentMessages.Single().PublishPacket.Topic);
        old.Dispose();
        await context.Finish(old, true);
        Assert.IsFalse(new MqttSessionStatus(context.Session).TryEnqueueApplicationMessage(Message("bypass"), out _, false));
        Assert.IsTrue(successor.TryStageApplicationMessage(Message("final"), null));
        successor.Commit();
        await context.Finish(successor, true);
        Assert.AreEqual("final", ((MqttPublishPacket)(await context.Session.DequeuePacketAsync(CancellationToken.None)).Packet).Topic);
    }

    [TestMethod]
    public async Task Abort_Before_Commit_Preserves_Original_Queue_And_Identifier()
    {
        using var context = new Context(1);
        var packet = context.Admit("original");
        var identifier = packet.PacketIdentifier;
        var lease = context.Begin();
        Assert.IsTrue(lease.TryStageApplicationMessage(Message("replacement"), null));
        Assert.IsFalse(lease.TryStageApplicationMessage(Message("overflow"), null));
        lease.Abort();
        Assert.ThrowsExactly<OperationCanceledException>(() => lease.Commit());
        await context.Finish(lease, false);
        context.Call("ActivateConnection");
        var next = context.Begin();
        Assert.AreEqual(identifier, next.NeverSentMessages.Single().PublishPacket.PacketIdentifier);
        Assert.AreEqual("original", next.NeverSentMessages.Single().PublishPacket.Topic);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Started_Transactions_Retain_Original_Phase_Outside_Unsent_Capacity(bool pubRel)
    {
        using var context = new Context(1);
        const long generation = 0;
        var packet = context.Admit("started", pubRel ? MqttQualityOfServiceLevel.ExactlyOnce : MqttQualityOfServiceLevel.AtLeastOnce);
        await context.Session.DequeuePacketAsync(CancellationToken.None);
        Assert.IsTrue((bool)context.Call("MarkPublishSent", packet, generation));
        if (pubRel) context.Call("ProcessPubRec", packet.PacketIdentifier, false, generation, null);
        context.Admit("later");
        var lease = context.Begin();
        Assert.HasCount(1, lease.NeverSentMessages);
        Assert.HasCount(1, lease.StartedTransactions);
        Assert.AreEqual(packet.PacketIdentifier, lease.StartedTransactions[0].PacketIdentifier);
        Assert.AreEqual(pubRel ? MqttOutgoingTransactionPhase.AwaitPubComp : MqttOutgoingTransactionPhase.AwaitPubAck, lease.StartedTransactions[0].Phase);
        Assert.IsTrue(lease.TryStageApplicationMessage(Message("earlier-durable"), null));
        lease.Commit();
        await context.Finish(lease, true);
        var resumed = (await context.Session.DequeuePacketAsync(CancellationToken.None)).Packet;
        if (pubRel) Assert.AreEqual(packet.PacketIdentifier, ((MqttPubRelPacket)resumed).PacketIdentifier);
        else { Assert.AreSame(packet, resumed); Assert.IsTrue(packet.Dup); }
        Assert.AreEqual("earlier-durable", ((MqttPublishPacket)(await context.Session.DequeuePacketAsync(CancellationToken.None)).Packet).Topic);
    }

    [TestMethod]
    public void Recovery_Owner_Is_Exclusive()
    {
        using var context = new Context();
        Assert.ThrowsExactly<InvalidOperationException>(() => context.Events.AddSessionRecoveryHandler(_ => Task.CompletedTask));
    }

    [TestMethod]
    public async Task Activation_Pauses_Admission_And_Waits_For_Actual_Previous_Owner_Exit()
    {
        using var context = new Context();
        context.Admit("old");
        var prior = context.Begin();
        Assert.IsTrue(prior.TryStageApplicationMessage(Message("committed"), new object()));
        prior.Commit();
        var successorGeneration = (long)context.Call("ActivateConnection");
        Assert.IsFalse(new MqttSessionStatus(context.Session).TryEnqueueApplicationMessage(Message("gap"), out _, false));
        var wait = (Task)context.Call("WaitForRecoveryOwnerAsync", CancellationToken.None);
        Assert.IsFalse(wait.IsCompleted);
        Assert.ThrowsExactly<TargetInvocationException>(() => context.Begin());
        await context.Finish(prior, false);
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        var successor = context.Begin();
        Assert.AreEqual(successorGeneration, successor.ConnectionGeneration);
        Assert.AreEqual("committed", successor.NeverSentMessages.Single().PublishPacket.Topic);
        await context.Finish(prior, true);
        Assert.IsFalse(new MqttSessionStatus(context.Session).TryEnqueueApplicationMessage(Message("stale-unpause"), out _, false));
    }

    static MqttApplicationMessage Message(string topic, MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtLeastOnce) =>
        new MqttApplicationMessageBuilder().WithTopic(topic).WithQualityOfServiceLevel(qos).Build();

    sealed class Context : IDisposable
    {
        readonly MqttRetainedMessagesManager _retained;
        readonly MqttClientSessionsManager _sessions;
        public MqttServerEventContainer Events { get; } = new();
        public MqttSession Session { get; }
        public Context(int capacity = 16)
        {
            Events.AddSessionRecoveryHandler(_ => Task.CompletedTask);
            var options = new MqttServerOptionsBuilder().WithMaxPendingMessagesPerClient(capacity).Build();
            var logger = new MqttNetNullLogger();
            _retained = new MqttRetainedMessagesManager(Events, logger);
            _sessions = new MqttClientSessionsManager(options, _retained, Events, logger);
            Session = new MqttSession(new MqttConnectPacket { ClientId = "ordered" }, new Hashtable(), options, Events, _retained, _sessions);
        }
        public object Call(string name, params object[] args) => typeof(MqttSession).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(Session, args);
        public MqttSessionRecoveryLease Begin() => (MqttSessionRecoveryLease)Call("BeginRecovery", CancellationToken.None);
        public Task Finish(MqttSessionRecoveryLease lease, bool success) => (Task)Call("FinalizeRecoveryAsync", lease, success);
        public MqttPublishPacket Admit(string topic, MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtLeastOnce, object token = null)
        {
            Assert.IsTrue(new MqttSessionStatus(Session).TryEnqueueApplicationMessage(Message(topic, qos), out var result, false, token));
            return qos == MqttQualityOfServiceLevel.AtMostOnce ? null : Session.PeekAcknowledgePublishPacket(result.PacketIdentifier);
        }
        public void Dispose() { Session.Dispose(); _sessions.Dispose(); _retained.Dispose(); }
    }
}
