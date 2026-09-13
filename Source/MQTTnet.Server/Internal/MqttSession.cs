// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections;
using MQTTnet.Internal;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using MQTTnet.Server.Exceptions;

namespace MQTTnet.Server.Internal;

public sealed class MqttSession : IDisposable
{
    readonly MqttClientSessionsManager _clientSessionsManager;
    readonly MqttConnectPacket _connectPacket;
    readonly object _dataEnqueueLock = new();
    readonly MqttServerEventContainer _eventContainer;
    readonly MqttPacketBus _packetBus = new();
    readonly MqttPacketIdentifierProvider _packetIdentifierProvider = new();
    readonly MqttServerOptions _serverOptions;
    readonly MqttClientSubscriptionsManager _subscriptionsManager;

    // Do not use a dictionary in order to keep the ordering of the messages.
    readonly List<MqttPublishPacket> _unacknowledgedPublishPackets = new();

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
        lock (_unacknowledgedPublishPackets)
        {
            var publishPacket = _unacknowledgedPublishPackets.FirstOrDefault(p => p.PacketIdentifier.Equals(packetIdentifier));
            _unacknowledgedPublishPackets.Remove(publishPacket);
            return publishPacket;
        }
    }

    public MqttPublishPacket AcknowledgePublishPacket(ushort packetIdentifier, MqttQualityOfServiceLevel qualityOfServiceLevel)
    {
        MqttPublishPacket publishPacket;

        lock (_unacknowledgedPublishPackets)
        {
            publishPacket = _unacknowledgedPublishPackets.FirstOrDefault(
                p => p.PacketIdentifier.Equals(packetIdentifier) && p.QualityOfServiceLevel == qualityOfServiceLevel);
            _unacknowledgedPublishPackets.Remove(publishPacket);
        }

        return publishPacket;
    }

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
        var context = new InterceptingClientApplicationMessageEnqueueEventArgs(senderId, Id, applicationMessage, Items);
        var isDropped = true;
        Exception failure = null;
        try
        {
            await _eventContainer.InterceptingClientEnqueueEvent.InvokeAsync(context).ConfigureAwait(false);
            if (!context.AcceptEnqueue)
            {
                return false;
            }

            var packet = createPublishPacket();
            isDropped = EnqueueDataPacket(new MqttPacketBusItem(packet)) == EnqueueDataPacketResult.Dropped;
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
                var outcome = new ApplicationMessageEnqueuedEventArgs(senderId, Id, applicationMessage, isDropped, Items, context.EnqueueState, failure);
                await _eventContainer.ApplicationMessageEnqueuedOrDroppedEvent.InvokeAsync(outcome).ConfigureAwait(false);
            }
        }
    }

    internal EnqueueDataPacketResult EnqueueDataPacket(MqttPacketBusItem packetBusItem, bool allowEviction, out ushort packetIdentifier)
    {
        ArgumentNullException.ThrowIfNull(packetBusItem);
        var publishPacket = (MqttPublishPacket)packetBusItem.Packet;
        MqttPacketBusItem overwritten;
        EnqueueDataPacketResult result;
        lock (_dataEnqueueLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            result = EnqueueDataPacketCore(packetBusItem, publishPacket, allowEviction, out overwritten);
            packetIdentifier = result == EnqueueDataPacketResult.Enqueued ? publishPacket.PacketIdentifier : (ushort)0;
        }

        NotifyOverwrittenPacket(overwritten);
        return result;
    }

    // All data producers and recovery share the admission gate. Dequeue may only free capacity.
    EnqueueDataPacketResult EnqueueDataPacketCore(
        MqttPacketBusItem packetBusItem, MqttPublishPacket publishPacket, bool allowEviction, out MqttPacketBusItem overwritten)
    {
        overwritten = null;
        if (PendingDataPacketsCount >= _serverOptions.MaxPendingMessagesPerClient)
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

            if (_serverOptions.PendingMessagesOverflowStrategy == MqttPendingMessagesOverflowStrategy.DropOldestQueuedMessage)
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
                            _unacknowledgedPublishPackets.Remove(evictedPublishPacket);
                        }
                    }

                    overwritten.Fail(new MqttPendingMessagesOverflowException(Id, _serverOptions.PendingMessagesOverflowStrategy));
                }
            }
        }

        if (publishPacket.QualityOfServiceLevel > MqttQualityOfServiceLevel.AtMostOnce)
        {
            publishPacket.PacketIdentifier = _packetIdentifierProvider.GetNextPacketIdentifier();

            lock (_unacknowledgedPublishPackets)
            {
                _unacknowledgedPublishPackets.Add(publishPacket);
            }
        }

        _packetBus.EnqueueItem(packetBusItem, MqttPacketBusPartition.Data);
        return EnqueueDataPacketResult.Enqueued;
    }

    void NotifyOverwrittenPacket(MqttPacketBusItem overwritten)
    {
        if (overwritten != null && _eventContainer.QueuedApplicationMessageOverwrittenEvent.HasHandlers)
        {
            var eventArgs = new QueueMessageOverwrittenEventArgs(Id, overwritten.Packet, Items);
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
        // TODO: Check if packet identifier must be restarted or not.
        // TODO: Recover package identifier.

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
            List<MqttPublishPacket> unacknowledgedPublishPackets;
            lock (_unacknowledgedPublishPackets)
            {
                unacknowledgedPublishPackets = _unacknowledgedPublishPackets.ToList();
                _unacknowledgedPublishPackets.Clear();
            }

            _packetBus.Clear();

            foreach (var publishPacket in unacknowledgedPublishPackets)
            {
                var result = EnqueueDataPacketCore(new MqttPacketBusItem(publishPacket), publishPacket, true, out var overwritten);
                if (overwritten != null)
                {
                    (overwrittenPackets ??= new List<MqttPacketBusItem>()).Add(overwritten);
                }
                if (_eventContainer.SessionApplicationMessagesInvalidatedEvent.HasHandlers)
                {
                    // Record actual overflow decisions, not a tracking difference that could misclassify a concurrent ACK.
                    if (result == EnqueueDataPacketResult.Dropped) (invalidated ??= new List<MqttPublishPacket>()).Add(publishPacket);
                    if (overwritten != null) (invalidated ??= new List<MqttPublishPacket>()).Add((MqttPublishPacket)overwritten.Packet);
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
        var eventArgs = new SessionApplicationMessagesInvalidatedEventArgs(Id, Items, packets.AsReadOnly(), reason);
        _ = _clientSessionsManager.NotifyApplicationMessagesInvalidatedAsync(eventArgs);
    }

    public void RemoveSubscribedTopic(string topic)
    {
        _subscribedTopics?.Remove(topic);
    }

    public Task<SubscribeResult> Subscribe(MqttSubscribePacket subscribePacket, CancellationToken cancellationToken)
    {
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
        return _subscriptionsManager.Unsubscribe(unsubscribePacket, cancellationToken);
    }
}
