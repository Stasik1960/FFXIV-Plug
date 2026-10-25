using System.IO.Compression;
using System.Text;
using System.Text.Json;
using EmoteTogether.Protocol;

namespace EmoteTogether.Tests;

internal static class ChatCodecTests
{
    private static int assertions;
    private const string Sender = "Alice Example@Omega";
    private const string OtherSender = "Bob Example@Omega";

    public static void Run()
    {
        assertions = 0;
        IdentityValidation();
        SmallRoundTrip();
        FragmentedUnicodeRoundTrip();
        AuthenticatedSenderPartition();
        CompletedReplaySuppression();
        FragmentLifetimeAndClear();
        MalformedFrames();
        InvalidPackets();
        AssemblyBounds();
        ConflictingFragments();
        ClockPacketsRemainAtomic();
        Console.WriteLine($"PASS: chat codec and identities (11 scenarios, {assertions} assertions)");
    }

    private static void IdentityValidation()
    {
        foreach (var value in new[]
        {
            "Alice Example@Omega", "A'ri Tia@Balmung", "Anne-Marie Example@Twintania",
            "Александр Волков@Omega", "Cafe\u0301 Example@Omega", "  Alice Example@Omega  ",
        })
        {
            Check(GameIdentity.TryNormalize(value, out var normalized), $"Valid identity: {value}");
            Check(normalized == normalized.Trim() && normalized.IsNormalized(NormalizationForm.FormC), "Identity must be trimmed and NFC normalized.");
            Check(!normalized.Any(char.IsControl), "Normalized identity cannot contain control characters.");
        }
        foreach (var value in new string?[]
        {
            null, "", "Alice", "Alice@Omega", "Alice  Example@Omega", "Alice Example@", "Alice Example@@Omega",
            "A Example@Omega", "Alice E@Omega", "Alice Example@O", "Alice Example Extra@Omega",
            "Alice Example@Omega\n/echo injected", "Alice Example@Omega\r", "Alice Example@Omega\0",
            "Alice Example@Omega\t", "Alice Example@Omega/echo", "Alice Example@\"Omega\"",
            "\"Alice Example\"@Omega", "Alice <t>@Omega", "Alice Example@<world>",
            "Alice Example@Omega;echo", "Alice Example@Omega\\echo", "Alice Example@Omega|echo",
            "Alice /echo@Omega", "Alice Example@Omega$(x)", "Alice Example@Omega`x",
            "Ali\ud800ce Example@Omega",
        })
            Check(!GameIdentity.TryNormalize(value, out _), $"Unsafe or malformed identity must be rejected: {value}");
    }

    private static void SmallRoundTrip()
    {
        var packet = new Packet
        {
            Type = Kind.Invitation, GroupId = "group-one", InviteId = "invite-one", FromName = Sender,
            LeaderId = Sender, TargetName = OtherSender, Revision = 1,
        };
        var frames = ChatCodec.Encode(packet);
        var assembler = new ChatFrameAssembler();
        Packet? actual = null;
        foreach (var frame in frames)
        {
            Check(ChatCodec.IsFrame(frame), "Encoded frame must be recognized.");
            Check(frame.Length <= ChatCodec.MaxFrameLength && frame.All(char.IsAscii), "Every encoded frame must fit the ASCII chat bound.");
            Check(assembler.TryAccept(Sender, frame, 1_000, out var decoded), "Valid frame must be accepted.");
            if (decoded != null) actual = decoded;
        }
        Equivalent(packet, actual);
        Check(!ChatCodec.IsFrame("A normal private message."), "Ordinary chat must not be recognized as a transport frame.");
    }

    private static void FragmentedUnicodeRoundTrip()
    {
        var packet = LargeGroup();
        var frames = ChatCodec.Encode(packet);
        Check(frames.Count is > 1 and <= 8, "Eight-member Unicode snapshot should exercise bounded fragmentation.");
        Check(frames.All(f => f.Length <= 240 && f.All(char.IsAscii)), "Unicode JSON must stay in bounded ASCII frames.");
        var assembler = new ChatFrameAssembler();
        var emitted = new List<Packet>();
        Check(assembler.TryAccept(Sender, frames[^1], 2_000, out var initial) && initial == null, "Last fragment alone cannot emit a packet.");
        Check(assembler.TryAccept(Sender, frames[^1], 2_001, out var duplicate) && duplicate == null, "Duplicate partial fragment cannot emit a packet.");
        foreach (var frame in frames.Reverse())
        {
            Check(assembler.TryAccept(Sender, frame, 2_002, out var decoded), "Reordered fragments must remain acceptable.");
            if (decoded != null) emitted.Add(decoded);
        }
        Check(emitted.Count == 1, "A fragmented packet must be emitted exactly once.");
        Equivalent(packet, emitted[0]);
    }

    private static void AuthenticatedSenderPartition()
    {
        var packet = LargeGroup();
        var frames = ChatCodec.Encode(packet);
        var assembler = new ChatFrameAssembler();
        Check(assembler.TryAccept(Sender, frames[0], 3_000, out var part) && part == null, "First sender opens its own assembly.");
        foreach (var frame in frames.Skip(1))
            Check(assembler.TryAccept(OtherSender, frame, 3_001, out var wrongSender) && wrongSender == null,
                "Fragments from another authenticated player cannot complete the first player's packet.");
        Packet? actual = null;
        foreach (var frame in frames.Skip(1))
        {
            Check(assembler.TryAccept(Sender, frame, 3_002, out var decoded), "Original sender can complete its own fragments.");
            if (decoded != null) actual = decoded;
        }
        Equivalent(packet, actual);
        Check(assembler.TryAccept(OtherSender, frames[0], 3_003, out var independent) && independent != null,
            "The same frame ID used by a different authenticated sender has independent state.");
        Check(!assembler.TryAccept("/echo injected", frames[0], 3_004, out _), "Malformed sender identity cannot create an assembly.");
    }

    private static void CompletedReplaySuppression()
    {
        var packet = new Packet { Type = Kind.Cancel, GroupId = "group-one", RunId = "run-one" };
        var frames = ChatCodec.Encode(packet);
        var assembler = new ChatFrameAssembler();
        var emitted = 0;
        foreach (var frame in frames)
        {
            Check(assembler.TryAccept(Sender, frame, 4_000, out var decoded), "Original completion must be accepted.");
            if (decoded != null) emitted++;
        }
        foreach (var frame in frames)
        {
            Check(assembler.TryAccept("ALICE EXAMPLE@OMEGA", frame, 4_001, out var decoded), "Duplicate frame can be consumed without replay.");
            Check(decoded == null, "Completed sequence must not be emitted a second time, including changed sender casing.");
        }
        Check(emitted == 1, "Initial sequence must emit once.");
        assembler.Expire(34_000);
        Packet? afterExpiry = null;
        foreach (var frame in frames)
        {
            assembler.TryAccept(Sender, frame, 34_001, out var decoded);
            if (decoded != null) afterExpiry = decoded;
        }
        Equivalent(packet, afterExpiry);
    }

    private static void FragmentLifetimeAndClear()
    {
        var frames = ChatCodec.Encode(LargeGroup());
        var assembler = new ChatFrameAssembler();
        Check(assembler.TryAccept(Sender, frames[0], 5_000, out _), "Initial fragment must be accepted.");
        assembler.Expire(35_000);
        foreach (var frame in frames.Skip(1))
            Check(assembler.TryAccept(Sender, frame, 35_001, out var stale) && stale == null, "Expired first fragment cannot complete later fragments.");
        assembler.Clear();
        foreach (var frame in frames.Skip(1))
            Check(assembler.TryAccept(Sender, frame, 35_002, out var cleared) && cleared == null, "Clear must discard all partial assemblies.");
        assembler.Clear();
        Packet? actual = null;
        foreach (var frame in frames)
        {
            Check(assembler.TryAccept(Sender, frame, 35_003, out var decoded), "A full new sequence must work after Clear.");
            if (decoded != null) actual = decoded;
        }
        Check(actual != null, "Clearing old state must leave the assembler usable.");
    }

    private static void MalformedFrames()
    {
        const string id = "0123456789ab";
        var assembler = new ChatFrameAssembler();
        foreach (var frame in new[]
        {
            "", "[ET2]", "[ET2]garbage", $"[ET2]short.1.1.AA", "[ET2]zzzzzzzzzzzz.1.1.AA",
            $"[ET2]{id}.0.1.AA", $"[ET2]{id}.2.1.AA", $"[ET2]{id}.1.0.AA", $"[ET2]{id}.1.9.AA",
            $"[ET2]{id}.01.1.AA", $"[ET2]{id}.1.01.AA", $"[ET2]{id}.1.1.", $"[ET2]{id}.1.1.AA=",
            $"[ET2]{id}.1.1.***", $"[ET2]{id}.1.1.AA.AA", $"[ET2]{id}.1.2.AA",
            $"[ET2]{id}.1.1.AA\n", "[ET2]" + new string('a', 240),
        })
        {
            Check(!ChatCodec.IsFrame(frame), $"Malformed frame cannot be classified as valid: {frame}");
            Check(!assembler.TryAccept(Sender, frame, 6_000, out var decoded) && decoded == null,
                "Malformed frame must be rejected without producing a packet.");
        }
        Check(!assembler.TryAccept(Sender, $"[ET2]{id}.1.1.AA", 6_001, out var corrupt) && corrupt == null,
            "Syntactically valid but corrupt Brotli payload must be rejected safely.");
    }

    private static void InvalidPackets()
    {
        foreach (var packet in new[]
        {
            new Packet { Type = Kind.Ping, ProtocolVersion = 1 },
            new Packet { Type = "futureUnknownKind" },
            new Packet { Type = Kind.Error, Reason = new string('x', 161) },
            new Packet { Type = Kind.Group, Members = Enumerable.Range(0, 9).Select(i => new Member($"m{i}", Sender)).ToArray() },
            new Packet { Type = Kind.Error, Reason = "unsafe\nreason" },
        })
        {
            ExpectArgument(() => ChatCodec.Encode(packet), "Encoder must reject invalid version, kind, bounds and controls.");
            var frames = UnsafeFrames(JsonSerializer.SerializeToUtf8Bytes(packet, Packet.Json));
            var assembler = new ChatFrameAssembler();
            var emitted = false;
            var finalAccepted = true;
            foreach (var frame in frames)
            {
                finalAccepted = assembler.TryAccept(Sender, frame, 7_000, out var decoded);
                emitted |= decoded != null;
            }
            Check(!emitted && !finalAccepted, "Decoder must independently reject packets that bypassed encoder validation.");
        }
        foreach (var bytes in new[]
        {
            Encoding.UTF8.GetBytes("{\"v\":2}"),
            Encoding.UTF8.GetBytes("{\"t\":\"ping\",\"v\":\"bad\"}"),
            Encoding.UTF8.GetBytes("not JSON"),
            Encoding.UTF8.GetBytes("{\"t\":\"error\",\"v\":2,\"z\":\"" + new string('x', 9_000) + "\"}"),
        })
        {
            var assembler = new ChatFrameAssembler();
            var emitted = false;
            var finalAccepted = true;
            foreach (var frame in UnsafeFrames(bytes))
            {
                finalAccepted = assembler.TryAccept(Sender, frame, 7_001, out var decoded);
                emitted |= decoded != null;
            }
            Check(!emitted && !finalAccepted, "Malformed JSON and decompression beyond the raw bound must be rejected.");
        }
    }

    private static void AssemblyBounds()
    {
        var assembler = new ChatFrameAssembler();
        var packets = Enumerable.Range(0, 5).Select(_ => ChatCodec.Encode(LargeGroup())).ToArray();
        foreach (var frames in packets.Take(4))
            Check(assembler.TryAccept(Sender, frames[0], 8_000, out var pending) && pending == null, "Four partial assemblies per sender must fit.");
        Check(!assembler.TryAccept(Sender, packets[4][0], 8_000, out _), "Fifth simultaneous assembly from the same sender must be rejected.");
        assembler.Clear();
        var senders = new[] { "Alpha Player@Omega", "Bravo Player@Omega", "Charlie Player@Omega", "Delta Player@Omega",
            "Echo Player@Omega", "Foxtrot Player@Omega", "Golf Player@Omega", "Hotel Player@Omega" };
        foreach (var sender in senders)
            foreach (var frames in packets.Take(4))
                Check(assembler.TryAccept(sender, frames[0], 8_001, out var pending) && pending == null, "Up to32 global partial assemblies must fit.");
        Check(!assembler.TryAccept("India Player@Omega", packets[4][0], 8_001, out _), "Thirty-third global partial assembly must be rejected.");
        assembler.Expire(38_001);
        Check(assembler.TryAccept("India Player@Omega", packets[4][0], 38_002, out var freed) && freed == null,
            "Expired assemblies must release bounded capacity.");
    }

    private static void ConflictingFragments()
    {
        var frames = ChatCodec.Encode(LargeGroup());
        var assembler = new ChatFrameAssembler();
        Check(assembler.TryAccept(Sender, frames[0], 9_000, out _), "Initial fragment opens assembly.");
        var last = frames[0][^1];
        var conflict = frames[0][..^1] + (last == 'A' ? 'B' : 'A');
        Check(ChatCodec.IsFrame(conflict), "Conflicting payload remains syntactically valid.");
        Check(!assembler.TryAccept(Sender, conflict, 9_001, out var result) && result == null,
            "A different payload for the same fragment cannot silently overwrite state.");
        foreach (var frame in frames.Skip(1))
            Check(assembler.TryAccept(Sender, frame, 9_002, out var incomplete) && incomplete == null,
                "Conflicting assembly must not be completed by later old fragments.");
    }

    private static void ClockPacketsRemainAtomic()
    {
        for (var i = 0; i < 100; i++)
        {
            var group = Guid.NewGuid().ToString("N");
            var nonce = Guid.NewGuid().ToString("N");
            var timestamp = 1_792_910_000_000L + i * 41;
            var ping = new Packet { Type = Kind.Ping, GroupId = group, Nonce = nonce, ClientSendTime = timestamp };
            var pong = new Packet { Type = Kind.Pong, GroupId = group, Nonce = nonce, ClientSendTime = timestamp,
                ServerReceiveTime = timestamp + 21, ServerSendTime = timestamp + 24 };
            Check(ChatCodec.Encode(ping).Count == 1, "Ping must be a single chat frame so queue fragmentation cannot bias NTP timestamps.");
            Check(ChatCodec.Encode(pong).Count == 1, "Pong must be a single chat frame so queue fragmentation cannot bias NTP timestamps.");
        }
        var oversized = new Packet { Type = Kind.Pong, GroupId = Guid.NewGuid().ToString("N"), Nonce = Guid.NewGuid().ToString("N"),
            Reason = string.Concat(Enumerable.Range(0, 5).Select(_ => Guid.NewGuid().ToString("N"))),
            Members = LargeGroup().Members, ClientSendTime = 1_792_910_000_000L,
            ServerReceiveTime = 1_792_910_000_021L, ServerSendTime = 1_792_910_000_024L };
        ExpectArgument(() => ChatCodec.Encode(oversized), "Clock packets that would require fragmentation must be rejected.");
    }

    private static Packet LargeGroup()
    {
        var names = new[] { "Александр Волков@Omega", "Мария Иванова@Omega", "Анна Петрова@Omega", "Елена Орлова@Omega",
            "София Соколова@Omega", "Андрей Смирнов@Omega", "Михаил Морозов@Omega", "Дмитрий Николаев@Omega" };
        var members = names.Select(name => new Member(Guid.NewGuid().ToString("N"), name)).ToArray();
        return new Packet { Type = Kind.Group, GroupId = Guid.NewGuid().ToString("N"), LeaderId = members[0].Id, Members = members, Revision = 17 };
    }

    private static IReadOnlyList<string> UnsafeFrames(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var compressor = new BrotliStream(output, CompressionLevel.Optimal, true)) compressor.Write(bytes);
        var payload = Convert.ToBase64String(output.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var count = (payload.Length + 195) / 196;
        Check(count is > 0 and <= 8, "Test fixture must fit the valid framing envelope.");
        var id = Guid.NewGuid().ToString("N")[..12];
        return Enumerable.Range(0, count).Select(i => $"[ET2]{id}.{i + 1}.{count}.{payload.Substring(i * 196, Math.Min(196, payload.Length - i * 196))}").ToArray();
    }

    private static void Equivalent(Packet expected, Packet? actual)
    {
        Check(actual != null, "Complete valid sequence must emit a packet.");
        Check(JsonSerializer.Serialize(expected, Packet.Json) == JsonSerializer.Serialize(actual, Packet.Json), "Decoded packet must preserve all original fields, including Unicode.");
    }
    private static void ExpectArgument(Action action, string message)
    {
        try { action(); }
        catch (ArgumentException) { assertions++; return; }
        throw new InvalidOperationException(message);
    }
    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }
}
