using EmoteTogether.Protocol;

namespace EmoteTogether.Client;

/// <summary>Bounded, paced tell transport. It performs no networking and does not call game APIs.</summary>
public abstract class PacedChatTransport : IChatTransport
{
    private readonly Func<long> nowMs;
    private readonly ChatFrameAssembler assembler = new();
    private readonly LinkedList<Pending> outgoing = [];
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

    public void SendTo(string recipient, Packet packet, bool urgent = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (LocalIdentity == null) throw new InvalidOperationException("Персонаж не загружен.");
        if (!GameIdentity.TryNormalize(recipient, out var identity)) throw new ArgumentException("Некорректное имя игрока.", nameof(recipient));
        var estimate = ChatCodec.Encode(Stamp(packet, nowMs())).Count;
        if (packet.Type == Kind.Group)
        {
            foreach (var obsolete in outgoing.Where(p => p.Index == 0 && p.Packet.Type == Kind.Group && p.Packet.GroupId == packet.GroupId &&
                         string.Equals(p.Recipient, identity, StringComparison.OrdinalIgnoreCase)).ToArray()) outgoing.Remove(obsolete);
        }
        if (PendingFrames + estimate > 128) throw new InvalidOperationException("Очередь личных сообщений заполнена; подождите.");
        var pending = new Pending(identity, packet, estimate);
        if (!urgent) outgoing.AddLast(pending);
        else if (outgoing.First is { Value.Index: > 0 } active) outgoing.AddAfter(active, pending);
        else outgoing.AddFirst(pending);
    }

    public void ForgetRun(string runId)
    {
        foreach (var item in outgoing.Where(p => p.Packet.RunId == runId).ToArray()) outgoing.Remove(item);
    }

    public void ForgetGroup(string groupId)
    {
        foreach (var item in outgoing.Where(p => p.Packet.GroupId == groupId).ToArray()) outgoing.Remove(item);
    }

    public virtual void Poll()
    {
        if (disposed || LocalIdentity == null) return;
        var now = nowMs();
        assembler.Expire(now);
        if (now < nextSendAt || outgoing.First is not { } first) return;
        var pending = first.Value;
        pending.Frames ??= ChatCodec.Encode(Stamp(pending.Packet, now));
        pending.Estimate = pending.Frames.Count;
        try
        {
            SendFrame(pending.Recipient, pending.Frames[pending.Index]);
            nextSendAt = now + SendIntervalMs;
            pending.Index++;
            if (pending.Index >= pending.Frames.Count) outgoing.RemoveFirst();
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
        assembler.Clear();
        LocalIdentity = GameIdentity.TryNormalize(identity, out var normalized) ? normalized : null;
        nextSendAt = 0;
        LastError = null;
    }

    protected abstract void SendFrame(string recipient, string text);

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
    private sealed class Pending(string recipient, Packet packet, int estimate)
    {
        public string Recipient { get; } = recipient;
        public Packet Packet { get; } = packet;
        public int Estimate { get; set; } = estimate;
        public IReadOnlyList<string>? Frames { get; set; }
        public int Index { get; set; }
    }
}
