// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using MQTTnet.Packets;
using MQTTnet.Protocol;

namespace MQTTnet.Server;

/// <summary>Immutable CONNECT Will image. Buffer and user-property getters return independent copies.</summary>
public sealed class MqttWillMessageSnapshot
{
    readonly byte[] _payload;
    readonly byte[] _correlationData;
    readonly MqttUserProperty[] _userProperties;

    internal MqttWillMessageSnapshot(MqttConnectPacket packet)
    {
        Topic = packet.WillTopic;
        _payload = packet.WillMessage?.ToArray() ?? Array.Empty<byte>();
        QualityOfServiceLevel = packet.WillQoS;
        Retain = packet.WillRetain;
        DelayInterval = packet.WillDelayInterval;
        MessageExpiryInterval = packet.WillMessageExpiryInterval;
        PayloadFormatIndicator = packet.WillPayloadFormatIndicator;
        ContentType = packet.WillContentType;
        ResponseTopic = packet.WillResponseTopic;
        _correlationData = packet.WillCorrelationData?.ToArray();
        _userProperties = packet.WillUserProperties?.Select(p => new MqttUserProperty(p.Name, p.ValueBuffer.ToArray().AsMemory())).ToArray();
    }

    public string Topic { get; }
    public byte[] Payload => _payload.ToArray();
    public MqttQualityOfServiceLevel QualityOfServiceLevel { get; }
    public bool Retain { get; }
    public uint DelayInterval { get; }
    public uint MessageExpiryInterval { get; }
    public MqttPayloadFormatIndicator PayloadFormatIndicator { get; }
    public string ContentType { get; }
    public string ResponseTopic { get; }
    public byte[] CorrelationData => _correlationData?.ToArray();
    public IReadOnlyList<MqttUserProperty> UserProperties => _userProperties?.Select(p => new MqttUserProperty(p.Name, p.ValueBuffer.ToArray().AsMemory())).ToList().AsReadOnly();
}
