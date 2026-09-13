// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections;

namespace MQTTnet.Server;

public sealed class InterceptingClientApplicationMessageEnqueueEventArgs : EventArgs
{
    public InterceptingClientApplicationMessageEnqueueEventArgs(string senderClientId, string receiverClientId, MqttApplicationMessage applicationMessage)
        : this(senderClientId, receiverClientId, applicationMessage, null)
    {
    }

    public InterceptingClientApplicationMessageEnqueueEventArgs(
        string senderClientId, string receiverClientId, MqttApplicationMessage applicationMessage, IDictionary receiverSessionItems)
    {
        SenderClientId = senderClientId ?? throw new ArgumentNullException(nameof(senderClientId));
        ReceiverClientId = receiverClientId ?? throw new ArgumentNullException(nameof(receiverClientId));
        ApplicationMessage = applicationMessage ?? throw new ArgumentNullException(nameof(applicationMessage));
        ReceiverSessionItems = receiverSessionItems;
    }

    /// <summary>
    ///     Gets or sets whether the enqueue of the application message should be performed or not.
    ///     If set to _False_ the client will not receive the application message.
    /// </summary>
    public bool AcceptEnqueue { get; set; } = true;

    public MqttApplicationMessage ApplicationMessage { get; }

    /// <summary>
    ///     Indicates if the connection with the sender should be closed.
    /// </summary>
    public bool CloseSenderConnection { get; set; }

    public string ReceiverClientId { get; }

    /// <summary>Gets the items of the actual receiving session, captured before this callback. Null for legacy constructors.</summary>
    public IDictionary ReceiverSessionItems { get; }

    /// <summary>Gets or sets opaque state carried to this attempt's enqueue outcome. The server does not inspect this value.</summary>
    public object EnqueueState { get; set; }

    public string SenderClientId { get; }
}
