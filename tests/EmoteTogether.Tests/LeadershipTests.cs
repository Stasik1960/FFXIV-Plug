using EmoteTogether.Client;
using EmoteTogether.Protocol;

namespace EmoteTogether.Tests;

internal static class LeadershipTests
{
    private static int assertions;

    public static void Run()
    {
        assertions = 0;
        TransferAndTransferAgain();
        AuthorityAndCountdown();
        RejoiningMemberMustConfirmFreshRoster();
        OfferTimeout();
        CommitLossFailsClosed();
        AcknowledgementRetry();
        FullGroupTransfer();
        Console.WriteLine($"PASS: leadership transfer (7 scenarios, {assertions} assertions)");
    }

    private static void TransferAndTransferAgain()
    {
        using var scene = new Scene();
        var host = scene.Add("Host Player@World", 900_000);
        var next = scene.Add("Next Player@World", -400_000);
        var third = scene.Add("Third Player@World", 123_456);
        host.Client.CreateGroup();
        scene.Join(host, next);
        scene.Join(host, third);
        scene.WaitForClocks();
        var group = host.Client.GroupId;
        host.Client.TransferLeadership(next.Name);
        Check(host.Client.IsTransferringLeadership && host.Client.PendingLeaderId == next.Name, "old leader exposes pending handoff");
        host.Client.Start(21);
        Check(host.Client.PendingRunId == null, "old leader cannot start during transfer");
        scene.Until(() => next.Client.IsLeader && next.Client.IsTransferringLeadership, "candidate waits for group acknowledgement");
        next.Client.Start(21);
        Check(next.Client.PendingRunId == null, "new leader cannot start before settled barrier");
        scene.WaitForLeader(next.Name);
        Check(scene.Nodes.All(n => n.Client.GroupId == group && n.Client.Members.Length == 3), "transfer preserves group and roster");
        Check(!host.Client.IsLeader && next.Client.IsLeader, "only designated member becomes leader");
        var oldCommit = next.Received.First(p => p.Type == LeadershipKinds.Changed);
        scene.WaitForClocks();
        Check(scene.Nodes.All(n => Math.Abs(n.Client.LeaderNowUnixMs - next.Client.LeaderNowUnixMs) <= 100), "every clock resynchronizes to new leader");

        foreach (var member in scene.Nodes) member.Client.PrepareReceived += packet => member.Client.SetReady(packet.RunId!, true);
        host.Client.Start(21);
        Check(host.Client.PendingRunId == null, "former leader loses start permission");
        next.Client.Start(21, 3);
        scene.Until(() => scene.Nodes.All(n => n.Schedules.Count == 1), "new leader schedules group emote");
        var deadline = next.Schedules[0].StartAtUnixMs;
        scene.Until(() => scene.Nodes.All(n => n.Client.LeaderNowUnixMs >= deadline), "consume performance deadline");
        foreach (var member in scene.Nodes) member.Client.CompleteLocalRun();
        scene.Advance(3_000);
        next.Client.TransferLeadership(third.Name);
        scene.WaitForLeader(third.Name);
        host.Transport.SendTo(next.Name, oldCommit, true);
        scene.Advance(5_000);
        Check(scene.Nodes.All(n => n.Client.LeaderId == third.Name), "stale commit cannot revert a later transfer");
        Check(scene.MaxLeaders <= 1, "normal chained transfers never expose two leaders");
    }

    private static void AuthorityAndCountdown()
    {
        using var scene = new Scene();
        var host = scene.Add("Host Player@World");
        var next = scene.Add("Next Player@World");
        var outsider = scene.Add("Fake Player@World");
        host.Client.CreateGroup();
        scene.Join(host, next);
        scene.WaitForClocks();
        next.Client.TransferLeadership(host.Name);
        host.Client.TransferLeadership(host.Name);
        host.Client.TransferLeadership(outsider.Name);
        Check(host.Client.IsLeader && !host.Client.IsTransferringLeadership, "guest, self and nonmember promotion are rejected");
        outsider.Transport.SendTo(next.Name, new Packet { Type = LeadershipKinds.Changed, GroupId = host.Client.GroupId,
            Nonce = Guid.NewGuid().ToString("N"), FromName = host.Name, LeaderId = next.Name,
            Revision = 3, Members = host.Client.Members }, true);
        scene.Advance(4_000);
        Check(host.Client.IsLeader && !next.Client.IsLeader, "payload old-leader claim cannot forge game-authenticated sender");
        host.Client.PrepareReceived += packet => host.Client.SetReady(packet.RunId!, true);
        next.Client.PrepareReceived += packet => next.Client.SetReady(packet.RunId!, true);
        host.Client.Start(21, 10);
        scene.Until(() => next.Client.ScheduledStartAtUnixMs != null, "countdown before transfer rejection");
        host.Client.TransferLeadership(next.Name);
        Check(host.Client.IsLeader && !host.Client.IsTransferringLeadership, "active countdown blocks transfer");
        host.Client.Cancel();
        scene.Until(() => next.Client.PendingRunId == null, "cancel before transfer");
        host.Client.TransferLeadership(next.Name);
        scene.Until(() => host.Client.IsTransferringLeadership, "transfer begins after cancel");
        host.Client.LeaveGroup();
        scene.Until(() => next.Client.GroupId == null, "disband clears pending handoff");
        Check(!host.Client.IsTransferringLeadership && !next.Client.IsTransferringLeadership, "closing clears leadership state");
    }

    private static void OfferTimeout()
    {
        using var scene = new Scene();
        var host = scene.Add("Host Player@World");
        var next = scene.Add("Next Player@World");
        host.Client.CreateGroup();
        scene.Join(host, next);
        scene.WaitForClocks();
        scene.Dropped.Add(next.Name);
        host.Client.TransferLeadership(next.Name);
        scene.Advance(125_000);
        Check(host.Client.IsLeader && !host.Client.IsTransferringLeadership, "unanswered offer retains previous leader");
        Check(!next.Client.IsLeader && scene.MaxLeaders <= 1, "missing offer never promotes candidate");
        var offers = scene.SentPackets.Count(p => p.Packet.Type == LeadershipKinds.Offer);
        Check(offers <= 3, "offer retries are bounded");
    }

    private static void RejoiningMemberMustConfirmFreshRoster()
    {
        using var scene = new Scene();
        var host = scene.Add("Host Player@World");
        var next = scene.Add("Next Player@World");
        host.Client.CreateGroup();
        scene.Join(host, next);
        scene.WaitForClocks();
        next.Client.LeaveGroup();
        scene.Until(() => host.Client.Members.Length == 1, "first membership leaves");
        host.Client.Invite(next.Name);
        scene.Until(() => next.Client.Invitation != null, "fresh invitation");
        next.Client.RespondInvite(true);
        scene.Until(() => host.Client.Members.Length == 2 && next.Client.IsJoining, "rejoin before fresh roster delivery");
        host.Client.TransferLeadership(next.Name);
        Check(!host.Client.IsTransferringLeadership, "proof from previous membership cannot authorize transfer before fresh roster");
        scene.Until(() => next.Client.GroupId == host.Client.GroupId && next.Client.ClockReady, "fresh membership confirms roster");
        host.Client.TransferLeadership(next.Name);
        scene.WaitForLeader(next.Name);
        Check(next.Client.IsLeader, "freshly confirmed rejoining member can become leader");
    }

    private static void CommitLossFailsClosed()
    {
        using var scene = new Scene();
        var host = scene.Add("Host Player@World");
        var next = scene.Add("Next Player@World");
        host.Client.CreateGroup();
        scene.Join(host, next);
        scene.WaitForClocks();
        host.Client.TransferLeadership(next.Name);
        scene.Until(() => !host.Client.IsLeader && host.Client.IsTransferringLeadership, "origin demotes before commit delivery");
        scene.Dropped.Add(next.Name);
        scene.Advance(125_000);
        Check(host.Client.GroupId == null && !next.Client.IsLeader, "uncertain committed transfer fails closed without rollback election");
        scene.Advance(190_000);
        Check(next.Client.GroupId == null && scene.MaxLeaders <= 1, "uncommitted candidate eventually closes stale group");
    }

    private static void AcknowledgementRetry()
    {
        using var scene = new Scene();
        var host = scene.Add("Host Player@World");
        var next = scene.Add("Next Player@World");
        host.Client.CreateGroup();
        scene.Join(host, next);
        scene.WaitForClocks();
        host.Client.TransferLeadership(next.Name);
        scene.Until(() => !host.Client.IsLeader && host.Client.IsTransferringLeadership, "commit begins");
        scene.DroppedRoutes.Add((next.Name, host.Name));
        scene.Until(() => next.Client.IsLeader && next.Client.IsTransferringLeadership, "candidate committed while ack route lost");
        scene.Advance(20_000);
        Check(next.Client.IsTransferringLeadership, "candidate stays blocked while acknowledgements are lost");
        scene.DroppedRoutes.Clear();
        scene.WaitForLeader(next.Name);
        Check(next.Client.IsLeader && !host.Client.IsLeader, "bounded commit retry recovers a lost acknowledgement");
    }

    private static void FullGroupTransfer()
    {
        using var scene = new Scene();
        var host = scene.Add("Host Player@World", 300_000);
        host.Client.CreateGroup();
        Node? next = null;
        foreach (var name in new[] { "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf" })
        {
            var member = scene.Add($"{name} Player@World", name.Length * -13_000);
            next ??= member;
            scene.Join(host, member);
        }
        scene.Until(() => scene.Nodes.All(n => n.Client.Members.Length == 8), "full roster before transfer", 180_000);
        scene.WaitForClocks();
        var before = scene.Now;
        host.Client.TransferLeadership(next!.Name);
        scene.WaitForLeader(next.Name, 115_000);
        Check(scene.Now - before < 120_000, "paced fragmented eight-player transfer fits lifetime");
        Check(scene.Nodes.All(n => n.Client.Members.Length == 8) && scene.MaxLeaders <= 1, "eight-player transfer preserves roster and exclusive leadership");
        scene.WaitForClocks();
        Check(scene.Nodes.All(n => Math.Abs(n.Client.LeaderNowUnixMs - next.Client.LeaderNowUnixMs) <= 100), "full group resynchronizes to promoted leader");
        foreach (var node in scene.Nodes)
        {
            var times = scene.SentFrames.Where(f => f.Sender == node.Name).Select(f => f.At).ToArray();
            Check(times.Zip(times.Skip(1), (a, b) => b - a).All(delta => delta >= node.Transport.SendIntervalMs), "handoff never bursts paced game messages");
        }
    }

    private static void Check(bool condition, string name)
    { assertions++; if (!condition) throw new InvalidOperationException($"Leadership test failed: {name}"); }

    private sealed class Scene : IDisposable
    {
        public long Now { get; private set; } = 1_800_000_000_000;
        public List<Node> Nodes { get; } = [];
        public HashSet<string> Dropped { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<(string Sender, string Recipient)> DroppedRoutes { get; } = [];
        public List<(string Sender, string Text, long At)> SentFrames { get; } = [];
        public List<(string Sender, Packet Packet)> SentPackets { get; } = [];
        public int MaxLeaders { get; private set; }
        private readonly List<(string Sender, string Recipient, string Text, long At)> network = [];
        private readonly Dictionary<string, ChatFrameAssembler> inspectors = new(StringComparer.OrdinalIgnoreCase);

        public Node Add(string name, long skew = 0)
        {
            var transport = new SimulatedTransport(this, () => Now + skew);
            var client = new GroupClient(transport, () => Now + skew);
            client.SetIdentity(name);
            var node = new Node(name, transport, client);
            client.ScheduledReceived += packet => node.Schedules.Add(packet);
            transport.MessageReceived += envelope => node.Received.Add(envelope.Packet);
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
        public void WaitForClocks() => Until(() => Nodes.Where(n => n.Client.GroupId != null).All(n => n.Client.ClockReady), "leadership clock synchronization", 150_000);
        public void WaitForLeader(string leader, long timeout = 90_000) =>
            Until(() => Nodes.Where(n => n.Client.GroupId != null).All(n => n.Client.LeaderId == leader && !n.Client.IsTransferringLeadership), "settled leader " + leader, timeout);
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
                foreach (var node in Nodes)
                {
                    node.Client.Poll();
                    MaxLeaders = Math.Max(MaxLeaders, Nodes.Count(n => n.Client.IsLeader));
                }
            }
        }
        public void Until(Func<bool> condition, string label, long timeout = 90_000)
        {
            var end = Now + timeout;
            while (Now < end) { if (condition()) return; Advance(50); }
            throw new InvalidOperationException($"Timeout: {label}; " + string.Join(" | ", Nodes.Select(n => $"{n.Name}: leader={n.Client.LeaderId}; transfer={n.Client.IsTransferringLeadership}; {n.Client.Status}; frames={n.Transport.PendingFrames}")));
        }
        public void Send(string sender, string recipient, string text)
        {
            SentFrames.Add((sender, text, Now));
            var key = sender + ">" + recipient;
            if (!inspectors.TryGetValue(key, out var inspector)) inspectors[key] = inspector = new ChatFrameAssembler();
            if (inspector.TryAccept(sender, text, Now, out var packet) && packet != null) SentPackets.Add((sender, packet));
            if (!Dropped.Contains(sender) && !Dropped.Contains(recipient) && !DroppedRoutes.Contains((sender, recipient)))
                network.Add((sender, recipient, text, Now + 100));
        }
        public void Dispose() { foreach (var node in Nodes) node.Client.Dispose(); }
    }
    private sealed record Node(string Name, SimulatedTransport Transport, GroupClient Client)
    {
        public List<Packet> Received { get; } = [];
        public List<Packet> Schedules { get; } = [];
    }
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
