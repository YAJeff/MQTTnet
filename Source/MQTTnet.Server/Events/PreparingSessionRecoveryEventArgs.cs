// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections;

namespace MQTTnet.Server;

public sealed class PreparingSessionRecoveryEventArgs : EventArgs
{
    public PreparingSessionRecoveryEventArgs(MqttSessionStatus session, MqttSessionRecoveryLease recovery, bool sessionPresent, bool cleanStart)
        : this(session, recovery, sessionPresent, cleanStart, session?.Items) { }

    public PreparingSessionRecoveryEventArgs(MqttSessionStatus session, MqttSessionRecoveryLease recovery, bool sessionPresent, bool cleanStart, IDictionary connectionAttemptItems)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        Recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        SessionPresent = sessionPresent;
        CleanStart = cleanStart;
        ConnectionAttemptItems = connectionAttemptItems ?? throw new ArgumentNullException(nameof(connectionAttemptItems));
    }

    public MqttSessionStatus Session { get; }
    public MqttSessionRecoveryLease Recovery { get; }
    public bool SessionPresent { get; }
    public bool CleanStart { get; }
    /// <summary>The exact current validating-connection dictionary, separate from a reused session's Items.</summary>
    public IDictionary ConnectionAttemptItems { get; }
    public CancellationToken CancellationToken => Recovery.CancellationToken;
}
