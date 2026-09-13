// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using MQTTnet.Packets;

namespace MQTTnet.Server.Internal;

static class MqttPublishPacketSnapshot
{
    public static MqttPublishPacket Clone(MqttPublishPacket packet)
    {
        var copy = new MqttPublishPacket();
        CopyFields(packet, copy);
        copy.Payload = new ReadOnlySequence<byte>(packet.Payload.ToArray());
        copy.CorrelationData = packet.CorrelationData?.ToArray();
        copy.SubscriptionIdentifiers = packet.SubscriptionIdentifiers?.ToList();
        copy.UserProperties = packet.UserProperties?.Select(property => new MqttUserProperty(property.Name, new ReadOnlyMemory<byte>(property.ValueBuffer.ToArray()))).ToList();
        return copy;
    }

    // Source is an ownership-isolated snapshot. No user callback retains its buffers.
    public static void CopyFields(MqttPublishPacket source, MqttPublishPacket destination)
    {
        destination.PacketIdentifier = source.PacketIdentifier;
        destination.ContentType = source.ContentType;
        destination.CorrelationData = source.CorrelationData;
        destination.Dup = source.Dup;
        destination.MessageExpiryInterval = source.MessageExpiryInterval;
        destination.PayloadFormatIndicator = source.PayloadFormatIndicator;
        destination.Payload = source.Payload;
        destination.QualityOfServiceLevel = source.QualityOfServiceLevel;
        destination.ResponseTopic = source.ResponseTopic;
        destination.Retain = source.Retain;
        destination.SubscriptionIdentifiers = source.SubscriptionIdentifiers;
        destination.Topic = source.Topic;
        destination.TopicAlias = source.TopicAlias;
        destination.UserProperties = source.UserProperties;
    }
}
