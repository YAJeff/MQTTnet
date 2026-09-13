// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections;
using MQTTnet.Packets;
using MQTTnet.Internal;
using MQTTnet.Server.Internal;

namespace MQTTnet.Server;

/// <summary>An exclusive, generation-bound preparation scope. Commit is atomic and irreversible, and invokes no callbacks.</summary>
public sealed class MqttSessionRecoveryLease : IDisposable
{
    readonly MqttSession _session;
    readonly CancellationTokenSource _cancellation;
    readonly CancellationToken _token;
    readonly object _cancellationGate = new();
    bool _cancellationDisposed;
    internal readonly List<MqttSessionApplicationMessage> StagedMessages = new();
    internal readonly IReadOnlyList<MqttPublishPacket> OriginalNeverSent;
    internal bool Committed;
    internal bool Finished;
    internal int NotificationsFinalized;
    internal readonly TaskCompletionSource OwnerExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal IReadOnlyList<MqttPacketBusItem> ReclaimedItems = Array.Empty<MqttPacketBusItem>();

    internal MqttSessionRecoveryLease(MqttSession session, long generation, IReadOnlyList<MqttPublishPacket> originalNeverSent,
        IReadOnlyList<MqttSessionApplicationMessage> neverSent, IReadOnlyList<MqttOutgoingTransactionSnapshot> started, CancellationToken cancellationToken)
    {
        _session = session;
        ConnectionGeneration = generation;
        SessionItems = session.Items;
        OriginalNeverSent = originalNeverSent;
        NeverSentMessages = neverSent;
        StartedTransactions = started;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _token = _cancellation.Token;
    }

    public IDictionary SessionItems { get; }
    public long ConnectionGeneration { get; }
    public IReadOnlyList<MqttSessionApplicationMessage> NeverSentMessages { get; }
    public IReadOnlyList<MqttOutgoingTransactionSnapshot> StartedTransactions { get; }
    public CancellationToken CancellationToken => _token;
    public bool IsCommitted => Committed;

    /// <summary>Copies and stages one first-transmission message without assigning an identifier or exposing data to the wire.</summary>
    public bool TryStageApplicationMessage(MqttApplicationMessage message, object enqueueState) => _session.TryStageRecovery(this, message, enqueueState);

    /// <summary>Atomically installs all staged messages and returns isolated packet/ID/token records. No external callback runs in this method.</summary>
    /// <remarks>After success, later handler failure closes that connection but retains this committed state. Publish successor tickets before returning from the exclusive recovery handler.</remarks>
    public IReadOnlyList<MqttSessionApplicationMessage> Commit() => _session.CommitRecovery(this);

    /// <summary>Fails preparation before commit. It never rolls back a committed batch or changes a successor generation.</summary>
    public void Abort() => _session.AbortRecovery(this);
    public void Dispose() => Abort();

    internal void Cancel()
    {
        // Cancellation callbacks are not executed inline under native/global locks.
        Task task;
        lock (_cancellationGate)
        {
            if (_cancellationDisposed) return;
            task = _cancellation.CancelAsync();
        }
        _ = task.ContinueWith(failed => { _ = failed.Exception; }, System.Threading.CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    internal void ReleaseCancellationRegistration()
    {
        lock (_cancellationGate)
        {
            if (_cancellationDisposed) return;
            _cancellation.Dispose();
            _cancellationDisposed = true;
        }
    }
}
