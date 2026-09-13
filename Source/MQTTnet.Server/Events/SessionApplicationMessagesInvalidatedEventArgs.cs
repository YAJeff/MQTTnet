// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections;
using MQTTnet.Packets;

namespace MQTTnet.Server;

public enum MqttSessionApplicationMessagesInvalidationReason
{
    RecoveryOverflow,
    SessionDisposed
}

public sealed class SessionApplicationMessagesInvalidatedEventArgs : EventArgs
{
    public SessionApplicationMessagesInvalidatedEventArgs(
        string receiverClientId, IDictionary receiverSessionItems, IReadOnlyList<MqttPublishPacket> publishPackets,
        MqttSessionApplicationMessagesInvalidationReason reason)
    {
        ReceiverClientId = receiverClientId ?? throw new ArgumentNullException(nameof(receiverClientId));
        ReceiverSessionItems = receiverSessionItems ?? throw new ArgumentNullException(nameof(receiverSessionItems));
        PublishPackets = publishPackets ?? throw new ArgumentNullException(nameof(publishPackets));
        Reason = reason;
    }

    public string ReceiverClientId { get; }

    public IDictionary ReceiverSessionItems { get; }

    /// <summary>Gets the exact QoS1/2 publish objects whose native ownership was invalidated, not an identifier-only lookup.</summary>
    public IReadOnlyList<MqttPublishPacket> PublishPackets { get; }

    /// <summary>SessionDisposed invalidates the entire session, including an empty packet list. RecoveryOverflow invalidates only the listed packets.</summary>
    public MqttSessionApplicationMessagesInvalidationReason Reason { get; }
}
