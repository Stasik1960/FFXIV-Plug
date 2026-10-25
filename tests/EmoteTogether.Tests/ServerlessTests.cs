using EmoteTogether.Client;
using EmoteTogether.Protocol;

namespace EmoteTogether.Tests;

internal static class ServerlessTests
{
    private static int count;
    public static void Run()
    {
        ConsentAndAuthority();
        CloseBeforeDelivery();
        FirstStartWaitsForClocks();
        ConfigurableCountdown();
        ThreePlayerRun();
        RefusalAndPartition();
        FullGroup();
        Console.WriteLine($"PASS: serverless groups over paced/fragmented simulated tells ({count} assertions)");
    }
    private static void ConsentAndAuthority()
    {
        using var scene = new Scene();
        var host = scene.Add("Leader Player@World", 300_000);
        var guest = scene.Add("Guest Player@World", -600_000);
        var stranger = scene.Add("Unknown Player@World");
        Check(host.Client.IsConnected && guest.Client.IsConnected, "logged-in identities activate automatically");
        stranger.Transport.SendTo(guest.Name, new Packet { Type = Kind.Group, GroupId = Guid.NewGuid().ToString("N"), LeaderId = stranger.Name,
            Members = [new(stranger.Name, stranger.Name), new(guest.Name, guest.Name)], Revision = 1 });
        scene.Advance(5_000);
        Check(guest.Client.GroupId == null, "unsolicited roster cannot force membership");
        host.Client.CreateGroup();
        host.Client.Invite(guest.Name);
        scene.Until(() => guest.Client.Invitation != null, "invitation");
        Check(guest.Client.GroupId == null && host.Client.Members.Length == 1, "invite does not join before consent");
        guest.Client.RespondInvite(false);
        scene.Advance(5_000);
        Check(guest.Client.GroupId == null && host.Client.Members.Length == 1, "decline preserves membership");
        host.Client.Invite(guest.Name);
        scene.Until(() => guest.Client.Invitation != null, "second invitation");
        guest.Client.RespondInvite(true);
        scene.Until(() => guest.Client.Members.Length == 2 && guest.Client.ClockReady, "membership and clocks", 100_000);
        Check(Math.Abs(guest.Client.LeaderNowUnixMs - host.Client.LeaderNowUnixMs) <= 100, "NTP corrects clocks differing by fifteen minutes");
        guest.Client.Start(21);
        scene.Advance(5_000);
        Check(host.Client.PendingRunId == null, "guest cannot start a run");
        var group = host.Client.GroupId;
        var members = host.Client.Members;
        guest.Client.LeaveGroup();
        Check(guest.Client.GroupId == null, "leave clears local group immediately");
        host.Transport.SendTo(guest.Name, new Packet { Type = Kind.Group, GroupId = group, LeaderId = host.Name, Members = members, Revision = 999 });
        scene.Advance(15_000);
        Check(guest.Client.GroupId == null, "late roster cannot rejoin a player who left");
    }
    private static void ThreePlayerRun()
    {
        using var scene = new Scene();
        var host = scene.Add("Leader Player@World", 123_456);
        var guest = scene.Add("Second Player@World", -222_222);
        var third = scene.Add("Third Player@World", 9_876_543);
        host.Client.CreateGroup();
        scene.Join(host, guest);
        scene.Join(host, third);
        scene.Until(() => guest.Client.ClockReady && third.Client.ClockReady, "both clocks", 120_000);
        host.Client.PrepareReceived += p => host.Client.SetReady(p.RunId!, true);
        guest.Client.PrepareReceived += p => guest.Client.SetReady(p.RunId!, true);
        var autoThird = false;
        third.Client.PrepareReceived += p => { if (autoThird) third.Client.SetReady(p.RunId!, true); };
        host.Client.Start(21);
        scene.Until(() => scene.Nodes.All(n => n.Client.PendingRunId != null), "prepare all three", 60_000);
        scene.Advance(6_000);
        Check(scene.Nodes.All(n => n.Schedules.Count == 0), "readiness barrier waits for third member");
        third.Client.SetReady(third.Client.PendingRunId!, true);
        scene.Until(() => scene.Nodes.All(n => n.Schedules.Count == 1), "shared schedule", 90_000);
        Check(scene.Nodes.Select(n => n.Schedules[0].StartAtUnixMs).Distinct().Count() == 1, "same leader deadline");
        Check(scene.Nodes.All(n => n.Schedules[0].StartAtUnixMs - n.Client.LeaderNowUnixMs >= 500), "paced delivery leaves enough time");
        guest.Client.Cancel();
        Check(guest.Client.PendingRunId == null, "cancel stops local pending run");
        scene.Until(() => scene.Nodes.All(n => n.Client.PendingRunId == null), "participant cancellation", 60_000);
        autoThird = true;
        host.Client.Start(22);
        scene.Until(() => scene.Nodes.All(n => n.Schedules.Count == 2), "second schedule", 90_000);
        host.Transport.SendTo(guest.Name, guest.Schedules[0]);
        scene.Advance(2_000);
        Check(guest.Schedules.Count == 2, "stale schedule cannot run twice");
        var deadline = host.Schedules[1].StartAtUnixMs;
        scene.Until(() => scene.Nodes.All(n => n.Client.LeaderNowUnixMs >= deadline), "reach deadline", 120_000);
        foreach (var node in scene.Nodes) node.Client.CompleteLocalRun();
        scene.Advance(3_000);
        Check(scene.Nodes.All(n => n.Client.PendingRunId == null), "local runs consumed");
        host.Client.Start(23);
        scene.Until(() => scene.Nodes.All(n => n.Schedules.Count == 3), "next completed run", 90_000);
        third.Client.LeaveGroup();
        scene.Until(() => host.Client.PendingRunId == null && guest.Client.PendingRunId == null && host.Client.Members.Length == 2, "leaving cancels countdown", 60_000);
        host.Client.LeaveGroup();
        scene.Until(() => guest.Client.GroupId == null, "leader closes group", 60_000);
    }
    private static void FullGroup()
    {
        using var scene = new Scene();
        var host = scene.Add("Leader Player@World");
        host.Client.CreateGroup();
        var first = scene.Add("Alpha Player@World", 2_000);
        var second = scene.Add("Bravo Player@World", -3_000);
        host.Client.Invite(first.Name);
        host.Client.Invite(second.Name);
        scene.Until(() => first.Client.Invitation != null && second.Client.Invitation != null, "parallel invitations");
        first.Client.RespondInvite(true);
        second.Client.RespondInvite(true);
        scene.Until(() => scene.Nodes.All(n => n.Client.Members.Length == 3), "coalesced joining snapshots preserve both accepted invites");
        Check(first.Client.GroupId == host.Client.GroupId && second.Client.GroupId == host.Client.GroupId, "simultaneous acceptance works");
        var names = new[] { "Charlie", "Delta", "Echo", "Foxtrot", "Golf" };
        foreach (var name in names) scene.Join(host, scene.Add($"{name} Player@World", names.ToList().IndexOf(name) * 12_345));
        scene.Until(() => scene.Nodes.All(n => n.Client.Members.Length == 8 && n.Client.ClockReady), "eight-member roster and clocks", 180_000);
        foreach (var node in scene.Nodes) node.Client.PrepareReceived += p => node.Client.SetReady(p.RunId!, true);
        host.Client.Start(21);
        scene.Until(() => scene.Nodes.All(n => n.Schedules.Count == 1), "eight-member schedule", 120_000);
        Check(scene.Nodes.Select(n => n.Schedules[0].RunId).Distinct().Count() == 1, "eight members share one run");
        Check(scene.Nodes.All(n => n.Client.ScheduledStartAtUnixMs - n.Client.LeaderNowUnixMs >= 500), "all schedules before deadline");
        Check(host.Client.RequestedCountdownSeconds == 3 && host.Client.EffectiveCountdownSeconds >= 10,
            "larger group exposes its delivery-constrained countdown instead of hiding it");
        Check(scene.SentFrames.All(f => f.Text.Length <= ChatCodec.MaxFrameLength), "all frames fit bounds");
        foreach (var node in scene.Nodes)
        {
            var times = scene.SentFrames.Where(f => f.Sender == node.Name).Select(f => f.At).ToArray();
            Check(times.Zip(times.Skip(1), (a, b) => b - a).All(delta => delta >= node.Transport.SendIntervalMs), "one sender never bursts frames");
        }
    }
    private static void Check(bool condition, string name)
    { count++; if (!condition) throw new InvalidOperationException($"Serverless test failed: {name}"); }

    private static void CloseBeforeDelivery()
    {
        using var scene = new Scene();
        var host = scene.Add("Leader Player@World");
        var guest = scene.Add("Guest Player@World");
        host.Client.CreateGroup();
        host.Client.Invite(guest.Name);
        host.Client.LeaveGroup();
        scene.Advance(15_000);
        Check(guest.Client.Invitation == null && guest.Client.GroupId == null, "disband purges unsent invitations");
        host.Client.CreateGroup();
        host.Client.Invite(guest.Name);
        scene.Until(() => guest.Client.Invitation != null, "new invite after disband");
        guest.Client.RespondInvite(true);
        scene.Until(() => host.Client.Members.Length == 2 && guest.Client.IsJoining, "accepted but roster not delivered");
        host.Client.LeaveGroup();
        scene.Until(() => !guest.Client.IsJoining, "closed before joining snapshot arrives");
        Check(guest.Client.GroupId == null, "closing acknowledged joining never grants ghost membership");
    }

    private static void RefusalAndPartition()
    {
        using var scene = new Scene();
        var host = scene.Add("Leader Player@World");
        var guest = scene.Add("Guest Player@World");
        var outsider = scene.Add("Unknown Player@World");
        host.Client.CreateGroup();
        scene.Join(host, guest);
        scene.Until(() => guest.Client.ClockReady, "clock before refusal");
        host.Client.PrepareReceived += p => host.Client.SetReady(p.RunId!, true);
        guest.Client.PrepareReceived += p => guest.Client.SetReady(p.RunId!, true);
        host.Client.Start(21);
        scene.Until(() => host.Schedules.Count == 1 && guest.Schedules.Count == 1, "countdown before negative readiness");
        outsider.Transport.SendTo(host.Name, new Packet { Type = Kind.Cancel, GroupId = host.Client.GroupId, RunId = host.Client.PendingRunId });
        scene.Advance(1_000);
        Check(host.Client.PendingRunId != null, "unknown sender cannot cancel another group");
        guest.Client.SetReady(guest.Client.PendingRunId!, false, "Character is busy");
        scene.Until(() => host.Client.PendingRunId == null && guest.Client.PendingRunId == null, "negative readiness cancels scheduled run");
        scene.Dropped.Add(guest.Name);
        host.Client.Start(22);
        scene.Until(() => host.Client.PendingRunId == null, "missing ready times out", 100_000);
        Check(host.Schedules.Count == 1, "dropped preparation never schedules another run");
        scene.Advance(300_000);
        Check(host.Client.Members.Length == 1 && guest.Client.IsLeader && guest.Client.Members.Length == 1 && guest.Client.GroupId != host.Client.GroupId,
            "partition expires old membership and isolates recovered singleton in a fresh group");
    }

    private static void FirstStartWaitsForClocks()
    {
        using var scene = new Scene();
        var host = scene.Add("Leader Player@World", 400_000);
        var guest = scene.Add("Guest Player@World", -500_000);
        host.Client.CreateGroup();
        scene.Join(host, guest);
        host.Client.PrepareReceived += p => host.Client.SetReady(p.RunId!, true);
        Packet? prepared = null;
        guest.Client.PrepareReceived += p => prepared = p;
        host.Client.Start(21);
        scene.Until(() => prepared != null, "prepare before initial clock negotiation completes");
        scene.Until(() => guest.Client.ClockReady, "clock catches up with pending preparation");
        Check(guest.Client.PendingRunId == prepared!.RunId, "initial unsynchronized preparation waits instead of cancelling");
        guest.Client.SetReady(prepared.RunId!, true);
        scene.Until(() => host.Schedules.Count == 1 && guest.Schedules.Count == 1, "first run after clock negotiation");
        Check(host.Schedules[0].StartAtUnixMs == guest.Schedules[0].StartAtUnixMs, "first-start shared deadline");
    }

    private static void ConfigurableCountdown()
    {
        foreach (var seconds in new[] { 3, 5, 10 })
        {
            using var scene = new Scene();
            var host = scene.Add("Leader Player@World");
            var guest = scene.Add("Guest Player@World");
            host.Client.CreateGroup();
            scene.Join(host, guest);
            scene.Until(() => guest.Client.ClockReady, "clocks for configurable countdown");
            host.Client.Start(21, 0);
            Check(host.Client.PendingRunId == null, "zero countdown rejected");
            host.Client.Start(21, -1);
            Check(host.Client.PendingRunId == null, "negative countdown rejected");
            host.Client.Start(21, 61);
            Check(host.Client.PendingRunId == null, "excessive countdown rejected");
            host.Client.PrepareReceived += p => host.Client.SetReady(p.RunId!, true);
            guest.Client.PrepareReceived += p => guest.Client.SetReady(p.RunId!, true);
            long scheduledAt = 0;
            host.Client.ScheduledReceived += _ => scheduledAt = scene.Now;
            host.Client.Start(21, seconds);
            scene.Until(() => host.Schedules.Count == 1 && guest.Schedules.Count == 1, $"{seconds}-second pair schedule");
            Check(host.Schedules[0].StartAtUnixMs - scheduledAt == seconds * 1_000L, $"pair honors selected {seconds}-second countdown");
            Check(guest.Client.RequestedCountdownSeconds == seconds && host.Client.EffectiveCountdownSeconds == seconds && guest.Client.EffectiveCountdownSeconds == seconds,
                "requested and agreed countdown delivered to both participants");
        }
    }
    private sealed class Scene : IDisposable
    {
        public long Now { get; private set; } = 1_800_000_000_000;
        public List<Node> Nodes { get; } = [];
        public List<(string Sender, string Text, long At)> SentFrames { get; } = [];
        public HashSet<string> Dropped { get; } = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string Sender, string Recipient, string Text, long At)> network = [];
        public Node Add(string name, long skew = 0)
        {
            var transport = new SimulatedTransport(this, () => Now + skew);
            var client = new GroupClient(transport, () => Now + skew);
            client.SetIdentity(name);
            var node = new Node(name, transport, client);
            client.ScheduledReceived += p => node.Schedules.Add(p);
            Nodes.Add(node);
            return node;
        }
        public void Join(Node host, Node guest)
        {
            host.Client.Invite(guest.Name);
            Until(() => guest.Client.Invitation != null, $"invite {guest.Name}", 90_000);
            guest.Client.RespondInvite(true);
            Until(() => guest.Client.GroupId == host.Client.GroupId && guest.Client.Members.Length >= 2, $"join {guest.Name}", 90_000);
        }
        public void Advance(long milliseconds)
        {
            var end = Now + milliseconds;
            while (Now < end)
            {
                Now += 50;
                foreach (var frame in network.Where(f => f.At <= Now).ToArray())
                {
                    network.Remove(frame);
                    Nodes.FirstOrDefault(n => string.Equals(n.Name, frame.Recipient, StringComparison.OrdinalIgnoreCase))?.Transport.Inbox.Enqueue((frame.Sender, frame.Text));
                }
                foreach (var node in Nodes) node.Client.Poll();
            }
        }
        public void Until(Func<bool> condition, string label, long timeout = 60_000)
        {
            var end = Now + timeout;
            while (Now < end) { if (condition()) return; Advance(50); }
            throw new InvalidOperationException($"Timeout: {label}; " + string.Join(" | ", Nodes.Select(n => $"{n.Name}: {n.Client.Status}; frames {n.Transport.PendingFrames}")));
        }
        public void Send(string sender, string recipient, string text)
        {
            SentFrames.Add((sender, text, Now));
            if (!Dropped.Contains(sender) && !Dropped.Contains(recipient)) network.Add((sender, recipient, text, Now + 100));
        }
        public void Dispose() { foreach (var node in Nodes) node.Client.Dispose(); }
    }
    private sealed record Node(string Name, SimulatedTransport Transport, GroupClient Client)
    { public List<Packet> Schedules { get; } = []; }
    private sealed class SimulatedTransport : PacedChatTransport
    {
        private readonly Scene scene;
        private readonly Func<long> now;
        public SimulatedTransport(Scene scene, Func<long> now) : base(now) { this.scene = scene; this.now = now; }
        public Queue<(string Sender, string Text)> Inbox { get; } = [];
        protected override void SendFrame(string recipient, string text) => scene.Send(LocalIdentity!, recipient, text);
        public override void Poll()
        { while (Inbox.TryDequeue(out var item)) ReceiveFrame(item.Sender, item.Text, now()); base.Poll(); }
    }
}
