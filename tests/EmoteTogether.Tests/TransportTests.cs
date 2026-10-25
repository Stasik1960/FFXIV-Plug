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
        PriorityScheduleBudget();
        Console.WriteLine($"PASS: paced chat transport ({count} assertions)");
    }

    private static void PriorityScheduleBudget()
    {
        long time = 1_800_000_000_000;
        using var transport = new Recorder(() => time);
        transport.Reset("Leader Player@World");
        var group = Guid.NewGuid().ToString("N");
        var scheduled = new Packet { Type = Kind.Scheduled, GroupId = group, RunId = Guid.NewGuid().ToString("N"),
            EmoteId = 21, Revision = 1, CountdownSeconds = 3, StartAtUnixMs = time + 3_000 };
        var withoutBacklog = transport.EstimatePriorityBroadcastMs(scheduled, 1);
        for (var i = 0; i < 20; i++) transport.SendTo("Guest Player@World", new Packet { Type = Kind.Ping, GroupId = group, Nonce = Guid.NewGuid().ToString("N") });
        Check(transport.EstimatePriorityBroadcastMs(scheduled, 1) == withoutBacklog,
            "unsent telemetry does not inflate priority countdown");
        transport.SendTo("Guest Player@World", scheduled, true);
        transport.Poll();
        var assembler = new ChatFrameAssembler();
        Check(assembler.TryAccept("Leader Player@World", transport.Sent.Last().Text, time, out var first), "priority schedule frame accepted");
        while (first is null)
        {
            time += transport.SendIntervalMs;
            transport.Poll();
            Check(assembler.TryAccept("Leader Player@World", transport.Sent.Last().Text, time, out first), "remaining priority frame accepted");
        }
        Check(first.Type == Kind.Scheduled, "priority scheduling overtakes normal telemetry");

        transport.Reset("Leader Player@World");
        var names = new[] { "Александр Волков", "Бенедикт Смирнов", "Кристофер Лонгнейм", "Доминик Соколов",
            "Елизавета Романова", "Фредерик Иванов", "Георгий Николаев", "Гермиона Петрова" };
        var roster = new Packet { Type = Kind.Group, GroupId = group, LeaderId = "Leader Player@World", Revision = 1,
            Members = names.Select(n => new Member($"{n}@Twintania", $"{n}@Twintania")).ToArray() };
        Check(ChatCodec.Encode(roster).Count > 1, "test roster spans multiple paced frames");
        transport.SendTo("Guest Player@World", roster);
        transport.Poll();
        var origin = time;
        var budget = transport.EstimatePriorityBroadcastMs(scheduled, 3);
        foreach (var recipient in new[] { "Alpha Player@World", "Bravo Player@World", "Charlie Player@World" })
            transport.SendTo(recipient, scheduled, true);
        var recipients = new Dictionary<string, ChatFrameAssembler>();
        var completed = new HashSet<string>();
        var index = transport.Sent.Count;
        while (completed.Count < 3 && time - origin < budget)
        {
            time += transport.SendIntervalMs;
            transport.Poll();
            foreach (var sent in transport.Sent.Skip(index).ToArray())
            {
                if (!recipients.TryGetValue(sent.Target, out var parts)) recipients[sent.Target] = parts = new ChatFrameAssembler();
                if (parts.TryAccept("Leader Player@World", sent.Text, time, out var packet) && packet?.Type == Kind.Scheduled) completed.Add(sent.Target);
            }
            index = transport.Sent.Count;
        }
        Check(completed.Count == 3 && time - origin <= budget - 1_000,
            "broadcast estimate includes partial batch, cooldown and every schedule frame");
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
