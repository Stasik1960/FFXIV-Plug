using System.IO.Compression;
using System.Text.Json;

namespace EmoteTogether.Protocol;

public static class ChatCodec
{
    public const string Prefix = "[ET3]";
    public const int MaxFrameLength = 240;
    public const int MaxFragments = 8;
    private const int ChunkLength = 196;
    private static readonly HashSet<string> KnownKinds =
    [
        Kind.Hello, Kind.Welcome, Kind.Ping, Kind.Pong, Kind.CreateGroup, Kind.Group,
        Kind.Invite, Kind.Invitation, Kind.RespondInvite, Kind.InviteResult, Kind.LeaveGroup,
        Kind.GroupClosed, Kind.Start, Kind.Prepare, Kind.Ready, Kind.Scheduled,
        Kind.Cancel, Kind.Cancelled, Kind.Error, Kind.Leave,
        LeadershipKinds.Offer, LeadershipKinds.Ready, LeadershipKinds.Changed, LeadershipKinds.Ack, LeadershipKinds.Settled,
    ];

    public static IReadOnlyList<string> Encode(Packet packet)
    {
        if (!IsValid(packet)) throw new ArgumentException("Некорректное сообщение протокола.", nameof(packet));
        var json = JsonSerializer.SerializeToUtf8Bytes(packet, Packet.Json);
        if (json.Length > Packet.MaxMessageBytes) throw new ArgumentException("Слишком большое сообщение.", nameof(packet));
        using var compressed = new MemoryStream();
        using (var writer = new BrotliStream(compressed, CompressionLevel.Optimal, true)) writer.Write(json);
        var payload = Convert.ToBase64String(compressed.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var count = (payload.Length + ChunkLength - 1) / ChunkLength;
        if (count is < 1 or > MaxFragments) throw new ArgumentException("Сообщение не помещается в чат.", nameof(packet));
        if (packet.Type is Kind.Ping or Kind.Pong && count != 1)
            throw new ArgumentException("Синхронизация часов должна помещаться в одно сообщение.", nameof(packet));
        var id = Guid.NewGuid().ToString("N")[..12];
        return Enumerable.Range(0, count)
            .Select(i => $"{Prefix}{id}.{i + 1}.{count}.{payload.Substring(i * ChunkLength, Math.Min(ChunkLength, payload.Length - i * ChunkLength))}")
            .ToArray();
    }

    public static bool IsFrame(string? text) => TryParseFrame(text, out _);

    internal static bool TryParseFrame(string? text, out Frame? frame)
    {
        frame = null;
        if (text is null || text.Length > MaxFrameLength || !text.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var parts = text[Prefix.Length..].Split('.');
        if (parts.Length != 4 || parts[0].Length != 12 || !parts[0].All(char.IsAsciiHexDigit) ||
            parts[1].Length != 1 || parts[2].Length != 1 ||
            !int.TryParse(parts[1], out var index) || !int.TryParse(parts[2], out var count) ||
            count is < 1 or > MaxFragments || index < 1 || index > count ||
            parts[3].Length is < 1 or > ChunkLength ||
            !parts[3].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
        if (index < count && parts[3].Length != ChunkLength) return false;
        frame = new Frame(parts[0], index - 1, count, parts[3]);
        return true;
    }

    internal static bool TryDecode(string encoded, out Packet? packet)
    {
        packet = null;
        try
        {
            var base64 = encoded.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight((base64.Length + 3) / 4 * 4, '=');
            using var input = new MemoryStream(Convert.FromBase64String(base64));
            using var reader = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[1_024];
            int count;
            while ((count = reader.Read(buffer)) > 0)
            {
                if (output.Length + count > Packet.MaxMessageBytes) return false;
                output.Write(buffer, 0, count);
            }
            packet = JsonSerializer.Deserialize<Packet>(output.ToArray(), Packet.Json);
            if (packet == null || !IsValid(packet)) { packet = null; return false; }
            return true;
        }
        catch (Exception error) when (error is FormatException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsValid(Packet packet) => packet.ProtocolVersion == Packet.Version &&
        packet.Type != null && KnownKinds.Contains(packet.Type) &&
        Short(packet.Name, 96) && Short(packet.MemberId, 96) && Short(packet.GroupId, 64) &&
        Short(packet.LeaderId, 96) && Short(packet.TargetName, 96) && Short(packet.InviteId, 64) &&
        Short(packet.FromName, 96) && Short(packet.RunId, 64) && Short(packet.Reason, 160) && Short(packet.Nonce, 64) &&
        (packet.Members == null || packet.Members.Length <= 8 && packet.Members.All(m => m != null && Short(m.Id, 96) && Short(m.Name, 96)));

    private static bool Short(string? value, int limit) => value == null || value.Length <= limit && !value.Any(char.IsControl);
    internal sealed record Frame(string Id, int Index, int Count, string Payload);
}

public sealed class ChatFrameAssembler
{
    public const long LifetimeMs = 30_000;
    private readonly Dictionary<(string Sender, string Id), Assembly> pending = [];
    private readonly Dictionary<(string Sender, string Id), long> completed = [];

    public bool TryAccept(string sender, string text, long now, out Packet? packet)
    {
        packet = null;
        Expire(now);
        if (!GameIdentity.TryNormalize(sender, out var normalized) || !ChatCodec.TryParseFrame(text, out var parsed) || parsed == null) return false;
        var key = (normalized.ToUpperInvariant(), parsed.Id.ToUpperInvariant());
        if (completed.ContainsKey(key)) return true;
        if (!pending.TryGetValue(key, out var assembly))
        {
            if (pending.Count >= 32 || pending.Keys.Count(k => k.Sender == key.Item1) >= 4) return false;
            assembly = new Assembly(now, new string?[parsed.Count]);
            pending.Add(key, assembly);
        }
        if (assembly.Parts.Length != parsed.Count) { pending.Remove(key); return false; }
        if (assembly.Parts[parsed.Index] is { } previous && previous != parsed.Payload) { pending.Remove(key); return false; }
        assembly.Parts[parsed.Index] = parsed.Payload;
        if (assembly.Parts.Any(p => p == null)) return true;
        pending.Remove(key);
        // Remember invalid completed attempts too, avoiding repeated expensive decompression.
        if (completed.Count >= 256) completed.Remove(completed.MinBy(p => p.Value).Key);
        completed[key] = now;
        return ChatCodec.TryDecode(string.Concat(assembly.Parts), out packet);
    }

    public void Expire(long now)
    {
        foreach (var key in pending.Where(p => now - p.Value.CreatedAt >= LifetimeMs).Select(p => p.Key).ToArray()) pending.Remove(key);
        foreach (var key in completed.Where(p => now - p.Value >= LifetimeMs).Select(p => p.Key).ToArray()) completed.Remove(key);
    }
    public void Clear() { pending.Clear(); completed.Clear(); }
    private sealed record Assembly(long CreatedAt, string?[] Parts);
}
