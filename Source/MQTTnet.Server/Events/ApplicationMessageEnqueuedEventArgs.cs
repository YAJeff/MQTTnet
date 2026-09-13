// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections;
using MQTTnet.Packets;

namespace MQTTnet.Server;

public sealed class ApplicationMessageEnqueuedEventArgs : EventArgs
{
    public ApplicationMessageEnqueuedEventArgs(string senderClientId, string receiverClientId, MqttApplicationMessage applicationMessage, bool isDropped)
        : this(senderClientId, receiverClientId, applicationMessage, isDropped, null, null, null)
    {
    }

    public ApplicationMessageEnqueuedEventArgs(
        string senderClientId, string receiverClientId, MqttApplicationMessage applicationMessage, bool isDropped,
        IDictionary receiverSessionItems, object enqueueState, Exception exception)
        : this(senderClientId, receiverClientId, applicationMessage, isDropped, receiverSessionItems, enqueueState, exception, null)
    {
    }

    public ApplicationMessageEnqueuedEventArgs(
        string senderClientId, string receiverClientId, MqttApplicationMessage applicationMessage, bool isDropped,
        IDictionary receiverSessionItems, object enqueueState, Exception exception, MqttPublishPacket publishPacket)
    {
        SenderClientId = senderClientId ?? throw new ArgumentNullException( nameof(senderClientId));
        ReceiverClientId = receiverClientId ?? throw new ArgumentNullException(nameof(receiverClientId));
        ApplicationMessage = applicationMessage ?? throw new ArgumentNullException(nameof(applicationMessage));
        IsDropped = isDropped;
        ReceiverSessionItems = receiverSessionItems;
        EnqueueState = enqueueState;
        Exception = exception;
        PublishPacket = publishPacket;
    }

    public string SenderClientId { get; }

    public string ReceiverClientId { get; }

    public bool IsDropped { get; }

    public MqttApplicationMessage ApplicationMessage { get; }

    /// <summary>Gets the actual receiving session's items. Persistent reconnects may reuse these items. Null for legacy constructors.</summary>
    public IDictionary ReceiverSessionItems { get; }

    /// <summary>Gets the opaque state from this particular interception attempt.</summary>
    public object EnqueueState { get; }

    /// <summary>Gets the exception that prevented admission, or null. A successful outcome describes past admission, not current queue membership.</summary>
    public Exception Exception { get; }

    /// <summary>Gets the original publish object, or null if interception prevented its creation. It may already be acknowledged or invalidated.</summary>
    public MqttPublishPacket PublishPacket { get; }
}
