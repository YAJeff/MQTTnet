// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using MQTTnet.Internal;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using MQTTnet.Server.Internal.Formatter;

namespace MQTTnet.Server.Internal;

public sealed partial class MqttSession
{
    MqttSessionRecoveryLease _recoveryLease;
    int _recoveryPending;
    Task _recoveryOwnerExit = Task.CompletedTask;
    long _nextAdmissionSequence;
    long _nextSendSequence;
    readonly Dictionary<MqttPublishPacket, long> _sendSequences = new();

    internal bool IsDataRecoveryPaused => Volatile.Read(ref _recoveryPending) != 0;

    internal Task WaitForRecoveryOwnerAsync(CancellationToken token)
    {
        lock (_dataEnqueueLock) return _recoveryOwnerExit.WaitAsync(token);
    }

    internal void CompleteUntrackedPublish(MqttPublishPacket packet, long generation)
    {
        if (packet == null || packet.QualityOfServiceLevel != MqttQualityOfServiceLevel.AtMostOnce) return;
        lock (_unacknowledgedPublishPackets)
        {
            if (IsCurrentConnection(generation)) RemoveTrackedPublish(packet);
        }
    }

    internal MqttSessionRecoveryLease BeginRecovery(CancellationToken cancellationToken)
        => BeginRecovery(Volatile.Read(ref _connectionGeneration), cancellationToken);

    internal MqttSessionRecoveryLease BeginRecovery(long generation, CancellationToken cancellationToken)
    {
        lock (_dataEnqueueLock)
        lock (_unacknowledgedPublishPackets)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentConnection(generation)) throw new InvalidOperationException("The connection attempt was superseded.");
            if (!_recoveryOwnerExit.IsCompleted) throw new InvalidOperationException("The previous recovery owner has not exited.");
            if (_recoveryLease != null) throw new InvalidOperationException("Session recovery is already being prepared.");
            var neverSent = _outgoingPublishStates.Where(pair => pair.Value == OutgoingPublishState.Queued)
                .Select(pair => pair.Key).OrderBy(GetAdmissionSequence).ToArray();
            var snapshots = neverSent.Select(packet => new MqttSessionApplicationMessage(MqttPublishPacketSnapshot.Clone(packet), GetEnqueueState(packet))).ToList().AsReadOnly();
            var started = _unacknowledgedPublishPackets.Where(packet => _outgoingPublishStates[packet] != OutgoingPublishState.Queued)
                .Select(packet => new MqttOutgoingTransactionSnapshot(packet,
                    _outgoingPublishStates[packet] >= OutgoingPublishState.PubRelPending ? MqttOutgoingTransactionPhase.AwaitPubComp :
                    packet.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtLeastOnce ? MqttOutgoingTransactionPhase.AwaitPubAck : MqttOutgoingTransactionPhase.AwaitPubRec,
                    _sendSequences.GetValueOrDefault(packet), GetEnqueueState(packet))).OrderBy(snapshot => snapshot.SendSequence).ToList().AsReadOnly();
            var lease = new MqttSessionRecoveryLease(this, _connectionGeneration, neverSent, snapshots, started, cancellationToken);
            _recoveryLease = lease;
            _recoveryPending = 1;
            _recoveryOwnerExit = lease.OwnerExited.Task;
            return lease;
        }
    }

    internal bool TryStageRecovery(MqttSessionRecoveryLease lease, MqttApplicationMessage message, object enqueueState)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_dataEnqueueLock)
        lock (_unacknowledgedPublishPackets)
        {
            ValidateRecovery(lease);
            if (lease.StagedMessages.Count >= _serverOptions.MaxPendingMessagesPerClient) return false;
            if (message.QualityOfServiceLevel < MqttQualityOfServiceLevel.AtMostOnce || message.QualityOfServiceLevel > MqttQualityOfServiceLevel.ExactlyOnce)
                throw new ArgumentOutOfRangeException(nameof(message));
            if (string.IsNullOrEmpty(message.Topic)) throw new ArgumentException("A staged publication requires a full topic.", nameof(message));
            if (message.QualityOfServiceLevel > MqttQualityOfServiceLevel.AtMostOnce)
            {
                var startedCount = _unacknowledgedPublishPackets.Count(packet => _outgoingPublishStates[packet] != OutgoingPublishState.Queued);
                var stagedIds = lease.StagedMessages.Count(item => item.PublishPacket.QualityOfServiceLevel > MqttQualityOfServiceLevel.AtMostOnce);
                if (startedCount + stagedIds >= ushort.MaxValue) return false;
            }
            var packet = MqttPublishPacketSnapshot.Clone(MqttPublishPacketFactory.Create(message));
            packet.Dup = false;
            packet.TopicAlias = 0;
            lease.StagedMessages.Add(new MqttSessionApplicationMessage(packet, enqueueState));
            return true;
        }
    }

    internal IReadOnlyList<MqttSessionApplicationMessage> CommitRecovery(MqttSessionRecoveryLease lease)
    {
        lock (_dataEnqueueLock)
        lock (_unacknowledgedPublishPackets)
        {
            ValidateRecovery(lease);
            if (lease.StagedMessages.Count > _serverOptions.MaxPendingMessagesPerClient) throw new InvalidOperationException("Staged recovery exceeds queue capacity.");
            var survivors = _unacknowledgedPublishPackets.Where(packet => _outgoingPublishStates[packet] != OutgoingPublishState.Queued).ToArray();
            var states = survivors.ToDictionary(packet => packet, packet => _outgoingPublishStates[packet]);
            var reserved = survivors.Select(packet => packet.PacketIdentifier).ToHashSet();
            if (reserved.Count != survivors.Length || reserved.Contains(0)) throw new InvalidOperationException("Native transaction identifiers are inconsistent.");
            var available = ushort.MaxValue - reserved.Count;
            if (lease.StagedMessages.Count(item => item.PublishPacket.QualityOfServiceLevel > MqttQualityOfServiceLevel.AtMostOnce) > available)
                throw new InvalidOperationException("Insufficient packet identifiers for atomic recovery commit.");

            // Complete validation, copies and capacity preparation before changing visible session state.
            ushort identifier = 0;
            foreach (var item in lease.StagedMessages)
            {
                if (item.PublishPacket.QualityOfServiceLevel > MqttQualityOfServiceLevel.AtMostOnce)
                {
                    do { identifier++; } while (reserved.Contains(identifier));
                    item.PublishPacket.PacketIdentifier = identifier;
                    reserved.Add(identifier);
                }
                _admissionContexts.GetValue(item.PublishPacket, _ => new AdmissionContext(item.EnqueueState, ++_nextAdmissionSequence));
            }
            var accepted = lease.StagedMessages.Select(item => new MqttSessionApplicationMessage(MqttPublishPacketSnapshot.Clone(item.PublishPacket), item.EnqueueState)).ToList().AsReadOnly();
            var oldItems = _packetBus.ExportItems(MqttPacketBusPartition.Data);
            var reclaimed = lease.OriginalNeverSent.ToHashSet();
            lease.ReclaimedItems = oldItems.Where(item => item.Packet is MqttPublishPacket publish && reclaimed.Contains(publish)).ToArray();
            _unacknowledgedPublishPackets.EnsureCapacity(survivors.Length + lease.StagedMessages.Count);
            _outgoingPublishStates.EnsureCapacity(survivors.Length + lease.StagedMessages.Count);
            _reservedPacketIdentifiers.EnsureCapacity(reserved.Count);

            var replacementItems = new List<(MqttPacketBusItem Item, MqttPacketBusPartition Partition)>();
            foreach (var packet in survivors.OrderBy(packet => _sendSequences.GetValueOrDefault(packet)))
            {
                if (states[packet] >= OutgoingPublishState.PubRelPending)
                    replacementItems.Add((new MqttPacketBusItem(new MqttPubRelPacket { PacketIdentifier = packet.PacketIdentifier }), MqttPacketBusPartition.Control));
                else
                {
                    replacementItems.Add((new MqttPacketBusItem(packet), MqttPacketBusPartition.Retransmission));
                }
            }
            foreach (var item in lease.StagedMessages) replacementItems.Add((new MqttPacketBusItem(item.PublishPacket), MqttPacketBusPartition.Data));
            // Allocate every queue node before replacing any visible queue or tracking entry.
            _packetBus.ReplaceItems(replacementItems);
            foreach (var packet in survivors) if (states[packet] < OutgoingPublishState.PubRelPending) packet.Dup = true;
            foreach (var packet in lease.OriginalNeverSent) RemoveTrackedPublish(packet);
            foreach (var item in lease.StagedMessages)
            {
                var packet = item.PublishPacket;
                if (packet.QualityOfServiceLevel > MqttQualityOfServiceLevel.AtMostOnce)
                {
                    _unacknowledgedPublishPackets.Add(packet);
                    _reservedPacketIdentifiers.Add(packet.PacketIdentifier);
                }
                _outgoingPublishStates.Add(packet, OutgoingPublishState.Queued);
            }
            lease.Committed = true;
            return accepted;
        }
    }

    internal void AbortRecovery(MqttSessionRecoveryLease lease)
    {
        lock (_dataEnqueueLock)
        {
            if (!ReferenceEquals(_recoveryLease, lease) || !IsCurrentConnection(lease.ConnectionGeneration)) return;
            if (lease.Committed || lease.Finished) return;
            lease.Finished = true;
        }
        lease.Cancel();
    }

    internal async Task FinalizeRecoveryAsync(MqttSessionRecoveryLease lease, bool ownerSucceeded)
    {
        if (lease.Committed && Interlocked.Exchange(ref lease.NotificationsFinalized, 1) == 0)
        {
            foreach (var item in lease.ReclaimedItems) item.Cancel();
            if (lease.OriginalNeverSent.Count > 0)
            {
                var messages = lease.OriginalNeverSent.Select(packet => new MqttSessionApplicationMessage(packet, GetEnqueueState(packet))).ToList().AsReadOnly();
                await _clientSessionsManager.NotifyApplicationMessagesInvalidatedAsync(new SessionApplicationMessagesInvalidatedEventArgs(
                    Id, Items, messages, MqttSessionApplicationMessagesInvalidationReason.ApplicationMessageReclaimed)).ConfigureAwait(false);
            }
        }
        lock (_dataEnqueueLock)
        {
            lease.Finished = true;
            if (ReferenceEquals(_recoveryLease, lease) && IsCurrentConnection(lease.ConnectionGeneration) && ownerSucceeded && lease.Committed && !lease.CancellationToken.IsCancellationRequested)
            {
                _recoveryLease = null;
                _recoveryPending = 0;
            }
        }
        lease.OwnerExited.TrySetResult();
        lease.ReleaseCancellationRegistration();
    }

    void ValidateRecovery(MqttSessionRecoveryLease lease)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(_recoveryLease, lease) || !IsCurrentConnection(lease.ConnectionGeneration)) throw new InvalidOperationException("The recovery lease was superseded.");
        lease.CancellationToken.ThrowIfCancellationRequested();
        if (lease.Finished || lease.Committed) throw new InvalidOperationException("The recovery lease is already completed.");
    }

    long GetAdmissionSequence(MqttPublishPacket packet) => _admissionContexts.TryGetValue(packet, out var context) ? context.Sequence : 0;
}
