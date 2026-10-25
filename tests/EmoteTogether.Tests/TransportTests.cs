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
        PriorityFifoAndPresence();
        DeadlinePresenceAndBudget();
        Console.WriteLine($"PASS: paced chat transport ({count} assertions)");
    }
    private static void DeadlinePresenceAndBudget()
    {
        long time = 1_800_000_000_000;
        using var transport = new Recorder(() => time);
        transport.Reset("Leader Player@World");
        var group = Guid.NewGuid().ToString("N");
        var peers = new[] { "Bastian Nightbloom@Ravana", "Cerys Silverthorne@Sephirot", "Darian Emberwatch@Bismarck",
            "Evelyn Starfall@Zurvan", "Freyja Moonlark@Halicarnassus", "Grayson Ashenvale@Maduin", "Helena Frostwind@Seraph" };
        var pulse = new Packet { Type = PresenceKinds.Pulse, GroupId = group };
        foreach (var peer in peers) transport.SendTo(peer, pulse, true);
        var receivedAt = new Dictionary<string, long>();
        foreach (var peer in peers) { transport.Poll(); receivedAt[transport.Sent.Last().Target] = time; time += transport.SendIntervalMs; }
        time += 8_000;
        foreach (var peer in peers) transport.SendTo(peer, pulse, true);
        var roster = new Packet { Type = Kind.Group, GroupId = group, LeaderId = "Aurelia Dawnwhisper@Sophia", Revision = 9,
            InviteId = Guid.NewGuid().ToString("N"), Members = peers.Append("Aurelia Dawnwhisper@Sophia").Select(n => new Member(n, n)).ToArray() };
        Check(ChatCodec.Encode(roster).Count >= 3, "deadline test carries distinct-name three-frame rosters");
        var origin = time;
        var budget = transport.EstimatePriorityBroadcastMs(roster, peers.Length);
        foreach (var peer in peers) transport.SendTo(peer, roster, true);
        var nextPresence = time + GroupClient.PresenceIntervalMs;
        var complete = new HashSet<string>();
        var assemblers = peers.ToDictionary(p => p, _ => new ChatFrameAssembler());
        var index = transport.Sent.Count;
        long largestGap = 0;
        while (complete.Count < peers.Length && time - origin < budget)
        {
            if (time >= nextPresence)
            {
                foreach (var peer in peers) transport.SendTo(peer, pulse, true);
                nextPresence = time + GroupClient.PresenceIntervalMs;
            }
            transport.Poll();
            foreach (var sent in transport.Sent.Skip(index).ToArray())
                if (assemblers[sent.Target].TryAccept("Leader Player@World", sent.Text, time, out var packet) && packet != null)
                {
                    largestGap = Math.Max(largestGap, time - receivedAt[sent.Target]); receivedAt[sent.Target] = time;
                    if (packet.Type == Kind.Group) complete.Add(sent.Target);
                }
            index = transport.Sent.Count;
            time += 100;
        }
        Check(complete.Count == peers.Length && time - origin <= budget, "critical fanout with emergency heartbeats fits its estimated budget");
        Check(largestGap < GroupClient.PeerLifetimeMs - 500, "deadline heartbeats preserve network slack under the strict 30 s lease");
        var sends = transport.Sent.Select(s => s.At).ToArray();
        Check(sends.Zip(sends.Skip(1), (a,b) => b-a).All(d => d >= transport.SendIntervalMs), "deadline heartbeats do not bypass chat pacing");

        transport.Reset("Leader Player@World");
        transport.SendTo(peers[0], pulse, true); transport.Poll();
        time += 600_000;
        var scheduled = new Packet { Type = Kind.Scheduled, GroupId = group, RunId = Guid.NewGuid().ToString("N"), EmoteId = 21, StartAtUnixMs = time+3_000 };
        Check(transport.EstimatePriorityBroadcastMs(scheduled, 1) < 5_000, "stale pulse routes do not accumulate missed rounds or block future starts");
        transport.SendTo(peers[0], pulse, true);
        transport.SendTo(peers[0], roster, true);
        transport.ForgetRecipient(peers[0], group);
        Check(transport.PendingFrames == 0 && transport.EstimatePriorityBroadcastMs(scheduled, 1) < 5_000,
            "removing a member clears its queued packets and liveness budget");
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
    private static void PriorityFifoAndPresence()
    {
        long time = 1_800_000_000_000;
        using var transport = new Recorder(() => time);
        transport.Reset("Leader Player@World");
        var group = Guid.NewGuid().ToString("N");
        var pulse = new Packet { Type = PresenceKinds.Pulse, GroupId = group };
        var pong = new Packet { Type = Kind.Pong, GroupId = group, Nonce = Guid.NewGuid().ToString("N") };
        var cancelled = new Packet { Type = Kind.Cancelled, GroupId = group, RunId = Guid.NewGuid().ToString("N") };
        var prepare = new Packet { Type = Kind.Prepare, GroupId = group, RunId = Guid.NewGuid().ToString("N"), EmoteId = 21 };
        foreach (var packet in new[] { pulse, pong, cancelled, prepare })
            transport.SendTo("Guest Player@World", packet, true);
        var received = new List<string>();
        while (transport.PendingFrames > 0)
        {
            transport.Poll();
            received.Add(Decode(transport.Sent.Last().Text, "Leader Player@World", time).Type);
            time += transport.SendIntervalMs;
        }
        Check(received.SequenceEqual(new[] { Kind.Cancelled, Kind.Prepare, Kind.Pong }),
            "delivered critical control replaces redundant queued presence before the clock reply");

        transport.Reset("Leader Player@World");
        transport.SendTo("Guest Player@World", pulse, true);
        transport.SendTo("Guest Player@World", pulse, true);
        Check(transport.PendingFrames == 1, "unsent presence for same peer coalesces");
        transport.SendTo("Other Player@World", pulse, true);
        transport.SendTo("Guest Player@World", pulse with { GroupId = Guid.NewGuid().ToString("N") }, true);
        Check(transport.PendingFrames == 3, "presence for distinct peer or group is retained");
        transport.SendTo("Guest Player@World", pulse, true);
        transport.Poll();
        Check(transport.Sent.Last().Target == "Guest Player@World" && Decode(transport.Sent.Last().Text, "Leader Player@World", time).GroupId == group,
            "coalesced presence keeps its original FIFO position");

        transport.Reset("Leader Player@World");
        var scheduled = new Packet { Type = Kind.Scheduled, GroupId = group, RunId = Guid.NewGuid().ToString("N"),
            EmoteId = 21, Revision = 1, CountdownSeconds = 3, StartAtUnixMs = time + 3_000 };
        var baseline = transport.EstimatePriorityBroadcastMs(scheduled, 1);
        transport.SendTo("Guest Player@World", pulse, true);
        transport.SendTo("Guest Player@World", pong, true);
        Check(transport.EstimatePriorityBroadcastMs(scheduled, 1) == baseline, "presence and pongs cannot delay a critical deadline");
        transport.SendTo("Guest Player@World", cancelled, true);
        transport.SendTo("Guest Player@World", prepare, true);
        var aheadFrames = ChatCodec.Encode(cancelled).Count + ChatCodec.Encode(prepare).Count;
        Check(transport.EstimatePriorityBroadcastMs(scheduled, 1) >= baseline + aheadFrames * transport.SendIntervalMs,
            "deadline budget includes all earlier critical frames and any heartbeat that becomes overdue");
    }
    private static Packet Decode(string text, string sender, long now)
    {
        var assembler = new ChatFrameAssembler();
        Check(assembler.TryAccept(sender, text, now, out var packet) && packet != null, "single frame decodes");
        return packet!;
    }
    private static void Check(bool condition, string name)
    { count++; if (!condition) throw new InvalidOperationException($"Transport test failed: {name}"); }
    private sealed class Recorder : PacedChatTransport
    {
        private readonly Func<long> clock;
        public Recorder(Func<long> now) : base(now) { clock = now; }
        public List<(string Target, string Text, long At)> Sent { get; } = [];
        protected override void SendFrame(string recipient, string text) => Sent.Add((recipient, text, clock()));
    }
}
