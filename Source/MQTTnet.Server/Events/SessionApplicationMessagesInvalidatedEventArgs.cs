// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections;
using MQTTnet.Packets;

namespace MQTTnet.Server;

public enum MqttSessionApplicationMessagesInvalidationReason
{
    RecoveryOverflow,
    SessionDisposed,
    ApplicationMessageReclaimed,
    DurableTransactionRetired,
    DurableGenerationReplaced
}

public sealed class SessionApplicationMessagesInvalidatedEventArgs : EventArgs
{
    public SessionApplicationMessagesInvalidatedEventArgs(
        string receiverClientId, IDictionary receiverSessionItems, IReadOnlyList<MqttPublishPacket> publishPackets,
        MqttSessionApplicationMessagesInvalidationReason reason)
        : this(receiverClientId, receiverSessionItems,
            (publishPackets ?? throw new ArgumentNullException(nameof(publishPackets))).Select(packet => new MqttSessionApplicationMessage(packet, null)).ToList().AsReadOnly(), reason)
    {
    }

    public SessionApplicationMessagesInvalidatedEventArgs(
        string receiverClientId, IDictionary receiverSessionItems, IReadOnlyList<MqttSessionApplicationMessage> messages,
        MqttSessionApplicationMessagesInvalidationReason reason)
    {
        ReceiverClientId = receiverClientId ?? throw new ArgumentNullException(nameof(receiverClientId));
        ReceiverSessionItems = receiverSessionItems ?? throw new ArgumentNullException(nameof(receiverSessionItems));
        Messages = messages ?? throw new ArgumentNullException(nameof(messages));
        PublishPackets = messages.Select(message => message.PublishPacket).ToList().AsReadOnly();
        Reason = reason;
    }

    public string ReceiverClientId { get; }

    public IDictionary ReceiverSessionItems { get; }

    /// <summary>Gets the exact QoS1/2 publish objects whose native ownership was invalidated, not an identifier-only lookup.</summary>
    public IReadOnlyList<MqttPublishPacket> PublishPackets { get; }

    /// <summary>Gets each original publish object together with its original admission state, even when the same receipt was admitted again.</summary>
    public IReadOnlyList<MqttSessionApplicationMessage> Messages { get; }

    /// <summary>SessionDisposed invalidates the entire session, including an empty packet list. RecoveryOverflow invalidates only the listed packets.</summary>
    public MqttSessionApplicationMessagesInvalidationReason Reason { get; }
}
