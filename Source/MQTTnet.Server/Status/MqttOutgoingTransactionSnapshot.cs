// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using MQTTnet.Packets;
using MQTTnet.Server.Internal;

namespace MQTTnet.Server;

public enum MqttOutgoingTransactionPhase { AwaitPubAck, AwaitPubRec, AwaitPubComp }

public sealed class MqttOutgoingTransactionSnapshot
{
    readonly MqttPublishPacket _publishPacket;

    public MqttOutgoingTransactionSnapshot(MqttPublishPacket publishPacket, MqttOutgoingTransactionPhase phase, long sendSequence, object enqueueState)
    {
        ArgumentNullException.ThrowIfNull(publishPacket);
        _publishPacket = MqttPublishPacketSnapshot.Clone(publishPacket);
        Phase = phase;
        SendSequence = sendSequence;
        EnqueueState = enqueueState;
    }

    public ushort PacketIdentifier => _publishPacket.PacketIdentifier;
    /// <summary>Returns an independent copy; mutating it does not change native transaction state.</summary>
    public MqttPublishPacket PublishPacket => MqttPublishPacketSnapshot.Clone(_publishPacket);
    public MqttOutgoingTransactionPhase Phase { get; }
    public long SendSequence { get; }
    public object EnqueueState { get; }
}
