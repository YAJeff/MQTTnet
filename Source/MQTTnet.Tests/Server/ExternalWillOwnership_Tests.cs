// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.InteropServices;
using System.Collections;
using MQTTnet.Adapter;
using MQTTnet.Diagnostics.Logger;
using MQTTnet.Formatter;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using MQTTnet.Server;
using MQTTnet.Tests.Mockups;

namespace MQTTnet.Tests.Server;

[TestClass]
public sealed class ExternalWillOwnership_Tests
{
    [TestMethod]
    [DataRow(false, 0U, false)]
    [DataRow(true, 0U, true)]
    [DataRow(false, 10U, true)]
    public void Will_Expiry_Presence_Survives_Wire_And_Snapshot(bool explicitPresence, uint interval, bool expectedPresence)
    {
        var packet = MqttPacketSerializationHelper.EncodeAndDecodePacket(new MqttConnectPacket
        {
            ClientId = "expiry", WillFlag = true, WillTopic = "will/test", WillMessage = new byte[] { 1 },
            WillMessageExpiryInterval = interval, HasWillMessageExpiryInterval = explicitPresence
        }, MqttProtocolVersion.V500);
        Assert.AreEqual(expectedPresence, packet.HasWillMessageExpiryInterval);
        using var adapter = new MqttChannelAdapter(new MemoryMqttChannel(new MemoryStream()), new MqttPacketFormatterAdapter(MqttProtocolVersion.V500, new MqttBufferWriter(4096, 65535)), MqttNetNullLogger.Instance);
        var snapshot = new ValidatingConnectionEventArgs(packet, adapter, new Hashtable(), CancellationToken.None).WillMessage;
        Assert.AreEqual(expectedPresence, snapshot.HasMessageExpiryInterval);
        Assert.AreEqual(interval, snapshot.MessageExpiryInterval);
    }

    [TestMethod]
    public void Snapshot_Owns_All_Will_Fields_And_Buffers()
    {
        var payload = new byte[] { 1, 2 };
        var correlation = new byte[] { 3, 4 };
        var property = new byte[] { 5, 6 };
        var packet = new MqttConnectPacket { WillFlag = true, WillTopic = "will/topic", WillMessage = payload,
            WillQoS = MqttQualityOfServiceLevel.ExactlyOnce, WillRetain = true, WillDelayInterval = 7,
            WillMessageExpiryInterval = 8, WillPayloadFormatIndicator = MqttPayloadFormatIndicator.CharacterData,
            WillContentType = "text/plain", WillResponseTopic = "response", WillCorrelationData = correlation,
            WillUserProperties = new List<MqttUserProperty> { new("key", property.AsMemory()), new("key", new byte[] { 9 }.AsMemory()) } };
        using var adapter = new MqttChannelAdapter(new MemoryMqttChannel(new MemoryStream()), new MqttPacketFormatterAdapter(MqttProtocolVersion.V500, new MqttBufferWriter(4096, 65535)), MqttNetNullLogger.Instance);
        var snapshot = new ValidatingConnectionEventArgs(packet, adapter, new Hashtable(), CancellationToken.None).WillMessage;
        payload[0] = correlation[0] = property[0] = 99;
        packet.WillTopic = "changed";
        packet.WillUserProperties.Clear();
        snapshot.Payload[0] = 99;
        snapshot.CorrelationData[0] = 99;
        Assert.IsTrue(MemoryMarshal.TryGetArray(snapshot.UserProperties[0].ValueBuffer, out var mutable));
        mutable.Array[mutable.Offset] = 99;
        Assert.AreEqual("will/topic", snapshot.Topic);
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, snapshot.Payload);
        CollectionAssert.AreEqual(new byte[] { 3, 4 }, snapshot.CorrelationData);
        CollectionAssert.AreEqual(new byte[] { 5, 6 }, snapshot.UserProperties[0].ValueBuffer.ToArray());
        Assert.HasCount(2, snapshot.UserProperties);
        Assert.AreEqual("key", snapshot.UserProperties[1].Name);
        Assert.AreEqual(MqttQualityOfServiceLevel.ExactlyOnce, snapshot.QualityOfServiceLevel);
        Assert.IsTrue(snapshot.Retain);
        Assert.AreEqual(7U, snapshot.DelayInterval);
        Assert.AreEqual(8U, snapshot.MessageExpiryInterval);
        Assert.AreEqual(MqttPayloadFormatIndicator.CharacterData, snapshot.PayloadFormatIndicator);
        Assert.AreEqual("text/plain", snapshot.ContentType);
        Assert.AreEqual("response", snapshot.ResponseTopic);
    }

    [TestMethod]
    [DataRow(false, MqttClientDisconnectOptionsReason.NormalDisconnection, 0)]
    [DataRow(false, MqttClientDisconnectOptionsReason.DisconnectWithWillMessage, 1)]
    [DataRow(false, MqttClientDisconnectOptionsReason.UnspecifiedError, 1)]
    [DataRow(true, MqttClientDisconnectOptionsReason.NormalDisconnection, 0)]
    [DataRow(true, MqttClientDisconnectOptionsReason.DisconnectWithWillMessage, 0)]
    [DataRow(true, MqttClientDisconnectOptionsReason.UnspecifiedError, 0)]
    public async Task Exact_Attempt_Ownership_Controls_Native_Dispatch(bool external, MqttClientDisconnectOptionsReason reason, int expected)
    {
        using var environment = new TestEnvironment();
        var server = Create(environment);
        ValidatingConnectionEventArgs validation = null;
        PreparingSessionRecoveryEventArgs preparation = null;
        var disconnected = Signal();
        var count = 0;
        server.ValidatingConnectionAsync += e => { validation = e; Assert.IsTrue(e.HasWill); Assert.AreEqual("will/test", e.WillMessage.Topic); return Task.CompletedTask; };
        server.PreparingSessionRecoveryAsync += e => { preparation = e; Assert.AreEqual(validation.ConnectionAttemptId, e.ConnectionAttemptId); Assert.AreSame(validation.WillMessage, e.WillMessage); if (external) e.TakeWillOwnership(); e.Recovery.Commit(); return Task.CompletedTask; };
        server.InterceptingPublishAsync += _ => { Interlocked.Increment(ref count); return Task.CompletedTask; };
        server.ClientDisconnectedAsync += e => { Assert.AreEqual(validation.ConnectionAttemptId, e.ConnectionAttemptId); disconnected.TrySetResult(); return Task.CompletedTask; };
        await server.StartAsync();
        var client = await environment.ConnectClient(Options());
        Assert.AreEqual(external, preparation.Connection.IsWillExternallyOwned);
        Assert.ThrowsExactly<InvalidOperationException>(() => preparation.TakeWillOwnership());
        await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().WithReason(reason).Build());
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        // Final disposal flushes eligible Wills, including deferred ones.
        await server.StopAsync();
        server.Dispose();
        Assert.AreEqual(expected, count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Shutdown_Does_Not_Publish_Externally_Owned_Will(bool external)
    {
        using var environment = new TestEnvironment();
        var server = Create(environment);
        var count = 0;
        server.PreparingSessionRecoveryAsync += e => { if (external) e.TakeWillOwnership(); e.Recovery.Commit(); return Task.CompletedTask; };
        server.InterceptingPublishAsync += _ => { Interlocked.Increment(ref count); return Task.CompletedTask; };
        await server.StartAsync();
        await environment.ConnectClient(Options().WithWillDelayInterval(uint.MaxValue));
        await server.StopAsync();
        server.Dispose();
        Assert.AreEqual(external ? 0 : 1, count);
    }

    [TestMethod]
    public async Task No_Will_Is_Explicit_And_Cannot_Be_Claimed()
    {
        using var environment = new TestEnvironment();
        var server = Create(environment);
        server.ValidatingConnectionAsync += e => { Assert.IsFalse(e.HasWill); Assert.IsNull(e.WillMessage); return Task.CompletedTask; };
        server.PreparingSessionRecoveryAsync += e => { Assert.IsFalse(e.HasWill); Assert.IsNull(e.WillMessage); Assert.ThrowsExactly<InvalidOperationException>(() => e.TakeWillOwnership()); e.Recovery.Commit(); return Task.CompletedTask; };
        await server.StartAsync();
        await environment.ConnectClient(new MqttClientOptionsBuilder().WithClientId("no-will").WithProtocolVersion(MqttProtocolVersion.V500));
    }

    [TestMethod]
    public async Task Host_Preparation_Is_Before_ConnAck_And_Ownership_Activation()
    {
        using var environment = new TestEnvironment();
        var server = Create(environment);
        var entered = Signal();
        var release = Signal();
        PreparingSessionRecoveryEventArgs preparation = null;
        var connAckCount = 0;
        server.InterceptingOutboundPacketAsync += e => { if (e.Packet is MqttConnAckPacket) Interlocked.Increment(ref connAckCount); return Task.CompletedTask; };
        server.PreparingSessionRecoveryAsync += async e => { preparation = e; entered.TrySetResult(); await release.Task; e.TakeWillOwnership(); e.Recovery.Commit(); };
        await server.StartAsync();
        var connect = environment.ConnectClient(Options());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(0, connAckCount);
        Assert.IsFalse(preparation.Connection.IsWillExternallyOwned);
        release.TrySetResult();
        await connect;
        Assert.AreEqual(1, connAckCount);
        Assert.IsTrue(preparation.Connection.IsWillExternallyOwned);
    }

    [TestMethod]
    [DataRow("throw")]
    [DataRow("fault")]
    [DataRow("uncommitted")]
    public async Task Failed_Preparation_Does_Not_Accept_Connection_Or_Arm_New_Will(string failure)
    {
        using var environment = new TestEnvironment { IgnoreServerLogErrors = true, IgnoreClientLogErrors = true };
        var server = Create(environment);
        var count = 0;
        var connAckCount = 0;
        PreparingSessionRecoveryEventArgs preparation = null;
        server.InterceptingPublishAsync += _ => { Interlocked.Increment(ref count); return Task.CompletedTask; };
        server.InterceptingOutboundPacketAsync += e => { if (e.Packet is MqttConnAckPacket) Interlocked.Increment(ref connAckCount); return Task.CompletedTask; };
        server.PreparingSessionRecoveryAsync += e => { preparation = e; e.TakeWillOwnership(); if (failure == "throw") throw new InvalidOperationException("synthetic failure"); if (failure == "fault") return Task.FromException(new InvalidOperationException("synthetic failure")); return Task.CompletedTask; };
        await server.StartAsync();
        try { await environment.ConnectClient(Options()); Assert.Fail("Unexpected successful connection"); }
        catch (MQTTnet.Exceptions.MqttCommunicationException) { }
        await server.StopAsync();
        server.Dispose();
        Assert.AreEqual(0, connAckCount);
        Assert.AreEqual(0, count);
        Assert.IsFalse(preparation.Connection.IsWillExternallyOwned);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Ownership_Is_Per_Attempt_And_Does_Not_Suppress_Successor(bool firstExternal)
    {
        using var environment = new TestEnvironment();
        var server = Create(environment);
        var count = 0;
        var attempts = new List<PreparingSessionRecoveryEventArgs>();
        server.PreparingSessionRecoveryAsync += e => { attempts.Add(e); if (attempts.Count == 1 ? firstExternal : !firstExternal) e.TakeWillOwnership(); e.Recovery.Commit(); return Task.CompletedTask; };
        server.InterceptingPublishAsync += _ => { Interlocked.Increment(ref count); return Task.CompletedTask; };
        await server.StartAsync();
        await environment.ConnectClient(Options().WithCleanSession(false));
        await environment.ConnectClient(Options().WithCleanSession(false));
        Assert.AreNotEqual(attempts[0].ConnectionAttemptId, attempts[1].ConnectionAttemptId);
        Assert.AreEqual(firstExternal, attempts[0].Connection.IsWillExternallyOwned);
        Assert.AreEqual(!firstExternal, attempts[1].Connection.IsWillExternallyOwned);
        await server.StopAsync();
        server.Dispose();
        Assert.AreEqual(1, count);
    }

    [TestMethod]
    public async Task Timed_Out_Owner_Cannot_Claim_Will_Later()
    {
        using var environment = new TestEnvironment { IgnoreServerLogErrors = true, IgnoreClientLogErrors = true };
        var server = Create(environment, TimeSpan.FromMilliseconds(200));
        var entered = Signal();
        var release = Signal();
        var exited = Signal();
        PreparingSessionRecoveryEventArgs preparation = null;
        Exception lateFailure = null;
        var count = 0;
        var connAckCount = 0;
        server.InterceptingPublishAsync += _ => { Interlocked.Increment(ref count); return Task.CompletedTask; };
        server.InterceptingOutboundPacketAsync += e => { if (e.Packet is MqttConnAckPacket) Interlocked.Increment(ref connAckCount); return Task.CompletedTask; };
        server.PreparingSessionRecoveryAsync += async e =>
        {
            preparation = e;
            entered.TrySetResult();
            await release.Task;
            try { e.TakeWillOwnership(); } catch (Exception exception) { lateFailure = exception; }
            exited.TrySetResult();
        };
        await server.StartAsync();
        var connect = environment.ConnectClient(Options());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try { await connect; Assert.Fail("Timed-out preparation accepted"); }
        catch (MQTTnet.Exceptions.MqttCommunicationException) { }
        release.TrySetResult();
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsInstanceOfType<OperationCanceledException>(lateFailure);
        Assert.IsFalse(preparation.Connection.IsWillExternallyOwned);
        await server.StopAsync();
        server.Dispose();
        Assert.AreEqual(0, connAckCount);
        Assert.AreEqual(0, count);
    }

    static MqttServer Create(TestEnvironment environment, TimeSpan? timeout = null)
    {
        var options = new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(0).WithPersistentSessions().Build();
        if (timeout.HasValue) options.DefaultCommunicationTimeout = timeout.Value;
        var server = environment.CreateServer(options);
        server.StartedAsync += _ => { environment.ServerPort = options.DefaultEndpointOptions.Port; return Task.CompletedTask; };
        return server;
    }
    static MqttClientOptionsBuilder Options() => new MqttClientOptionsBuilder().WithClientId("will-owner")
        .WithProtocolVersion(MqttProtocolVersion.V500).WithSessionExpiryInterval(30).WithWillTopic("will/test").WithWillPayload("value");
    static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
