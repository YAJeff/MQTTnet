// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using MQTTnet.Formatter;
using MQTTnet.LowLevelClient;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using MQTTnet.Server;
using MQTTnet.Tests.Mockups;

namespace MQTTnet.Tests.Server;

[TestClass]
public sealed class IncomingQos2Persistence_Tests
{
    [TestMethod]
    [DataRow(MqttProtocolVersion.V311)]
    [DataRow(MqttProtocolVersion.V500)]
    public async Task Memory_Reconnect_Deduplicates_Lost_PubRec_And_Allows_Reuse(MqttProtocolVersion protocol)
    {
        using var context = await Context.Start();
        var dropped = Signal();
        context.Server.InterceptingOutboundPacketAsync += e => { if (e.Packet is MqttPubRecPacket && !dropped.Task.IsCompleted) { e.ProcessPacket = false; dropped.TrySetResult(); } return Task.CompletedTask; };
        using var first = await context.Connect(false, protocol);
        await first.SendAsync(Packet());
        await dropped.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await context.Close(first);
        using var second = await context.Connect(true, protocol);
        await second.SendAsync(Packet(dup: true));
        await Receive<MqttPubRecPacket>(second);
        Assert.AreEqual(1, context.NativeDispatches);
        await Complete(second);
        await second.SendAsync(Packet());
        await Receive<MqttPubRecPacket>(second);
        Assert.AreEqual(2, context.NativeDispatches);
        await Complete(second);
        await second.SendAsync(new MqttPubRelPacket { PacketIdentifier = 99 });
        Assert.AreEqual(protocol == MqttProtocolVersion.V500 ? MqttPubCompReasonCode.PacketIdentifierNotFound : MqttPubCompReasonCode.Success,
            (await Receive<MqttPubCompPacket>(second)).ReasonCode);
    }

    [TestMethod]
    [DataRow("lost-pubrec")]
    [DataRow("after-pubrec")]
    [DataRow("lost-pubcomp")]
    [DataRow("journal-before-mark")]
    [DataRow("lost-resolve-reply")]
    public async Task Cross_Server_Resume_Binds_One_Journal_And_Retires_Before_Reuse(string cut)
    {
        var store = new Store();
        using var firstContext = await Context.Start(store, ignoreErrors: cut is "journal-before-mark" or "lost-resolve-reply");
        var dropped = Signal();
        firstContext.Server.InterceptingOutboundPacketAsync += e =>
        {
            if ((cut == "lost-pubrec" && e.Packet is MqttPubRecPacket) || (cut == "lost-pubcomp" && e.Packet is MqttPubCompPacket))
            { e.ProcessPacket = false; dropped.TrySetResult(); }
            return Task.CompletedTask;
        };
        if (cut == "journal-before-mark") store.BeforeCommit = _ => throw new InvalidOperationException("synthetic before mark");
        if (cut == "lost-resolve-reply") store.AfterResolve = _ => throw new InvalidOperationException("synthetic lost resolve reply");
        using var first = await firstContext.Connect(false);
        await first.SendAsync(Packet());
        if (cut is "journal-before-mark" or "lost-resolve-reply") await firstContext.Validation.TransportClosed.WaitAsync(TimeSpan.FromSeconds(3));
        else if (cut == "lost-pubrec") await dropped.Task.WaitAsync(TimeSpan.FromSeconds(3));
        else
        {
            await Receive<MqttPubRecPacket>(first);
            if (cut == "lost-pubcomp") { await first.SendAsync(new MqttPubRelPacket { PacketIdentifier = 17 }); await dropped.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        }
        var original = store.LastResolved.Identity;
        if (cut is not ("journal-before-mark" or "lost-resolve-reply")) await firstContext.Close(first);
        await firstContext.Server.StopAsync();
        store.BeforeCommit = null;
        store.AfterResolve = null;
        using var secondContext = await Context.Start(store);
        using var second = await secondContext.Connect(true);
        if (cut is "lost-pubrec" or "journal-before-mark" or "lost-resolve-reply")
        {
            await second.SendAsync(Packet(dup: true));
            await Receive<MqttPubRecPacket>(second);
            Assert.AreEqual(original, store.LastResolved.Identity);
        }
        await Complete(second);
        Assert.HasCount(1, store.Journals);
        Assert.AreEqual(cut == "journal-before-mark" ? 2 : 1, store.AcceptanceCalls);
        Assert.AreEqual(0, firstContext.NativeDispatches + secondContext.NativeDispatches);
        await second.SendAsync(Packet());
        await Receive<MqttPubRecPacket>(second);
        Assert.AreNotEqual(original, store.LastResolved.Identity);
        Assert.HasCount(2, store.Journals);
        await Complete(second);
        Assert.IsEmpty(store.Live);
    }

    [TestMethod]
    public async Task Acceptance_And_Terminal_Commit_Are_Actual_Wire_Barriers()
    {
        var store = new Store();
        var accepting = Signal(); var acceptRelease = Signal();
        store.Accept = async _ => { accepting.TrySetResult(); await acceptRelease.Task; };
        using var context = await Context.Start(store);
        var pubRecCount = 0; var pubCompCount = 0;
        context.Server.InterceptingOutboundPacketAsync += e => { if (e.Packet is MqttPubRecPacket) Interlocked.Increment(ref pubRecCount); if (e.Packet is MqttPubCompPacket) Interlocked.Increment(ref pubCompCount); return Task.CompletedTask; };
        using var client = await context.Connect(false);
        await client.SendAsync(Packet());
        await accepting.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(0, pubRecCount);
        Assert.AreEqual(MqttIncomingQos2Phase.AwaitAcceptance, store.Live[17].Phase);
        acceptRelease.TrySetResult();
        await Receive<MqttPubRecPacket>(client);
        var retiring = Signal(); var retireRelease = Signal();
        store.BeforeCommit = async t => { if (t.NextPhase == MqttIncomingQos2Phase.Completed) { retiring.TrySetResult(); await retireRelease.Task; } };
        await client.SendAsync(new MqttPubRelPacket { PacketIdentifier = 17 });
        await retiring.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(0, pubCompCount);
        Assert.HasCount(1, store.Live);
        retireRelease.TrySetResult();
        await Receive<MqttPubCompPacket>(client);
        Assert.IsEmpty(store.Live);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Sequential_Ids_Do_Not_Consume_Lifetime_Live_Capacity(bool durable)
    {
        var store = durable ? new Store() : null;
        using var context = await Context.Start(store, liveLimit: 1);
        using var client = await context.Connect(false);
        for (ushort id = 1; id <= 65; id++)
        {
            await client.SendAsync(Packet(id));
            await Receive<MqttPubRecPacket>(client);
            await Complete(client, id);
        }
        await client.SendAsync(Packet(1));
        await Receive<MqttPubRecPacket>(client);
        await Complete(client, 1);
        Assert.AreEqual(66, durable ? store.Journals.Count : context.NativeDispatches);
        if (durable)
        {
            Assert.IsEmpty(store.Live);
            Assert.AreEqual(66L, store.LastRequest.ResolveRequestSequence);
            var stale = await store.ResolvePublishAsync(store.FirstRequest, CancellationToken.None);
            Assert.AreEqual(MqttIncomingQos2PersistenceStatus.Superseded, stale.Status);
            Assert.IsEmpty(store.Live);
        }
    }

    [TestMethod]
    [DataRow(false, "payload")]
    [DataRow(true, "payload")]
    [DataRow(true, "topic")]
    [DataRow(true, "retain")]
    [DataRow(true, "correlation")]
    [DataRow(true, "property")]
    [DataRow(true, "expiry")]
    public async Task Conflicting_Active_Identifier_Cannot_Dispatch_Again(bool durable, string field)
    {
        var store = durable ? new Store() : null;
        using var context = await Context.Start(store, ignoreErrors: true);
        using var first = await context.Connect(false);
        await first.SendAsync(Packet());
        await Receive<MqttPubRecPacket>(first);
        await context.Close(first);
        using var second = await context.Connect(true);
        var conflict = Packet(dup: true);
        switch (field)
        {
            case "payload": conflict.Payload = new ReadOnlySequence<byte>(new byte[] { 99 }); break;
            case "topic": conflict.Topic = "other"; break;
            case "retain": conflict.Retain = true; break;
            case "correlation": conflict.CorrelationData = Array.Empty<byte>(); break;
            case "property": conflict.UserProperties = new List<MqttUserProperty> { new("key", new byte[] { 1 }.AsMemory()) }; break;
            case "expiry": conflict.MessageExpiryInterval = 1; break;
        }
        await second.SendAsync(conflict);
        await context.Validation.TransportClosed.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(1, durable ? store.AcceptanceCalls : context.NativeDispatches);
    }

    [TestMethod]
    [DataRow(false, 0U, false)]
    [DataRow(true, 0U, true)]
    [DataRow(false, 7U, true)]
    public async Task Original_Expiry_Presence_And_Deadline_Reach_Journal(bool presence, uint interval, bool expected)
    {
        var store = new Store();
        using var context = await Context.Start(store);
        using var client = await context.Connect(false);
        var packet = Packet(); packet.HasMessageExpiryInterval = presence; packet.MessageExpiryInterval = interval;
        await client.SendAsync(packet);
        await Receive<MqttPubRecPacket>(client);
        var original = store.LastResolved;
        Assert.AreEqual(expected, original.PublishPacket.HasMessageExpiryInterval);
        Assert.AreEqual(expected ? original.ReceivedAtUtc.AddSeconds(interval) : (DateTime?)null, original.ExpiresAtUtc);
        await context.Close(client);
        using var resumed = await context.Connect(true);
        packet.Dup = true;
        if (expected) { packet.MessageExpiryInterval = 0; packet.HasMessageExpiryInterval = true; }
        await resumed.SendAsync(packet);
        await Receive<MqttPubRecPacket>(resumed);
        Assert.AreEqual(original.ExpiresAtUtc, store.LastResolved.ExpiresAtUtc);
        Assert.AreEqual(1, store.AcceptanceCalls);
        await Complete(resumed);
    }

    [TestMethod]
    public async Task Clean_Start_Discards_Memory_Receive_State()
    {
        using var context = await Context.Start();
        using var first = await context.Connect(false);
        await first.SendAsync(Packet()); await Receive<MqttPubRecPacket>(first); await context.Close(first);
        using var second = await context.Connect(false, clean: true);
        await second.SendAsync(Packet(dup: true)); await Receive<MqttPubRecPacket>(second);
        Assert.AreEqual(2, context.NativeDispatches);
    }

    [TestMethod]
    [DataRow("owner")]
    [DataRow("operation")]
    [DataRow("sequence")]
    [DataRow("identity")]
    [DataRow("phase")]
    [DataRow("expiry")]
    [DataRow("uncertain")]
    public async Task Unconfirmed_Resolve_Cannot_Invoke_Acceptance_Or_Send_PubRec(string corruption)
    {
        var store = new Store();
        store.OverrideResolve = result => corruption switch
        {
            "owner" => result with { Owner = result.Owner with { OwnerFence = result.Owner.OwnerFence + 1 } },
            "operation" => result with { OperationId = Guid.NewGuid() },
            "sequence" => result with { ResolveRequestSequence = result.ResolveRequestSequence + 1 },
            "identity" => result with { Transaction = new MqttIncomingQos2Transaction(result.Transaction.Identity with { PacketIdentifier = 18 }, 1, MqttIncomingQos2Phase.AwaitAcceptance, result.Transaction.PublishPacket, result.Transaction.ReceivedAtUtc, result.Transaction.ExpiresAtUtc) },
            "phase" => result with { Transaction = new MqttIncomingQos2Transaction(result.Transaction.Identity, 1, MqttIncomingQos2Phase.Completed, null, result.Transaction.ReceivedAtUtc, null) },
            "expiry" => result with { Transaction = new MqttIncomingQos2Transaction(result.Transaction.Identity, 1, MqttIncomingQos2Phase.AwaitAcceptance, result.Transaction.PublishPacket, result.Transaction.ReceivedAtUtc, DateTime.UtcNow.AddDays(1)) },
            _ => result with { Status = MqttIncomingQos2PersistenceStatus.Uncertain }
        };
        using var context = await Context.Start(store, ignoreErrors: true);
        var pubRecCount = 0;
        context.Server.InterceptingOutboundPacketAsync += e => { if (e.Packet is MqttPubRecPacket) Interlocked.Increment(ref pubRecCount); return Task.CompletedTask; };
        using var client = await context.Connect(false);
        await client.SendAsync(Packet());
        await context.Validation.TransportClosed.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(0, store.AcceptanceCalls);
        Assert.AreEqual(0, pubRecCount);
    }

    [TestMethod]
    public async Task Stale_Acceptance_Cannot_Commit_Or_Acknowledge_After_Other_Server_Claims_Owner()
    {
        var store = new Store(); var entered = Signal(); var release = Signal();
        var firstCall = 0;
        store.Accept = async _ => { if (Interlocked.Increment(ref firstCall) == 1) { entered.TrySetResult(); await release.Task; } };
        using var a = await Context.Start(store, ignoreErrors: true);
        var acknowledgements = 0;
        a.Server.InterceptingOutboundPacketAsync += e => { if (e.Packet is MqttPubRecPacket) Interlocked.Increment(ref acknowledgements); return Task.CompletedTask; };
        using var first = await a.Connect(false);
        await first.SendAsync(Packet()); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var original = store.LastResolved.Identity;
        using var b = await Context.Start(store);
        using var second = await b.Connect(true);
        await second.SendAsync(Packet(dup: true)); await Receive<MqttPubRecPacket>(second);
        Assert.AreEqual(original, store.LastResolved.Identity);
        release.TrySetResult();
        await a.Validation.TransportClosed.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(0, acknowledgements);
        Assert.HasCount(1, store.Journals);
        await Complete(second);
    }

    [TestMethod]
    public async Task Expired_Memory_Session_Does_Not_Deduplicate_A_New_Session()
    {
        using var context = await Context.Start();
        using var first = await context.Connect(false, expiry: 1);
        await first.SendAsync(Packet()); await Receive<MqttPubRecPacket>(first); await context.Close(first);
        await Task.Delay(1200);
        using var second = await context.Connect(false);
        await second.SendAsync(Packet(dup: true)); await Receive<MqttPubRecPacket>(second);
        Assert.AreEqual(2, context.NativeDispatches);
    }

    [TestMethod]
    [DataRow(false, MqttDisconnectReasonCode.NormalDisconnection, MqttWillDisposition.Suppress)]
    [DataRow(false, MqttDisconnectReasonCode.DisconnectWithWillMessage, MqttWillDisposition.Schedule)]
    [DataRow(false, MqttDisconnectReasonCode.UnspecifiedError, MqttWillDisposition.Schedule)]
    [DataRow(true, MqttDisconnectReasonCode.NormalDisconnection, MqttWillDisposition.Suppress)]
    [DataRow(true, MqttDisconnectReasonCode.DisconnectWithWillMessage, MqttWillDisposition.Schedule)]
    [DataRow(true, MqttDisconnectReasonCode.UnspecifiedError, MqttWillDisposition.Schedule)]
    public async Task External_Will_Disposition_Commits_Before_Expiry_Zero_Retirement(bool volatileSession, MqttDisconnectReasonCode reason, MqttWillDisposition disposition)
    {
        var store = new Store { UsePersistence = !volatileSession };
        using var context = await Context.Start(store, externalWill: true);
        var lifecycleObservedDisposition = false;
        context.Server.ClientDisconnectedAsync += e => { Assert.IsTrue(e.DurableDisconnectionConfirmed.GetValueOrDefault()); lifecycleObservedDisposition = store.LastDisconnect?.WillDisposition == disposition; return Task.CompletedTask; };
        using var client = await context.Connect(false, expiry: volatileSession ? 0U : 30U, will: true);
        var attempt = context.Validation.ConnectionAttemptId;
        await client.SendAsync(new MqttDisconnectPacket { ReasonCode = reason, HasSessionExpiryInterval = true, SessionExpiryInterval = 0 });
        await context.Validation.TransportClosed.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsNotNull(store.LastDisconnect);
        Assert.AreEqual(attempt, store.LastDisconnect.ConnectionAttemptId);
        Assert.IsTrue(store.LastDisconnect.HasWill);
        Assert.IsTrue(store.LastDisconnect.IsWillExternallyOwned);
        Assert.AreSame(context.Validation.WillMessage, store.LastDisconnect.WillMessage);
        Assert.AreEqual(disposition, store.LastDisconnect.WillDisposition);
        Assert.AreEqual(reason, store.LastDisconnect.DisconnectReasonCode);
        Assert.AreEqual(0U, store.LastDisconnect.SessionExpiryInterval);
        Assert.AreEqual(5U, store.LastDisconnect.WillMessage.DelayInterval);
        Assert.AreEqual(volatileSession ? 0 : 1, store.DeleteCalls);
        Assert.IsTrue(lifecycleObservedDisposition);
        if (!volatileSession) Assert.IsTrue(store.DeleteObservedDisconnected);
        Assert.AreEqual(0, context.NativeDispatches);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Unconfirmed_Will_Disposition_Prevents_Retirement(bool volatileSession)
    {
        var store = new Store { UsePersistence = !volatileSession, BeforeDisconnect = _ => throw new InvalidOperationException("synthetic uncertainty") };
        using var context = await Context.Start(store, ignoreErrors: true, externalWill: true);
        var unconfirmed = false;
        context.Server.ClientDisconnectedAsync += e => { unconfirmed = e.DurableDisconnectionConfirmed == false; return Task.CompletedTask; };
        using var client = await context.Connect(false, expiry: volatileSession ? 0U : 30U, will: true);
        await client.SendAsync(new MqttDisconnectPacket { ReasonCode = MqttDisconnectReasonCode.NormalDisconnection, HasSessionExpiryInterval = true, SessionExpiryInterval = 0 });
        await context.Validation.TransportClosed.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(0, store.DeleteCalls);
        Assert.IsTrue(unconfirmed);
        Assert.AreEqual(0, context.NativeDispatches);
    }

    [TestMethod]
    [DataRow(false, 1)]
    [DataRow(true, 0)]
    public async Task Memory_Explicit_Zero_Expiry_Does_Not_Dispatch(bool present, int expected)
    {
        using var context = await Context.Start();
        using var client = await context.Connect(false);
        var packet = Packet(); packet.HasMessageExpiryInterval = present;
        await client.SendAsync(packet); await Receive<MqttPubRecPacket>(client); await Complete(client);
        Assert.AreEqual(expected, context.NativeDispatches);
    }

    [TestMethod]
    [DataRow(MqttIncomingQos2Phase.AwaitPubRel)]
    [DataRow(MqttIncomingQos2Phase.Completed)]
    public async Task Unconfirmed_Phase_Commit_Does_Not_Advance_Wire(MqttIncomingQos2Phase phase)
    {
        var store = new Store();
        store.OverrideCommit = result => result.Transaction.Phase == phase ? result with { OperationId = Guid.NewGuid() } : result;
        using var context = await Context.Start(store, ignoreErrors: true);
        var pubRec = 0; var pubComp = 0;
        context.Server.InterceptingOutboundPacketAsync += e => { if (e.Packet is MqttPubRecPacket) Interlocked.Increment(ref pubRec); if (e.Packet is MqttPubCompPacket) Interlocked.Increment(ref pubComp); return Task.CompletedTask; };
        using var client = await context.Connect(false);
        await client.SendAsync(Packet());
        if (phase == MqttIncomingQos2Phase.Completed) { await Receive<MqttPubRecPacket>(client); await client.SendAsync(new MqttPubRelPacket { PacketIdentifier = 17 }); }
        await context.Validation.TransportClosed.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(phase == MqttIncomingQos2Phase.Completed ? 1 : 0, pubRec);
        Assert.AreEqual(0, pubComp);
        Assert.HasCount(1, store.Journals);
    }

    [TestMethod]
    public async Task Queued_Predecessor_PubRec_Is_Not_Sent_On_Replacement_Connection()
    {
        using var context = await Context.Start();
        var held = Signal(); var release = Signal(); var publicationProcessed = Signal();
        context.Server.InterceptingOutboundPacketAsync += async e => { if (e.Packet is MqttPingRespPacket && !held.Task.IsCompleted) { held.TrySetResult(); await release.Task; } };
        context.Server.InterceptingInboundPacketAsync += e => { if (e.Packet is MqttPubAckPacket) publicationProcessed.TrySetResult(); return Task.CompletedTask; };
        using var first = await context.Connect(false);
        await first.SendAsync(MqttPingReqPacket.Instance); await held.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await first.SendAsync(Packet());
        await first.SendAsync(new MqttPubAckPacket { PacketIdentifier = 99 });
        await publicationProcessed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var second = await context.Connect(true);
        release.TrySetResult();
        await second.SendAsync(MqttPingReqPacket.Instance);
        await Receive<MqttPingRespPacket>(second);
        await second.SendAsync(Packet(dup: true)); await Receive<MqttPubRecPacket>(second);
        Assert.AreEqual(1, context.NativeDispatches);
        await Complete(second);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Timed_Out_Acceptance_Observes_Late_Fault_Without_Wire_Progress(bool durable)
    {
        var entered = Signal(); var release = Signal(); var exited = Signal();
        async Task Hold()
        {
            entered.TrySetResult(); await release.Task; exited.TrySetResult();
            throw new InvalidOperationException("synthetic late owner fault");
        }
        var store = durable ? new Store { Accept = _ => Hold() } : null;
        using var context = await Context.Start(store, ignoreErrors: true, timeout: TimeSpan.FromMilliseconds(200));
        if (!durable) context.Server.InterceptingPublishAsync += _ => Hold();
        var pubRec = 0;
        context.Server.InterceptingOutboundPacketAsync += e => { if (e.Packet is MqttPubRecPacket) Interlocked.Increment(ref pubRec); return Task.CompletedTask; };
        using var client = await context.Connect(false);
        await client.SendAsync(Packet()); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await context.Validation.TransportClosed.WaitAsync(TimeSpan.FromSeconds(3));
        release.TrySetResult(); await exited.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(30);
        GC.Collect(); GC.WaitForPendingFinalizers();
        Assert.AreEqual(0, pubRec);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Intercepted_Negative_PubRec_Retires_Exact_Publication_Before_Immediate_Reuse(bool durable, bool replacePacket)
    {
        var store = durable ? new Store() : null;
        using var context = await Context.Start(store, liveLimit: 1);
        var receipts = 0;
        context.Server.InterceptingOutboundPacketAsync += e =>
        {
            if (e.Packet is MqttPubRecPacket pubRec && Interlocked.Increment(ref receipts) == 1)
            {
                if (replacePacket) e.Packet = new MqttPubRecPacket { PacketIdentifier = pubRec.PacketIdentifier, ReasonCode = MqttPubRecReasonCode.ImplementationSpecificError };
                else pubRec.ReasonCode = MqttPubRecReasonCode.ImplementationSpecificError;
            }
            return Task.CompletedTask;
        };
        using var client = await context.Connect(false);
        await client.SendAsync(Packet());
        Assert.AreEqual(MqttPubRecReasonCode.ImplementationSpecificError, (await Receive<MqttPubRecPacket>(client)).ReasonCode);
        var original = store?.LastResolved.Identity;
        var fresh = Packet(); fresh.Payload = new ReadOnlySequence<byte>(new byte[] { 43 });
        await client.SendAsync(fresh);
        Assert.AreEqual(durable ? MqttPubRecReasonCode.Success : MqttPubRecReasonCode.NoMatchingSubscribers, (await Receive<MqttPubRecPacket>(client)).ReasonCode);
        if (durable) { Assert.AreNotEqual(original, store.LastResolved.Identity); Assert.AreEqual(2, store.AcceptanceCalls); }
        else Assert.AreEqual(2, context.NativeDispatches);
        await Complete(client);
        if (durable) Assert.IsEmpty(store.Live);
    }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Suppressed_Negative_PubRec_Preserves_Acceptance_For_Reconnect(bool durable)
    {
        var store = durable ? new Store() : null;
        using var context = await Context.Start(store);
        var suppressed = Signal();
        context.Server.InterceptingOutboundPacketAsync += e =>
        {
            if (e.Packet is MqttPubRecPacket pubRec && !suppressed.Task.IsCompleted)
            { pubRec.ReasonCode = MqttPubRecReasonCode.ImplementationSpecificError; e.ProcessPacket = false; suppressed.TrySetResult(); }
            return Task.CompletedTask;
        };
        using var first = await context.Connect(false);
        await first.SendAsync(Packet());
        await suppressed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var original = store?.LastResolved.Identity;
        await context.Close(first);
        using var second = await context.Connect(true);
        await second.SendAsync(Packet(dup: true));
        Assert.AreEqual(durable ? MqttPubRecReasonCode.Success : MqttPubRecReasonCode.NoMatchingSubscribers, (await Receive<MqttPubRecPacket>(second)).ReasonCode);
        if (durable) { Assert.AreEqual(original, store.LastResolved.Identity); Assert.AreEqual(1, store.AcceptanceCalls); }
        else Assert.AreEqual(1, context.NativeDispatches);
        await Complete(second);
    }

    [TestMethod]
    public async Task Negative_PubRec_Is_Frozen_And_Waits_For_Durable_Retirement()
    {
        var store = new Store(); var retiring = Signal(); var release = Signal();
        store.BeforeCommit = async t => { if (t.NextPhase == MqttIncomingQos2Phase.Completed) { retiring.TrySetResult(); await release.Task; } };
        using var context = await Context.Start(store);
        MqttPubRecPacket escaped = null;
        context.Server.InterceptingOutboundPacketAsync += e =>
        {
            if (e.Packet is MqttPubRecPacket pubRec)
            { escaped = pubRec; pubRec.ReasonCode = MqttPubRecReasonCode.ImplementationSpecificError; pubRec.ReasonString = "fixed"; }
            return Task.CompletedTask;
        };
        using var client = await context.Connect(false);
        await client.SendAsync(Packet());
        var receive = Receive<MqttPubRecPacket>(client);
        try
        {
            await retiring.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.IsFalse(receive.IsCompleted);
            escaped.ReasonCode = MqttPubRecReasonCode.Success; escaped.PacketIdentifier = 999; escaped.ReasonString = "late";
        }
        finally { release.TrySetResult(); }
        var pubRec = await receive;
        Assert.AreEqual((ushort)17, pubRec.PacketIdentifier);
        Assert.AreEqual(MqttPubRecReasonCode.ImplementationSpecificError, pubRec.ReasonCode);
        Assert.AreEqual("fixed", pubRec.ReasonString);
        Assert.IsEmpty(store.Live);
    }

    [TestMethod]
    public async Task Unconfirmed_Negative_PubRec_Retirement_Closes_Without_Acknowledgement()
    {
        var store = new Store { OverrideCommit = r => r.Transaction.Phase == MqttIncomingQos2Phase.Completed ? r with { Status = MqttIncomingQos2PersistenceStatus.Uncertain } : r };
        using var context = await Context.Start(store, ignoreErrors: true);
        context.Server.InterceptingOutboundPacketAsync += e => { if (e.Packet is MqttPubRecPacket p) p.ReasonCode = MqttPubRecReasonCode.ImplementationSpecificError; return Task.CompletedTask; };
        using var client = await context.Connect(false);
        await client.SendAsync(Packet());
        await context.Validation.TransportClosed.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsExactlyAsync<MQTTnet.Exceptions.MqttCommunicationException>(() => client.ReceiveAsync(CancellationToken.None));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Delayed_Negative_PubRec_Cannot_Retire_Reused_Identifier(bool durable)
    {
        var store = durable ? new Store() : null;
        using var context = await Context.Start(store);
        var held = Signal(); var release = Signal(); var freshAccepted = Signal(); var receipts = 0;
        if (durable) store.Accept = _ => { if (store.AcceptanceCalls == 2) freshAccepted.TrySetResult(); return Task.CompletedTask; };
        else context.Server.InterceptingPublishAsync += _ => { if (context.NativeDispatches == 2) freshAccepted.TrySetResult(); return Task.CompletedTask; };
        context.Server.InterceptingOutboundPacketAsync += async e =>
        {
            if (e.Packet is MqttPubRecPacket p && Interlocked.Increment(ref receipts) == 1)
            { p.ReasonCode = MqttPubRecReasonCode.ImplementationSpecificError; held.TrySetResult(); await release.Task; }
        };
        using var client = await context.Connect(false);
        await client.SendAsync(Packet());
        try
        {
            await held.Task.WaitAsync(TimeSpan.FromSeconds(3));
            // Deliberately advance the receive side while the old ACK callback is held.
            await client.SendAsync(new MqttPubRelPacket { PacketIdentifier = 17 });
            var fresh = Packet(); fresh.Payload = new ReadOnlySequence<byte>(new byte[] { 43 });
            await client.SendAsync(fresh);
            await freshAccepted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { release.TrySetResult(); }
        await Receive<MqttPubCompPacket>(client);
        Assert.AreEqual(durable ? MqttPubRecReasonCode.Success : MqttPubRecReasonCode.NoMatchingSubscribers, (await Receive<MqttPubRecPacket>(client)).ReasonCode);
        await Complete(client);
        if (durable) { Assert.IsEmpty(store.Live); Assert.AreEqual(2, store.AcceptanceCalls); }
        else Assert.AreEqual(2, context.NativeDispatches);
    }

    [TestMethod]
    public async Task Mqtt311_PubRec_Without_A_Wire_Reason_Does_Not_Retire_Early()
    {
        using var context = await Context.Start();
        context.Server.InterceptingOutboundPacketAsync += e => { if (e.Packet is MqttPubRecPacket p) p.ReasonCode = MqttPubRecReasonCode.ImplementationSpecificError; return Task.CompletedTask; };
        using var client = await context.Connect(false, MqttProtocolVersion.V311);
        await client.SendAsync(Packet()); await Receive<MqttPubRecPacket>(client);
        await client.SendAsync(Packet(dup: true)); await Receive<MqttPubRecPacket>(client);
        Assert.AreEqual(1, context.NativeDispatches);
        await Complete(client);
    }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Negative_PubRec_With_Changed_Identifier_Cannot_Retire_Publication(bool durable)
    {
        var store = durable ? new Store() : null;
        using var context = await Context.Start(store, ignoreErrors: true);
        context.Server.InterceptingOutboundPacketAsync += e =>
        {
            if (e.Packet is MqttPubRecPacket p) { p.PacketIdentifier = 99; p.ReasonCode = MqttPubRecReasonCode.ImplementationSpecificError; }
            return Task.CompletedTask;
        };
        using var client = await context.Connect(false);
        await client.SendAsync(Packet());
        await context.Validation.TransportClosed.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsExactlyAsync<MQTTnet.Exceptions.MqttCommunicationException>(() => client.ReceiveAsync(CancellationToken.None));
        if (durable) Assert.HasCount(1, store.Live);
    }
    static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    static MqttPublishPacket Packet(ushort id = 17, bool dup = false) => new() { PacketIdentifier = id, Topic = "incoming/test", QualityOfServiceLevel = MqttQualityOfServiceLevel.ExactlyOnce, Payload = new ReadOnlySequence<byte>(new byte[] { 42 }), Dup = dup };
    static async Task<T> Receive<T>(ILowLevelMqttClient client) where T : MqttPacket
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var result = await client.ReceiveAsync(timeout.Token);
        Assert.IsInstanceOfType<T>(result);
        return (T)result;
    }
    static async Task Complete(ILowLevelMqttClient client, ushort id = 17)
    {
        await client.SendAsync(new MqttPubRelPacket { PacketIdentifier = id });
        Assert.AreEqual(id, (await Receive<MqttPubCompPacket>(client)).PacketIdentifier);
    }

    sealed class Context : IDisposable
    {
        public TestEnvironment Environment { get; } = new();
        public MqttServer Server;
        public ValidatingConnectionEventArgs Validation;
        public int NativeDispatches;
        public static async Task<Context> Start(Store store = null, bool ignoreErrors = false, int liveLimit = ushort.MaxValue, bool externalWill = false, TimeSpan? timeout = null)
        {
            var context = new Context();
            context.Environment.IgnoreServerLogErrors = ignoreErrors;
            var options = new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(0).WithPersistentSessions().Build();
            options.MaxIncomingQos2Transactions = liveLimit;
            if (timeout.HasValue) options.DefaultCommunicationTimeout = timeout.Value;
            options.SessionPersistence = store; options.IncomingQos2Persistence = store;
            context.Server = context.Environment.CreateServer(options);
            context.Server.ValidatingConnectionAsync += e => { context.Validation = e; return Task.CompletedTask; };
            context.Server.InterceptingPublishAsync += _ => { Interlocked.Increment(ref context.NativeDispatches); return Task.CompletedTask; };
            if (store != null)
            {
                context.Server.PreparingSessionRecoveryAsync += e => { if (externalWill) e.TakeWillOwnership(); e.Recovery.Commit(); return Task.CompletedTask; };
                context.Server.AcceptingIncomingQos2MessageAsync += async e =>
                {
                    Assert.AreEqual(context.Validation.ConnectionAttemptId, e.Owner.ConnectionAttemptId);
                    Assert.AreSame(context.Validation.SessionItems, e.ConnectionAttemptItems);
                    Interlocked.Increment(ref store.AcceptanceCalls);
                    if (store.Accept != null) await store.Accept(e);
                    lock (store.Journals) store.Journals.Add(e.Transaction.Identity);
                };
            }
            await context.Server.StartAsync(); context.Environment.ServerPort = options.DefaultEndpointOptions.Port;
            return context;
        }
        public async Task<ILowLevelMqttClient> Connect(bool present, MqttProtocolVersion protocol = MqttProtocolVersion.V500, bool clean = false, uint expiry = uint.MaxValue, bool will = false)
        {
            var client = new MqttClientFactory().CreateLowLevelMqttClient();
            await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", Environment.ServerPort).WithProtocolVersion(protocol).Build());
            await client.SendAsync(new MqttConnectPacket { ClientId = "publisher", CleanSession = clean, SessionExpiryInterval = expiry,
                WillFlag = will, WillTopic = will ? "will/test" : null, WillMessage = will ? new byte[] { 42 } : null, WillDelayInterval = will ? 5U : 0U });
            Assert.AreEqual(present, (await Receive<MqttConnAckPacket>(client)).IsSessionPresent);
            return client;
        }
        public async Task Close(ILowLevelMqttClient client) { var closed = Validation.TransportClosed; await client.DisconnectAsync(); await closed.WaitAsync(TimeSpan.FromSeconds(3)); }
        public void Dispose() => Environment.Dispose();
    }

    sealed class Store : IMqttServerSessionPersistence, IMqttServerIncomingQos2Persistence
    {
        readonly object gate = new();
        readonly Guid generation = Guid.NewGuid();
        long fence;
        Guid attempt;
        long sequence;
        MqttIncomingQos2PersistenceResult lastResult;
        public readonly Dictionary<ushort, MqttIncomingQos2Transaction> Live = new();
        public readonly HashSet<MqttIncomingQos2Identity> Journals = new();
        public MqttIncomingQos2ResolveRequest FirstRequest, LastRequest;
        public MqttIncomingQos2Transaction LastResolved;
        public int AcceptanceCalls;
        public Func<AcceptingIncomingQos2MessageEventArgs, Task> Accept;
        public Func<MqttIncomingQos2Transition, Task> BeforeCommit;
        public Action<MqttIncomingQos2PersistenceResult> AfterResolve;
        public Func<MqttIncomingQos2PersistenceResult, MqttIncomingQos2PersistenceResult> OverrideResolve;
        public Func<MqttIncomingQos2PersistenceResult, MqttIncomingQos2PersistenceResult> OverrideCommit;
        public bool UsePersistence = true;
        public Func<MqttSessionDisconnectedTransition, Task> BeforeDisconnect;
        public MqttSessionDisconnectedTransition LastDisconnect;
        public int DeleteCalls;
        public bool DeleteObservedDisconnected;
        public Task<MqttPersistedSessionSnapshot> LoadAsync(MqttSessionPersistenceRequest request, CancellationToken token)
        {
            lock (gate)
            {
                attempt = request.ConnectionAttemptId; sequence = 0; lastResult = null; fence++;
                return Task.FromResult(new MqttPersistedSessionSnapshot(1, generation, fence, 0, UsePersistence && fence > 1, Array.Empty<MqttPersistedOutgoingTransaction>(), Array.Empty<MqttPersistedSubscription>(), usePersistence: UsePersistence));
            }
        }
        bool IsOwner(MqttIncomingQos2Owner owner) => owner.SessionGeneration == generation && owner.OwnerFence == fence && owner.ConnectionAttemptId == attempt;
        public Task<MqttIncomingQos2PersistenceResult> ResolvePublishAsync(MqttIncomingQos2ResolveRequest request, CancellationToken token)
        {
            MqttIncomingQos2PersistenceResult result;
            lock (gate)
            {
                if (!IsOwner(request.Owner)) return Task.FromResult(Result(request.Owner, request.OperationId, request.ResolveRequestSequence, MqttIncomingQos2PersistenceStatus.Fenced));
                if (request.ResolveRequestSequence < sequence) return Task.FromResult(Result(request.Owner, request.OperationId, request.ResolveRequestSequence, MqttIncomingQos2PersistenceStatus.Superseded));
                if (request.ResolveRequestSequence == sequence)
                {
                    if (LastRequest.OperationId != request.OperationId || LastRequest.ProposedIncarnation != request.ProposedIncarnation) throw new InvalidOperationException("Conflicting resolve retry");
                    return Task.FromResult(lastResult);
                }
                if (request.ResolveRequestSequence != sequence + 1) throw new InvalidOperationException("Resolve sequence gap");
                var packet = request.PublishPacket;
                if (!Live.TryGetValue(packet.PacketIdentifier, out var transaction))
                {
                    if (Live.Count >= request.MaxTransactions) throw new InvalidOperationException("Live capacity");
                    transaction = new MqttIncomingQos2Transaction(new MqttIncomingQos2Identity(generation, packet.PacketIdentifier, Guid.NewGuid()), 1,
                        MqttIncomingQos2Phase.AwaitAcceptance, packet, request.ReceivedAtUtc, request.ExpiresAtUtc);
                    Live.Add(packet.PacketIdentifier, transaction);
                }
                sequence = request.ResolveRequestSequence; LastRequest = request; FirstRequest ??= request; LastResolved = transaction;
                result = lastResult = Result(request.Owner, request.OperationId, sequence, MqttIncomingQos2PersistenceStatus.Confirmed, transaction);
            }
            AfterResolve?.Invoke(result);
            return Task.FromResult(OverrideResolve?.Invoke(result) ?? result);
        }
        public Task<MqttIncomingQos2PersistenceResult> ReadAsync(MqttIncomingQos2ReadRequest request, CancellationToken token)
        {
            lock (gate)
            {
                if (!IsOwner(request.Owner)) return Task.FromResult(Result(request.Owner, request.OperationId, 0, MqttIncomingQos2PersistenceStatus.Fenced));
                return Task.FromResult(Live.TryGetValue(request.PacketIdentifier, out var transaction)
                    ? Result(request.Owner, request.OperationId, 0, MqttIncomingQos2PersistenceStatus.Confirmed, transaction)
                    : Result(request.Owner, request.OperationId, 0, MqttIncomingQos2PersistenceStatus.NotFound));
            }
        }
        public async Task<MqttIncomingQos2PersistenceResult> CommitAsync(MqttIncomingQos2Transition transition, CancellationToken token)
        {
            if (BeforeCommit != null) await BeforeCommit(transition);
            lock (gate)
            {
                if (!IsOwner(transition.Owner)) return Result(transition.Owner, transition.OperationId, 0, MqttIncomingQos2PersistenceStatus.Fenced);
                if (!Live.TryGetValue(transition.Identity.PacketIdentifier, out var previous) || previous.Identity != transition.Identity || previous.Revision != transition.ExpectedRevision || previous.Phase != transition.PreviousPhase)
                    return Result(transition.Owner, transition.OperationId, 0, MqttIncomingQos2PersistenceStatus.Superseded);
                var updated = new MqttIncomingQos2Transaction(previous.Identity, previous.Revision + 1, transition.NextPhase,
                    transition.NextPhase == MqttIncomingQos2Phase.Completed ? null : previous.PublishPacket, previous.ReceivedAtUtc, previous.ExpiresAtUtc);
                if (updated.Phase == MqttIncomingQos2Phase.Completed) Live.Remove(previous.Identity.PacketIdentifier);
                else Live[previous.Identity.PacketIdentifier] = updated;
                var result = Result(transition.Owner, transition.OperationId, 0, MqttIncomingQos2PersistenceStatus.Confirmed, updated);
                return OverrideCommit?.Invoke(result) ?? result;
            }
        }
        static MqttIncomingQos2PersistenceResult Result(MqttIncomingQos2Owner owner, Guid operation, long sequence, MqttIncomingQos2PersistenceStatus status, MqttIncomingQos2Transaction transaction = null) => new(owner, operation, sequence, status, transaction);
        public Task<MqttPersistenceCommitResult> CommitTransitionAsync(MqttOutgoingTransactionTransition transition, CancellationToken token) => throw new NotSupportedException();
        public Task<MqttPersistenceCommitResult> CommitSubscriptionsAsync(MqttSubscriptionTransition transition, CancellationToken token) => throw new NotSupportedException();
        public async Task<MqttPersistenceCommitResult> CommitDisconnectedAsync(MqttSessionDisconnectedTransition transition, CancellationToken token)
        {
            LastDisconnect = transition;
            if (BeforeDisconnect != null) await BeforeDisconnect(transition);
            return new MqttPersistenceCommitResult(transition.TransitionId, transition.SessionGeneration, transition.OwnerFence, transition.ExpectedRevision + 1, MqttPersistenceCommitStatus.Applied, null);
        }
        public Task<MqttPersistenceCommitResult> DeleteSessionAsync(MqttSessionPersistenceDeletion deletion, CancellationToken token)
        {
            DeleteCalls++; DeleteObservedDisconnected = LastDisconnect != null;
            return Task.FromResult(new MqttPersistenceCommitResult(deletion.TransitionId, deletion.SessionGeneration, deletion.OwnerFence, 0, MqttPersistenceCommitStatus.Applied, null));
        }
    }
}
