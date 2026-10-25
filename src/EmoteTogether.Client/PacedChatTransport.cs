using EmoteTogether.Protocol;

namespace EmoteTogether.Client;

/// <summary>Bounded, paced tell transport. It performs no networking and does not call game APIs.</summary>
public abstract class PacedChatTransport : IChatTransport
{
    private readonly Func<long> nowMs;
    private readonly ChatFrameAssembler assembler = new();
    private readonly LinkedList<Pending> outgoing = [];
    private readonly Dictionary<Destination, long> completedAt = [];
    private readonly Dictionary<Destination, long> presenceRoutes = [];
    private long nextPruneAt;
    private const long PulseDueMs = 12_000;
    private const long PulseDeadlineMs = 16_000;
    private long nextSendAt;
    private bool disposed;

    protected PacedChatTransport(Func<long>? nowMs = null, int sendIntervalMs = 1_500)
    {
        var clock = new MonotonicClock();
        this.nowMs = nowMs ?? (() => clock.UnixMilliseconds);
        SendIntervalMs = Math.Clamp(sendIntervalMs, 1_200, 10_000);
    }

    public string? LocalIdentity { get; private set; }
    public int PendingFrames => outgoing.Sum(p => p.Estimate - p.Index);
    public int SendIntervalMs { get; }
    public string? LastError { get; private set; }
    public event Action<ChatEnvelope>? MessageReceived;

    // Urgent packets overtake unsent work, but a fragmented message already on the wire finishes first.
    // The final frame must reach the final recipient before the common deadline.
    public int EstimatePriorityBroadcastMs(Packet packet, int recipients)
    {
        if (recipients <= 0) return 0;
        var frames = ChatCodec.Encode(Stamp(packet, nowMs())).Count;
        var partial = outgoing.First is { Value.Index: > 0 } first ? first.Value.Estimate - first.Value.Index : 0;
        var priority = Priority(packet, true);
        var active = outgoing.First is { Value.Index: > 0 } started ? started.Value : null;
        var ahead = outgoing.Where(p => p != active && p.Priority <= priority).Sum(p => p.Estimate - p.Index);
        var cooldown = Math.Max(0, nextSendAt - nowMs());
        var baseline = cooldown + (partial + ahead + frames * (long)recipients - 1) * SendIntervalMs + 1_000;
        if (priority != 0) return checked((int)Math.Min(int.MaxValue, baseline));
        // A long critical broadcast may have to yield to heartbeats. Include every round
        // that can become due within the estimated delivery window, including those yields.
        var time = nowMs();
        var clocks = presenceRoutes.Where(p => p.Key.Group == packet.GroupId && time - p.Value <= 30_000)
            .ToDictionary(p => p.Key, p => completedAt.GetValueOrDefault(p.Key, p.Value - PulseDueMs));
        foreach (var pulse in outgoing.Where(p => p.Packet.Type == PresenceKinds.Pulse))
            clocks.TryAdd(pulse.Destination, pulse.EnqueuedAt - PulseDueMs);
        long budget = baseline;
        for (var attempt = 0; attempt < 128; attempt++)
        {
            long rounds = 0;
            foreach (var last in clocks.Values)
            {
                var available = Math.Min(PulseDeadlineMs, Math.Max(0, time - last)) + budget - PulseDeadlineMs;
                if (available >= 0) rounds += available / PulseDeadlineMs + 1;
            }
            var next = baseline + rounds * SendIntervalMs;
            if (next <= budget) return checked((int)Math.Min(int.MaxValue, budget));
            budget = next;
            if (budget > 240_000) return int.MaxValue;
        }
        return int.MaxValue;
    }

    public void SendTo(string recipient, Packet packet, bool urgent = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (LocalIdentity == null) throw new InvalidOperationException("Персонаж не загружен.");
        if (!GameIdentity.TryNormalize(recipient, out var identity)) throw new ArgumentException("Некорректное имя игрока.", nameof(recipient));
        var estimate = ChatCodec.Encode(Stamp(packet, nowMs())).Count;
        if (packet.Type == PresenceKinds.Pulse) presenceRoutes[DestinationFor(identity, packet.GroupId)] = nowMs();
        if (packet.Type == PresenceKinds.Pulse && outgoing.Any(p => p.Index == 0 && p.Packet.Type == PresenceKinds.Pulse &&
                p.Packet.GroupId == packet.GroupId && string.Equals(p.Recipient, identity, StringComparison.OrdinalIgnoreCase)))
            return; // Keep the existing FIFO position; repeated polls must not postpone the heartbeat.
        if (packet.Type == Kind.Group)
        {
            foreach (var obsolete in outgoing.Where(p => p.Index == 0 && p.Packet.Type == packet.Type && p.Packet.GroupId == packet.GroupId &&
                         string.Equals(p.Recipient, identity, StringComparison.OrdinalIgnoreCase)).ToArray()) outgoing.Remove(obsolete);
        }
        if (PendingFrames + estimate > 128) throw new InvalidOperationException("Очередь личных сообщений заполнена; подождите.");
        var pending = new Pending(identity, packet, estimate, Priority(packet, urgent), nowMs());
        var before = outgoing.First;
        if (before is { Value.Index: > 0 }) before = before.Next;
        while (before != null && before.Value.Priority <= pending.Priority) before = before.Next;
        if (before == null) outgoing.AddLast(pending);
        else outgoing.AddBefore(before, pending);
    }

    public void ForgetRun(string runId)
    {
        foreach (var item in outgoing.Where(p => p.Packet.RunId == runId).ToArray()) outgoing.Remove(item);
    }

    public void ForgetGroup(string groupId)
    {
        foreach (var item in outgoing.Where(p => p.Packet.GroupId == groupId).ToArray()) outgoing.Remove(item);
        foreach (var key in completedAt.Keys.Where(k => k.Group == groupId).ToArray()) completedAt.Remove(key);
        foreach (var key in presenceRoutes.Keys.Where(k => k.Group == groupId).ToArray()) presenceRoutes.Remove(key);
    }

    public void ForgetRecipient(string recipient, string groupId)
    {
        var destination = DestinationFor(recipient, groupId);
        foreach (var pending in outgoing.Where(p => p.Destination == destination).ToArray()) outgoing.Remove(pending);
        completedAt.Remove(destination);
        presenceRoutes.Remove(destination);
    }

    protected void ClearPendingMessages() => outgoing.Clear();

    public virtual void Poll()
    {
        if (disposed || LocalIdentity == null) return;
        var now = nowMs();
        assembler.Expire(now);
        if (now >= nextPruneAt)
        {
            foreach (var stale in completedAt.Where(p => now - p.Value > 120_000).Select(p => p.Key).ToArray()) completedAt.Remove(stale);
            foreach (var stale in presenceRoutes.Where(p => now - p.Value > 120_000).Select(p => p.Key).ToArray()) presenceRoutes.Remove(stale);
            nextPruneAt = now + 30_000;
        }
        if (now < nextSendAt || outgoing.First is null) return;
        var first = outgoing.First;
        if (first.Value.Index == 0)
        {
            LinkedListNode<Pending>? due = null;
            long oldest = long.MaxValue;
            for (var candidate = outgoing.First; candidate != null; candidate = candidate.Next)
            {
                if (candidate.Value.Packet.Type != PresenceKinds.Pulse) continue;
                var last = LastCompleted(candidate.Value);
                if (now - last >= PulseDeadlineMs && last < oldest) { due = candidate; oldest = last; }
            }
            if (due is not null) first = due;
            else while (first != null && first.Value.Packet.Type == PresenceKinds.Pulse && now - LastCompleted(first.Value) < PulseDueMs)
                first = first.Next;
            if (first is null) return;
            if (first != outgoing.First) { outgoing.Remove(first); outgoing.AddFirst(first); }
        }
        var pending = first.Value;
        pending.Frames ??= ChatCodec.Encode(Stamp(pending.Packet, now));
        pending.Estimate = pending.Frames.Count;
        try
        {
            SendFrame(pending.Recipient, pending.Frames[pending.Index]);
            nextSendAt = now + SendIntervalMs;
            pending.Index++;
            if (pending.Index >= pending.Frames.Count)
            {
                completedAt[pending.Destination] = now;
                outgoing.RemoveFirst();
                // An actual delivered packet provides the same liveness as a queued Pulse.
                foreach (var pulse in outgoing.Where(p => p.Index == 0 && p.Packet.Type == PresenceKinds.Pulse && p.Destination == pending.Destination).ToArray())
                    outgoing.Remove(pulse);
            }
        }
        catch (Exception error)
        {
            LastError = error.Message;
            outgoing.Clear();
            throw;
        }
    }

    protected bool ReceiveFrame(string authenticatedSender, string text, long receivedAtUnixMs)
    {
        if (disposed || LocalIdentity == null) return false;
        if (!assembler.TryAccept(authenticatedSender, text, receivedAtUnixMs, out var packet)) return false;
        if (packet != null) MessageReceived?.Invoke(new ChatEnvelope(authenticatedSender, packet, receivedAtUnixMs));
        return true;
    }

    public virtual void Reset(string? identity)
    {
        outgoing.Clear();
        completedAt.Clear();
        presenceRoutes.Clear();
        nextPruneAt = 0;
        assembler.Clear();
        LocalIdentity = GameIdentity.TryNormalize(identity, out var normalized) ? normalized : null;
        nextSendAt = 0;
        LastError = null;
    }

    protected abstract void SendFrame(string recipient, string text);
    private long LastCompleted(Pending pending) => completedAt.GetValueOrDefault(pending.Destination, pending.EnqueuedAt - PulseDueMs);

    private static Packet Stamp(Packet packet, long now) => packet.Type switch
    {
        Kind.Ping => packet with { ClientSendTime = now },
        Kind.Pong => packet with { ServerSendTime = now },
        _ => packet,
    };
    public virtual void Dispose()
    {
        if (disposed) return;
        Reset(null);
        disposed = true;
    }
    private static int Priority(Packet packet, bool urgent) => !urgent ? 3 : packet.Type switch
    {
        PresenceKinds.Pulse or Kind.Pong => 1,
        _ => 0,
    };

    private readonly record struct Destination(string Recipient, string? Group);
    private static Destination DestinationFor(string recipient, string? group) => new(recipient.ToUpperInvariant(), group);
    private sealed class Pending(string recipient, Packet packet, int estimate, int priority, long enqueuedAt)
    {
        public string Recipient { get; } = recipient;
        public Packet Packet { get; } = packet;
        public int Estimate { get; set; } = estimate;
        public IReadOnlyList<string>? Frames { get; set; }
        public int Index { get; set; }
        public int Priority { get; } = priority;
        public long EnqueuedAt { get; } = enqueuedAt;
        public Destination Destination { get; } = DestinationFor(recipient, packet.GroupId);
    }
}
