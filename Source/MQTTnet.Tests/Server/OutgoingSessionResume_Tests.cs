// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using MQTTnet.Formatter;
using System.Collections;
using System.Reflection;
using MQTTnet.Diagnostics.Logger;
using MQTTnet.Adapter;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using MQTTnet.LowLevelClient;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using MQTTnet.Server;
using MQTTnet.Server.Internal;
using MQTTnet.Tests.Mockups;

namespace MQTTnet.Tests.Server;

[TestClass]
public sealed class OutgoingSessionResume_Tests
{
    [TestMethod]
    [DataRow(MqttProtocolVersion.V311, MqttQualityOfServiceLevel.AtLeastOnce, false)]
    [DataRow(MqttProtocolVersion.V500, MqttQualityOfServiceLevel.AtLeastOnce, false)]
    [DataRow(MqttProtocolVersion.V311, MqttQualityOfServiceLevel.ExactlyOnce, false)]
    [DataRow(MqttProtocolVersion.V500, MqttQualityOfServiceLevel.ExactlyOnce, false)]
    [DataRow(MqttProtocolVersion.V311, MqttQualityOfServiceLevel.ExactlyOnce, true)]
    [DataRow(MqttProtocolVersion.V500, MqttQualityOfServiceLevel.ExactlyOnce, true)]
    public async Task Repeated_Resume_Preserves_Original_Identifier_Phase_And_Admission_State(
        MqttProtocolVersion protocol, MqttQualityOfServiceLevel qos, bool afterPubRel)
    {
        using var environment = new TestEnvironment();
        var server = await environment.StartServer(options => options.WithPersistentSessions());
        var current = await Connect(environment, protocol);
        var status = (await server.GetSessionsAsync()).Single();
        var state = new object();
        var acknowledged = new TaskCompletionSource<ClientAcknowledgedPublishPacketEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ClientAcknowledgedPublishPacketAsync += args => { acknowledged.TrySetResult(args); return Task.CompletedTask; };
        Assert.IsTrue(status.TryEnqueueApplicationMessage(Message(qos), out var admission, false, state));
        var original = await Receive<MqttPublishPacket>(current);
        Assert.AreEqual(admission.PacketIdentifier, original.PacketIdentifier);
        Assert.IsFalse(original.Dup);
        if (afterPubRel)
        {
            await current.SendAsync(new MqttPubRecPacket { PacketIdentifier = original.PacketIdentifier });
            Assert.AreEqual(original.PacketIdentifier, (await Receive<MqttPubRelPacket>(current)).PacketIdentifier);
        }

        for (var i = 0; i < 2; i++)
        {
            await Close(server, current);
            current = await Connect(environment, protocol, sessionPresent: true);
            Assert.AreSame(status.Items, (await server.GetSessionsAsync()).Single().Items);
            if (afterPubRel)
            {
                Assert.AreEqual(original.PacketIdentifier, (await Receive<MqttPubRelPacket>(current)).PacketIdentifier);
                // A duplicate success PUBREC must retain the PUBREL phase and context.
                await current.SendAsync(new MqttPubRecPacket { PacketIdentifier = original.PacketIdentifier });
                Assert.AreEqual(original.PacketIdentifier, (await Receive<MqttPubRelPacket>(current)).PacketIdentifier);
            }
            else
            {
                var retry = await Receive<MqttPublishPacket>(current);
                Assert.AreEqual(original.PacketIdentifier, retry.PacketIdentifier);
                Assert.AreEqual(original.Topic, retry.Topic);
                Assert.IsTrue(retry.Dup);
            }
        }

        if (qos == MqttQualityOfServiceLevel.AtLeastOnce)
            await current.SendAsync(new MqttPubAckPacket { PacketIdentifier = original.PacketIdentifier });
        else
        {
            if (!afterPubRel)
            {
                await current.SendAsync(new MqttPubRecPacket { PacketIdentifier = original.PacketIdentifier });
                await Receive<MqttPubRelPacket>(current);
            }
            await current.SendAsync(new MqttPubCompPacket { PacketIdentifier = original.PacketIdentifier });
        }
        var completion = await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreSame(state, completion.EnqueueState);
        Assert.AreSame(status.Items, completion.SessionItems);
        Assert.AreEqual(original.PacketIdentifier, completion.PublishPacket.PacketIdentifier);
        Assert.IsTrue(completion.IsCompleted);
        await Close(server, current);
        current = await Connect(environment, protocol, sessionPresent: true);
        await current.SendAsync(MqttPingReqPacket.Instance);
        await Receive<MqttPingRespPacket>(current); // No transaction is replayed after terminal ACK.
        Assert.AreEqual(0L, (await server.GetSessionsAsync()).Single().PendingApplicationMessagesCount);
        await Close(server, current);
    }

    [TestMethod]
    public async Task Pending_PubRel_Resumes_As_Control_With_Smaller_Receive_Window()
    {
        using var environment = new TestEnvironment();
        var server = await environment.StartServer(options => options.WithPersistentSessions());
        var client = await Connect(environment, MqttProtocolVersion.V500, receiveMaximum: 2);
        var status = (await server.GetSessionsAsync()).Single();
        var ids = new List<ushort>();
        for (var i = 0; i < 2; i++)
        {
            Assert.IsTrue(status.TryEnqueueApplicationMessage(Message(MqttQualityOfServiceLevel.ExactlyOnce), out _, false));
            var publish = await Receive<MqttPublishPacket>(client);
            ids.Add(publish.PacketIdentifier);
            await client.SendAsync(new MqttPubRecPacket { PacketIdentifier = publish.PacketIdentifier });
            await Receive<MqttPubRelPacket>(client);
        }
        await Close(server, client);
        client = await Connect(environment, MqttProtocolVersion.V500, sessionPresent: true, receiveMaximum: 1);
        foreach (var id in ids) Assert.AreEqual(id, (await Receive<MqttPubRelPacket>(client)).PacketIdentifier);
        foreach (var id in ids) await client.SendAsync(new MqttPubCompPacket { PacketIdentifier = id });
        await client.SendAsync(MqttPingReqPacket.Instance);
        await Receive<MqttPingRespPacket>(client);
        await Close(server, client);
    }

    [TestMethod]
    public async Task Takeover_Fences_Delayed_Old_Publish_Without_Reassigning_Identifier()
    {
        using var environment = new TestEnvironment();
        var server = await environment.StartServer(options => options.WithPersistentSessions());
        var old = await Connect(environment, MqttProtocolVersion.V500);
        var status = (await server.GetSessionsAsync()).Single();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldEdited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var intercepted = 0;
        server.InterceptingOutboundPacketAsync += async args =>
        {
            if (args.Packet is MqttPublishPacket oldPacket && Interlocked.Increment(ref intercepted) == 1)
            {
                blocked.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
                oldPacket.Topic = "late-old-mutation";
                oldPacket.PacketIdentifier++;
                oldPacket.CorrelationData[0] = 0;
                oldPacket.UserProperties.Clear();
                oldEdited.TrySetResult();
            }
        };
        var state = new object();
        Assert.IsTrue(status.TryEnqueueApplicationMessage(Message(MqttQualityOfServiceLevel.ExactlyOnce), out var admission, false, state));
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var replacement = await Connect(environment, MqttProtocolVersion.V500, sessionPresent: true);
        var publish = await Receive<MqttPublishPacket>(replacement);
        Assert.AreEqual(admission.PacketIdentifier, publish.PacketIdentifier);
        Assert.IsFalse(publish.Dup); // The old interceptor prevented any write attempt.
        var completion = new TaskCompletionSource<ClientAcknowledgedPublishPacketEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ClientAcknowledgedPublishPacketAsync += args => { completion.TrySetResult(args); return Task.CompletedTask; };
        release.TrySetResult();
        await oldEdited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await replacement.SendAsync(new MqttPubRecPacket { PacketIdentifier = publish.PacketIdentifier });
        await Receive<MqttPubRelPacket>(replacement);
        await replacement.SendAsync(new MqttPubCompPacket { PacketIdentifier = publish.PacketIdentifier });
        var acknowledged = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreSame(state, acknowledged.EnqueueState);
        Assert.AreEqual("outgoing/one", acknowledged.PublishPacket.Topic);
        Assert.AreEqual(admission.PacketIdentifier, acknowledged.PublishPacket.PacketIdentifier);
        Assert.AreEqual((byte)7, acknowledged.PublishPacket.CorrelationData[0]);
        Assert.HasCount(1, acknowledged.PublishPacket.UserProperties);
        await replacement.SendAsync(MqttPingReqPacket.Instance);
        await Receive<MqttPingRespPacket>(replacement);
        await Close(server, replacement);
        await old.DisconnectAsync();
    }

    [TestMethod]
    [DataRow(MqttProtocolVersion.V311)]
    [DataRow(MqttProtocolVersion.V500)]
    public async Task Received_PubRec_With_Suppressed_PubRel_Resumes_PubRel(MqttProtocolVersion protocol)
    {
        using var environment = new TestEnvironment();
        var server = await environment.StartServer(options => options.WithPersistentSessions());
        var client = await Connect(environment, protocol);
        var suppress = true;
        server.InterceptingOutboundPacketAsync += args =>
        {
            if (args.Packet is MqttPubRelPacket && suppress) args.ProcessPacket = false;
            return Task.CompletedTask;
        };
        var status = (await server.GetSessionsAsync()).Single();
        Assert.IsTrue(status.TryEnqueueApplicationMessage(Message(MqttQualityOfServiceLevel.ExactlyOnce), out _, false));
        var publish = await Receive<MqttPublishPacket>(client);
        await client.SendAsync(new MqttPubRecPacket { PacketIdentifier = publish.PacketIdentifier });
        await client.SendAsync(MqttPingReqPacket.Instance);
        await Receive<MqttPingRespPacket>(client);
        await Close(server, client);
        suppress = false;
        client = await Connect(environment, protocol, sessionPresent: true);
        Assert.AreEqual(publish.PacketIdentifier, (await Receive<MqttPubRelPacket>(client)).PacketIdentifier);
        await client.SendAsync(new MqttPubCompPacket { PacketIdentifier = publish.PacketIdentifier });
        await client.SendAsync(MqttPingReqPacket.Instance);
        await Receive<MqttPingRespPacket>(client);
        await Close(server, client);
    }

    [TestMethod]
    [DataRow(MqttQualityOfServiceLevel.AtLeastOnce)]
    [DataRow(MqttQualityOfServiceLevel.ExactlyOnce)]
    public void Stale_Wire_Generation_Cannot_Retire_Resumed_Exchange(MqttQualityOfServiceLevel qos)
    {
        var options = new MqttServerOptionsBuilder().Build();
        var events = new MqttServerEventContainer();
        var logger = new MqttNetNullLogger();
        using var retained = new MqttRetainedMessagesManager(events, logger);
        using var manager = new MqttClientSessionsManager(options, retained, events, logger);
        using var session = new MqttSession(new MqttConnectPacket { ClientId = "generation" }, new Hashtable(), options, events, retained, manager);
        object Call(string name, params object[] args) => typeof(MqttSession).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(session, args);
        var oldGeneration = (long)Call("ActivateConnection");
        Assert.IsTrue(new MqttSessionStatus(session).TryEnqueueApplicationMessage(Message(qos), out var admission, false));
        var packet = session.PeekAcknowledgePublishPacket(admission.PacketIdentifier);
        Assert.IsTrue((bool)Call("MarkPublishSent", packet, oldGeneration));
        if (qos == MqttQualityOfServiceLevel.ExactlyOnce)
        {
            Assert.IsTrue((bool)Call("ProcessPubRec", packet.PacketIdentifier, false, oldGeneration, null));
            Call("MarkPubRelSent", packet.PacketIdentifier, oldGeneration);
        }
        var newGeneration = (long)Call("ActivateConnection");
        session.Recover();
        Assert.IsFalse((bool)Call("MarkPublishSent", packet, oldGeneration));
        Assert.IsNull(Call("AcknowledgePublishPacket", packet.PacketIdentifier, qos, oldGeneration));
        Assert.AreSame(packet, session.PeekAcknowledgePublishPacket(packet.PacketIdentifier));
        Assert.AreSame(packet, Call("AcknowledgePublishPacket", packet.PacketIdentifier, qos, newGeneration));
        Assert.IsNull(session.PeekAcknowledgePublishPacket(packet.PacketIdentifier));
    }

    [TestMethod]
    [DataRow(MqttQualityOfServiceLevel.AtLeastOnce, MqttPendingMessagesOverflowStrategy.DropNewMessage)]
    [DataRow(MqttQualityOfServiceLevel.AtLeastOnce, MqttPendingMessagesOverflowStrategy.DropOldestQueuedMessage)]
    [DataRow(MqttQualityOfServiceLevel.ExactlyOnce, MqttPendingMessagesOverflowStrategy.DropNewMessage)]
    [DataRow(MqttQualityOfServiceLevel.ExactlyOnce, MqttPendingMessagesOverflowStrategy.DropOldestQueuedMessage)]
    public async Task Started_Publish_Does_Not_Compete_With_Unsent_Backlog_Capacity(MqttQualityOfServiceLevel qos, MqttPendingMessagesOverflowStrategy strategy)
    {
        using var environment = new TestEnvironment();
        var server = await environment.StartServer(options => options.WithPersistentSessions().WithMaxPendingMessagesPerClient(1).WithPendingMessagesOverflowStrategy(strategy));
        var client = await Connect(environment, MqttProtocolVersion.V500);
        var status = (await server.GetSessionsAsync()).Single();
        Assert.IsTrue(status.TryEnqueueApplicationMessage(Message(qos), out var firstAdmission, false));
        var first = await Receive<MqttPublishPacket>(client);
        Assert.IsTrue(status.TryEnqueueApplicationMessage(Message(qos), out var secondAdmission, false));
        var overwritten = 0;
        server.QueuedApplicationMessageOverwrittenAsync += args => { overwritten++; return Task.CompletedTask; };
        await Close(server, client);
        client = await Connect(environment, MqttProtocolVersion.V500, sessionPresent: true);
        var retry = await Receive<MqttPublishPacket>(client);
        Assert.AreEqual(firstAdmission.PacketIdentifier, retry.PacketIdentifier);
        Assert.IsTrue(retry.Dup);
        Assert.AreEqual(1L, status.PendingApplicationMessagesCount);
        await Finish(client, retry);
        var neverSent = await Receive<MqttPublishPacket>(client);
        Assert.AreEqual(secondAdmission.PacketIdentifier, neverSent.PacketIdentifier);
        Assert.IsFalse(neverSent.Dup);
        Assert.AreEqual(0, overwritten);
        await Finish(client, neverSent);
        await client.SendAsync(MqttPingReqPacket.Instance);
        await Receive<MqttPingRespPacket>(client);
        await Close(server, client);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Takeover_Joins_Actual_Channel_Write_Before_Recovery(bool cooperativeCancellation)
    {
        var options = new MqttServerOptionsBuilder().Build();
        var events = new MqttServerEventContainer();
        var logger = new MqttNetNullLogger();
        using var retained = new MqttRetainedMessagesManager(events, logger);
        using var manager = new MqttClientSessionsManager(options, retained, events, logger);
        var connect = new MqttConnectPacket { ClientId = "write-boundary", ReceiveMaximum = 1 };
        using var session = new MqttSession(connect, new Hashtable(), options, events, retained, manager);
        using var adapter = new GatedAdapter(cooperativeCancellation);
        using var client = new MqttConnectedClient(connect, adapter, session, options, events, manager, logger);
        Assert.IsTrue(new MqttSessionStatus(session).TryEnqueueApplicationMessage(Message(MqttQualityOfServiceLevel.AtLeastOnce), out var admission, false));
        var run = client.RunAsync();
        await adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var quiesce = (Task)typeof(MqttConnectedClient).GetMethod("QuiesceForRecoveryAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(client, new object[] { timeout.Token });
        await adapter.CancellationObserved.Task.WaitAsync(timeout.Token);
        if (!cooperativeCancellation)
        {
            Assert.IsFalse(quiesce.IsCompleted);
            Assert.IsFalse(adapter.Finished.Task.IsCompleted);
            adapter.Release.TrySetResult();
        }
        await quiesce;
        Assert.IsTrue(adapter.Finished.Task.IsCompleted);
        await run.WaitAsync(timeout.Token);
        typeof(MqttSession).GetMethod("ActivateConnection", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(session, null);
        session.Recover();
        var retry = (MqttPublishPacket)(await session.DequeuePacketAsync(timeout.Token)).Packet;
        Assert.AreEqual(admission.PacketIdentifier, retry.PacketIdentifier);
        Assert.IsTrue(retry.Dup);
        await client.SendPacketAsync(retry, CancellationToken.None);
        Assert.AreEqual(1, adapter.Writes);
    }

    [TestMethod]
    public async Task Identifier_Exhaustion_Is_Backpressure_And_Terminal_Ack_Releases_Reservation()
    {
        var options = new MqttServerOptionsBuilder().WithMaxPendingMessagesPerClient(ushort.MaxValue + 1).Build();
        var events = new MqttServerEventContainer();
        var logger = new MqttNetNullLogger();
        using var retained = new MqttRetainedMessagesManager(events, logger);
        using var manager = new MqttClientSessionsManager(options, retained, events, logger);
        using var session = new MqttSession(new MqttConnectPacket { ClientId = "identifier-space" }, new Hashtable(), options, events, retained, manager);
        var status = new MqttSessionStatus(session);
        var message = Message(MqttQualityOfServiceLevel.AtLeastOnce);
        for (var i = 1; i <= ushort.MaxValue; i++)
        {
            Assert.IsTrue(status.TryEnqueueApplicationMessage(message, out var admitted, false));
            Assert.AreEqual(i, (int)admitted.PacketIdentifier);
        }
        Assert.IsFalse(status.TryEnqueueApplicationMessage(message, out var rejected, false));
        Assert.IsNull(rejected);
        Assert.AreEqual((long)ushort.MaxValue, status.PendingApplicationMessagesCount);
        var first = await session.DequeuePacketAsync(CancellationToken.None);
        first.Complete();
        Assert.AreSame(first.Packet, session.AcknowledgePublishPacket(1));
        Assert.IsTrue(status.TryEnqueueApplicationMessage(message, out var resumed, false));
        Assert.AreEqual((ushort)1, resumed.PacketIdentifier);
        Assert.AreNotSame(first.Packet, session.PeekAcknowledgePublishPacket(1));
    }

    [TestMethod]
    public void Identifier_Wrap_Skips_Original_Reservations_After_Recovery()
    {
        var options = new MqttServerOptionsBuilder().Build();
        var events = new MqttServerEventContainer();
        var logger = new MqttNetNullLogger();
        using var retained = new MqttRetainedMessagesManager(events, logger);
        using var manager = new MqttClientSessionsManager(options, retained, events, logger);
        using var session = new MqttSession(new MqttConnectPacket { ClientId = "identifier-wrap" }, new Hashtable(), options, events, retained, manager);
        var status = new MqttSessionStatus(session);
        Assert.IsTrue(status.TryEnqueueApplicationMessage(Message(MqttQualityOfServiceLevel.ExactlyOnce), out var original, false));
        var packet = session.PeekAcknowledgePublishPacket(original.PacketIdentifier);
        var provider = (MqttPacketIdentifierProvider)typeof(MqttSession).GetField("_packetIdentifierProvider", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(session);
        typeof(MqttPacketIdentifierProvider).GetField("_value", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(provider, (ushort)(ushort.MaxValue - 1));
        Assert.IsTrue(status.TryEnqueueApplicationMessage(Message(MqttQualityOfServiceLevel.AtLeastOnce), out var last, false));
        Assert.AreEqual(ushort.MaxValue, last.PacketIdentifier);
        session.Recover();
        Assert.AreSame(packet, session.PeekAcknowledgePublishPacket(1));
        Assert.IsTrue(status.TryEnqueueApplicationMessage(Message(MqttQualityOfServiceLevel.AtLeastOnce), out var next, false));
        Assert.AreEqual((ushort)2, next.PacketIdentifier);
    }

    sealed class GatedAdapter(bool cooperativeCancellation) : IMqttChannelAdapter
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Writes { get; private set; }
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
        public async Task<MqttPacket> ReceivePacketAsync(CancellationToken token) { await Task.Delay(Timeout.Infinite, token); return null; }
        public async Task SendPacketAsync(MqttPacket packet, CancellationToken token)
        {
            Writes++;
            using var registration = token.Register(() => CancellationObserved.TrySetResult());
            Entered.TrySetResult();
            try { await Release.Task.WaitAsync(cooperativeCancellation ? token : CancellationToken.None); }
            catch (OperationCanceledException) { CancellationObserved.TrySetResult(); throw; }
            finally { Finished.TrySetResult(); }
        }
    }

    static async Task Finish(ILowLevelMqttClient client, MqttPublishPacket publish)
    {
        if (publish.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtLeastOnce)
            await client.SendAsync(new MqttPubAckPacket { PacketIdentifier = publish.PacketIdentifier });
        else
        {
            await client.SendAsync(new MqttPubRecPacket { PacketIdentifier = publish.PacketIdentifier });
            await Receive<MqttPubRelPacket>(client);
            await client.SendAsync(new MqttPubCompPacket { PacketIdentifier = publish.PacketIdentifier });
        }
    }

    static MqttApplicationMessage Message(MqttQualityOfServiceLevel qos) =>
        new MqttApplicationMessageBuilder().WithTopic("outgoing/one").WithPayload("one-receipt").WithCorrelationData(new byte[] { 7 })
            .WithUserProperty("receipt", new ReadOnlyMemory<byte>(System.Text.Encoding.UTF8.GetBytes("original"))).WithQualityOfServiceLevel(qos).Build();

    static async Task<ILowLevelMqttClient> Connect(TestEnvironment environment, MqttProtocolVersion protocol, bool sessionPresent = false, ushort receiveMaximum = 1)
    {
        var client = environment.CreateLowLevelClient();
        await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", environment.ServerPort).WithProtocolVersion(protocol).Build());
        await client.SendAsync(new MqttConnectPacket { ClientId = "resume", CleanSession = false, SessionExpiryInterval = 3600, ReceiveMaximum = receiveMaximum, KeepAlivePeriod = 30 });
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

    static async Task Close(MqttServer server, ILowLevelMqttClient client)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Handler(ClientDisconnectedEventArgs args) { closed.TrySetResult(); return Task.CompletedTask; }
        server.ClientDisconnectedAsync += Handler;
        try { await client.DisconnectAsync(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { server.ClientDisconnectedAsync -= Handler; }
    }
}
