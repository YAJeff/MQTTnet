// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections;
using MQTTnet.Packets;

namespace MQTTnet.Server;

public sealed class ClientAcknowledgedPublishPacketEventArgs : EventArgs
{
    public ClientAcknowledgedPublishPacketEventArgs(string clientId, string userName, IDictionary sessionItems, MqttPublishPacket publishPacket, MqttPacketWithIdentifier acknowledgePacket)
        : this(clientId, userName, sessionItems, publishPacket, acknowledgePacket, null)
    {
    }

    public ClientAcknowledgedPublishPacketEventArgs(string clientId, string userName, IDictionary sessionItems, MqttPublishPacket publishPacket, MqttPacketWithIdentifier acknowledgePacket, object enqueueState)
    {
        ClientId = clientId ?? throw new ArgumentNullException(nameof(clientId));
        UserName = userName;
        SessionItems = sessionItems ?? throw new ArgumentNullException(nameof(sessionItems));
        PublishPacket = publishPacket ?? throw new ArgumentNullException(nameof(publishPacket));
        AcknowledgePacket = acknowledgePacket ?? throw new ArgumentNullException(nameof(acknowledgePacket));
        EnqueueState = enqueueState;
    }

    /// <summary>
    ///     Gets the packet which was used for acknowledge. This can be a PubAck or PubComp packet.
    /// </summary>
    public MqttPacketWithIdentifier AcknowledgePacket { get; }

    /// <summary>
    ///     Gets the ID of the client which acknowledged a PUBLISH packet.
    /// </summary>
    public string ClientId { get; }

    /// <summary>
    /// Gets the user name of the client.
    /// </summary>
    public string UserName { get; }

    /// <summary>
    ///     Gets whether the PUBLISH packet is fully acknowledged. This is the case for PUBACK (QoS 1) and PUBCOMP (QoS 2.
    /// </summary>
    public bool IsCompleted => AcknowledgePacket is MqttPubAckPacket || AcknowledgePacket is MqttPubCompPacket ||
        (AcknowledgePacket is MqttPubRecPacket pubRec && (byte)pubRec.ReasonCode >= 0x80);

    /// <summary>
    ///     Gets the PUBLISH packet which was acknowledged.
    /// </summary>
    public MqttPublishPacket PublishPacket { get; }

    /// <summary>
    ///     Gets the session items which contain custom user data per session.
    /// </summary>
    public IDictionary SessionItems { get; }

    /// <summary>Gets the opaque context of the original publish admission, including after recovery.</summary>
    public object EnqueueState { get; }

    /// <summary>AlreadyRetired proves retirement of this exact durable delivery, but does not prove its historical terminal reason or transition.</summary>
    public MqttPersistenceCommitStatus? DurableCommitStatus { get; internal set; }
}
