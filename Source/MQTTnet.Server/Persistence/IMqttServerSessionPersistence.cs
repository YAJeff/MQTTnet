// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections;
using MQTTnet.Formatter;
using MQTTnet.Packets;
using MQTTnet.Server.Internal;
using MQTTnet.Protocol;

namespace MQTTnet.Server;

/// <summary>Optional fenced storage gates. Exceptions and unconfirmed results stop protocol progress.</summary>
public interface IMqttServerSessionPersistence
{
    /// <summary>Claims a new connection owner fence and returns its consistent snapshot. Clean start must atomically retire the prior generation before returning a new generation.</summary>
    Task<MqttPersistedSessionSnapshot> LoadAsync(MqttSessionPersistenceRequest request, CancellationToken cancellationToken);
    Task<MqttPersistenceCommitResult> CommitTransitionAsync(MqttOutgoingTransactionTransition transition, CancellationToken cancellationToken);
    Task<MqttPersistenceCommitResult> CommitSubscriptionsAsync(MqttSubscriptionTransition transition, CancellationToken cancellationToken);
    Task<MqttPersistenceCommitResult> CommitDisconnectedAsync(MqttSessionDisconnectedTransition transition, CancellationToken cancellationToken);
    /// <summary>Atomically retires precisely the supplied generation and owner fence. Local disposal is not durable deletion.</summary>
    Task<MqttPersistenceCommitResult> DeleteSessionAsync(MqttSessionPersistenceDeletion deletion, CancellationToken cancellationToken);
}

/// <summary>Implemented by an admission token to identify a durable delivery independently of packet identifiers.</summary>
public interface IMqttDurableDeliveryContext
{
    string DeliveryHandle { get; }
}

public enum MqttPersistenceCommitStatus { Applied, AlreadyApplied, Fenced, Conflict, Uncertain, AlreadyRetired }
public enum MqttTransactionDirection { Outgoing }

public sealed record MqttSessionPersistenceRequest(string ClientId, Guid ConnectionAttemptId, IDictionary ConnectionAttemptItems,
    bool CleanStart, MqttProtocolVersion ProtocolVersion, uint SessionExpiryInterval)
{
    /// <summary>The exact captured native session dictionary; distinct from this connection attempt's validation dictionary on reuse.</summary>
    public IDictionary NativeSessionItems { get; init; }
    /// <summary>Bounded local handles requiring authoritative presence or retirement resolution, including lost terminal replies.</summary>
    public IReadOnlyList<string> PendingDeliveryHandles { get; init; } = Array.Empty<string>();
}

public sealed record MqttPersistenceCommitResult(Guid TransitionId, Guid SessionGeneration, long OwnerFence, long Revision, MqttPersistenceCommitStatus Status, string DeliveryHandle);

public sealed record MqttSessionPersistenceDeletion(string ClientId, Guid SessionGeneration, long OwnerFence, Guid TransitionId);

public sealed record MqttSessionDisconnectedTransition(string ClientId, Guid SessionGeneration, long OwnerFence, Guid TransitionId,
    long ExpectedRevision, DateTime DisconnectedAtUtc, uint SessionExpiryInterval)
{
    public Guid ConnectionAttemptId { get; init; }
    public bool HasWill { get; init; }
    public bool IsWillExternallyOwned { get; init; }
    public MqttWillMessageSnapshot WillMessage { get; init; }
    public MqttWillDisposition WillDisposition { get; init; }
    public MqttDisconnectReasonCode? DisconnectReasonCode { get; init; }
}

/// <summary>Schedule respects Will delay or session end, including DISCONNECT 0x04.</summary>
public enum MqttWillDisposition { None, Suppress, Schedule }

public sealed record MqttPersistedSubscription(string Topic, MqttQualityOfServiceLevel QualityOfServiceLevel,
    bool NoLocal, bool RetainAsPublished, MqttRetainHandling RetainHandling, uint SubscriptionIdentifier);

public sealed class MqttSubscriptionTransition
{
    public MqttSubscriptionTransition(string clientId, Guid sessionGeneration, long ownerFence, Guid transitionId, long expectedRevision, IReadOnlyList<MqttPersistedSubscription> subscriptions,
        MqttSubscriptionRequestSnapshot request = null, IReadOnlyList<MqttSubscriptionMutationResult> results = null)
    {
        ClientId = clientId; SessionGeneration = sessionGeneration; OwnerFence = ownerFence; TransitionId = transitionId; ExpectedRevision = expectedRevision;
        Subscriptions = subscriptions.ToList().AsReadOnly();
        Request = request;
        Results = (results ?? Array.Empty<MqttSubscriptionMutationResult>()).ToList().AsReadOnly();
    }
    public string ClientId { get; }
    public Guid SessionGeneration { get; }
    public long OwnerFence { get; }
    public Guid TransitionId { get; }
    public long ExpectedRevision { get; }
    public IReadOnlyList<MqttPersistedSubscription> Subscriptions { get; }
    public MqttSubscriptionRequestSnapshot Request { get; }
    public IReadOnlyList<MqttSubscriptionMutationResult> Results { get; }
}

public sealed class MqttPersistedSessionSnapshot
{
    public MqttPersistedSessionSnapshot(int schemaVersion, Guid sessionGeneration, long ownerFence, long revision,
        bool sessionPresent, IReadOnlyList<MqttPersistedOutgoingTransaction> outgoingTransactions, IReadOnlyList<MqttPersistedSubscription> subscriptions,
        IReadOnlyList<string> retiredDeliveryHandles = null, bool usePersistence = true)
    {
        SchemaVersion = schemaVersion;
        SessionGeneration = sessionGeneration;
        OwnerFence = ownerFence;
        Revision = revision;
        SessionPresent = sessionPresent;
        OutgoingTransactions = (outgoingTransactions ?? throw new ArgumentNullException(nameof(outgoingTransactions))).ToList().AsReadOnly();
        Subscriptions = (subscriptions ?? throw new ArgumentNullException(nameof(subscriptions))).ToList().AsReadOnly();
        RetiredDeliveryHandles = (retiredDeliveryHandles ?? Array.Empty<string>()).ToList().AsReadOnly();
        UsePersistence = usePersistence;
    }
    public int SchemaVersion { get; }
    public Guid SessionGeneration { get; }
    public long OwnerFence { get; }
    public long Revision { get; }
    public bool SessionPresent { get; }
    public IReadOnlyList<MqttPersistedOutgoingTransaction> OutgoingTransactions { get; }
    public IReadOnlyList<MqttPersistedSubscription> Subscriptions { get; }
    /// <summary>Authoritatively retired members of the request's bounded PendingDeliveryHandles. No historical outcome is implied.</summary>
    public IReadOnlyList<string> RetiredDeliveryHandles { get; }
    /// <summary>False is allowed only for an authoritatively new, empty, nonpersistent connection; never for resumed state.</summary>
    public bool UsePersistence { get; }
}

public sealed class MqttPersistedOutgoingTransaction
{
    readonly MqttPublishPacket _packet;
    public MqttPersistedOutgoingTransaction(string deliveryHandle, long revision, Guid lastTransitionId,
        MqttOutgoingTransactionPhase phase, long sendSequence, MqttPublishPacket publishPacket, object enqueueState)
    {
        DeliveryHandle = deliveryHandle;
        Revision = revision;
        LastTransitionId = lastTransitionId;
        Phase = phase;
        SendSequence = sendSequence;
        _packet = MqttPublishPacketSnapshot.Clone(publishPacket ?? throw new ArgumentNullException(nameof(publishPacket)));
        EnqueueState = enqueueState;
    }
    public string DeliveryHandle { get; }
    public long Revision { get; }
    public Guid LastTransitionId { get; }
    public MqttOutgoingTransactionPhase Phase { get; }
    public long SendSequence { get; }
    public ushort PacketIdentifier => _packet.PacketIdentifier;
    public MqttPublishPacket PublishPacket => MqttPublishPacketSnapshot.Clone(_packet);
    public object EnqueueState { get; }
}

public sealed class MqttOutgoingTransactionTransition
{
    readonly MqttPublishPacket _packet;
    public MqttOutgoingTransactionTransition(string clientId, Guid sessionGeneration, long ownerFence, Guid transitionId,
        string deliveryHandle, long expectedRevision, MqttOutgoingTransactionPhase? previousPhase,
        MqttOutgoingTransactionPhase? nextPhase, long sendSequence, MqttPublishPacket publishPacket, byte? terminalReasonCode)
    {
        ClientId = clientId;
        SessionGeneration = sessionGeneration;
        OwnerFence = ownerFence;
        TransitionId = transitionId;
        DeliveryHandle = deliveryHandle;
        ExpectedRevision = expectedRevision;
        PreviousPhase = previousPhase;
        NextPhase = nextPhase;
        SendSequence = sendSequence;
        _packet = MqttPublishPacketSnapshot.Clone(publishPacket);
        _packet.TopicAlias = 0;
        TerminalReasonCode = terminalReasonCode;
    }
    public int SchemaVersion { get; } = 1;
    public MqttTransactionDirection Direction { get; } = MqttTransactionDirection.Outgoing;
    public string ClientId { get; }
    public Guid SessionGeneration { get; }
    public long OwnerFence { get; }
    public Guid TransitionId { get; }
    public string DeliveryHandle { get; }
    public long ExpectedRevision { get; }
    public MqttOutgoingTransactionPhase? PreviousPhase { get; }
    /// <summary>Null denotes terminal completion; the reason code distinguishes negative acknowledgements.</summary>
    public MqttOutgoingTransactionPhase? NextPhase { get; }
    public long SendSequence { get; }
    public MqttPublishPacket PublishPacket => MqttPublishPacketSnapshot.Clone(_packet);
    public byte? TerminalReasonCode { get; }
}
