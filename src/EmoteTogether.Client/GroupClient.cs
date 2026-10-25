using System.Collections.Concurrent;
using EmoteTogether.Protocol;

namespace EmoteTogether.Client;

/// <summary>A leader-authoritative group carried by game-authenticated private chat.</summary>
/// <remarks>All state changes and public events occur on the caller's thread. Call Poll once per frame.</remarks>
public sealed partial class GroupClient : IDisposable
{
    public const int MaxMembers = 8;
    public const long InviteLifetimeMs = 90_000;
    public const long PeerLifetimeMs = 180_000;
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;
    private readonly IChatTransport transport;
    private readonly Func<long> now;
    private readonly ClockSynchronizer synchronization = new();
    private readonly ConcurrentQueue<ChatEnvelope> received = new();
    private readonly Dictionary<string, OutgoingInvite> invitations = [];
    private readonly Dictionary<string, long> memberSeen = new(Names);
    private readonly Dictionary<string, string> membershipInvites = new(Names);
    private readonly Dictionary<string, long> pings = [];
    private readonly HashSet<string> seenRuns = [];
    private readonly Queue<string> recentRuns = [];
    private readonly HashSet<string> seenInvites = [];
    private readonly Queue<string> recentInvites = [];
    private string? identity;
    private Joining? joining;
    private long invitationDeadline;
    private long revision;
    private long nextPing;
    private long lastLeaderSeen;
    private long prepareReceivedAt;
    private ActiveRun? activeRun;
    private bool disposed;
    private bool localReady;
    private readonly string countdownProbeId = NewId();
    private bool resetting;

    public GroupClient(IChatTransport transport, Func<long>? nowMs = null)
    {
        this.transport = transport;
        var clock = new MonotonicClock();
        now = nowMs ?? (() => clock.UnixMilliseconds);
        transport.MessageReceived += OnTransportMessage;
    }

    public bool IsConnected => !disposed && identity is not null;
    public bool IsJoining => joining is not null;
    public bool IsLeader => identity is not null && Same(identity, LeaderId) && GroupId is not null;
    public string Status { get; private set; } = "Войдите в игру, чтобы создать группу.";
    public string? MemberId => identity;
    public string? GroupId { get; private set; }
    public string? LeaderId { get; private set; }
    public Member[] Members { get; private set; } = [];
    public Packet? Invitation { get; private set; }
    public string? PendingRunId { get; private set; }
    public uint PendingEmoteId { get; private set; }
    public long? ScheduledStartAtUnixMs { get; private set; }
    public long LeaderNowUnixMs => now() + (IsLeader ? 0 : (long)Math.Round(synchronization.OffsetMs));
    public bool ClockReady => IsLeader || (GroupId is not null && synchronization.IsReady(now()));
    public double RoundTripMs => IsLeader ? 0 : synchronization.RoundTripMs;
    public int RequestedCountdownSeconds { get; private set; } = 3;
    public int EffectiveCountdownSeconds { get; private set; } = 3;
    public int MinimumCountdownSeconds => Members.Length < 2 ? 1 : MinimumForPacket(new Packet
    {
        Type = Kind.Scheduled, GroupId = GroupId, Revision = revision,
        RunId = activeRun?.Id ?? countdownProbeId, EmoteId = activeRun?.EmoteId ?? ushort.MaxValue,
        StartAtUnixMs = now() + 60_000, CountdownSeconds = 60,
    });

    public event Action<Packet>? PrepareReceived;
    public event Action<Packet>? ScheduledReceived;
    public event Action<string>? Cancelled;
    public event Action? Disconnected;

    public void SetIdentity(string? value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var normalized = Normalize(value);
        if (Same(identity, normalized)) return;
        var wasAvailable = identity is not null;
        ClearGroup("Персонаж изменился; предыдущая группа закрыта локально.");
        identity = normalized;
        transport.Reset(identity);
        while (received.TryDequeue(out _)) { }
        seenRuns.Clear(); recentRuns.Clear(); seenInvites.Clear(); recentInvites.Clear();
        Status = identity is null ? "Войдите в игру для работы плагина." : "Готово. Создайте группу или примите приглашение.";
        if (wasAvailable) Disconnected?.Invoke();
    }

    public void CreateGroup()
    {
        if (!RequireIdentity()) return;
        if (GroupId is not null) { Status = "Вы уже состоите в группе."; return; }
        if (joining is not null) { Status = "Ожидаем подтверждение принятого приглашения."; return; }
        Invitation = null;
        GroupId = NewId();
        LeaderId = identity;
        Members = [new Member(identity!, identity!)];
        revision = 1;
        synchronization.Reset();
        memberSeen.Clear();
        memberSeen[identity!] = now();
        Status = "Эмоциональная группа создана. Пригласите игроков.";
    }

    public void Invite(string name)
    {
        if (!RequireLeader()) return;
        var target = Normalize(name);
        if (target is null) { Status = "Укажите имя в формате Character Name@HomeWorld."; return; }
        if (Members.Any(m => Same(m.Id, target))) { Status = "Этот игрок уже состоит в группе."; return; }
        if (Members.Length >= MaxMembers) { Status = "Группа заполнена (8 игроков)."; return; }
        if (invitations.Values.Any(i => Same(i.Target, target))) { Status = "Приглашение этому игроку уже отправлено."; return; }
        if (invitations.Count >= MaxMembers) { Status = "Слишком много ожидающих приглашений."; return; }
        var inviteId = NewId();
        invitations.Add(inviteId, new OutgoingInvite(target, now() + InviteLifetimeMs));
        if (Send(target, new Packet { Type = Kind.Invitation, GroupId = GroupId, InviteId = inviteId }))
            Status = $"Приглашение {target} отправлено личным сообщением (90 секунд).";
    }

    public void RespondInvite(bool accepted)
    {
        if (!RequireIdentity() || Invitation is not { InviteId: { } inviteId, GroupId: { } group, FromName: { } leader }) return;
        if (now() >= invitationDeadline) { Invitation = null; Status = "Время приглашения истекло."; return; }
        Invitation = null;
        if (accepted)
        {
            if (GroupId is not null || joining is not null) { Status = "Вы уже вступаете или состоите в группе."; return; }
            joining = new Joining(group, leader, inviteId, now() + 120_000);
        }
        if (Send(leader, new Packet { Type = Kind.RespondInvite, GroupId = group, InviteId = inviteId, Accepted = accepted }))
            Status = accepted ? "Приглашение принято. Ожидаем состав группы от лидера…" : "Приглашение отклонено.";
    }

    public void LeaveGroup()
    {
        if (GroupId is null)
        {
            if (joining is { } pending)
            {
                transport.ForgetGroup(pending.Group);
                Send(pending.Leader, new Packet { Type = Kind.Leave, GroupId = pending.Group });
                joining = null;
                Status = "Вступление в группу отменено локально.";
            }
            return;
        }
        // A final notification must not be overtaken later by an obsolete queued invitation or roster.
        transport.ForgetGroup(GroupId);
        if (IsLeader)
        {
            AbortRun("Лидер распустил группу.");
            Broadcast(new Packet { Type = Kind.GroupClosed, GroupId = GroupId, Reason = "Лидер распустил группу." }, true);
            foreach (var (id, invite) in invitations.ToArray())
                Send(invite.Target, new Packet { Type = Kind.InviteResult, GroupId = GroupId, InviteId = id, Reason = "Группа закрыта." });
        }
        else if (LeaderId is { } leader)
        {
            if (PendingRunId is { } run) transport.ForgetRun(run);
            Send(leader, new Packet { Type = Kind.Leave, GroupId = GroupId }, true);
        }
        ClearGroup("Вы вышли из эмоциональной группы.", false);
    }

    public void Start(uint emoteId, int countdownSeconds = 3)
    {
        if (!RequireLeader()) return;
        if (emoteId is 0 or > ushort.MaxValue) { Status = "Некорректная эмоция."; return; }
        if (countdownSeconds is < 1 or > 60) { Status = "Выберите задержку старта от 1 до 60 секунд."; return; }
        if (Members.Length < 2) { Status = "Пригласите хотя бы одного участника."; return; }
        if (activeRun is not null) { Status = "Предыдущий запуск ещё не завершён."; return; }
        if (transport.PendingFrames > 24) { Status = "Подождите, пока отправятся сообщения группы."; return; }
        var timeout = Math.Max(60_000, (transport.PendingFrames + Members.Length * 2L + 2) * transport.SendIntervalMs + 15_000);
        activeRun = new ActiveRun(NewId(), emoteId, Members.Select(m => m.Id).ToArray(), now() + timeout, countdownSeconds);
        var packet = new Packet { Type = Kind.Prepare, GroupId = GroupId, Revision = revision, RunId = activeRun.Id, EmoteId = emoteId, CountdownSeconds = countdownSeconds };
        if (!Broadcast(packet)) return;
        PrepareLocal(packet);
    }

    public void SetReady(string runId, bool ready, string? reason = null)
    {
        if (GroupId is null || runId != PendingRunId) return;
        localReady = ready;
        var packet = new Packet { Type = Kind.Ready, GroupId = GroupId, Revision = revision, RunId = runId,
            Ready = ready, Reason = ready ? null : CleanReason(reason) };
        if (IsLeader) ReceiveReady(identity!, packet);
        else if (LeaderId is { } leader) Send(leader, packet, !ready);
    }

    public void Cancel()
    {
        var run = PendingRunId ?? (IsLeader ? activeRun?.Id : null);
        if (run is null || GroupId is null) return;
        if (IsLeader) AbortRun("Лидер отменил запуск.");
        else if (LeaderId is { } leader)
        {
            transport.ForgetRun(run);
            Send(leader, new Packet { Type = Kind.Cancel, GroupId = GroupId, RunId = run }, true);
            ClearRun("Запуск отменён локально.");
        }
    }

    // Consuming the local native call must not erase the leader's independent coordination state.
    public void CompleteLocalRun()
    {
        localReady = false;
        PendingRunId = null;
        PendingEmoteId = 0;
        ScheduledStartAtUnixMs = null;
    }

    public void Poll()
    {
        if (disposed) return;
        try { transport.Poll(); }
        catch (Exception error) { TransportFailed(error); return; }
        for (var i = 0; i < 256 && received.TryDequeue(out var envelope); i++) Receive(envelope);
        if (!IsConnected) return;
        var time = now();
        PollLeadership(time);
        if (Invitation is not null && time >= invitationDeadline) { Invitation = null; Status = "Время приглашения истекло."; }
        if (joining is { } pending && time >= pending.Expires)
        {
            joining = null;
            Status = "Лидер не подтвердил вступление. Проверьте доступность /tell.";
        }
        if (IsLeader)
        {
            foreach (var (id, invite) in invitations.Where(i => i.Value.Expires <= time).ToArray())
            {
                invitations.Remove(id);
                Send(invite.Target, new Packet { Type = Kind.InviteResult, GroupId = GroupId, InviteId = id, Reason = "Время приглашения истекло." });
            }
            var lost = Members.Where(m => !Same(m.Id, identity) && time - memberSeen.GetValueOrDefault(m.Id) >= PeerLifetimeMs).Select(m => m.Id).ToArray();
            if (lost.Length > 0)
            {
                AbortRun("Один из участников не отвечает.");
                Members = Members.Where(m => !lost.Contains(m.Id, Names)).ToArray();
                foreach (var member in lost) { memberSeen.Remove(member); membershipInvites.Remove(member); }
                revision++;
                Snapshot();
            }
            if (activeRun is { } current)
            {
                if (current.StartAt == 0 && time >= current.ReadyDeadline) AbortRun("Не все участники подтвердили готовность вовремя.");
                else if (current.StartAt != 0 && time >= current.StartAt + 2_000) activeRun = null;
            }
        }
        else if (GroupId is not null && LeaderId is { } leader)
        {
            if (time - lastLeaderSeen >= PeerLifetimeMs)
            {
                ClearGroup("Лидер не отвечает. Группа закрыта локально.");
                return;
            }
            foreach (var expired in pings.Where(p => time - p.Value >= 90_000).Select(p => p.Key).ToArray()) pings.Remove(expired);
            if (time >= nextPing && pings.Count < 2)
            {
                var nonce = NewId();
                pings[nonce] = time;
                Send(leader, new Packet { Type = Kind.Ping, GroupId = GroupId, Nonce = nonce });
                nextPing = time + (ClockReady ? 30_000 : 2_000);
            }
        }
        if (PendingRunId is not null && !ClockReady && (localReady || ScheduledStartAtUnixMs != null)) Cancel();
        if (PendingRunId is not null && ScheduledStartAtUnixMs is null && time - prepareReceivedAt >= 90_000) Cancel();
    }

    private void OnTransportMessage(ChatEnvelope envelope)
    {
        if (!disposed && received.Count < 256) received.Enqueue(envelope);
    }

    private void Receive(ChatEnvelope envelope)
    {
        var packet = envelope.Packet;
        var sender = Normalize(envelope.Sender);
        if (!IsConnected || sender is null || Same(sender, identity) || packet.ProtocolVersion != Packet.Version) return;
        if (HandleLeadership(sender, packet, envelope.ReceivedAtUnixMs)) return;
        if (packet.Type == Kind.Invitation) { ReceiveInvitation(sender, packet); return; }
        if (packet.Type == Kind.RespondInvite) { ReceiveInviteResponse(sender, packet); return; }
        if (packet.Type == Kind.InviteResult) { ReceiveInviteResult(sender, packet); return; }
        if (packet.Type == Kind.Group) { ReceiveSnapshot(sender, packet); return; }
        if (packet.Type == Kind.GroupClosed)
        {
            var closesCurrent = GroupId is not null && packet.GroupId == GroupId && Same(sender, LeaderId);
            var closesJoining = joining is { } pending && packet.GroupId == pending.Group && Same(sender, pending.Leader);
            var closesInvitation = Invitation is { } invitation && packet.GroupId == invitation.GroupId && Same(sender, invitation.FromName);
            if (!IsLeader && (closesCurrent || closesJoining || closesInvitation)) ClearGroup(CleanReason(packet.Reason));
            return;
        }
        if (GroupId is null || packet.GroupId != GroupId) return;
        if (IsLeader)
        {
            if (!Members.Any(m => Same(m.Id, sender))) return;
            memberSeen[sender] = now();
            ObserveLeadershipMember(sender);
            switch (packet.Type)
            {
                case Kind.Ping:
                    if (ValidId(packet.Nonce) && packet.ClientSendTime > 0)
                        Send(sender, new Packet { Type = Kind.Pong, GroupId = GroupId, Nonce = packet.Nonce,
                            ClientSendTime = packet.ClientSendTime, ServerReceiveTime = envelope.ReceivedAtUnixMs });
                    break;
                case Kind.Ready: ReceiveReady(sender, packet); break;
                case Kind.Cancel:
                    if (activeRun is { } run && packet.RunId == run.Id) AbortRun($"{sender} отменил запуск.");
                    break;
                case Kind.Leave:
                case Kind.LeaveGroup:
                    AbortRun("Участник вышел из группы.");
                    Members = Members.Where(m => !Same(m.Id, sender)).ToArray();
                    memberSeen.Remove(sender);
                    membershipInvites.Remove(sender);
                    revision++;
                    Snapshot();
                    break;
            }
            return;
        }
        if (!Same(sender, LeaderId)) return;
        lastLeaderSeen = now();
        switch (packet.Type)
        {
            case Kind.Pong:
                if (packet.Nonce is { } nonce && pings.Remove(nonce))
                {
                    if (synchronization.Observe(packet.ClientSendTime, packet.ServerReceiveTime, packet.ServerSendTime, envelope.ReceivedAtUnixMs))
                        Status = ClockReady ? "Часы с лидером синхронизированы." : "Синхронизация времени с лидером…";
                }
                break;
            case Kind.Prepare:
                if (packet.Revision == revision && ValidId(packet.RunId) && packet.EmoteId is > 0 and <= ushort.MaxValue && PendingRunId is null)
                    PrepareLocal(packet);
                break;
            case Kind.Scheduled: ScheduleLocal(packet); break;
            case Kind.Cancelled:
                if (ValidId(packet.RunId))
                {
                    Remember(packet.RunId!, seenRuns, recentRuns);
                    if (packet.RunId == PendingRunId) ClearRun(CleanReason(packet.Reason));
                }
                break;
            case Kind.GroupClosed:
                ClearGroup(CleanReason(packet.Reason));
                break;
        }
    }

    private void ReceiveInvitation(string sender, Packet packet)
    {
        if (GroupId is not null || joining is not null || Invitation is not null || !ValidId(packet.GroupId) || !ValidId(packet.InviteId)) return;
        if (!Remember(packet.InviteId!, seenInvites, recentInvites)) return;
        Invitation = packet with { FromName = sender, LeaderId = sender };
        invitationDeadline = now() + InviteLifetimeMs;
        Status = $"{sender} приглашает вас в эмоциональную группу.";
    }

    private void ReceiveInviteResponse(string sender, Packet packet)
    {
        if (!IsLeader || packet.GroupId != GroupId || packet.InviteId is not { } id ||
            !invitations.TryGetValue(id, out var invite) || !Same(invite.Target, sender)) return;
        invitations.Remove(id);
        if (!packet.Accepted) { Status = $"{sender} отклонил приглашение."; return; }
        if (invite.Expires <= now() || Members.Length >= MaxMembers || Members.Any(m => Same(m.Id, sender)))
        {
            Send(sender, new Packet { Type = Kind.InviteResult, GroupId = GroupId, InviteId = id, Reason = "Приглашение истекло или группа заполнена." });
            return;
        }
        AbortRun("Состав группы изменился. Запустите эмоцию заново.");
        Members = [.. Members, new Member(sender, sender)];
        memberSeen[sender] = now();
        membershipInvites[sender] = id;
        revision++;
        Snapshot();
        Status = $"{sender} вступил в эмоциональную группу.";
    }

    private void ReceiveInviteResult(string sender, Packet packet)
    {
        if (Invitation is { } invitation && packet.InviteId == invitation.InviteId && Same(sender, invitation.FromName))
        {
            Invitation = null;
            Status = CleanReason(packet.Reason);
        }
        if (joining is { } pending && packet.GroupId == pending.Group && packet.InviteId == pending.Invite && Same(sender, pending.Leader) && !packet.Accepted)
        {
            joining = null;
            Status = CleanReason(packet.Reason);
        }
    }

    private void ReceiveSnapshot(string sender, Packet packet)
    {
        if (IsLeader || !ValidId(packet.GroupId) || !Same(packet.LeaderId, sender) || packet.Members is not { Length: > 0 and <= MaxMembers } roster) return;
        var acceptedJoin = joining is { } pending && pending.Expires > now() && packet.GroupId == pending.Group && packet.InviteId == pending.Invite && Same(sender, pending.Leader);
        if (!acceptedJoin && (GroupId is null || packet.GroupId != GroupId || !Same(sender, LeaderId))) return;
        if (!acceptedJoin && packet.Revision <= revision) return;
        if (packet.Revision <= 0 || roster.Any(m => Normalize(m.Id) is null || !Same(m.Id, m.Name)) ||
            roster.Select(m => m.Id).Distinct(Names).Count() != roster.Length ||
            !roster.Any(m => Same(m.Id, sender)) || !roster.Any(m => Same(m.Id, identity))) return;
        if (PendingRunId is not null) ClearRun("Состав группы изменился.");
        GroupId = packet.GroupId;
        LeaderId = sender;
        Members = roster;
        revision = packet.Revision;
        lastLeaderSeen = now();
        if (acceptedJoin)
        {
            joining = null;
            Invitation = null;
            synchronization.Reset();
            pings.Clear();
            nextPing = 0;
        }
        Status = ClockReady ? "Состав эмоциональной группы обновлён." : "Вы в группе. Синхронизация времени с лидером…";
    }

    private void PrepareLocal(Packet packet)
    {
        if (packet.RunId is null || !Remember(packet.RunId, seenRuns, recentRuns)) return;
        PendingRunId = packet.RunId;
        localReady = false;
        RequestedCountdownSeconds = packet.CountdownSeconds is >= 1 and <= 60 ? packet.CountdownSeconds : 3;
        EffectiveCountdownSeconds = RequestedCountdownSeconds;
        PendingEmoteId = packet.EmoteId;
        ScheduledStartAtUnixMs = null;
        prepareReceivedAt = now();
        Status = "Проверяем готовность всех участников…";
        PrepareReceived?.Invoke(packet);
    }

    private void ReceiveReady(string sender, Packet packet)
    {
        var run = activeRun;
        if (run is null || packet.RunId != run.Id || packet.GroupId != GroupId || packet.Revision != revision || !run.Participants.Contains(sender, Names)) return;
        if (!packet.Ready) { AbortRun($"{sender}: {CleanReason(packet.Reason)}"); return; }
        if (run.StartAt != 0) return;
        run.Ready.Add(sender);
        if (run.Ready.Count != run.Participants.Length) return;
        var time = now();
        var seconds = run.CountdownSeconds;
        Packet scheduled;
        // Encoding can cross a fragment boundary after the timestamp/delay changes. Only raise
        // the deadline, and recalculate the actual packet until its own delivery budget fits.
        do
        {
            scheduled = new Packet { Type = Kind.Scheduled, GroupId = GroupId, Revision = revision,
                RunId = run.Id, EmoteId = run.EmoteId, StartAtUnixMs = time + seconds * 1_000L, CountdownSeconds = seconds };
            var required = MinimumForPacket(scheduled);
            if (required <= seconds) break;
            seconds = required;
        } while (seconds <= 115);
        if (seconds > 115) { AbortRun("Очередь сообщений занята; повторите запуск позже."); return; }
        run.StartAt = scheduled.StartAtUnixMs;
        if (Broadcast(scheduled, true)) ScheduleLocal(scheduled);
    }

    private void ScheduleLocal(Packet packet)
    {
        if (PendingRunId is null || packet.RunId != PendingRunId || packet.EmoteId != PendingEmoteId ||
            packet.GroupId != GroupId || packet.Revision != revision || ScheduledStartAtUnixMs is not null) return;
        var remaining = packet.StartAtUnixMs - LeaderNowUnixMs;
        if (!ClockReady || remaining < 500 || remaining > 120_000)
        {
            Cancel();
            Status = "Запуск отменён: дедлайн опоздал или часы не синхронизированы.";
            return;
        }
        ScheduledStartAtUnixMs = packet.StartAtUnixMs;
        EffectiveCountdownSeconds = packet.CountdownSeconds is >= 1 and <= 115
            ? packet.CountdownSeconds : (int)Math.Ceiling(remaining / 1_000d);
        Status = EffectiveCountdownSeconds > RequestedCountdownSeconds
            ? $"Отсчёт {EffectiveCountdownSeconds} с: учтена доставка сообщений всей группе."
            : "Общий обратный отсчёт.";
        ScheduledReceived?.Invoke(packet);
    }

    private void AbortRun(string reason)
    {
        if (activeRun is not { } run) return;
        activeRun = null;
        transport.ForgetRun(run.Id);
        Broadcast(new Packet { Type = Kind.Cancelled, GroupId = GroupId, RunId = run.Id, Reason = CleanReason(reason) }, true);
        if (PendingRunId == run.Id) ClearRun(reason);
    }

    private void Snapshot()
    {
        if (GroupId is null) return;
        foreach (var member in Members.ToArray())
        {
            if (Same(member.Id, identity)) continue;
            if (!Send(member.Id, new Packet { Type = Kind.Group, GroupId = GroupId, LeaderId = LeaderId,
                Members = Members, Revision = revision, InviteId = membershipInvites.GetValueOrDefault(member.Id) })) return;
        }
    }

    private bool Broadcast(Packet packet, bool urgent = false)
    {
        foreach (var member in Members.ToArray())
        {
            if (!Same(member.Id, identity) && !Send(member.Id, packet, urgent)) return false;
        }
        return true;
    }

    private bool Send(string recipient, Packet packet, bool urgent = false)
    {
        if (disposed || identity is null) return false;
        try { transport.SendTo(recipient, packet, urgent); return true; }
        catch (Exception error) { TransportFailed(error); return false; }
    }

    private void TransportFailed(Exception error)
    {
        if (resetting) return;
        resetting = true;
        try
        {
            ClearGroup("Не удалось доставить сообщения группы.");
            try { transport.Reset(identity); } catch { }
            while (received.TryDequeue(out _)) { }
            Status = $"Обмен сообщениями прерван: {CleanReason(error.Message)}";
        }
        finally { resetting = false; }
    }

    private void ClearGroup(string reason, bool purgeOutbound = true)
    {
        ClearLeadership();
        if (purgeOutbound)
        {
            if (GroupId is { } group) transport.ForgetGroup(group);
            if (joining is { } pending) transport.ForgetGroup(pending.Group);
            if (Invitation?.GroupId is { } invitedGroup) transport.ForgetGroup(invitedGroup);
        }
        if (PendingRunId is { } run) transport.ForgetRun(run);
        activeRun = null;
        if (PendingRunId is not null) ClearRun(reason);
        GroupId = null;
        LeaderId = null;
        Members = [];
        Invitation = null;
        joining = null;
        invitations.Clear();
        memberSeen.Clear();
        membershipInvites.Clear();
        pings.Clear();
        synchronization.Reset();
        revision = 0;
        Status = reason;
    }

    private void ClearRun(string reason)
    {
        CompleteLocalRun();
        Status = CleanReason(reason);
        Cancelled?.Invoke(Status);
    }
    private bool RequireIdentity()
    {
        if (IsConnected) return true;
        Status = "Сначала войдите в игру.";
        return false;
    }
    private bool RequireLeader()
    {
        if (IsTransferringLeadership) { Status = "Дождитесь завершения передачи лидерства."; return false; }
        if (RequireIdentity() && IsLeader) return true;
        Status = "Это действие доступно лидеру эмоциональной группы.";
        return false;
    }
    private static string NewId() => Guid.NewGuid().ToString("N");
    private static bool ValidId(string? value) => Guid.TryParseExact(value, "N", out _);
    private static bool Same(string? left, string? right) => Names.Equals(left, right);
    private static bool Remember(string id, HashSet<string> set, Queue<string> order)
    {
        if (!set.Add(id)) return false;
        order.Enqueue(id);
        if (order.Count > 64) set.Remove(order.Dequeue());
        return true;
    }
    private static string CleanReason(string? value)
    {
        var clean = new string((value ?? "Игрок не готов.").Where(c => !char.IsControl(c)).Take(160).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "Игрок не готов." : clean;
    }
    private static string? Normalize(string? value)
    {
        return GameIdentity.TryNormalize(value, out var normalized) ? normalized : null;
    }

    private int MinimumForPacket(Packet packet) => Math.Max(1,
        (int)Math.Ceiling(transport.EstimatePriorityBroadcastMs(packet, Math.Max(0, Members.Length - 1)) / 1_000d));

    public void Dispose()
    {
        if (disposed) return;
        ClearGroup("Плагин выгружен.");
        identity = null;
        disposed = true;
        transport.MessageReceived -= OnTransportMessage;
        transport.Dispose();
    }

    private sealed record OutgoingInvite(string Target, long Expires);
    private sealed record Joining(string Group, string Leader, string Invite, long Expires);
    private sealed class ActiveRun(string id, uint emoteId, string[] participants, long readyDeadline, int countdownSeconds)
    {
        public string Id { get; } = id;
        public uint EmoteId { get; } = emoteId;
        public string[] Participants { get; } = participants;
        public long ReadyDeadline { get; } = readyDeadline;
        public int CountdownSeconds { get; } = countdownSeconds;
        public HashSet<string> Ready { get; } = new(Names);
        public long StartAt { get; set; }
    }
}
