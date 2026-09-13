// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using MQTTnet.Packets;
using MQTTnet.Protocol;

namespace MQTTnet.Server;

/// <summary>Original wire intent captured before inbound packet interception. Packet identifier reuse does not reuse RequestId.</summary>
public sealed class MqttSubscriptionRequestSnapshot
{
    readonly IReadOnlyList<MqttUserProperty> _userProperties;

    internal MqttSubscriptionRequestSnapshot(Guid connectionAttemptId, MqttPacket packet)
    {
        ConnectionAttemptId = connectionAttemptId;
        RequestId = Guid.NewGuid();
        if (packet is MqttSubscribePacket subscribe)
        {
            PacketIdentifier = subscribe.PacketIdentifier;
            SubscriptionIdentifier = subscribe.SubscriptionIdentifier;
            Filters = subscribe.TopicFilters.Select(filter => new MqttPersistedSubscription(filter.Topic, filter.QualityOfServiceLevel,
                filter.NoLocal, filter.RetainAsPublished, filter.RetainHandling, subscribe.SubscriptionIdentifier)).ToList().AsReadOnly();
            _userProperties = CopyProperties(subscribe.UserProperties);
        }
        else if (packet is MqttUnsubscribePacket unsubscribe)
        {
            IsUnsubscribe = true;
            PacketIdentifier = unsubscribe.PacketIdentifier;
            Filters = unsubscribe.TopicFilters.Select(topic => new MqttPersistedSubscription(topic, MqttQualityOfServiceLevel.AtMostOnce,
                false, false, MqttRetainHandling.SendAtSubscribe, 0)).ToList().AsReadOnly();
            _userProperties = CopyProperties(unsubscribe.UserProperties);
        }
        else throw new ArgumentException("A subscription packet is required.", nameof(packet));
    }
    public Guid ConnectionAttemptId { get; }
    public Guid RequestId { get; }
    public bool IsUnsubscribe { get; }
    public ushort PacketIdentifier { get; }
    public uint SubscriptionIdentifier { get; }
    public IReadOnlyList<MqttPersistedSubscription> Filters { get; }
    public IReadOnlyList<MqttUserProperty> UserProperties => CopyProperties(_userProperties);
    public object RequestState { get; internal set; }

    static IReadOnlyList<MqttUserProperty> CopyProperties(IEnumerable<MqttUserProperty> properties) =>
        properties?.Select(property => new MqttUserProperty(property.Name, new ReadOnlyMemory<byte>(property.ValueBuffer.ToArray()))).ToList().AsReadOnly()
        ?? (IReadOnlyList<MqttUserProperty>)Array.Empty<MqttUserProperty>();
}

/// <summary>One actual post-inbound-interception input and its final subscription-hook decision, including denials and host-owned handling.</summary>
public sealed record MqttSubscriptionMutationResult(int InputIndex, string InputTopic, string EffectiveTopic, byte ReasonCode,
    bool ProcessNatively, MqttPersistedSubscription AcceptedSubscription);
