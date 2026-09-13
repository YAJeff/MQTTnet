// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections;
using System.Runtime.CompilerServices;
using MQTTnet.Internal;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using MQTTnet.Server.Exceptions;

namespace MQTTnet.Server.Internal;

public sealed partial class MqttSession : IDisposable
{
    readonly MqttClientSessionsManager _clientSessionsManager;
    readonly MqttConnectPacket _connectPacket;
    readonly object _dataEnqueueLock = new();
    readonly MqttServerEventContainer _eventContainer;
    readonly MqttPacketBus _packetBus = new();
    readonly MqttPacketIdentifierProvider _packetIdentifierProvider = new();
    readonly ConditionalWeakTable<MqttPublishPacket, AdmissionContext> _admissionContexts = new();
    readonly ConditionalWeakTable<MqttPublishPacket, MqttPacketBusItem> _admissionBusItems = new();
    readonly MqttServerOptions _serverOptions;
    readonly MqttClientSubscriptionsManager _subscriptionsManager;
    readonly MqttRetainedMessagesManager _retainedMessagesManagerForPersistence;

    // Do not use a dictionary in order to keep the ordering of the messages.
    readonly List<MqttPublishPacket> _unacknowledgedPublishPackets = new();
    readonly Dictionary<MqttPublishPacket, OutgoingPublishState> _outgoingPublishStates = new();
    readonly HashSet<ushort> _reservedPacketIdentifiers = new();
    long _connectionGeneration;

    // Bookkeeping to know if this is a subscribing client; lazy initialize later.
    HashSet<string> _subscribedTopics;
    bool _disposed;

    public MqttSession(
        MqttConnectPacket connectPacket,
        IDictionary items,
        MqttServerOptions serverOptions,
        MqttServerEventContainer eventContainer,
        MqttRetainedMessagesManager retainedMessagesManager,
        MqttClientSessionsManager clientSessionsManager)
    {
        Items = items ?? throw new ArgumentNullException(nameof(items));

        _connectPacket = connectPacket ?? throw new ArgumentNullException(nameof(connectPacket));
        ExpiryInterval = connectPacket.SessionExpiryInterval;
        _serverOptions = serverOptions ?? throw new ArgumentNullException(nameof(serverOptions));
        _clientSessionsManager = clientSessionsManager ?? throw new ArgumentNullException(nameof(clientSessionsManager));
        _eventContainer = eventContainer ?? throw new ArgumentNullException(nameof(eventContainer));

        _subscriptionsManager = new MqttClientSubscriptionsManager(this, eventContainer, retainedMessagesManager, clientSessionsManager);
        _retainedMessagesManagerForPersistence = retainedMessagesManager;
    }

    public DateTime CreatedTimestamp { get; } = DateTime.UtcNow;

    public DateTime? DisconnectedTimestamp { get; set; }

    public uint ExpiryInterval { get; internal set; }

    public bool HasSubscribedTopics => _subscribedTopics != null && _subscribedTopics.Count > 0;

    public string Id => _connectPacket.ClientId;

    public string UserName => _connectPacket.Username;

    public IDictionary Items { get; }

    public MqttConnectPacket LatestConnectPacket { get; set; }

    public MqttPacketIdentifierProvider PacketIdentifierProvider { get; } = new();

    public long PendingDataPacketsCount => _packetBus.PartitionItemsCount(MqttPacketBusPartition.Data);

    public bool WillMessageSent { get; set; }

    // Preserve identifier-only acknowledgement for existing callers. The server's
    // packet handlers use the QoS-aware overload to validate wire acknowledgements.
    public MqttPublishPacket AcknowledgePublishPacket(ushort packetIdentifier)
    {
        lock (_dataEnqueueLock)
        lock (_unacknowledgedPublishPackets)
        {
            if (IsDataRecoveryPaused || HasDurablePersistence) throw new InvalidOperationException("Synchronous acknowledgement is unavailable during ordered recovery or durable persistence.");
            var publishPacket = _unacknowledgedPublishPackets.FirstOrDefault(p => p.PacketIdentifier.Equals(packetIdentifier));
            RemoveTrackedPublish(publishPacket);
            return publishPacket;
        }
    }

    public MqttPublishPacket AcknowledgePublishPacket(ushort packetIdentifier, MqttQualityOfServiceLevel qualityOfServiceLevel)
    {
        MqttPublishPacket publishPacket;

        lock (_dataEnqueueLock)
        lock (_unacknowledgedPublishPackets)
        {
            if (IsDataRecoveryPaused || HasDurablePersistence) throw new InvalidOperationException("Synchronous acknowledgement is unavailable during ordered recovery or durable persistence.");
            publishPacket = _unacknowledgedPublishPackets.FirstOrDefault(
                p => p.PacketIdentifier.Equals(packetIdentifier) && p.QualityOfServiceLevel == qualityOfServiceLevel);
            RemoveTrackedPublish(publishPacket);
        }

        return publishPacket;
    }

    internal long ActivateConnection()
    {
        MqttSessionRecoveryLease previous;
        long generation;
        lock (_dataEnqueueLock)
        lock (_unacknowledgedPublishPackets)
        {
            previous = _recoveryLease;
            _recoveryLease = null;
            _recoveryPending = _eventContainer.PreparingSessionRecoveryHandler != null ? 1 : 0;
            _durableOptOut = false;
            generation = Interlocked.Increment(ref _connectionGeneration);
        }
        previous?.Cancel();
        return generation;
    }

    internal bool IsCurrentConnection(long generation) =>
        !Volatile.Read(ref _disposed) && Volatile.Read(ref _connectionGeneration) == generation;

    internal bool MarkPublishSent(MqttPublishPacket packet, long generation)
    {
        return MarkPublishSent(packet, generation, null);
    }

    internal bool MarkPublishSent(MqttPublishPacket packet, long generation, MqttPublishPacket wireSnapshot)
    {
        lock (_unacknowledgedPublishPackets)
        {
            if (!IsCurrentConnection(generation) || !_outgoingPublishStates.TryGetValue(packet, out var state) || state >= OutgoingPublishState.PubRelPending) return false;
            if (wireSnapshot != null)
            {
                if (wireSnapshot.PacketIdentifier != packet.PacketIdentifier || wireSnapshot.QualityOfServiceLevel != packet.QualityOfServiceLevel)
                    throw new InvalidOperationException("An outbound interceptor cannot change the identifier or QoS of a session-owned transaction.");
                MqttPublishPacketSnapshot.CopyFields(wireSnapshot, packet);
            }
            // A failed write may have been partially visible; recovery must use DUP in that case too.
            if (state == OutgoingPublishState.Queued) _sendSequences[packet] = ++_nextSendSequence;
            _outgoingPublishStates[packet] = OutgoingPublishState.PublishSent;
            return true;
        }
    }

    internal bool CanSendPubRel(ushort identifier, long generation)
    {
        lock (_unacknowledgedPublishPackets)
        {
            var packet = _unacknowledgedPublishPackets.FirstOrDefault(p => p.PacketIdentifier == identifier);
            return IsCurrentConnection(generation) && packet != null && _outgoingPublishStates.TryGetValue(packet, out var state) && state >= OutgoingPublishState.PubRelPending;
        }
    }

    internal void MarkPubRelSent(ushort identifier, long generation)
    {
        lock (_unacknowledgedPublishPackets)
        {
            var packet = _unacknowledgedPublishPackets.FirstOrDefault(p => p.PacketIdentifier == identifier);
            if (IsCurrentConnection(generation) && packet != null && _outgoingPublishStates.TryGetValue(packet, out var state) && state >= OutgoingPublishState.PubRelPending)
                _outgoingPublishStates[packet] = OutgoingPublishState.PubRelSent;
        }
    }

    internal bool ProcessPubRec(ushort identifier, bool isError, long generation, out MqttPublishPacket completed)
    {
        completed = null;
        lock (_unacknowledgedPublishPackets)
        {
            var packet = _unacknowledgedPublishPackets.FirstOrDefault(p => p.PacketIdentifier == identifier && p.QualityOfServiceLevel == MqttQualityOfServiceLevel.ExactlyOnce);
            if (!IsCurrentConnection(generation) || packet == null || !_outgoingPublishStates.TryGetValue(packet, out var state) || state == OutgoingPublishState.Queued) return false;
            if (isError)
            {
                if (state >= OutgoingPublishState.PubRelPending) return false;
                completed = packet;
                RemoveTrackedPublish(packet);
                return false;
            }

            // Duplicate PUBREC must not regress an exchange whose PUBREL was already sent.
            if (state == OutgoingPublishState.PublishSent) _outgoingPublishStates[packet] = OutgoingPublishState.PubRelPending;
            return true;
        }
    }

    internal MqttPublishPacket AcknowledgePublishPacket(ushort identifier, MqttQualityOfServiceLevel qos, long generation)
    {
        lock (_unacknowledgedPublishPackets)
        {
            var packet = _unacknowledgedPublishPackets.FirstOrDefault(p => p.PacketIdentifier == identifier && p.QualityOfServiceLevel == qos);
            var expected = qos == MqttQualityOfServiceLevel.ExactlyOnce ? OutgoingPublishState.PubRelSent : OutgoingPublishState.PublishSent;
            if (!IsCurrentConnection(generation) || packet == null || !_outgoingPublishStates.TryGetValue(packet, out var state) || state != expected) return null;
            RemoveTrackedPublish(packet);
            return packet;
        }
    }

    void RemoveTrackedPublish(MqttPublishPacket packet)
    {
        if (packet == null) return;
        _unacknowledgedPublishPackets.Remove(packet);
        _outgoingPublishStates.Remove(packet);
        _sendSequences.Remove(packet);
        if (_durableAdmissionHandles.Remove(packet, out var handle)) _durableHandlePackets.Remove(handle);
        _reservedPacketIdentifiers.Remove(packet.PacketIdentifier);
    }

    enum OutgoingPublishState { Queued, PreparingPublish, PublishSent, PubRelPending, PubRelSent }

    public void AddSubscribedTopic(string topic)
    {
        if (_subscribedTopics == null)
        {
            _subscribedTopics = new HashSet<string>();
        }

        _subscribedTopics.Add(topic);
    }

    public Task DeleteAsync()
    {
        return _clientSessionsManager.DeleteSessionAsync(Id);
    }

    public Task<MqttPacketBusItem> DequeuePacketAsync(CancellationToken cancellationToken)
    {
        return _packetBus.DequeueItemAsync(cancellationToken);
    }

    internal Task<MqttPacketBusItem> DequeuePacketAsync(Func<MqttPacketBusItem, bool> canDequeue, CancellationToken cancellationToken)
    {
        return _packetBus.DequeueItemAsync(canDequeue, cancellationToken);
    }

    internal void SignalPacketBus()
    {
        _packetBus.Signal();
    }

    public void Dispose()
    {
        _clientSessionsManager.EndWillSession(this);
        List<MqttPublishPacket> invalidated = null;
        lock (_dataEnqueueLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            lock (_incomingQos2) { _incomingQos2.Clear(); _incomingQos2Bytes = 0; }
            if (_eventContainer.SessionApplicationMessagesInvalidatedEvent.HasHandlers)
            {
                lock (_unacknowledgedPublishPackets)
                {
                    invalidated = _unacknowledgedPublishPackets.ToList();
                }
            }
            _packetBus.Dispose();
        }

        NotifyInvalidatedPackets(invalidated, MqttSessionApplicationMessagesInvalidationReason.SessionDisposed);
        _recoveryLease?.Cancel();
        _subscriptionsManager.Dispose();
    }

    public void EnqueueControlPacket(MqttPacketBusItem packetBusItem)
    {
        _packetBus.EnqueueItem(packetBusItem, MqttPacketBusPartition.Control);
    }

    public EnqueueDataPacketResult EnqueueDataPacket(MqttPacketBusItem packetBusItem)
    {
        return EnqueueDataPacket(packetBusItem, true, out _);
    }

    // Capture this session and one interception context per attempt, even when callers await user code.
    // Return whether the interceptor accepted the attempt, preserving dispatch subscriber accounting.
    internal async Task<bool> EnqueueApplicationMessageAsync(
        string senderId, MqttApplicationMessage applicationMessage, Func<MqttPublishPacket> createPublishPacket)
    {
        if (!_eventContainer.InterceptingClientEnqueueEvent.HasHandlers && !_eventContainer.ApplicationMessageEnqueuedOrDroppedEvent.HasHandlers)
        {
            EnqueueDataPacket(new MqttPacketBusItem(createPublishPacket()));
            return true;
        }

        var context = new InterceptingClientApplicationMessageEnqueueEventArgs(senderId, Id, applicationMessage, Items);
        var isDropped = true;
        Exception failure = null;
        MqttPublishPacket packet = null;
        try
        {
            await _eventContainer.InterceptingClientEnqueueEvent.InvokeAsync(context).ConfigureAwait(false);
            if (!context.AcceptEnqueue)
            {
                return false;
            }

            packet = createPublishPacket();
            isDropped = EnqueueDataPacket(new MqttPacketBusItem(packet), true, out _, context.EnqueueState) == EnqueueDataPacketResult.Dropped;
            return true;
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            if (_eventContainer.ApplicationMessageEnqueuedOrDroppedEvent.HasHandlers)
            {
                var outcome = new ApplicationMessageEnqueuedEventArgs(senderId, Id, applicationMessage, isDropped, Items, context.EnqueueState, failure, packet);
                await _eventContainer.ApplicationMessageEnqueuedOrDroppedEvent.InvokeAsync(outcome).ConfigureAwait(false);
            }
        }
    }

    internal EnqueueDataPacketResult EnqueueDataPacket(MqttPacketBusItem packetBusItem, bool allowEviction, out ushort packetIdentifier)
    {
        return EnqueueDataPacket(packetBusItem, allowEviction, out packetIdentifier, null);
    }

    internal EnqueueDataPacketResult EnqueueDataPacket(MqttPacketBusItem packetBusItem, bool allowEviction, out ushort packetIdentifier, object enqueueState)
    {
        ArgumentNullException.ThrowIfNull(packetBusItem);
        var publishPacket = (MqttPublishPacket)packetBusItem.Packet;
        string durableHandle = null;
        if (HasDurablePersistence && publishPacket.QualityOfServiceLevel > MqttQualityOfServiceLevel.AtMostOnce)
        {
            if (enqueueState is not IMqttDurableDeliveryContext context || string.IsNullOrEmpty(durableHandle = context.DeliveryHandle))
            {
                packetIdentifier = 0;
                if (allowEviction) packetBusItem.Fail(new InvalidOperationException("Durable admission requires a stable delivery context."));
                return EnqueueDataPacketResult.Dropped;
            }
        }
        MqttPacketBusItem overwritten;
        EnqueueDataPacketResult result;
        lock (_dataEnqueueLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsDataRecoveryPaused)
            {
                packetIdentifier = 0;
                if (allowEviction) packetBusItem.Fail(new InvalidOperationException("Session admission is paused for ordered recovery."));
                return EnqueueDataPacketResult.Dropped;
            }
            lock (_unacknowledgedPublishPackets)
            {
                if (durableHandle != null && (_durableBlocked || _durableHandlePackets.ContainsKey(durableHandle)))
                {
                    packetIdentifier = 0;
                    if (allowEviction) packetBusItem.Fail(new InvalidOperationException("Durable admission is blocked or this delivery is already pending."));
                    return EnqueueDataPacketResult.Dropped;
                }
                result = EnqueueDataPacketCore(packetBusItem, publishPacket, allowEviction, out overwritten, enqueueState);
                if (result == EnqueueDataPacketResult.Enqueued && durableHandle != null)
                {
                    _durableAdmissionHandles.Add(publishPacket, durableHandle);
                    _durableHandlePackets.Add(durableHandle, publishPacket);
                }
            }
            packetIdentifier = result == EnqueueDataPacketResult.Enqueued ? publishPacket.PacketIdentifier : (ushort)0;
        }

        NotifyOverwrittenPacket(overwritten);
        return result;
    }

    // All data producers and recovery share the admission gate. Dequeue may only free capacity.
    EnqueueDataPacketResult EnqueueDataPacketCore(
        MqttPacketBusItem packetBusItem, MqttPublishPacket publishPacket, bool allowEviction, out MqttPacketBusItem overwritten, object enqueueState = null, bool isRecovery = false)
    {
        overwritten = null;
        var full = PendingDataPacketsCount >= _serverOptions.MaxPendingMessagesPerClient;
        if (full)
        {
            if (!allowEviction)
            {
                // Rejection is backpressure: no packet identifier, tracking entry or faulted promise.
                return EnqueueDataPacketResult.Dropped;
            }

            if (_serverOptions.PendingMessagesOverflowStrategy == MqttPendingMessagesOverflowStrategy.DropNewMessage)
            {
                packetBusItem.Fail(new MqttPendingMessagesOverflowException(Id, _serverOptions.PendingMessagesOverflowStrategy));
                return EnqueueDataPacketResult.Dropped;
            }
        }

        ushort newIdentifier = 0;
        if (!isRecovery && publishPacket.QualityOfServiceLevel > MqttQualityOfServiceLevel.AtMostOnce)
        {
            lock (_unacknowledgedPublishPackets)
            {
                if (_reservedPacketIdentifiers.Count == ushort.MaxValue)
                {
                    if (allowEviction) packetBusItem.Fail(new InvalidOperationException("No packet identifier is available in this session."));
                    return EnqueueDataPacketResult.Dropped;
                }
                do { newIdentifier = _packetIdentifierProvider.GetNextPacketIdentifier(); }
                while (_reservedPacketIdentifiers.Contains(newIdentifier));
            }
        }

        if (full && _serverOptions.PendingMessagesOverflowStrategy == MqttPendingMessagesOverflowStrategy.DropOldestQueuedMessage)
        {
            // Only drop from the data partition. Dropping from control partition might break the connection
            // because the client does not receive PINGREQ packets etc. any longer.
            overwritten = _packetBus.DropFirstItem(MqttPacketBusPartition.Data);
            if (overwritten != null)
            {
                if (overwritten.Packet is MqttPublishPacket evictedPublishPacket)
                {
                    lock (_unacknowledgedPublishPackets)
                    {
                        // Remove only the queued packet that was evicted, not an in-flight exchange.
                        RemoveTrackedPublish(evictedPublishPacket);
                    }
                }

                overwritten.Fail(new MqttPendingMessagesOverflowException(Id, _serverOptions.PendingMessagesOverflowStrategy));
            }
        }

        if (publishPacket.QualityOfServiceLevel > MqttQualityOfServiceLevel.AtMostOnce)
        {
            lock (_unacknowledgedPublishPackets)
            {
                if (!isRecovery)
                {
                    publishPacket.PacketIdentifier = newIdentifier;
                    _reservedPacketIdentifiers.Add(newIdentifier);
                }
                _unacknowledgedPublishPackets.Add(publishPacket);
                if (!isRecovery) _outgoingPublishStates[publishPacket] = OutgoingPublishState.Queued;
            }
        }

        if (publishPacket.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtMostOnce && _eventContainer.PreparingSessionRecoveryHandler != null)
        {
            lock (_unacknowledgedPublishPackets) _outgoingPublishStates[publishPacket] = OutgoingPublishState.Queued;
        }

        if (enqueueState != null || _eventContainer.PreparingSessionRecoveryHandler != null)
        {
            // Attach before the packet becomes visible to the sender. Recovery reuses the packet and its original context.
            _admissionContexts.GetValue(publishPacket, _ => new AdmissionContext(enqueueState, ++_nextAdmissionSequence));
        }
        _admissionBusItems.GetValue(publishPacket, _ => packetBusItem);
        _packetBus.EnqueueItem(packetBusItem, MqttPacketBusPartition.Data);
        return EnqueueDataPacketResult.Enqueued;
    }

    void NotifyOverwrittenPacket(MqttPacketBusItem overwritten)
    {
        if (overwritten != null && _eventContainer.QueuedApplicationMessageOverwrittenEvent.HasHandlers)
        {
            var eventArgs = new QueueMessageOverwrittenEventArgs(Id, overwritten.Packet, Items,
                overwritten.Packet is MqttPublishPacket publish ? GetEnqueueState(publish) : null);
            _eventContainer.QueuedApplicationMessageOverwrittenEvent.InvokeAsync(eventArgs).ConfigureAwait(false);
        }
    }

    public void EnqueueHealthPacket(MqttPacketBusItem packetBusItem)
    {
        _packetBus.EnqueueItem(packetBusItem, MqttPacketBusPartition.Health);
    }

    public MqttPublishPacket PeekAcknowledgePublishPacket(ushort packetIdentifier)
    {
        // This will only return the matching PUBLISH packet but does not remove it.
        // This is required for QoS 2.
        lock (_unacknowledgedPublishPackets)
        {
            return _unacknowledgedPublishPackets.FirstOrDefault(p => p.PacketIdentifier.Equals(packetIdentifier));
        }
    }

    public void Recover()
    {
        // TODO: Keep the bus and only insert pending items again.

        /*
            The Session state in the Client consists of:
            ·         QoS 1 and QoS 2 messages which have been sent to the Server, but have not been completely acknowledged.
            ·         QoS 2 messages which have been received from the Server, but have not been completely acknowledged.

            The Session state in the Server consists of:
            ·         The existence of a Session, even if the rest of the Session state is empty.
            ·         The Client’s subscriptions.
            ·         QoS 1 and QoS 2 messages which have been sent to the Client, but have not been completely acknowledged.
            ·         QoS 1 and QoS 2 messages pending transmission to the Client.
            ·         QoS 2 messages which have been received from the Client, but have not been completely acknowledged.
            ·         Optionally, QoS 0 messages pending transmission to the Client.
         */

        // Create a copy of all currently unacknowledged publish packets and clear the storage.
        // We must re-enqueue them in order to trigger other code.
        List<MqttPacketBusItem> overwrittenPackets = null;
        List<MqttPublishPacket> invalidated = null;
        lock (_dataEnqueueLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsDataRecoveryPaused || HasDurablePersistence) throw new InvalidOperationException("Legacy recovery is unavailable during ordered recovery or durable persistence.");
            lock (_unacknowledgedPublishPackets)
            {
                var packets = _unacknowledgedPublishPackets.ToList();
                _unacknowledgedPublishPackets.Clear();
                _packetBus.Clear();
                foreach (var publishPacket in packets)
                {
                    var state = _outgoingPublishStates[publishPacket];
                    if (state >= OutgoingPublishState.PubRelPending)
                    {
                        _unacknowledgedPublishPackets.Add(publishPacket);
                        _packetBus.EnqueueItem(new MqttPacketBusItem(new MqttPubRelPacket { PacketIdentifier = publishPacket.PacketIdentifier }), MqttPacketBusPartition.Control);
                        continue;
                    }

                    if (state == OutgoingPublishState.PublishSent)
                    {
                        // A possibly-sent transaction is not a new queued message and cannot be evicted by backlog capacity.
                        publishPacket.Dup = true;
                        _unacknowledgedPublishPackets.Add(publishPacket);
                        _packetBus.EnqueueItem(new MqttPacketBusItem(publishPacket), MqttPacketBusPartition.Retransmission);
                        continue;
                    }

                    publishPacket.Dup = state == OutgoingPublishState.PublishSent;
                    var result = EnqueueDataPacketCore(new MqttPacketBusItem(publishPacket), publishPacket, true, out var overwritten, isRecovery: true);
                    if (result == EnqueueDataPacketResult.Dropped) RemoveTrackedPublish(publishPacket);
                    if (overwritten != null) (overwrittenPackets ??= new List<MqttPacketBusItem>()).Add(overwritten);
                    if (_eventContainer.SessionApplicationMessagesInvalidatedEvent.HasHandlers)
                    {
                        if (result == EnqueueDataPacketResult.Dropped) (invalidated ??= new List<MqttPublishPacket>()).Add(publishPacket);
                        if (overwritten != null) (invalidated ??= new List<MqttPublishPacket>()).Add((MqttPublishPacket)overwritten.Packet);
                    }
                }
            }
        }

        NotifyInvalidatedPackets(invalidated, MqttSessionApplicationMessagesInvalidationReason.RecoveryOverflow);
        if (overwrittenPackets != null)
        {
            foreach (var overwritten in overwrittenPackets)
            {
                NotifyOverwrittenPacket(overwritten);
            }
        }
    }

    void NotifyInvalidatedPackets(List<MqttPublishPacket> packets, MqttSessionApplicationMessagesInvalidationReason reason)
    {
        if (packets == null || (packets.Count == 0 && reason != MqttSessionApplicationMessagesInvalidationReason.SessionDisposed)) return;
        var messages = packets.Select(packet => new MqttSessionApplicationMessage(packet, GetEnqueueState(packet))).ToList().AsReadOnly();
        var eventArgs = new SessionApplicationMessagesInvalidatedEventArgs(Id, Items, messages, reason);
        _ = _clientSessionsManager.NotifyApplicationMessagesInvalidatedAsync(eventArgs);
    }

    internal object GetEnqueueState(MqttPublishPacket packet) =>
        _admissionContexts.TryGetValue(packet, out var context) ? context.State : null;

    // Weak keys retain correlation for late notifications without retaining completed publish packets.
    sealed class AdmissionContext(object state, long sequence)
    {
        public object State { get; } = state;
        public long Sequence { get; } = sequence;
    }

    public void RemoveSubscribedTopic(string topic)
    {
        _subscribedTopics?.Remove(topic);
    }

    public Task<SubscribeResult> Subscribe(MqttSubscribePacket subscribePacket, CancellationToken cancellationToken)
    {
        if (HasDurablePersistence) throw new InvalidOperationException("Durable subscriptions must be changed through the authoritative store and restored by the recovery owner.");
        return _subscriptionsManager.Subscribe(subscribePacket, cancellationToken);
    }

    public bool TryCheckSubscriptions(string topic, ulong topicHash, MqttQualityOfServiceLevel qualityOfServiceLevel, string senderId, out CheckSubscriptionsResult result)
    {
        result = null;

        try
        {
            result = _subscriptionsManager.CheckSubscriptions(topic, topicHash, qualityOfServiceLevel, senderId);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Task<UnsubscribeResult> Unsubscribe(MqttUnsubscribePacket unsubscribePacket, CancellationToken cancellationToken)
    {
        if (HasDurablePersistence) throw new InvalidOperationException("Durable subscriptions must be changed through the authoritative store and restored by the recovery owner.");
        return _subscriptionsManager.Unsubscribe(unsubscribePacket, cancellationToken);
    }
}
