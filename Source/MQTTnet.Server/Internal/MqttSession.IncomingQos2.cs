// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using System.Runtime.CompilerServices;
using MQTTnet.Internal;
using MQTTnet.Packets;
using MQTTnet.Protocol;

namespace MQTTnet.Server.Internal;

public sealed partial class MqttSession
{
    readonly AsyncLock _incomingQos2Gate = new();
    readonly Dictionary<ushort, IncomingMemoryTransaction> _incomingQos2 = new();
    long _incomingQos2Bytes;
    readonly ConditionalWeakTable<MqttPacket, IncomingAcknowledgementOwner> _incomingAcknowledgementOwners = new();
    bool HasDurableIncomingQos2 => HasDurablePersistence && _serverOptions.IncomingQos2Persistence != null;
    static DispatchApplicationMessageResult IncomingSuccess() => new(0, false, null, null);

    internal void EnqueueIncomingQos2Acknowledgement(MqttPacket packet, long generation, IncomingQos2PublishResult publication = null)
    {
        lock (_dataEnqueueLock)
        {
            if (!IsCurrentConnection(generation)) return;
            _incomingAcknowledgementOwners.Add(packet, new IncomingAcknowledgementOwner(generation, publication));
            EnqueueControlPacket(new MqttPacketBusItem(packet));
        }
    }
    internal bool CanSendIncomingAcknowledgement(MqttPacket packet, long generation) =>
        packet is not (MqttPubRecPacket or MqttPubCompPacket) || !_incomingAcknowledgementOwners.TryGetValue(packet, out var owner) || owner.Generation == generation;

    internal async Task<IncomingQos2PublishResult> ProcessIncomingQos2PublishAsync(MqttConnectedClient client,
        MqttPublishPacket received, Func<MqttPublishPacket, Task<DispatchApplicationMessageResult>> dispatch, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_serverOptions.DefaultCommunicationTimeout);
        var token = timeout.Token;
        using (await _incomingQos2Gate.EnterAsync(token).ConfigureAwait(false))
        {
            CheckIncomingConnection(client);
            var bytes = ValidateIncomingPacket(received);
            var packet = MqttPublishPacketSnapshot.Clone(received);
            packet.Dup = false;
            packet.TopicAlias = 0;
            packet.HasMessageExpiryInterval |= packet.MessageExpiryInterval != 0;
            try
            {
                if (HasDurableIncomingQos2)
                    return await ProcessDurableIncomingPublishAsync(client, packet, token).ConfigureAwait(false);

                IncomingMemoryTransaction entry;
                lock (_incomingQos2)
                {
                    CheckIncomingConnection(client);
                    if (_incomingQos2.TryGetValue(packet.PacketIdentifier, out entry))
                        ValidateIncomingRetransmission(entry.Packet, packet);
                    else
                    {
                        if (_incomingQos2.Count >= _serverOptions.MaxIncomingQos2Transactions || bytes > _serverOptions.MaxIncomingQos2StateBytes - _incomingQos2Bytes)
                            throw new InvalidOperationException("Incoming QoS2 state capacity exceeded.");
                        entry = new IncomingMemoryTransaction(packet, bytes);
                        _incomingQos2.Add(packet.PacketIdentifier, entry);
                        _incomingQos2Bytes += bytes;
                    }
                }
                // Keep the one dispatch Task in the session, including across takeover/timeout.
                // A successor awaits it instead of starting a second native fan-out. A fault stays
                // fail-closed until the session ends; native fan-out is not crash-atomic.
                entry.Dispatch ??= packet.HasMessageExpiryInterval && packet.MessageExpiryInterval == 0
                    ? Task.FromResult(IncomingSuccess()) : DispatchIncomingOnceAsync(dispatch, entry.Packet);
                DispatchApplicationMessageResult result;
                try { result = await entry.Dispatch.WaitAsync(token).ConfigureAwait(false); }
                catch { _ = ObserveAbandonedIncomingTask(entry.Dispatch); throw; }
                CheckIncomingConnection(client);
                return new IncomingQos2PublishResult(result, packet.PacketIdentifier, entry.Incarnation);
            }
            catch
            {
                BlockIncomingOwner(client);
                throw;
            }
        }
    }

    static async Task<DispatchApplicationMessageResult> DispatchIncomingOnceAsync(Func<MqttPublishPacket, Task<DispatchApplicationMessageResult>> dispatch, MqttPublishPacket packet)
        => await dispatch(MqttPublishPacketSnapshot.Clone(packet)).ConfigureAwait(false);

    static async Task ObserveAbandonedIncomingTask(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { /* The connection already failed; observe a later owner callback failure. */ }
    }

    async Task<IncomingQos2PublishResult> ProcessDurableIncomingPublishAsync(MqttConnectedClient client, MqttPublishPacket packet, CancellationToken token)
    {
        var owner = GetIncomingOwner(client);
        var request = client.IncomingResolveRequest;
        if (request == null)
        {
            var now = DateTime.UtcNow;
            var sequence = checked(client.IncomingResolveSequence + 1);
            request = new MqttIncomingQos2ResolveRequest(owner, Guid.NewGuid(), sequence, Guid.NewGuid(), packet, now,
                packet.HasMessageExpiryInterval ? now.AddSeconds(packet.MessageExpiryInterval) : null,
                _serverOptions.MaxIncomingQos2Transactions, _serverOptions.MaxIncomingQos2StateBytes);
            client.IncomingResolveRequest = request;
        }
        var resolved = await _serverOptions.IncomingQos2Persistence.ResolvePublishAsync(request, token).WaitAsync(token).ConfigureAwait(false);
        CheckIncomingResult(client, resolved, owner, request.OperationId, request.ResolveRequestSequence);
        var transaction = resolved.Transaction;
        ValidateIncomingTransaction(transaction, owner, packet.PacketIdentifier, false);
        ValidateIncomingRetransmission(transaction.PublishPacket, packet);
        if (transaction.ReceivedAtUtc > request.ReceivedAtUtc) throw new InvalidOperationException("Incoming acceptance moved its original receipt time forward.");
        client.IncomingResolveSequence = request.ResolveRequestSequence;
        client.IncomingResolveRequest = null;
        if (transaction.Phase == MqttIncomingQos2Phase.AwaitAcceptance)
        {
            var args = new AcceptingIncomingQos2MessageEventArgs(owner, transaction,
                new MqttClientStatus(client) { Session = new MqttSessionStatus(this) }, Items, client.ConnectionAttemptItems, token);
            var accepted = _eventContainer.IncomingQos2Handler(args) ?? throw new InvalidOperationException("Incoming acceptance returned no task.");
            try { await accepted.WaitAsync(token).ConfigureAwait(false); }
            catch { _ = ObserveAbandonedIncomingTask(accepted); throw; }
            CheckIncomingConnection(client);
            transaction = await CommitIncomingAsync(client, owner, transaction, MqttIncomingQos2Phase.AwaitPubRel, token).ConfigureAwait(false);
        }
        return new IncomingQos2PublishResult(IncomingSuccess(), packet.PacketIdentifier, Guid.Empty, transaction.Identity, transaction.Revision);
    }

    internal async Task<bool> ProcessIncomingQos2PubRelAsync(MqttConnectedClient client, ushort packetIdentifier, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_serverOptions.DefaultCommunicationTimeout);
        var token = timeout.Token;
        using (await _incomingQos2Gate.EnterAsync(token).ConfigureAwait(false))
        {
            CheckIncomingConnection(client);
            try
            {
                if (!HasDurableIncomingQos2)
                {
                    lock (_incomingQos2)
                    {
                        if (!_incomingQos2.TryGetValue(packetIdentifier, out var entry)) return false;
                        if (entry.Dispatch == null || !entry.Dispatch.IsCompletedSuccessfully || entry.Dispatch.Result.CloseConnection)
                            throw new InvalidOperationException("PUBREL preceded confirmed incoming acceptance.");
                        _incomingQos2.Remove(packetIdentifier);
                        _incomingQos2Bytes -= entry.Bytes;
                        return true;
                    }
                }
                var owner = GetIncomingOwner(client);
                var request = new MqttIncomingQos2ReadRequest(owner, Guid.NewGuid(), packetIdentifier);
                var result = await _serverOptions.IncomingQos2Persistence.ReadAsync(request, token).WaitAsync(token).ConfigureAwait(false);
                CheckIncomingResult(client, result, owner, request.OperationId, 0, true);
                if (result.Status == MqttIncomingQos2PersistenceStatus.NotFound)
                {
                    if (result.Transaction != null) throw new InvalidOperationException("NotFound returned an incoming transaction.");
                    return false;
                }
                ValidateIncomingTransaction(result.Transaction, owner, packetIdentifier, true);
                if (result.Transaction.Phase == MqttIncomingQos2Phase.Completed) return true;
                if (result.Transaction.Phase != MqttIncomingQos2Phase.AwaitPubRel)
                    throw new InvalidOperationException("PUBREL preceded durable journal acceptance.");
                await CommitIncomingAsync(client, owner, result.Transaction, MqttIncomingQos2Phase.Completed, token).ConfigureAwait(false);
                return true;
            }
            catch { BlockIncomingOwner(client); throw; }
        }
    }

    async Task<MqttIncomingQos2Transaction> CommitIncomingAsync(MqttConnectedClient client, MqttIncomingQos2Owner owner, MqttIncomingQos2Transaction transaction, MqttIncomingQos2Phase nextPhase, CancellationToken token)
    {
        CheckIncomingConnection(client);
        var transition = new MqttIncomingQos2Transition(owner, Guid.NewGuid(), transaction.Identity, transaction.Revision, transaction.Phase, nextPhase);
        var result = await _serverOptions.IncomingQos2Persistence.CommitAsync(transition, token).WaitAsync(token).ConfigureAwait(false);
        CheckIncomingResult(client, result, owner, transition.OperationId, 0);
        ValidateIncomingTransaction(result.Transaction, owner, transaction.Identity.PacketIdentifier, true);
        if (result.Transaction.Identity != transaction.Identity || result.Transaction.Revision != checked(transaction.Revision + 1) || result.Transaction.Phase != nextPhase)
            throw new InvalidOperationException("Incoming transition was not confirmed exactly.");
        if (nextPhase != MqttIncomingQos2Phase.Completed)
        {
            ValidateIncomingRetransmission(transaction.PublishPacket, result.Transaction.PublishPacket);
            if (transaction.ReceivedAtUtc != result.Transaction.ReceivedAtUtc || transaction.ExpiresAtUtc != result.Transaction.ExpiresAtUtc)
                throw new InvalidOperationException("Incoming transition changed the original expiry deadline.");
        }
        return result.Transaction;
    }

    // Bind terminal wire effects to the exact publication captured before interception.
    // A suppressed PUBREC keeps its state; a final MQTT 5 failure retires before wire
    // visibility, so the peer can immediately reuse the identifier without PUBREL.
    internal async Task<bool> PrepareIncomingQos2AcknowledgementAsync(MqttConnectedClient client, MqttPacket originalPacket,
        MqttPacket packet, CancellationToken cancellationToken)
    {
        if (packet is not MqttPubRecPacket pubRec || (int)pubRec.ReasonCode < 0x80 ||
            client.ChannelAdapter.PacketFormatterAdapter.ProtocolVersion != MQTTnet.Formatter.MqttProtocolVersion.V500 ||
            !_incomingAcknowledgementOwners.TryGetValue(originalPacket, out var acknowledgement) || acknowledgement.Publication == null)
            return true;
        var publication = acknowledgement.Publication;
        var packetIdentifier = publication.PacketIdentifier;
        if (pubRec.PacketIdentifier != packetIdentifier)
            throw new InvalidOperationException("Incoming PUBREC interception changed the packet identifier.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_serverOptions.DefaultCommunicationTimeout);
        var token = timeout.Token;
        using (await _incomingQos2Gate.EnterAsync(token).ConfigureAwait(false))
        {
            CheckIncomingConnection(client);
            try
            {
                if (publication.MemoryIncarnation != Guid.Empty)
                {
                    lock (_incomingQos2)
                    {
                        if (!_incomingQos2.TryGetValue(packetIdentifier, out var current) ||
                            current.Incarnation != publication.MemoryIncarnation) return false;
                        _incomingQos2.Remove(packetIdentifier);
                        _incomingQos2Bytes -= current.Bytes;
                    }
                    return true;
                }
                var owner = GetIncomingOwner(client);
                var request = new MqttIncomingQos2ReadRequest(owner, Guid.NewGuid(), packetIdentifier);
                var result = await _serverOptions.IncomingQos2Persistence.ReadAsync(request, token).WaitAsync(token).ConfigureAwait(false);
                CheckIncomingResult(client, result, owner, request.OperationId, 0, true);
                if (result.Status == MqttIncomingQos2PersistenceStatus.NotFound)
                {
                    if (result.Transaction != null) throw new InvalidOperationException("NotFound returned an incoming transaction.");
                    return false;
                }
                ValidateIncomingTransaction(result.Transaction, owner, packetIdentifier, true);
                if (result.Transaction.Identity != publication.DurableIdentity || result.Transaction.Phase != MqttIncomingQos2Phase.AwaitPubRel)
                    return false;
                if (result.Transaction.Revision != publication.DurableRevision)
                    throw new InvalidOperationException("Incoming PUBREC retirement revision changed.");
                await CommitIncomingAsync(client, owner, result.Transaction, MqttIncomingQos2Phase.Completed, token).ConfigureAwait(false);
                return true;
            }
            catch { BlockIncomingOwner(client); throw; }
        }
    }
    MqttIncomingQos2Owner GetIncomingOwner(MqttConnectedClient client)
    {
        lock (_unacknowledgedPublishPackets)
        {
            CheckIncomingConnection(client);
            if (_durableGeneration == Guid.Empty || _durableOwnerFence <= 0 || _durableBlocked || IsDataRecoveryPaused)
                throw new InvalidOperationException("Incoming persistence has no active durable owner.");
            return new MqttIncomingQos2Owner(Id, _durableGeneration, _durableOwnerFence, client.ConnectionAttemptId);
        }
    }

    void CheckIncomingConnection(MqttConnectedClient client)
    {
        if (!IsCurrentConnection(client.ConnectionGeneration) || client.IsTakenOver || !client.IsRunning)
            throw new InvalidOperationException("Incoming QoS2 owner was superseded.");
    }
    void BlockIncomingOwner(MqttConnectedClient client)
    {
        lock (_unacknowledgedPublishPackets)
            if (HasDurableIncomingQos2 && IsCurrentConnection(client.ConnectionGeneration)) _durableBlocked = true;
    }
    void CheckIncomingResult(MqttConnectedClient client, MqttIncomingQos2PersistenceResult result, MqttIncomingQos2Owner owner, Guid operationId, long sequence, bool allowNotFound = false)
    {
        CheckIncomingConnection(client);
        if (result == null || result.Owner != owner || result.OperationId != operationId || result.ResolveRequestSequence != sequence ||
            (result.Status != MqttIncomingQos2PersistenceStatus.Confirmed && !(allowNotFound && result.Status == MqttIncomingQos2PersistenceStatus.NotFound)))
            throw new InvalidOperationException("Incoming persistence did not confirm the exact fenced operation.");
    }

    void ValidateIncomingTransaction(MqttIncomingQos2Transaction transaction, MqttIncomingQos2Owner owner, ushort packetIdentifier, bool allowCompleted)
    {
        if (transaction?.Identity == null || transaction.Identity.SessionGeneration != owner.SessionGeneration || transaction.Identity.PacketIdentifier != packetIdentifier ||
            transaction.Identity.Incarnation == Guid.Empty || transaction.Revision <= 0 || !Enum.IsDefined(transaction.Phase) ||
            (!allowCompleted && transaction.Phase == MqttIncomingQos2Phase.Completed))
            throw new InvalidOperationException("Incoming transaction identity or phase is invalid.");
        if (transaction.Phase == MqttIncomingQos2Phase.Completed) return;
        var packet = transaction.PublishPacket;
        ValidateIncomingPacket(packet);
        if (packet.PacketIdentifier != packetIdentifier || packet.Dup || packet.TopicAlias != 0 || transaction.ReceivedAtUtc.Kind != DateTimeKind.Utc ||
            transaction.ExpiresAtUtc != (packet.HasMessageExpiryInterval || packet.MessageExpiryInterval != 0 ? transaction.ReceivedAtUtc.AddSeconds(packet.MessageExpiryInterval) : null))
            throw new InvalidOperationException("Incoming canonical envelope or expiry is invalid.");
    }

    long ValidateIncomingPacket(MqttPublishPacket packet)
    {
        if (packet == null || packet.PacketIdentifier == 0 || packet.QualityOfServiceLevel != MqttQualityOfServiceLevel.ExactlyOnce || !Enum.IsDefined(packet.PayloadFormatIndicator))
            throw new InvalidOperationException("Incoming QoS2 packet is invalid.");
        ValidateDurableTopic(packet.Topic, false);
        if (!string.IsNullOrEmpty(packet.ResponseTopic)) ValidateDurableTopic(packet.ResponseTopic, false);
        var bytes = checked(128 + packet.Payload.Length + DurableStringBytes(packet.Topic) + DurableStringBytes(packet.ContentType) + DurableStringBytes(packet.ResponseTopic) + (packet.CorrelationData?.LongLength ?? 0));
        if (packet.UserProperties != null)
            foreach (var property in packet.UserProperties)
            {
                if (property == null) throw new InvalidOperationException("Incoming user property is missing.");
                bytes = checked(bytes + 32 + DurableStringBytes(property.Name) + DurableStringBytes(StrictUtf8.GetString(property.ValueBuffer.Span)));
            }
        if (packet.SubscriptionIdentifiers?.Count > 0) throw new InvalidOperationException("Incoming client PUBLISH contains subscription identifiers.");
        if (bytes > _serverOptions.MaxIncomingQos2StateBytes) throw new InvalidOperationException("Incoming QoS2 packet exceeds its state budget.");
        return bytes;
    }

    static void ValidateIncomingRetransmission(MqttPublishPacket original, MqttPublishPacket incoming)
    {
        var originalExpiry = original.HasMessageExpiryInterval || original.MessageExpiryInterval != 0;
        var incomingExpiry = incoming.HasMessageExpiryInterval || incoming.MessageExpiryInterval != 0;
        if (original.PacketIdentifier != incoming.PacketIdentifier || original.Topic != incoming.Topic || original.QualityOfServiceLevel != incoming.QualityOfServiceLevel ||
            original.Retain != incoming.Retain || original.ContentType != incoming.ContentType || original.ResponseTopic != incoming.ResponseTopic ||
            original.PayloadFormatIndicator != incoming.PayloadFormatIndicator || !original.Payload.ToArray().AsSpan().SequenceEqual(incoming.Payload.ToArray()) ||
            (original.CorrelationData == null) != (incoming.CorrelationData == null) ||
            !(original.CorrelationData ?? Array.Empty<byte>()).AsSpan().SequenceEqual(incoming.CorrelationData ?? Array.Empty<byte>()) ||
            originalExpiry != incomingExpiry || (originalExpiry && incoming.MessageExpiryInterval > original.MessageExpiryInterval) ||
            (original.UserProperties?.Count ?? 0) != (incoming.UserProperties?.Count ?? 0))
            throw new InvalidOperationException("Active incoming packet identifier was reused with conflicting content.");
        for (var i = 0; i < (original.UserProperties?.Count ?? 0); i++)
            if (original.UserProperties[i].Name != incoming.UserProperties[i].Name || !original.UserProperties[i].ValueBuffer.Span.SequenceEqual(incoming.UserProperties[i].ValueBuffer.Span))
                throw new InvalidOperationException("Incoming retransmission user properties changed.");
    }

    sealed class IncomingMemoryTransaction
    {
        public IncomingMemoryTransaction(MqttPublishPacket packet, long bytes) { Packet = packet; Bytes = bytes; }
        public Guid Incarnation { get; } = Guid.NewGuid();
        public MqttPublishPacket Packet { get; }
        public long Bytes { get; }
        public Task<DispatchApplicationMessageResult> Dispatch { get; set; }
    }
    internal sealed record IncomingQos2PublishResult(DispatchApplicationMessageResult Dispatch, ushort PacketIdentifier, Guid MemoryIncarnation = default, MqttIncomingQos2Identity DurableIdentity = null, long DurableRevision = 0);
    sealed record IncomingAcknowledgementOwner(long Generation, IncomingQos2PublishResult Publication);
}
