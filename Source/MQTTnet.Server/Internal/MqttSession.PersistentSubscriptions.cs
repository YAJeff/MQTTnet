// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using MQTTnet.Packets;
using MQTTnet.Protocol;

namespace MQTTnet.Server.Internal;

public sealed partial class MqttSession
{
    Dictionary<string, MqttPersistedSubscription> _durableSubscriptions = new(StringComparer.Ordinal);
    long _durableSubscriptionRevision;

    internal Task<SubscribeResult> SubscribeForConnectionAsync(MqttSubscribePacket packet, long generation, CancellationToken cancellationToken)
        => SubscribeForConnectionAsync(packet, generation, new MqttSubscriptionRequestSnapshot(Guid.Empty, packet), cancellationToken);

    internal async Task<SubscribeResult> SubscribeForConnectionAsync(MqttSubscribePacket packet, long generation, MqttSubscriptionRequestSnapshot request, CancellationToken cancellationToken)
    {
        if (!HasDurablePersistence) return await _subscriptionsManager.Subscribe(packet, request, cancellationToken).ConfigureAwait(false);
        var result = new SubscribeResult(packet.TopicFilters.Count);
        var accepted = new List<MqttPersistedSubscription>();
        var decisions = new List<MqttSubscriptionMutationResult>();
        var inputIndex = 0;
        foreach (var filter in packet.TopicFilters)
        {
            var inputTopic = filter.Topic;
            var args = new InterceptingSubscriptionEventArgs(Id, UserName, new MqttSessionStatus(this), filter, packet.UserProperties, cancellationToken)
                { Request = request, RequestFilterIndex = inputIndex };
            args.Response.ReasonCode = filter.Topic.StartsWith("$share/", StringComparison.Ordinal)
                ? MqttSubscribeReasonCode.SharedSubscriptionsNotSupported : (MqttSubscribeReasonCode)filter.QualityOfServiceLevel;
            // Extensions may implement a rejected-by-default filter themselves with ProcessSubscription=false.
            await _eventContainer.InterceptingSubscriptionEvent.InvokeAsync(args).ConfigureAwait(false);
            result.ReasonCodes.Add(args.Response.ReasonCode);
            result.UserProperties = args.UserProperties;
            result.ReasonString = args.ReasonString;
            result.CloseConnection |= args.CloseConnection;
            MqttPersistedSubscription acceptedSubscription = null;
            if (args.ProcessSubscription && (int)args.Response.ReasonCode <= 2)
            {
                if (string.IsNullOrEmpty(args.TopicFilter.Topic) || args.TopicFilter.Topic.StartsWith("$share/", StringComparison.Ordinal))
                    throw new InvalidOperationException("A native durable subscription requires a supported effective filter.");
                ValidateDurableTopic(args.TopicFilter.Topic, true);
                acceptedSubscription = new MqttPersistedSubscription(args.TopicFilter.Topic, (MqttQualityOfServiceLevel)args.Response.ReasonCode,
                    args.TopicFilter.NoLocal, args.TopicFilter.RetainAsPublished, args.TopicFilter.RetainHandling, packet.SubscriptionIdentifier);
                accepted.Add(acceptedSubscription);
            }
            decisions.Add(new MqttSubscriptionMutationResult(inputIndex++, inputTopic, args.TopicFilter.Topic, (byte)args.Response.ReasonCode, acceptedSubscription != null, acceptedSubscription));
        }
        var newlyAdded = new HashSet<string>(StringComparer.Ordinal);
        using (await _persistenceGate.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            Dictionary<string, MqttPersistedSubscription> updated;
            lock (_unacknowledgedPublishPackets)
            {
                ValidateDurableOwner(generation);
                updated = new Dictionary<string, MqttPersistedSubscription>(_durableSubscriptions, StringComparer.Ordinal);
                foreach (var subscription in accepted)
                {
                    if (!updated.ContainsKey(subscription.Topic)) newlyAdded.Add(subscription.Topic);
                    updated[subscription.Topic] = subscription;
                }
            }
            if (result.ReasonCodes.Any(reason => (int)reason <= 2)) await CommitDurableSubscriptionsAsync(updated, generation, request, decisions, cancellationToken).ConfigureAwait(false);
        }
        if (!IsCurrentConnection(generation)) throw new InvalidOperationException("The subscribing connection was superseded.");
        foreach (var subscription in accepted)
        {
            var filter = new MqttTopicFilter { Topic = subscription.Topic, QualityOfServiceLevel = subscription.QualityOfServiceLevel,
                NoLocal = subscription.NoLocal, RetainAsPublished = subscription.RetainAsPublished, RetainHandling = subscription.RetainHandling };
            if (_eventContainer.ClientSubscribedTopicEvent.HasHandlers)
                await _eventContainer.ClientSubscribedTopicEvent.InvokeAsync(new ClientSubscribedTopicEventArgs(Id, UserName, filter, Items)).ConfigureAwait(false);
        }
        // Import does not enter this path. Retained replay belongs only to a newly committed wire subscription.
        var retained = await _retainedMessagesManagerForPersistence.GetMessages().ConfigureAwait(false);
        foreach (var message in retained)
        {
            var matches = accepted.Where(subscription => subscription.RetainHandling != MqttRetainHandling.DoNotSendOnSubscribe &&
                (subscription.RetainHandling != MqttRetainHandling.SendAtSubscribeIfNewSubscriptionOnly || newlyAdded.Contains(subscription.Topic)) &&
                MqttTopicFilterComparer.Compare(message.Topic, subscription.Topic) == MqttTopicFilterCompareResult.IsMatch).ToArray();
            if (matches.Length == 0) continue;
            (result.RetainedMessages ??= new List<MqttRetainedMessageMatch>()).Add(new MqttRetainedMessageMatch(message,
                (MqttQualityOfServiceLevel)Math.Min((int)message.QualityOfServiceLevel, matches.Max(subscription => (int)subscription.QualityOfServiceLevel)))
            {
                SubscriptionIdentifiers = matches.Select(subscription => subscription.SubscriptionIdentifier).Where(identifier => identifier > 0).Distinct().ToList()
            });
        }
        return result;
    }

    internal Task<UnsubscribeResult> UnsubscribeForConnectionAsync(MqttUnsubscribePacket packet, long generation, CancellationToken cancellationToken)
        => UnsubscribeForConnectionAsync(packet, generation, new MqttSubscriptionRequestSnapshot(Guid.Empty, packet), cancellationToken);

    internal async Task<UnsubscribeResult> UnsubscribeForConnectionAsync(MqttUnsubscribePacket packet, long generation, MqttSubscriptionRequestSnapshot request, CancellationToken cancellationToken)
    {
        if (!HasDurablePersistence) return await _subscriptionsManager.Unsubscribe(packet, request, cancellationToken).ConfigureAwait(false);
        var result = new UnsubscribeResult();
        var removed = new List<string>();
        var decisions = new List<MqttSubscriptionMutationResult>();
        var inputIndex = 0;
        foreach (var topic in packet.TopicFilters)
        {
            var args = new InterceptingUnsubscriptionEventArgs(Id, UserName, Items, topic, packet.UserProperties, cancellationToken)
                { Request = request, RequestFilterIndex = inputIndex };
            lock (_unacknowledgedPublishPackets) args.Response.ReasonCode = _durableSubscriptions.ContainsKey(topic) ? MqttUnsubscribeReasonCode.Success : MqttUnsubscribeReasonCode.NoSubscriptionExisted;
            await _eventContainer.InterceptingUnsubscriptionEvent.InvokeAsync(args).ConfigureAwait(false);
            result.ReasonCodes.Add(args.Response.ReasonCode);
            result.UserProperties = args.UserProperties;
            result.CloseConnection |= args.CloseConnection;
            if (args.ProcessUnsubscription && args.Response.ReasonCode == MqttUnsubscribeReasonCode.Success) removed.Add(topic);
            decisions.Add(new MqttSubscriptionMutationResult(inputIndex++, topic, args.Topic, (byte)args.Response.ReasonCode,
                args.ProcessUnsubscription && args.Response.ReasonCode == MqttUnsubscribeReasonCode.Success, null));
        }
        using (await _persistenceGate.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            Dictionary<string, MqttPersistedSubscription> updated;
            lock (_unacknowledgedPublishPackets)
            {
                ValidateDurableOwner(generation);
                updated = new Dictionary<string, MqttPersistedSubscription>(_durableSubscriptions, StringComparer.Ordinal);
                foreach (var topic in removed) updated.Remove(topic);
            }
            if (result.ReasonCodes.Contains(MqttUnsubscribeReasonCode.Success)) await CommitDurableSubscriptionsAsync(updated, generation, request, decisions, cancellationToken).ConfigureAwait(false);
        }
        foreach (var topic in removed)
            if (_eventContainer.ClientUnsubscribedTopicEvent.HasHandlers)
                await _eventContainer.ClientUnsubscribedTopicEvent.InvokeAsync(new ClientUnsubscribedTopicEventArgs(Id, UserName, topic, Items)).ConfigureAwait(false);
        return result;
    }

    async Task CommitDurableSubscriptionsAsync(Dictionary<string, MqttPersistedSubscription> updated, long generation, MqttSubscriptionRequestSnapshot request,
        IReadOnlyList<MqttSubscriptionMutationResult> decisions, CancellationToken cancellationToken)
    {
        lock (_unacknowledgedPublishPackets)
        {
            var bytes = updated.Values.Sum(subscription => (long)DurableStringBytes(subscription.Topic));
            foreach (var record in _durableTransactions) bytes = checked(bytes + DurablePacketBytes(record.Key) + DurableStringBytes(record.Value.Handle));
            if (updated.Count > ushort.MaxValue || bytes > _serverOptions.MaxDurableSessionSnapshotBytes)
                throw new InvalidOperationException("The subscription update would exceed the durable snapshot limits.");
        }
        var transition = new MqttSubscriptionTransition(Id, _durableGeneration, _durableOwnerFence, Guid.NewGuid(), _durableSubscriptionRevision, updated.Values.ToList(), request, decisions);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_serverOptions.DefaultCommunicationTimeout);
        try
        {
            var result = await _serverOptions.SessionPersistence.CommitSubscriptionsAsync(transition, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            lock (_dataEnqueueLock)
            lock (_unacknowledgedPublishPackets)
            {
                ValidateDurableOwner(generation);
                if (result == null || result.TransitionId != transition.TransitionId || result.SessionGeneration != transition.SessionGeneration || result.OwnerFence != transition.OwnerFence ||
                    result.Revision != transition.ExpectedRevision + 1 || result.Status is not (MqttPersistenceCommitStatus.Applied or MqttPersistenceCommitStatus.AlreadyApplied))
                    throw new InvalidOperationException("The durable subscription change was not confirmed.");
                _subscriptionsManager.RestoreSubscriptions(transition.Subscriptions);
                _durableSubscriptions = updated;
                _durableSubscriptionRevision = result.Revision;
                _subscribedTopics = updated.Keys.ToHashSet(StringComparer.Ordinal);
            }
            _clientSessionsManager.RefreshRestoredSubscriptions(this, generation);
        }
        catch
        {
            lock (_unacknowledgedPublishPackets) { if (IsCurrentConnection(generation)) _durableBlocked = true; }
            throw;
        }
    }
}
