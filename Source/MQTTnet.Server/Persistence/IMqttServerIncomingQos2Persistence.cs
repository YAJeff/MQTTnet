// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using MQTTnet.Packets;
using MQTTnet.Server.Internal;

namespace MQTTnet.Server;

/// <summary>Optional incoming QoS2 journal gates. Every operation must fence the exact owner.
/// Resolve atomically retains one latest GLOBAL operation sequence/proposal/proof per exact owner,
/// including across terminal retirement and packet-ID reuse. Same-sequence exact retries
/// return the original incarnation; lower sequences are Superseded, gaps/conflicting retries Conflict.
/// A freshly fenced connection starts sequence one. Never advance an uncertain operation.
/// Retire compares the exact incarnation/revision and cannot remove a reused slot.
/// Use a global issued-incarnation counter plus live exceptions rather than lifetime-message
/// tombstones or lifetime packet-ID slots. Unknown retired PUBREL needs no historical success proof.
/// MaxTransactions bounds live transactions only. Constant owner operation metadata is charged
/// to MaxStateBytes. Completed envelopes should release their payload (null PublishPacket).
/// Providers enforce these limits, including their bounded operation metadata.</summary>
public interface IMqttServerIncomingQos2Persistence
{
    Task<MqttIncomingQos2PersistenceResult> ResolvePublishAsync(MqttIncomingQos2ResolveRequest request, CancellationToken cancellationToken);
    Task<MqttIncomingQos2PersistenceResult> ReadAsync(MqttIncomingQos2ReadRequest request, CancellationToken cancellationToken);
    Task<MqttIncomingQos2PersistenceResult> CommitAsync(MqttIncomingQos2Transition transition, CancellationToken cancellationToken);
}

public enum MqttIncomingQos2Phase { AwaitAcceptance, AwaitPubRel, Completed }
public enum MqttIncomingQos2PersistenceStatus { Confirmed, Fenced, Conflict, Uncertain, Superseded, NotFound }

/// <summary>Attempt ID is fencing context; it is not part of the stable publication journal identity.</summary>
public sealed record MqttIncomingQos2Owner(string ClientId, Guid SessionGeneration, long OwnerFence, Guid ConnectionAttemptId);
public sealed record MqttIncomingQos2Identity(Guid SessionGeneration, ushort PacketIdentifier, Guid Incarnation);

/// <summary>The canonical first-received envelope and original absolute expiry survive retries.
/// DUP and TopicAlias are normalized to false/zero. Full topic is required. Retransmissions must
/// match all other fields except a finite MessageExpiryInterval may remain equal or decrease (zero
/// is immediate expiry, not unlimited). HasMessageExpiryInterval distinguishes absence from zero;
/// the original absolute deadline must never be recomputed or extended.</summary>
public sealed class MqttIncomingQos2Transaction
{
    readonly MqttPublishPacket _publishPacket;
    public MqttIncomingQos2Transaction(MqttIncomingQos2Identity identity, long revision, MqttIncomingQos2Phase phase,
        MqttPublishPacket publishPacket, DateTime receivedAtUtc, DateTime? expiresAtUtc)
    {
        Identity = identity;
        Revision = revision;
        Phase = phase;
        _publishPacket = publishPacket == null ? null : MqttPublishPacketSnapshot.Clone(publishPacket);
        ReceivedAtUtc = receivedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }
    public MqttIncomingQos2Identity Identity { get; }
    public long Revision { get; }
    public MqttIncomingQos2Phase Phase { get; }
    public MqttPublishPacket PublishPacket => _publishPacket == null ? null : MqttPublishPacketSnapshot.Clone(_publishPacket);
    public DateTime ReceivedAtUtc { get; }
    public DateTime? ExpiresAtUtc { get; }
}

public sealed class MqttIncomingQos2ResolveRequest
{
    readonly MqttPublishPacket _publishPacket;
    public MqttIncomingQos2ResolveRequest(MqttIncomingQos2Owner owner, Guid operationId, long resolveRequestSequence,
        Guid proposedIncarnation, MqttPublishPacket publishPacket, DateTime receivedAtUtc, DateTime? expiresAtUtc,
        int maxTransactions, long maxStateBytes)
    {
        Owner = owner; OperationId = operationId; ResolveRequestSequence = resolveRequestSequence;
        ProposedIncarnation = proposedIncarnation; _publishPacket = MqttPublishPacketSnapshot.Clone(publishPacket);
        ReceivedAtUtc = receivedAtUtc; ExpiresAtUtc = expiresAtUtc; MaxTransactions = maxTransactions; MaxStateBytes = maxStateBytes;
    }
    public MqttIncomingQos2Owner Owner { get; }
    public Guid OperationId { get; }
    public long ResolveRequestSequence { get; }
    public Guid ProposedIncarnation { get; }
    public MqttPublishPacket PublishPacket => MqttPublishPacketSnapshot.Clone(_publishPacket);
    public DateTime ReceivedAtUtc { get; }
    public DateTime? ExpiresAtUtc { get; }
    public int MaxTransactions { get; }
    public long MaxStateBytes { get; }
}

public sealed record MqttIncomingQos2ReadRequest(MqttIncomingQos2Owner Owner, Guid OperationId, ushort PacketIdentifier);
public sealed record MqttIncomingQos2Transition(MqttIncomingQos2Owner Owner, Guid OperationId, MqttIncomingQos2Identity Identity,
    long ExpectedRevision, MqttIncomingQos2Phase PreviousPhase, MqttIncomingQos2Phase NextPhase);

/// <summary>Confirmed results echo the exact request owner/operation/sequence and canonical transaction.
/// Read/Commit use sequence zero. Confirmed Commit returns expected revision + 1 and requested phase.
/// Read may return NotFound with null transaction. Other non-confirmed results never allow wire progress.</summary>
public sealed record MqttIncomingQos2PersistenceResult(MqttIncomingQos2Owner Owner, Guid OperationId, long ResolveRequestSequence,
    MqttIncomingQos2PersistenceStatus Status, MqttIncomingQos2Transaction Transaction);
