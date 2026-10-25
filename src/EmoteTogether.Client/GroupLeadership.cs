using EmoteTogether.Protocol;

namespace EmoteTogether.Client;

public sealed partial class GroupClient
{
    private const long LeadershipTimeoutMs = 120_000;
    private const long LeadershipRetryMs = 15_000;
    private readonly Dictionary<string, string?> leadershipConfirmedMembers = new(Names);
    private OutgoingLeadership? outgoingLeadership;
    private IncomingLeadership? incomingLeadership;
    private LeadershipChange? lastLeadershipChange;
    private long lastLeadershipAcknowledgement;

    public bool IsTransferringLeadership => outgoingLeadership is not null || incomingLeadership is not null;
    public string? PendingLeaderId => outgoingLeadership?.Change.NewLeader ?? incomingLeadership?.Change.NewLeader;

    public void TransferLeadership(string memberId)
    {
        if (!RequireLeader()) return;
        if (IsTransferringLeadership) { Status = "Передача лидерства уже выполняется."; return; }
        var target = Normalize(memberId);
        if (target is null || Same(target, identity) || !Members.Any(m => Same(m.Id, target)))
        { Status = "Выберите другого участника этой группы."; return; }
        if (PendingRunId is not null || activeRun is not null)
        { Status = "Завершите или отмените запуск перед передачей лидерства."; return; }
        if (Members.Any(m => !Same(m.Id, identity) && (!leadershipConfirmedMembers.TryGetValue(m.Id, out var invite) ||
                invite != membershipInvites.GetValueOrDefault(m.Id))))
        { Status = "Дождитесь подтверждения связи со всеми участниками группы."; return; }

        // Pending invitations belong to the current leader and must not survive a handoff.
        foreach (var (id, invite) in invitations.ToArray())
            if (!Send(invite.Target, new Packet { Type = Kind.InviteResult, GroupId = GroupId, InviteId = id,
                    Reason = "Лидер группы меняется. Попросите новое приглашение." })) return;
        invitations.Clear();
        var change = new LeadershipChange(GroupId!, NewId(), identity!, target, revision + 1, Members.ToArray(), now() + LeadershipTimeoutMs);
        outgoingLeadership = new OutgoingLeadership(change, revision, now() + LeadershipRetryMs);
        if (Send(target, LeadershipPacket(LeadershipKinds.Offer, change), true))
            Status = $"Передаём лидерство: {target}…";
    }

    // Called only after the main receiver has authenticated an existing member.
    private void ObserveLeadershipMember(string sender) => leadershipConfirmedMembers[sender] = membershipInvites.GetValueOrDefault(sender);

    private bool HandleLeadership(string sender, Packet packet, long receivedAt)
    {
        if (packet.Type is not (LeadershipKinds.Offer or LeadershipKinds.Ready or LeadershipKinds.Changed or LeadershipKinds.Ack or LeadershipKinds.Settled)) return false;
        if (GroupId is null || packet.GroupId != GroupId || !ValidId(packet.Nonce) || packet.LeaderId is not { } target ||
            Normalize(target) is null || !Members.Any(m => Same(m.Id, sender)) || !Members.Any(m => Same(m.Id, target))) return true;

        switch (packet.Type)
        {
            case LeadershipKinds.Offer:
                ReceiveLeadershipOffer(sender, packet, receivedAt);
                break;
            case LeadershipKinds.Ready:
                ReceiveLeadershipReady(sender, packet);
                break;
            case LeadershipKinds.Changed:
                ReceiveLeadershipCommit(sender, packet);
                break;
            case LeadershipKinds.Ack:
                ReceiveLeadershipAck(sender, packet);
                break;
            case LeadershipKinds.Settled:
                ReceiveLeadershipSettled(sender, packet);
                break;
        }
        return true;
    }

    private void ReceiveLeadershipOffer(string sender, Packet packet, long receivedAt)
    {
        if (!Same(sender, LeaderId) || !Same(packet.LeaderId, identity) || packet.Revision != revision + 1 ||
            PendingRunId is not null || activeRun is not null || outgoingLeadership is not null || !SameRoster(packet.Members, Members)) return;
        if (incomingLeadership is { } existing)
        {
            if (!Matches(existing.Change, sender, packet) || existing.Committed || now() >= existing.Change.ExpiresAt) return;
            if (now() - lastLeadershipAcknowledgement >= 4_000) AcknowledgeOffer(existing.Change);
            return;
        }
        var change = new LeadershipChange(GroupId!, packet.Nonce!, sender, identity!, packet.Revision,
            Members.ToArray(), now() + LeadershipTimeoutMs);
        incomingLeadership = new IncomingLeadership(change);
        lastLeaderSeen = receivedAt;
        Status = "Подтверждаем передачу лидерства…";
        AcknowledgeOffer(change);
    }

    private void AcknowledgeOffer(LeadershipChange change)
    {
        lastLeadershipAcknowledgement = now();
        Send(change.OldLeader, LeadershipPacket(LeadershipKinds.Ready, change, false), true);
    }

    private void ReceiveLeadershipReady(string sender, Packet packet)
    {
        if (outgoingLeadership is not { Phase: LeadershipPhase.Offer } operation ||
            !Same(sender, operation.Change.NewLeader) || !Matches(operation.Change, operation.Change.OldLeader, packet) ||
            !IsLeader || revision != operation.BaseRevision || now() >= operation.Change.ExpiresAt ||
            PendingRunId is not null || activeRun is not null || !SameRoster(operation.Change.Roster, Members)) return;

        operation.Phase = LeadershipPhase.Commit;
        operation.Acknowledged.Add(identity!);
        var commitPacket = LeadershipPacket(LeadershipKinds.Changed, operation.Change);
        operation.NextRetry = now() + Math.Max(LeadershipRetryMs,
            transport.EstimatePriorityBroadcastMs(commitPacket, operation.Change.Roster.Length - 1) + LeadershipRetryMs);
        foreach (var member in operation.Change.Roster)
            if (!Same(member.Id, identity) && !Send(member.Id, commitPacket, true)) return;
        if (GroupId == operation.Change.Group) ApplyLeadership(operation.Change, preserveOutgoing: true);
    }

    private void ReceiveLeadershipCommit(string sender, Packet packet)
    {
        if (lastLeadershipChange is { } previous && Matches(previous, sender, packet) && revision == previous.Revision && Same(LeaderId, previous.NewLeader))
        {
            if (now() - lastLeadershipAcknowledgement >= 4_000)
                AcknowledgeLeadership(previous, incomingLeadership is null);
            return;
        }
        if (!Same(sender, LeaderId) || packet.Revision != revision + 1 || !SameRoster(packet.Members, Members) ||
            PendingRunId is not null || activeRun is not null || outgoingLeadership is not null) return;
        if (Same(packet.LeaderId, identity) && (incomingLeadership is not { Committed: false } proposal ||
            !Matches(proposal.Change, sender, packet) || now() >= proposal.Change.ExpiresAt)) return;

        var change = new LeadershipChange(GroupId!, packet.Nonce!, sender, packet.LeaderId!, packet.Revision,
            Members.ToArray(), now() + LeadershipTimeoutMs);
        incomingLeadership = new IncomingLeadership(change) { Committed = true };
        ApplyLeadership(change, preserveOutgoing: false);
        AcknowledgeLeadership(change, false);
    }

    private void ApplyLeadership(LeadershipChange change, bool preserveOutgoing)
    {
        if (!preserveOutgoing) transport.ForgetGroup(change.Group);
        CompleteLocalRun();
        activeRun = null;
        LeaderId = change.NewLeader;
        revision = change.Revision;
        Members = change.Roster.ToArray();
        synchronization.Reset();
        pings.Clear();
        nextPing = 0;
        lastLeaderSeen = now();
        memberSeen.Clear();
        membershipInvites.Clear();
        invitations.Clear();
        leadershipConfirmedMembers.Clear();
        foreach (var member in Members)
        {
            memberSeen[member.Id] = now();
            // This frozen roster was confirmed by the previous leader before transfer.
            leadershipConfirmedMembers[member.Id] = null;
        }
        lastLeadershipChange = change;
        Status = "Лидер меняется; ждём подтверждение всей группы…";
    }

    private void AcknowledgeLeadership(LeadershipChange change, bool settled)
    {
        lastLeadershipAcknowledgement = now();
        Send(change.OldLeader, LeadershipPacket(LeadershipKinds.Ack, change, false) with { Accepted = settled }, true);
    }

    private void ReceiveLeadershipAck(string sender, Packet packet)
    {
        if (outgoingLeadership is not { } operation || operation.Phase == LeadershipPhase.Offer ||
            !Matches(operation.Change, operation.Change.OldLeader, packet) || !operation.Change.Roster.Any(m => Same(m.Id, sender)) ||
            !Same(LeaderId, operation.Change.NewLeader) || revision != operation.Change.Revision) return;
        var expectsSettled = operation.Phase == LeadershipPhase.Settle;
        if (packet.Accepted != expectsSettled) return;
        operation.Acknowledged.Add(sender);
        if (operation.Acknowledged.Count != operation.Change.Roster.Length) return;
        if (operation.Phase == LeadershipPhase.Commit)
        {
            operation.Phase = LeadershipPhase.Settle;
            operation.Acknowledged.Clear();
            operation.Acknowledged.Add(identity!);
            operation.Retries = 0;
            operation.NextRetry = now() + LeadershipRetryMs;
            foreach (var member in operation.Change.Roster)
                if (!Same(member.Id, identity) && !Send(member.Id, LeadershipPacket(LeadershipKinds.Settled, operation.Change, false), true)) return;
        }
        else
        {
            outgoingLeadership = null;
            Status = $"Лидерство передано: {operation.Change.NewLeader}.";
        }
    }

    private void ReceiveLeadershipSettled(string sender, Packet packet)
    {
        if (lastLeadershipChange is not { } change || !Matches(change, sender, packet) ||
            revision != change.Revision || !Same(LeaderId, change.NewLeader) || now() >= change.ExpiresAt) return;
        incomingLeadership = null;
        Status = IsLeader ? "Вы стали лидером эмоциональной группы." : $"Лидер группы: {change.NewLeader}.";
        AcknowledgeLeadership(change, true);
    }

    private void PollLeadership(long time)
    {
        if (incomingLeadership is { } incoming)
        {
            var expectedRevision = incoming.Committed ? incoming.Change.Revision : incoming.Change.Revision - 1;
            if (GroupId != incoming.Change.Group || revision != expectedRevision || !SameRoster(incoming.Change.Roster, Members))
                incomingLeadership = null;
            else if (time >= incoming.Change.ExpiresAt)
            {
                if (incoming.Committed) ClearGroup("Передача лидерства не завершена. Создайте группу заново.");
                else { incomingLeadership = null; Status = "Передача лидерства не подтверждена; прежний лидер сохранён."; }
            }
        }
        if (outgoingLeadership is not { } operation) return;
        var expected = operation.Phase == LeadershipPhase.Offer ? operation.BaseRevision : operation.Change.Revision;
        if (GroupId != operation.Change.Group || revision != expected || !SameRoster(operation.Change.Roster, Members))
        {
            outgoingLeadership = null;
            Status = "Состав группы изменился; передача лидерства остановлена.";
            return;
        }
        if (time >= operation.Change.ExpiresAt)
        {
            if (operation.Phase == LeadershipPhase.Offer)
            { outgoingLeadership = null; Status = "Новый лидер не ответил. Лидерство осталось у вас."; }
            else ClearGroup("Не все участники подтвердили нового лидера. Группа закрыта локально.");
            return;
        }
        if (time < operation.NextRetry || operation.Retries >= 2) return;
        operation.Retries++;
        operation.NextRetry = time + LeadershipRetryMs;
        if (operation.Phase == LeadershipPhase.Offer)
            Send(operation.Change.NewLeader, LeadershipPacket(LeadershipKinds.Offer, operation.Change), true);
        else
        {
            var kind = operation.Phase == LeadershipPhase.Commit ? LeadershipKinds.Changed : LeadershipKinds.Settled;
            foreach (var member in operation.Change.Roster)
                if (!Same(member.Id, identity) && !operation.Acknowledged.Contains(member.Id) &&
                    !Send(member.Id, LeadershipPacket(kind, operation.Change, operation.Phase == LeadershipPhase.Commit), true)) return;
        }
    }

    private void ClearLeadership()
    {
        outgoingLeadership = null;
        incomingLeadership = null;
        lastLeadershipChange = null;
        leadershipConfirmedMembers.Clear();
        lastLeadershipAcknowledgement = 0;
    }

    private static Packet LeadershipPacket(string kind, LeadershipChange change, bool includeRoster = true) => new()
    {
        Type = kind, GroupId = change.Group, Nonce = change.Id, LeaderId = change.NewLeader,
        FromName = change.OldLeader, Revision = change.Revision, Members = includeRoster ? change.Roster : null,
    };
    private static bool Matches(LeadershipChange change, string sender, Packet packet) =>
        Same(sender, change.OldLeader) && packet.GroupId == change.Group && packet.Nonce == change.Id &&
        packet.Revision == change.Revision && Same(packet.LeaderId, change.NewLeader) && Same(packet.FromName, change.OldLeader);

    private static bool SameRoster(Member[]? first, Member[] second) => first is not null && first.Length == second.Length &&
        first.Length is > 0 and <= MaxMembers && first.All(m => m is not null && Same(m.Id, m.Name)) &&
        first.Select(m => m.Id).Distinct(Names).Count() == first.Length && first.All(m => second.Any(s => Same(s.Id, m.Id)));

    private enum LeadershipPhase { Offer, Commit, Settle }
    private sealed record LeadershipChange(string Group, string Id, string OldLeader, string NewLeader, long Revision, Member[] Roster, long ExpiresAt);
    private sealed class IncomingLeadership(LeadershipChange change)
    {
        public LeadershipChange Change { get; } = change;
        public bool Committed { get; set; }
    }
    private sealed class OutgoingLeadership(LeadershipChange change, long baseRevision, long nextRetry)
    {
        public LeadershipChange Change { get; } = change;
        public long BaseRevision { get; } = baseRevision;
        public long NextRetry { get; set; } = nextRetry;
        public LeadershipPhase Phase { get; set; }
        public HashSet<string> Acknowledged { get; } = new(Names);
        public int Retries { get; set; }
    }
}
