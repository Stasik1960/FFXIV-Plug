using System.Security.Cryptography;
using System.Text;
using EmoteTogether.Protocol;

namespace EmoteTogether.Client;

public sealed record DepartureNotice(string Recipient, Packet Packet);

public sealed partial class GroupClient
{
    public const long PresenceIntervalMs = 12_000;
    private const long RecoveryDiscoveryMs = 10_000;
    private const long RecoveryLifetimeMs = 120_000;
    private readonly Dictionary<string, long> pulseQueuedAt = new(Names);
    private int clockRequests;
    private long clockBurstDeadline;
    private Recovery? recovery;
    private RecoveryDelivery? recoveryDelivery;
    private readonly HashSet<string> departureWaiting = new(Names);
    private readonly Dictionary<string, long> unconfirmedJoins = new(Names);
    private readonly Dictionary<string, long> recoveryPeers = new(Names);
    private readonly Dictionary<string, long> recoveryVotes = new(Names);
    private readonly HashSet<string> recoveryReplies = new(Names);
    private string? recoveryContext;

    public bool IsRecoveringGroup => recovery is not null || departureWaiting.Count != 0;

    /// <summary>Capture before logout/unload. The adapter may drain these after the core stops polling.</summary>
    public IReadOnlyList<DepartureNotice> PrepareDeparture()
    {
        List<DepartureNotice> result = [];
        if (identity is null) return result;
        if (GroupId is { } group)
        {
            if (IsLeader)
            {
                var remaining = Members.Where(m => !Same(m.Id, identity)).OrderBy(m => m.Id, Names).ToArray();
                if (remaining.Length > 0)
                {
                    var notice = new Packet { Type = PresenceKinds.Departed, GroupId = group, LeaderId = remaining[0].Id,
                        Revision = revision + 1, Nonce = NewId() };
                    // The designated leader receives its notice first, before followers acknowledge it.
                    foreach (var member in remaining) result.Add(new DepartureNotice(member.Id, notice));
                }
            }
            else if (LeaderId is { } leader) result.Add(new DepartureNotice(leader, new Packet { Type = Kind.Leave, GroupId = group }));
        }
        else if (joining is { } pending)
            result.Add(new DepartureNotice(pending.Leader, new Packet { Type = Kind.Leave, GroupId = pending.Group }));
        ClearGroup("Вы вышли из игры или отключили плагин.");
        return result;
    }

    private void BeginClockBurst()
    {
        if (IsLeader || GroupId is null) return;
        clockRequests = 0;
        clockBurstDeadline = now() + 90_000;
        pings.Clear();
        // Small deterministic slots prevent every follower requesting both samples together.
        // Private-chat pacing remains in the transport; this only spreads optional clock work.
        var peers = Members.Where(m => !Same(m.Id, LeaderId)).OrderBy(m => m.Id, Names).ToArray();
        var slot = Array.FindIndex(peers, m => Same(m.Id, identity));
        nextPing = now() + Math.Max(0, slot) * 2_000;
    }

    private void PollQuietPresence(long time)
    {
        PollRecoveryDelivery(time);
        if (recovery is not null) { PollRecovery(time); return; }
        if (GroupId is null) return;
        if (IsLeader)
        {
            departureWaiting.RemoveWhere(id => !Members.Any(m => Same(m.Id, id)));
            foreach (var member in Members)
                if (!Same(member.Id, identity) && !unconfirmedJoins.ContainsKey(member.Id)) QueuePulse(member.Id, time);
            return;
        }
        if (LeaderId is not { } leader) return;
        QueuePulse(leader, time);
        foreach (var expired in pings.Where(p => time - p.Value >= 90_000).Select(p => p.Key).ToArray()) pings.Remove(expired);
        if (ClockReady || time >= clockBurstDeadline || clockRequests >= 4 || pings.Count >= 2 || time < nextPing) return;
        var nonce = NewId();
        pings[nonce] = time;
        clockRequests++;
        Send(leader, new Packet { Type = Kind.Ping, GroupId = GroupId, Nonce = nonce });
        nextPing = time + 2_000;
    }

    private void QueuePulse(string recipient, long time)
    {
        if (time - pulseQueuedAt.GetValueOrDefault(recipient) < PresenceIntervalMs) return;
        if (Send(recipient, new Packet { Type = PresenceKinds.Pulse, GroupId = GroupId }, true)) pulseQueuedAt[recipient] = time;
    }

    private bool HandlePresence(string sender, Packet packet)
    {
        if (packet.Type == PresenceKinds.RecoveryAck)
        {
            if (recoveryDelivery is { } delivery && packet.GroupId == delivery.OldGroup && packet.Nonce == delivery.Token &&
                packet.NewGroupId == GroupId && Members.Any(m => Same(m.Id, sender)))
            {
                delivery.Acknowledged.Add(sender);
                memberSeen[sender] = now();
                unconfirmedJoins.Remove(sender);
                departureWaiting.Remove(sender);
                ObserveLeadershipMember(sender);
            }
            return true;
        }
        if (packet.Type == PresenceKinds.Departed)
        {
            if (GroupId is null || packet.GroupId != GroupId || !Same(sender, LeaderId) || packet.Revision != revision + 1 || !ValidId(packet.Nonce)) return true;
            var remaining = Members.Where(m => !Same(m.Id, sender)).OrderBy(m => m.Id, Names).ToArray();
            if (remaining.Length == 0 || !Same(packet.LeaderId, remaining[0].Id)) return true;
            var group = GroupId;
            ApplyRecoveredMembership(group, remaining[0].Id, remaining, packet.Revision, "Лидер вышел; группа продолжает работу.");
            if (IsLeader)
                foreach (var member in Members.Where(m => !Same(m.Id, identity))) departureWaiting.Add(member.Id);
            else Send(LeaderId!, new Packet { Type = PresenceKinds.Pulse, GroupId = GroupId }, true);
            return true;
        }
        if (packet.Type is not (PresenceKinds.RecoveryProbe or PresenceKinds.RecoveryVote or PresenceKinds.RecoveryCommit)) return false;
        if (recovery is null)
        {
            if (GroupId is null || packet.GroupId != GroupId || IsLeader || !Members.Any(m => Same(m.Id, sender)) || Same(sender, LeaderId)) return true;
            var expectedToken = RecoveryId(GroupId + "|" + LeaderId + "|" + revision);
            if (packet.Nonce != expectedToken) return true;
            if (recoveryContext != expectedToken)
            {
                recoveryPeers.Clear(); recoveryVotes.Clear(); recoveryReplies.Clear(); recoveryContext = expectedToken;
            }
            // Replying proves only this member is online; it does not declare the leader gone.
            recoveryPeers[sender] = now();
            if (packet.Type == PresenceKinds.RecoveryProbe && !packet.Accepted && recoveryReplies.Add(sender))
                Send(sender, new Packet { Type = PresenceKinds.RecoveryProbe, GroupId = GroupId, Nonce = expectedToken,
                    Revision = revision, Accepted = true }, true);
            if (packet.Type == PresenceKinds.RecoveryVote && Same(packet.LeaderId, identity)) recoveryVotes[sender] = now();
            if (timeSinceLeader() < PeerLifetimeMs) return true;
            BeginRecovery();
        }
        if (recovery is not { } state || packet.GroupId != state.OldGroup || packet.Nonce != state.Token ||
            !state.Roster.Any(m => Same(m.Id, sender))) return true;
        if (packet.Type == PresenceKinds.RecoveryProbe)
        {
            state.Responsive.Add(sender);
            if (!packet.Accepted && state.Replied.Add(sender))
                Send(sender, RecoveryPacket(PresenceKinds.RecoveryProbe, state) with { Accepted = true }, true);
        }
        else if (packet.Type == PresenceKinds.RecoveryVote)
        {
            state.Responsive.Add(sender);
            if (Same(packet.LeaderId, identity)) state.Votes.Add(sender);
            TryFinishRecovery(state);
        }
        else
        {
            state.Responsive.Add(sender);
            ReceiveRecoveryCommit(sender, packet, state);
        }
        return true;

        long timeSinceLeader() => now() - lastLeaderSeen;
    }

    private void BeginRecovery()
    {
        if (GroupId is null || LeaderId is null || identity is null || IsLeader || recovery is not null) return;
        var roster = Members.Where(m => !Same(m.Id, LeaderId)).OrderBy(m => m.Id, Names).ToArray();
        if (!roster.Any(m => Same(m.Id, identity))) { ClearGroup("Группа недоступна."); return; }
        var oldGroup = GroupId;
        var token = RecoveryId(oldGroup + "|" + LeaderId + "|" + revision);
        transport.ForgetGroup(oldGroup);
        ClearLeadership();
        if (PendingRunId is not null) ClearRun("Лидер не отвечает; запуск отменён.");
        activeRun = null;
        pings.Clear();
        clockBurstDeadline = 0;
        var state = new Recovery(oldGroup, token, revision, roster, now() + RecoveryDiscoveryMs, now() + RecoveryLifetimeMs);
        recovery = state;
        state.Responsive.Add(identity);
        foreach (var peer in recoveryPeers.Where(p => now() - p.Value <= PeerLifetimeMs))
            if (roster.Any(m => Same(m.Id, peer.Key))) state.Responsive.Add(peer.Key);
        foreach (var vote in recoveryVotes.Where(p => now() - p.Value <= PeerLifetimeMs))
            if (roster.Any(m => Same(m.Id, vote.Key))) state.Votes.Add(vote.Key);
        Status = "Лидер не отвечает. Восстанавливаем группу…";
        foreach (var member in roster)
            if (!Same(member.Id, identity) && !Send(member.Id, RecoveryPacket(PresenceKinds.RecoveryProbe, state), true)) return;
        if (roster.Length == 1) { state.VotedFor = identity; state.Votes.Add(identity); TryFinishRecovery(state); }
    }

    private void PollRecovery(long time)
    {
        if (recovery is not { } state) return;
        if (time >= state.ExpiresAt) { ClearGroup("Недостаточно подтверждений для восстановления группы. Создайте её заново."); return; }
        if (state.VotedFor is null && time >= state.VoteAt)
        {
            state.VotedFor = state.Responsive.OrderBy(name => name, Names).First();
            if (Same(state.VotedFor, identity)) state.Votes.Add(identity!);
            else Send(state.VotedFor, RecoveryPacket(PresenceKinds.RecoveryVote, state) with { LeaderId = state.VotedFor }, true);
            TryFinishRecovery(state);
            if (recovery == state && state.PendingCommits.TryGetValue(state.VotedFor, out var early))
                ReceiveRecoveryCommit(state.VotedFor, early, state);
        }
    }

    private void TryFinishRecovery(Recovery state)
    {
        if (recovery != state || !Same(state.VotedFor, identity) || state.Votes.Count < state.Roster.Length / 2 + 1) return;
        var roster = state.Roster.Where(m => state.Responsive.Contains(m.Id)).ToArray();
        var newGroup = RecoveryId(state.Token + "|" + identity);
        var commit = RecoveryPacket(PresenceKinds.RecoveryCommit, state) with
        {
            NewGroupId = newGroup, LeaderId = identity, Revision = state.Revision + 1, Members = roster,
        };
        // Once a quorum is known, unsent discovery probes are obsolete. Let the result reach
        // every participant before background work resumes.
        transport.ForgetGroup(state.OldGroup);
        var admissionDeadline = now() + Math.Min(InviteLifetimeMs,
            PeerLifetimeMs + (long)transport.EstimatePriorityBroadcastMs(commit, Math.Max(0, roster.Length - 1)));
        foreach (var member in roster)
            if (!Same(member.Id, identity)) Send(member.Id, commit, true);
        var delivery = new RecoveryDelivery(state.OldGroup, state.Token, commit, roster, now() + 30_000);
        ApplyRecoveredMembership(newGroup, identity!, roster, state.Revision + 1, "Группа восстановлена. Вы стали лидером.", false);
        recoveryDelivery = delivery;
        foreach (var member in roster.Where(m => !Same(m.Id, identity)))
        {
            departureWaiting.Add(member.Id);
            // A fresh epoch is a new admission. The participant starts its normal 30 s
            // lease after acknowledging the commit, which may still be in the paced queue.
            unconfirmedJoins[member.Id] = admissionDeadline;
        }
    }

    private void ReceiveRecoveryCommit(string sender, Packet packet, Recovery state)
    {
        // Leader heartbeats reach members at different times. A quorum can finish while a
        // later member is still inside its own discovery window; preserve its commit until
        // this member has independently chosen and voted for that same candidate.
        if (state.VotedFor is null) { state.PendingCommits[sender] = packet; return; }
        if (!Same(state.VotedFor, sender) || !Same(packet.LeaderId, sender) || packet.Revision != state.Revision + 1 ||
            packet.NewGroupId != RecoveryId(state.Token + "|" + sender) || packet.Members is not { Length: > 0 and <= MaxMembers } roster ||
            roster.Select(m => m.Id).Distinct(Names).Count() != roster.Length ||
            !roster.Any(m => Same(m.Id, identity)) || !roster.Any(m => Same(m.Id, sender)) ||
            roster.Any(m => !state.Roster.Any(old => Same(old.Id, m.Id)) || !Same(m.Id, m.Name))) return;
        var oldGroup = state.OldGroup;
        var token = state.Token;
        ApplyRecoveredMembership(packet.NewGroupId!, sender, roster, packet.Revision, "Группа восстановлена с новым лидером.");
        Send(sender, new Packet { Type = PresenceKinds.RecoveryAck, GroupId = oldGroup, Nonce = token, NewGroupId = GroupId }, true);
        Send(sender, new Packet { Type = PresenceKinds.Pulse, GroupId = GroupId }, true);
    }

    private void ApplyRecoveredMembership(string group, string leader, Member[] roster, long newRevision, string reason, bool purge = true)
    {
        if (purge && GroupId is { } oldGroup) transport.ForgetGroup(oldGroup);
        ClearLeadership();
        if (PendingRunId is not null) ClearRun(reason);
        activeRun = null;
        CompleteLocalRun();
        GroupId = group;
        LeaderId = leader;
        Members = roster.ToArray();
        revision = newRevision;
        invitations.Clear(); membershipInvites.Clear(); memberSeen.Clear(); unconfirmedJoins.Clear();
        foreach (var member in Members)
        {
            memberSeen[member.Id] = now();
            leadershipConfirmedMembers[member.Id] = null;
        }
        recovery = null;
        recoveryDelivery = null;
        departureWaiting.Clear();
        synchronization.Reset(); pings.Clear(); pulseQueuedAt.Clear();
        lastLeaderSeen = now();
        BeginClockBurst();
        Status = reason;
    }

    private void PollRecoveryDelivery(long time)
    {
        if (recoveryDelivery is not { } delivery) return;
        if (delivery.Roster.Where(m => !Same(m.Id, identity)).All(m => delivery.Acknowledged.Contains(m.Id))) { recoveryDelivery = null; return; }
        if (time < delivery.NextRetry || delivery.Retries >= 2) return;
        delivery.Retries++;
        delivery.NextRetry = time + 30_000;
        foreach (var member in delivery.Roster)
            if (!Same(member.Id, identity) && !delivery.Acknowledged.Contains(member.Id)) Send(member.Id, delivery.Packet, true);
    }

    private void ClearPresence()
    {
        recovery = null; recoveryDelivery = null; departureWaiting.Clear(); pulseQueuedAt.Clear(); unconfirmedJoins.Clear();
        recoveryPeers.Clear(); recoveryVotes.Clear(); recoveryReplies.Clear(); recoveryContext = null;
        clockRequests = 0; clockBurstDeadline = 0;
    }
    private static string RecoveryId(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32].ToLowerInvariant();
    private static Packet RecoveryPacket(string type, Recovery state) => new()
    { Type = type, GroupId = state.OldGroup, Nonce = state.Token, Revision = state.Revision };
    private sealed class Recovery(string oldGroup, string token, long revision, Member[] roster, long voteAt, long expiresAt)
    {
        public string OldGroup { get; } = oldGroup;
        public string Token { get; } = token;
        public long Revision { get; } = revision;
        public Member[] Roster { get; } = roster;
        public long VoteAt { get; } = voteAt;
        public long ExpiresAt { get; } = expiresAt;
        public string? VotedFor { get; set; }
        public HashSet<string> Responsive { get; } = new(Names);
        public HashSet<string> Replied { get; } = new(Names);
        public HashSet<string> Votes { get; } = new(Names);
        public Dictionary<string, Packet> PendingCommits { get; } = new(Names);
    }
    private sealed class RecoveryDelivery(string oldGroup, string token, Packet packet, Member[] roster, long nextRetry)
    {
        public string OldGroup { get; } = oldGroup;
        public string Token { get; } = token;
        public Packet Packet { get; } = packet;
        public Member[] Roster { get; } = roster;
        public long NextRetry { get; set; } = nextRetry;
        public int Retries { get; set; }
        public HashSet<string> Acknowledged { get; } = new(Names);
    }
}
