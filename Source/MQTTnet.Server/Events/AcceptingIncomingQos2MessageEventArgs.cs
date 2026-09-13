// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections;

namespace MQTTnet.Server;

/// <summary>Exclusive durable acceptance owner. Success means an immutable publication journal
/// owns retryable onward work, not that recipients received it. Retry the same Identity idempotently.
/// Native dispatch is suppressed in this mode. The host must enforce Owner fencing on committed
/// writes; cancellation does not retract work already committed. Honour the original expiry deadline.</summary>
public sealed class AcceptingIncomingQos2MessageEventArgs : EventArgs
{
    public AcceptingIncomingQos2MessageEventArgs(MqttIncomingQos2Owner owner, MqttIncomingQos2Transaction transaction,
        MqttClientStatus connection, IDictionary sessionItems, IDictionary connectionAttemptItems, CancellationToken cancellationToken)
    {
        Owner = owner; Transaction = transaction; Connection = connection; SessionItems = sessionItems;
        ConnectionAttemptItems = connectionAttemptItems; CancellationToken = cancellationToken;
    }
    public MqttIncomingQos2Owner Owner { get; }
    public MqttIncomingQos2Transaction Transaction { get; }
    public MqttClientStatus Connection { get; }
    public IDictionary SessionItems { get; }
    public IDictionary ConnectionAttemptItems { get; }
    public CancellationToken CancellationToken { get; }
}
