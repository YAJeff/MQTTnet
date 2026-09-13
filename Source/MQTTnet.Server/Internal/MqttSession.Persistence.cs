// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using MQTTnet.Internal;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using System.Runtime.CompilerServices;
using System.Text;

namespace MQTTnet.Server.Internal;

public sealed partial class MqttSession
{
    static readonly UTF8Encoding StrictUtf8 = new(false, true);
    readonly AsyncLock _persistenceGate = new();
    readonly Dictionary<MqttPublishPacket, DurableTransaction> _durableTransactions = new();
    readonly Dictionary<MqttPublishPacket, string> _durableAdmissionHandles = new();
    readonly Dictionary<string, MqttPublishPacket> _durableHandlePackets = new(StringComparer.Ordinal);
    readonly ConditionalWeakTable<MqttPublishPacket, DurableCompletion> _durableCompletions = new();
    Guid _durableGeneration;
    long _durableOwnerFence;
    bool _durableBlocked;
    bool _durableOptOut;
    Guid? _durableDeletionId;
    MqttSessionDisconnectedTransition _durableDisconnection;
    volatile bool _durableDisconnectionUnconfirmed;

    internal bool HasDurablePersistence => _serverOptions.SessionPersistence != null && !_durableOptOut;

    internal async Task<bool> RestoreDurableStateAsync(MqttSessionPersistenceRequest request, long generation, CancellationToken cancellationToken)
    {
        List<(object State, string Handle)> capturedContexts;
        lock (_unacknowledgedPublishPackets)
        {
            request = request with { NativeSessionItems = Items, PendingDeliveryHandles = _durableAdmissionHandles.Values.Distinct(StringComparer.Ordinal).ToList().AsReadOnly() };
            capturedContexts = _durableAdmissionHandles.Select(pair => (GetEnqueueState(pair.Key), pair.Value)).ToList();
        }
        foreach (var (state, handle) in capturedContexts)
            if (state is not IMqttDurableDeliveryContext context || context.DeliveryHandle != handle)
                throw new InvalidOperationException("The preserved native context does not identify its durable transaction.");
        var snapshot = await _serverOptions.SessionPersistence.LoadAsync(request, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot == null || snapshot.SchemaVersion != 1 || snapshot.SessionGeneration == Guid.Empty || snapshot.OwnerFence <= 0 || snapshot.Revision < 0 ||
            snapshot.OutgoingTransactions.Count > ushort.MaxValue || (request.CleanStart && snapshot.SessionPresent))
            throw new InvalidOperationException("The durable session snapshot is invalid.");
        var identifiers = new HashSet<ushort>();
        var handles = new HashSet<string>(StringComparer.Ordinal);
        var sequences = new HashSet<long>();
        var imported = new List<(MqttPersistedOutgoingTransaction Record, MqttPublishPacket Packet)>();
        long snapshotBytes = 0;
        foreach (var record in snapshot.OutgoingTransactions)
        {
            if (record == null) throw new InvalidOperationException("A durable transaction is missing.");
            if (record.EnqueueState is not IMqttDurableDeliveryContext restoredContext || restoredContext.DeliveryHandle != record.DeliveryHandle)
                throw new InvalidOperationException("The imported delivery context does not identify its durable transaction.");
            var packet = record.PublishPacket;
            ValidateDurableTopic(packet.Topic, false);
            if (!string.IsNullOrEmpty(packet.ResponseTopic)) ValidateDurableTopic(packet.ResponseTopic, false);
            snapshotBytes = checked(snapshotBytes + packet.Payload.Length + DurableStringBytes(packet.Topic) + DurableStringBytes(record.DeliveryHandle) +
                DurableStringBytes(packet.ContentType) + DurableStringBytes(packet.ResponseTopic) + (packet.CorrelationData?.LongLength ?? 0));
            if (packet.UserProperties != null)
                foreach (var property in packet.UserProperties)
                {
                    if (property == null) throw new InvalidOperationException("A durable user property is missing.");
                    snapshotBytes = checked(snapshotBytes + DurableStringBytes(property.Name) + DurableStringBytes(StrictUtf8.GetString(property.ValueBuffer.Span)));
                }
            if (packet.SubscriptionIdentifiers != null && packet.SubscriptionIdentifiers.Any(identifier => identifier == 0 || identifier > 268435455))
                throw new InvalidOperationException("A durable subscription identifier is invalid.");
            if (snapshotBytes > _serverOptions.MaxDurableSessionSnapshotBytes) throw new InvalidOperationException("The durable snapshot exceeds its byte limit.");
            if (string.IsNullOrEmpty(record.DeliveryHandle) || record.Revision <= 0 || record.LastTransitionId == Guid.Empty || record.SendSequence <= 0 ||
                !handles.Add(record.DeliveryHandle) || !sequences.Add(record.SendSequence) || packet.PacketIdentifier == 0 || !identifiers.Add(packet.PacketIdentifier) ||
                string.IsNullOrEmpty(packet.Topic) || packet.TopicAlias != 0 ||
                (record.Phase == MqttOutgoingTransactionPhase.AwaitPubAck ? packet.QualityOfServiceLevel != MqttQualityOfServiceLevel.AtLeastOnce :
                    !Enum.IsDefined(record.Phase) || packet.QualityOfServiceLevel != MqttQualityOfServiceLevel.ExactlyOnce))
                throw new InvalidOperationException("A durable outgoing transaction is corrupt or conflicts with another record.");
            imported.Add((record, packet));
        }
        if (!snapshot.SessionPresent && (imported.Count != 0 || snapshot.Subscriptions.Count != 0)) throw new InvalidOperationException("A new durable session contains old state.");
        if (!snapshot.UsePersistence && (snapshot.SessionPresent || imported.Count != 0 || snapshot.Subscriptions.Count != 0 ||
            (request.ProtocolVersion == MQTTnet.Formatter.MqttProtocolVersion.V500 ? request.SessionExpiryInterval != 0 : !request.CleanStart)))
            throw new InvalidOperationException("Persistence opt-out is only valid for a confirmed new empty volatile session.");
        if (snapshot.Subscriptions.Count > ushort.MaxValue) throw new InvalidOperationException("The subscription snapshot exceeds its bound.");
        var topics = new HashSet<string>(StringComparer.Ordinal);
        var requested = request.PendingDeliveryHandles.ToHashSet(StringComparer.Ordinal);
        var retired = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handle in snapshot.RetiredDeliveryHandles)
            if (string.IsNullOrEmpty(handle) || !requested.Contains(handle) || handles.Contains(handle) || !retired.Add(handle))
                throw new InvalidOperationException("The durable retirement resolution is inconsistent.");
        foreach (var subscription in snapshot.Subscriptions)
        {
            if (subscription == null || string.IsNullOrEmpty(subscription.Topic) || !topics.Add(subscription.Topic) ||
                !Enum.IsDefined(subscription.QualityOfServiceLevel) || !Enum.IsDefined(subscription.RetainHandling) || subscription.SubscriptionIdentifier > 268435455)
                throw new InvalidOperationException("A durable subscription is invalid.");
            ValidateDurableTopic(subscription.Topic, true);
            snapshotBytes = checked(snapshotBytes + DurableStringBytes(subscription.Topic));
            if (snapshotBytes > _serverOptions.MaxDurableSessionSnapshotBytes) throw new InvalidOperationException("The durable snapshot exceeds its byte limit.");
        }

        List<MqttSessionApplicationMessage> invalidated;
        bool generationChanged;
        lock (_dataEnqueueLock)
        lock (_unacknowledgedPublishPackets)
        {
            if (!IsCurrentConnection(generation) || !IsDataRecoveryPaused) throw new InvalidOperationException("Durable restore lost its quiesced connection generation.");
            if (snapshot.SessionGeneration == _durableGeneration && snapshot.OwnerFence <= _durableOwnerFence)
                throw new InvalidOperationException("Durable restore must acquire a newer owner fence.");
            var existing = _durableTransactions.ToDictionary(pair => pair.Value.Handle, pair => pair.Key, StringComparer.Ordinal);
            foreach (var (record, _) in imported)
                if (existing.TryGetValue(record.DeliveryHandle, out var existingPacket) &&
                    (!_durableAdmissionHandles.TryGetValue(existingPacket, out var originalHandle) || originalHandle != record.DeliveryHandle))
                    throw new InvalidOperationException("The preserved native context does not identify its durable transaction.");
            generationChanged = _durableGeneration != Guid.Empty && snapshot.SessionGeneration != _durableGeneration;
            if (generationChanged) lock (_incomingQos2) { _incomingQos2.Clear(); _incomingQos2Bytes = 0; }
            if (snapshot.SessionGeneration == _durableGeneration && _durableTransactions.Any(pair => pair.Value.Revision > 0 && !handles.Contains(pair.Value.Handle) && !retired.Contains(pair.Value.Handle)))
                throw new InvalidOperationException("The durable snapshot omitted a locally confirmed transaction.");
            invalidated = _outgoingPublishStates.Keys.Where(packet => generationChanged ||
                (_durableAdmissionHandles.TryGetValue(packet, out var handle) && retired.Contains(handle)))
                .Select(packet => new MqttSessionApplicationMessage(packet, GetEnqueueState(packet))).ToList();
            var queued = _outgoingPublishStates.Where(pair => !generationChanged && (pair.Value == OutgoingPublishState.Queued ||
                (_durableTransactions.TryGetValue(pair.Key, out var durable) && durable.Revision == 0 && !handles.Contains(durable.Handle)))
                && (!_durableAdmissionHandles.TryGetValue(pair.Key, out var candidate) || !retired.Contains(candidate)))
                .Select(pair => pair.Key).Where(packet => !_durableAdmissionHandles.TryGetValue(packet, out var handle) || !handles.Contains(handle)).ToArray();
            var queuedHandles = queued.Where(_durableAdmissionHandles.ContainsKey).ToDictionary(packet => packet, packet => _durableAdmissionHandles[packet]);
            _unacknowledgedPublishPackets.Clear();
            _outgoingPublishStates.Clear();
            _reservedPacketIdentifiers.Clear();
            _sendSequences.Clear();
            _durableTransactions.Clear();
            _durableAdmissionHandles.Clear();
            _durableHandlePackets.Clear();
            foreach (var (record, restoredPacket) in imported)
            {
                var packet = (generationChanged ? null : existing.GetValueOrDefault(record.DeliveryHandle)) ?? restoredPacket;
                if (!ReferenceEquals(packet, restoredPacket)) MqttPublishPacketSnapshot.CopyFields(restoredPacket, packet);
                else _admissionContexts.GetValue(packet, _ => new AdmissionContext(record.EnqueueState, ++_nextAdmissionSequence));
                _unacknowledgedPublishPackets.Add(packet);
                _reservedPacketIdentifiers.Add(packet.PacketIdentifier);
                _outgoingPublishStates.Add(packet, record.Phase == MqttOutgoingTransactionPhase.AwaitPubComp ? OutgoingPublishState.PubRelSent : OutgoingPublishState.PublishSent);
                _sendSequences.Add(packet, record.SendSequence);
                _nextSendSequence = Math.Max(_nextSendSequence, record.SendSequence);
                _durableTransactions.Add(packet, new DurableTransaction(record.DeliveryHandle, record.Revision, record.Phase, record.SendSequence));
                _durableAdmissionHandles.Add(packet, record.DeliveryHandle);
                _durableHandlePackets.Add(record.DeliveryHandle, packet);
            }
            foreach (var packet in queued)
            {
                // These are never-attempted admissions; the exclusive owner will reclaim/restage them before activation.
                if (packet.QualityOfServiceLevel > MqttQualityOfServiceLevel.AtMostOnce)
                {
                    if (_reservedPacketIdentifiers.Contains(packet.PacketIdentifier)) packet.PacketIdentifier = 0;
                    else _reservedPacketIdentifiers.Add(packet.PacketIdentifier);
                    _unacknowledgedPublishPackets.Add(packet);
                }
                _outgoingPublishStates.Add(packet, OutgoingPublishState.Queued);
                if (queuedHandles.TryGetValue(packet, out var handle))
                {
                    _durableAdmissionHandles.Add(packet, handle);
                    _durableHandlePackets.Add(handle, packet);
                }
            }
            _durableGeneration = snapshot.SessionGeneration;
            _durableOwnerFence = snapshot.OwnerFence;
            _durableDisconnection = null;
            _durableDisconnectionUnconfirmed = false;
            _durableDeletionId = null;
            _durableBlocked = false;
            _durableOptOut = !snapshot.UsePersistence;
            _subscriptionsManager.RestoreSubscriptions(snapshot.Subscriptions);
            _durableSubscriptions = snapshot.Subscriptions.ToDictionary(subscription => subscription.Topic, StringComparer.Ordinal);
            _durableSubscriptionRevision = snapshot.Revision;
            _subscribedTopics = topics;
        }
        _clientSessionsManager.RefreshRestoredSubscriptions(this, generation);
        foreach (var message in invalidated)
            if (_admissionBusItems.TryGetValue(message.PublishPacket, out var item)) item.Cancel();
        if (invalidated.Count > 0)
            await _clientSessionsManager.NotifyApplicationMessagesInvalidatedAsync(new SessionApplicationMessagesInvalidatedEventArgs(Id, Items, invalidated.AsReadOnly(),
                generationChanged ? MqttSessionApplicationMessagesInvalidationReason.DurableGenerationReplaced : MqttSessionApplicationMessagesInvalidationReason.DurableTransactionRetired)).ConfigureAwait(false);
        return snapshot.SessionPresent;
    }

    internal async Task PrepareDurablePublishAsync(MqttPublishPacket packet, MqttPublishPacket wirePacket, long generation, CancellationToken cancellationToken)
    {
        if (!HasDurablePersistence || packet.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtMostOnce) return;
        ValidateDurableTopic(wirePacket.Topic, false);
        using (await _persistenceGate.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            DurableTransaction transaction;
            lock (_unacknowledgedPublishPackets)
            {
                ValidateDurableOwner(generation);
                if (!_outgoingPublishStates.ContainsKey(packet)) throw new InvalidOperationException("The durable publication is no longer tracked.");
                if (wirePacket.PacketIdentifier != packet.PacketIdentifier || wirePacket.QualityOfServiceLevel != packet.QualityOfServiceLevel)
                    throw new InvalidOperationException("A durable publication cannot change its identifier or QoS.");
                var bytes = DurablePacketBytes(wirePacket) + _durableSubscriptions.Values.Sum(subscription => (long)DurableStringBytes(subscription.Topic));
                foreach (var existing in _durableTransactions)
                    if (!ReferenceEquals(existing.Key, packet)) bytes = checked(bytes + DurablePacketBytes(existing.Key) + DurableStringBytes(existing.Value.Handle));
                if (_durableAdmissionHandles.TryGetValue(packet, out var admittedHandle)) bytes = checked(bytes + DurableStringBytes(admittedHandle));
                if (bytes > _serverOptions.MaxDurableSessionSnapshotBytes) throw new InvalidOperationException("The publication would exceed the durable snapshot byte limit.");
                if (_durableTransactions.TryGetValue(packet, out transaction))
                {
                    if (transaction.Pending != null) throw new InvalidOperationException("An unresolved durable transition requires restoration.");
                    if (transaction.Phase == MqttOutgoingTransactionPhase.AwaitPubComp) return;
                    transaction.Pending = CreateTransition(transaction, wirePacket, transaction.Phase, null);
                }
                else
                {
                if (!_durableAdmissionHandles.TryGetValue(packet, out var deliveryHandle))
                    throw new InvalidOperationException("Durable publication requires a stable delivery context.");
                if (wirePacket.PacketIdentifier != packet.PacketIdentifier || wirePacket.QualityOfServiceLevel != packet.QualityOfServiceLevel)
                    throw new InvalidOperationException("A durable publication cannot change its identifier or QoS.");
                var phase = packet.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtLeastOnce ? MqttOutgoingTransactionPhase.AwaitPubAck : MqttOutgoingTransactionPhase.AwaitPubRec;
                transaction = new DurableTransaction(deliveryHandle, 0, null, ++_nextSendSequence);
                _durableTransactions.Add(packet, transaction);
                _sendSequences[packet] = transaction.Sequence;
                _outgoingPublishStates[packet] = OutgoingPublishState.PreparingPublish;
                transaction.Pending = CreateTransition(transaction, wirePacket, phase, null);
                }
            }
            await CommitDurableTransitionAsync(transaction, generation, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task<MqttPublishPacket> CompleteDurableAcknowledgementAsync(ushort identifier, MqttQualityOfServiceLevel qos, byte reasonCode, bool pubRec,
        long generation, CancellationToken cancellationToken)
    {
        using (await _persistenceGate.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            MqttPublishPacket packet;
            DurableTransaction transaction;
            lock (_unacknowledgedPublishPackets)
            {
                ValidateDurableOwner(generation);
                packet = _unacknowledgedPublishPackets.FirstOrDefault(item => item.PacketIdentifier == identifier && item.QualityOfServiceLevel == qos);
                if (packet == null || !_durableTransactions.TryGetValue(packet, out transaction)) return null;
                var nativeState = _outgoingPublishStates[packet];
                if (nativeState == OutgoingPublishState.Queued || nativeState == OutgoingPublishState.PreparingPublish ||
                    (!pubRec && qos == MqttQualityOfServiceLevel.ExactlyOnce && nativeState != OutgoingPublishState.PubRelSent)) return null;
                if (pubRec && transaction.Phase == MqttOutgoingTransactionPhase.AwaitPubComp) return reasonCode < 0x80 ? packet : null;
                var expected = pubRec ? MqttOutgoingTransactionPhase.AwaitPubRec : qos == MqttQualityOfServiceLevel.ExactlyOnce ? MqttOutgoingTransactionPhase.AwaitPubComp : MqttOutgoingTransactionPhase.AwaitPubAck;
                if (transaction.Phase != expected) return null;
                transaction.Pending ??= CreateTransition(transaction, packet, pubRec && reasonCode < 0x80 ? MqttOutgoingTransactionPhase.AwaitPubComp : null,
                    pubRec && reasonCode < 0x80 ? null : reasonCode);
            }
            await CommitDurableTransitionAsync(transaction, generation, cancellationToken).ConfigureAwait(false);
            lock (_unacknowledgedPublishPackets)
            {
                ValidateDurableOwner(generation);
                if (pubRec && reasonCode < 0x80) _outgoingPublishStates[packet] = OutgoingPublishState.PubRelPending;
                else
                {
                    _durableCompletions.Add(packet, new DurableCompletion(transaction.LastStatus));
                    RemoveTrackedPublish(packet);
                    _durableTransactions.Remove(packet);
                }
            }
            return packet;
        }
    }

    internal async Task PrepareDurablePubRelAsync(ushort identifier, long generation, CancellationToken cancellationToken)
    {
        if (!HasDurablePersistence) return;
        using (await _persistenceGate.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            DurableTransaction transaction;
            lock (_unacknowledgedPublishPackets)
            {
                ValidateDurableOwner(generation);
                var packet = _unacknowledgedPublishPackets.FirstOrDefault(candidate => candidate.PacketIdentifier == identifier);
                if (packet == null) return;
                if (!_durableTransactions.TryGetValue(packet, out transaction) || transaction.Phase != MqttOutgoingTransactionPhase.AwaitPubComp || transaction.Pending != null)
                    throw new InvalidOperationException("PUBREL requires a resolved durable QoS 2 transaction.");
                transaction.Pending = CreateTransition(transaction, packet, MqttOutgoingTransactionPhase.AwaitPubComp, null);
            }
            await CommitDurableTransitionAsync(transaction, generation, cancellationToken).ConfigureAwait(false);
        }
    }

    MqttOutgoingTransactionTransition CreateTransition(DurableTransaction transaction, MqttPublishPacket packet, MqttOutgoingTransactionPhase? next, byte? reason) =>
        new(Id, _durableGeneration, _durableOwnerFence, Guid.NewGuid(), transaction.Handle, transaction.Revision, transaction.Phase, next, transaction.Sequence, packet, reason);

    async Task CommitDurableTransitionAsync(DurableTransaction transaction, long generation, CancellationToken cancellationToken)
    {
        var pending = transaction.Pending;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_serverOptions.DefaultCommunicationTimeout);
        try
        {
            var result = await _serverOptions.SessionPersistence.CommitTransitionAsync(pending, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            lock (_unacknowledgedPublishPackets)
            {
                ValidateDurableOwner(generation);
                var retired = result?.Status == MqttPersistenceCommitStatus.AlreadyRetired && pending.NextPhase == null;
                if (result == null || result.SessionGeneration != pending.SessionGeneration || result.OwnerFence != pending.OwnerFence || result.DeliveryHandle != pending.DeliveryHandle ||
                    (!retired && (result.TransitionId != pending.TransitionId || result.Revision != pending.ExpectedRevision + 1 ||
                        result.Status is not (MqttPersistenceCommitStatus.Applied or MqttPersistenceCommitStatus.AlreadyApplied))))
                    throw new InvalidOperationException("The durable transition was not confirmed for this owner and revision.");
                if (!retired) transaction.Revision = result.Revision;
                transaction.LastStatus = result.Status;
                transaction.Phase = pending.NextPhase;
                transaction.Pending = null;
            }
        }
        catch
        {
            lock (_unacknowledgedPublishPackets) { if (IsCurrentConnection(generation)) _durableBlocked = true; }
            throw;
        }
    }

    void ValidateDurableOwner(long generation)
    {
        if (!IsCurrentConnection(generation) || _durableBlocked || _durableGeneration == Guid.Empty) throw new InvalidOperationException("Durable protocol progress requires a restored active owner.");
    }

    internal async Task DeleteDurableSessionAsync(CancellationToken cancellationToken)
    {
        if (!HasDurablePersistence || _durableGeneration == Guid.Empty) return;
        MqttSessionPersistenceDeletion deletion;
        lock (_unacknowledgedPublishPackets)
        {
            _durableBlocked = true;
            deletion = new MqttSessionPersistenceDeletion(Id, _durableGeneration, _durableOwnerFence, _durableDeletionId ??= Guid.NewGuid());
        }
        var result = await _serverOptions.SessionPersistence.DeleteSessionAsync(deletion, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (result == null || result.TransitionId != deletion.TransitionId || result.SessionGeneration != deletion.SessionGeneration || result.OwnerFence != deletion.OwnerFence ||
            result.Status is not (MqttPersistenceCommitStatus.Applied or MqttPersistenceCommitStatus.AlreadyApplied))
            throw new InvalidOperationException("Durable session retirement was not confirmed.");
    }

    internal long BeginDurableDeletion(long? expectedGeneration)
    {
        lock (_dataEnqueueLock)
        {
            if (_durableDisconnectionUnconfirmed)
            {
                if (expectedGeneration.HasValue) return -1;
                throw new InvalidOperationException("Session retirement cannot bypass unconfirmed disconnection metadata.");
            }
            if (expectedGeneration.HasValue && (!IsCurrentConnection(expectedGeneration.Value) || IsDataRecoveryPaused)) return -1;
            if (IsDataRecoveryPaused) throw new InvalidOperationException("Session deletion cannot bypass an active restore or recovery owner.");
            return ActivateConnection();
        }
    }

    internal async Task<bool?> PersistDisconnectedAsync(MqttConnectedClient client, CancellationToken cancellationToken)
    {
        var generation = client.ConnectionGeneration;
        if (!HasDurablePersistence && !(_serverOptions.SessionPersistence != null && client.IsWillExternallyOwned)) return null;
        using (await _persistenceGate.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            MqttSessionDisconnectedTransition transition;
            lock (_unacknowledgedPublishPackets)
            {
                if (!IsCurrentConnection(generation) || _durableGeneration == Guid.Empty) return null;
                transition = _durableDisconnection ??= new MqttSessionDisconnectedTransition(Id, _durableGeneration, _durableOwnerFence,
                    Guid.NewGuid(), _durableSubscriptionRevision, DisconnectedTimestamp ?? DateTime.UtcNow, ExpiryInterval)
                {
                    ConnectionAttemptId = client.ConnectionAttemptId,
                    HasWill = client.WillMessage != null,
                    IsWillExternallyOwned = client.IsWillExternallyOwned,
                    WillMessage = client.WillMessage,
                    DisconnectReasonCode = client.DisconnectPacket?.ReasonCode,
                    WillDisposition = client.WillMessage == null ? MqttWillDisposition.None :
                        client.DisconnectPacket?.ReasonCode == MqttDisconnectReasonCode.NormalDisconnection ? MqttWillDisposition.Suppress : MqttWillDisposition.Schedule
                };
                _durableDisconnectionUnconfirmed = true;
            }
            try
            {
                var result = await _serverOptions.SessionPersistence.CommitDisconnectedAsync(transition, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
                lock (_unacknowledgedPublishPackets)
                {
                    if (!IsCurrentConnection(generation)) return null;
                    if (result == null || result.TransitionId != transition.TransitionId || result.SessionGeneration != transition.SessionGeneration || result.OwnerFence != transition.OwnerFence ||
                        result.Revision != transition.ExpectedRevision + 1 || result.Status is not (MqttPersistenceCommitStatus.Applied or MqttPersistenceCommitStatus.AlreadyApplied))
                        throw new InvalidOperationException("Durable disconnection metadata was not confirmed.");
                    _durableSubscriptionRevision = result.Revision;
                    _durableDisconnectionUnconfirmed = false;
                    return true;
                }
            }
            catch
            {
                lock (_unacknowledgedPublishPackets) { if (IsCurrentConnection(generation)) _durableBlocked = true; }
                throw;
            }
        }
    }

    sealed class DurableTransaction(string handle, long revision, MqttOutgoingTransactionPhase? phase, long sequence)
    {
        public string Handle { get; } = handle;
        public long Revision = revision;
        public MqttOutgoingTransactionPhase? Phase = phase;
        public long Sequence { get; } = sequence;
        public MqttOutgoingTransactionTransition Pending;
        public MqttPersistenceCommitStatus LastStatus;
    }

    internal MqttPersistenceCommitStatus? GetDurableCompletionStatus(MqttPublishPacket packet) =>
        _durableCompletions.TryGetValue(packet, out var completion) ? completion.Status : null;

    sealed record DurableCompletion(MqttPersistenceCommitStatus Status);

    static int DurableStringBytes(string value)
    {
        if (value == null) return 0;
        if (value.Contains('\0')) throw new InvalidOperationException("Durable MQTT strings cannot contain a null character.");
        var bytes = StrictUtf8.GetByteCount(value);
        if (bytes > ushort.MaxValue) throw new InvalidOperationException("A durable MQTT string exceeds its encoded length limit.");
        return bytes;
    }

    static long DurablePacketBytes(MqttPublishPacket packet)
    {
        var bytes = checked(packet.Payload.Length + DurableStringBytes(packet.Topic) + DurableStringBytes(packet.ContentType) +
            DurableStringBytes(packet.ResponseTopic) + (packet.CorrelationData?.LongLength ?? 0));
        if (packet.UserProperties != null)
            foreach (var property in packet.UserProperties)
                bytes = checked(bytes + DurableStringBytes(property.Name) + DurableStringBytes(StrictUtf8.GetString(property.ValueBuffer.Span)));
        return bytes;
    }

    static void ValidateDurableTopic(string topic, bool filter)
    {
        if (string.IsNullOrEmpty(topic)) throw new InvalidOperationException("A durable topic must be nonempty.");
        DurableStringBytes(topic);
        if (!filter)
        {
            if (topic.Contains('+') || topic.Contains('#')) throw new InvalidOperationException("A publication topic cannot contain wildcards.");
            return;
        }
        var levels = topic.Split('/');
        for (var index = 0; index < levels.Length; index++)
            if ((levels[index].Contains('+') && levels[index] != "+") || (levels[index].Contains('#') && (levels[index] != "#" || index != levels.Length - 1)))
                throw new InvalidOperationException("A durable subscription filter has an invalid wildcard.");
    }
}
