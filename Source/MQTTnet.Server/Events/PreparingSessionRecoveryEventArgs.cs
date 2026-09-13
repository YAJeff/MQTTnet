// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections;

namespace MQTTnet.Server;

public sealed class PreparingSessionRecoveryEventArgs : EventArgs
{
    readonly object _willOwnershipLock = new();
    bool _preparationClosed;
    bool _takeWillOwnership;
    public PreparingSessionRecoveryEventArgs(MqttSessionStatus session, MqttSessionRecoveryLease recovery, bool sessionPresent, bool cleanStart)
        : this(session, recovery, sessionPresent, cleanStart, session?.Items) { }

    public PreparingSessionRecoveryEventArgs(MqttSessionStatus session, MqttSessionRecoveryLease recovery, bool sessionPresent, bool cleanStart, IDictionary connectionAttemptItems)
        : this(session, recovery, sessionPresent, cleanStart, connectionAttemptItems, null) { }

    public PreparingSessionRecoveryEventArgs(MqttSessionStatus session, MqttSessionRecoveryLease recovery, bool sessionPresent, bool cleanStart, IDictionary connectionAttemptItems, MqttClientStatus connection)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        Recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        SessionPresent = sessionPresent;
        CleanStart = cleanStart;
        ConnectionAttemptItems = connectionAttemptItems ?? throw new ArgumentNullException(nameof(connectionAttemptItems));
        Connection = connection;
    }

    public MqttSessionStatus Session { get; }
    public MqttSessionRecoveryLease Recovery { get; }
    public bool SessionPresent { get; }
    public bool CleanStart { get; }
    /// <summary>The exact current validating-connection dictionary, separate from a reused session's Items.</summary>
    public IDictionary ConnectionAttemptItems { get; }
    /// <summary>The exact captured connection. Production recovery events always supply it; no client-ID lookup is required.</summary>
    public MqttClientStatus Connection { get; }
    public Guid ConnectionAttemptId => Connection?.ConnectionAttemptId ?? Guid.Empty;
    public CancellationToken CancellationToken => Recovery.CancellationToken;
    public bool HasWill => Connection?.HasWill ?? false;
    public MqttWillMessageSnapshot WillMessage => Connection?.WillMessage;

    /// <summary>Call only after the host has durably accepted this attempt's Will responsibility.
    /// Native suppression activates only if this preparation commits and completes successfully.
    /// The host owns publication, delay, expiry, disconnect suppression and fencing thereafter.</summary>
    public void TakeWillOwnership()
    {
        lock (_willOwnershipLock)
        {
            CancellationToken.ThrowIfCancellationRequested();
            if (_preparationClosed) throw new InvalidOperationException("Will preparation has already completed.");
            if (!HasWill) throw new InvalidOperationException("This connection attempt has no Will.");
            _takeWillOwnership = true;
        }
    }

    internal bool CloseWillPreparation()
    {
        lock (_willOwnershipLock)
        {
            _preparationClosed = true;
            return _takeWillOwnership;
        }
    }
}
