// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections;
using MQTTnet.Packets;

namespace MQTTnet.Server;

public sealed class QueueMessageOverwrittenEventArgs : EventArgs
{
    public QueueMessageOverwrittenEventArgs(string receiverClientId, MqttPacket packet)
        : this(receiverClientId, packet, null)
    {
    }

    public QueueMessageOverwrittenEventArgs(string receiverClientId, MqttPacket packet, IDictionary receiverSessionItems)
    {
        ReceiverClientId = receiverClientId ?? throw new ArgumentNullException(nameof(receiverClientId));
        Packet = packet ?? throw new ArgumentNullException(nameof(packet));
        ReceiverSessionItems = receiverSessionItems;
    }

    public MqttPacket Packet { get; }

    public string ReceiverClientId { get; }

    /// <summary>Gets the items of the session whose queue lost the packet. Null for legacy constructors.</summary>
    public IDictionary ReceiverSessionItems { get; }
}
