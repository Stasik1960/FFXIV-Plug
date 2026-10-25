using EmoteTogether.Client;
using EmoteTogether.Protocol;

namespace EmoteTogether.Tests;

internal static class TransportTests
{
    private static int count;
    public static void Run()
    {
        long now = 1_800_000_000_000;
        using var transport = new Recorder(() => now);
        transport.Reset("Leader Player@World");
        var group = Guid.NewGuid().ToString("N");
        transport.SendTo("Guest Player@World", new Packet { Type = Kind.Invitation, GroupId = group, InviteId = Guid.NewGuid().ToString("N") });
        transport.SendTo("Guest Player@World", new Packet { Type = Kind.Ping, GroupId = group, Nonce = Guid.NewGuid().ToString("N") });
        transport.Poll();
        Check(transport.Sent.Count == 1, "one frame per poll");
        transport.Poll();
        Check(transport.Sent.Count == 1, "spacing enforced");
        now += transport.SendIntervalMs;
        transport.Poll();
        var ping = Decode(transport.Sent.Last().Text, "Leader Player@World", now);
        Check(ping.Type == Kind.Ping && ping.ClientSendTime == now, "ping stamps actual send after queue");
        transport.Reset("Leader Player@World");
        transport.SendTo("Guest Player@World", new Packet { Type = Kind.Pong, GroupId = group, Nonce = Guid.NewGuid().ToString("N"), ClientSendTime = now - 20_000, ServerReceiveTime = now - 19_000 });
        now += 15_000;
        transport.Poll();
        var pong = Decode(transport.Sent.Last().Text, "Leader Player@World", now);
        Check(pong.ServerSendTime == now && pong.ServerReceiveTime == now - 34_000, "pong stamps send and preserves receive");
        transport.Reset("Leader Player@World");
        var run = Guid.NewGuid().ToString("N");
        transport.SendTo("Guest Player@World", new Packet { Type = Kind.Prepare, GroupId = group, RunId = run, EmoteId = 21 });
        transport.ForgetRun(run);
        Check(transport.PendingFrames == 0, "cancel purges stale run frames");
        transport.SendTo("Guest Player@World", new Packet { Type = Kind.Ping, GroupId = group, Nonce = Guid.NewGuid().ToString("N") });
        transport.SendTo("Guest Player@World", new Packet { Type = Kind.Cancelled, GroupId = group, RunId = run }, true);
        transport.Poll();
        Check(Decode(transport.Sent.Last().Text, "Leader Player@World", now).Type == Kind.Cancelled, "urgent cancellation precedes pending telemetry");
        transport.Reset("Leader Player@World");
        transport.SendTo("Guest Player@World", new Packet { Type = Kind.Group, GroupId = group, Revision = 1 });
        var oldFrames = transport.PendingFrames;
        transport.SendTo("Guest Player@World", new Packet { Type = Kind.Group, GroupId = group, Revision = 2 });
        Check(transport.PendingFrames <= oldFrames + 1, "pending snapshots coalesce");
        transport.Poll();
        Check(Decode(transport.Sent.Last().Text, "Leader Player@World", now).Revision == 2, "latest snapshot sent");
        transport.Reset(null);
        Check(transport.PendingFrames == 0 && transport.LocalIdentity == null, "reset clears queue");
        transport.Reset("Leader Player@World");
        var rejected = false;
        try { transport.SendTo("Guest Player@World\n/logout", new Packet { Type = Kind.Ping }); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "command injection rejected");
        var full = false;
        try { for (var i = 0; i < 140; i++) transport.SendTo("Guest Player@World", new Packet { Type = Kind.Ready, RunId = Guid.NewGuid().ToString("N"), Ready = true }); }
        catch (InvalidOperationException) { full = true; }
        Check(full && transport.PendingFrames <= 128, "bounded queue");
        Console.WriteLine($"PASS: paced chat transport ({count} assertions)");
    }
    private static Packet Decode(string text, string sender, long now)
    {
        var assembler = new ChatFrameAssembler();
        Check(assembler.TryAccept(sender, text, now, out var packet) && packet != null, "single frame decodes");
        return packet!;
    }
    private static void Check(bool condition, string name)
    { count++; if (!condition) throw new InvalidOperationException($"Transport test failed: {name}"); }
    private sealed class Recorder(Func<long> now) : PacedChatTransport(now)
    {
        public List<(string Target, string Text)> Sent { get; } = [];
        protected override void SendFrame(string recipient, string text) => Sent.Add((recipient, text));
    }
}
