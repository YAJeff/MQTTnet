// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Security.Cryptography.X509Certificates;
using MQTTnet.Adapter;
using MQTTnet.Diagnostics.Logger;
using MQTTnet.Formatter;
using MQTTnet.Packets;
using MQTTnet.Server;
using MQTTnet.Server.Internal;

namespace MQTTnet.Tests.Server;

[TestClass]
public sealed class ConnectionAttemptClosure_Tests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Failed_Load_Exposes_Exact_Confirmed_Or_Faulted_Transport_Close(bool closeFails)
    {
        var store = new FailedLoad();
        var events = new MqttServerEventContainer();
        ValidatingConnectionEventArgs validation = null;
        events.ValidatingConnectionEvent.AddHandler(e => { validation = e; return Task.CompletedTask; });
        events.ClientDisconnectedEvent.AddHandler(e => { Assert.IsNull(e.DurableDisconnectionConfirmed); return Task.CompletedTask; });
        events.AddSessionRecoveryHandler(_ => throw new InvalidOperationException("Failed Load must not invoke Preparing"));
        var options = new MqttServerOptionsBuilder().WithPersistentSessions().Build(); options.SessionPersistence = store;
        using var retained = new MqttRetainedMessagesManager(events, MqttNetNullLogger.Instance);
        using var manager = new MqttClientSessionsManager(options, retained, events, MqttNetNullLogger.Instance);
        using var adapter = new HeldAdapter { CloseFails = closeFails };
        var run = manager.HandleClientConnectionAsync(adapter, CancellationToken.None);
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(store.Request.ConnectionAttemptId, validation.ConnectionAttemptId);
        Assert.AreSame(adapter, validation.ChannelAdapter);
        Assert.IsFalse(validation.TransportClosed.IsCompleted);
        store.Release.TrySetResult();
        await adapter.CloseEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsFalse(validation.TransportClosed.IsCompleted);
        Assert.AreEqual(0, adapter.Writes);
        adapter.CloseRelease.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        if (closeFails) Assert.IsTrue(validation.TransportClosed.IsFaulted);
        else await validation.TransportClosed.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [TestMethod]
    public async Task Transport_Close_Joins_Existing_Wire_Write_And_Fences_Late_Writes()
    {
        var store = new FailedLoad();
        var events = new MqttServerEventContainer();
        ValidatingConnectionEventArgs validation = null;
        events.ValidatingConnectionEvent.AddHandler(e => { validation = e; return Task.CompletedTask; });
        events.AddSessionRecoveryHandler(_ => throw new InvalidOperationException("Failed Load must not invoke Preparing"));
        var options = new MqttServerOptionsBuilder().WithPersistentSessions().Build(); options.SessionPersistence = store;
        using var retained = new MqttRetainedMessagesManager(events, MqttNetNullLogger.Instance);
        using var manager = new MqttClientSessionsManager(options, retained, events, MqttNetNullLogger.Instance);
        using var adapter = new HeldAdapter { HoldWrites = true };
        var run = manager.HandleClientConnectionAsync(adapter, CancellationToken.None);
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var captured = manager.GetClients().Single();
        var write = captured.SendPacketAsync(MqttPingRespPacket.Instance, CancellationToken.None);
        await adapter.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        store.Release.TrySetResult();
        await adapter.CloseEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        adapter.CloseRelease.TrySetResult();
        await Task.Delay(30);
        Assert.IsFalse(validation.TransportClosed.IsCompleted);
        adapter.WriteRelease.TrySetResult();
        await write;
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        await validation.TransportClosed;
        await captured.SendPacketAsync(MqttPingRespPacket.Instance, CancellationToken.None);
        Assert.AreEqual(1, adapter.Writes);
    }

    static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    sealed class FailedLoad : IMqttServerSessionPersistence
    {
        public readonly TaskCompletionSource Entered = Signal(), Release = Signal();
        public MqttSessionPersistenceRequest Request;
        public async Task<MqttPersistedSessionSnapshot> LoadAsync(MqttSessionPersistenceRequest request, CancellationToken cancellationToken)
        { Request = request; Entered.TrySetResult(); await Release.Task; throw new InvalidOperationException("synthetic submitted Load lost its reply"); }
        public Task<MqttPersistenceCommitResult> CommitTransitionAsync(MqttOutgoingTransactionTransition transition, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MqttPersistenceCommitResult> CommitSubscriptionsAsync(MqttSubscriptionTransition transition, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MqttPersistenceCommitResult> CommitDisconnectedAsync(MqttSessionDisconnectedTransition transition, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MqttPersistenceCommitResult> DeleteSessionAsync(MqttSessionPersistenceDeletion deletion, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    sealed class HeldAdapter : IMqttChannelAdapter
    {
        public readonly TaskCompletionSource CloseEntered = Signal(), CloseRelease = Signal(), WriteEntered = Signal(), WriteRelease = Signal();
        public bool CloseFails, HoldWrites;
        public int Writes;
        public long BytesReceived => 0;
        public long BytesSent => 0;
        public X509Certificate2 ClientCertificate => null;
        public EndPoint RemoteEndPoint => null;
        public EndPoint LocalEndPoint => null;
        public bool IsSecureConnection => false;
        public MqttPacketFormatterAdapter PacketFormatterAdapter { get; } = new(MqttProtocolVersion.V500, new MqttBufferWriter(4096, 65535));
        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task DisconnectAsync(CancellationToken cancellationToken) { CloseEntered.TrySetResult(); await CloseRelease.Task; if (CloseFails) throw new InvalidOperationException("synthetic unconfirmed close"); }
        public Task<MqttPacket> ReceivePacketAsync(CancellationToken cancellationToken) => Task.FromResult<MqttPacket>(new MqttConnectPacket { ClientId = "failed-load", CleanSession = false, SessionExpiryInterval = uint.MaxValue });
        public void ResetStatistics() { }
        public async Task SendPacketAsync(MqttPacket packet, CancellationToken cancellationToken) { Interlocked.Increment(ref Writes); WriteEntered.TrySetResult(); if (HoldWrites) await WriteRelease.Task; }
        public void Dispose() { CloseRelease.TrySetResult(); WriteRelease.TrySetResult(); }
    }
}
