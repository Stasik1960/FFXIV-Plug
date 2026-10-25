using EmoteTogether.Client;
using EmoteTogether.Protocol;

namespace EmoteTogether.Tests;

internal static class PresenceTests
{
    private static int assertions;
    public static void Run()
    {
        assertions = 0;
        QuietIdleAndFreshStart();
        StrictFollowerTimeout();
        FullGroupIdleAndFreshStart();
        foreach (var phase in new[] { 3_000L, 6_000L, 9_000L, 11_800L }) FullGroupIdleAndFreshStart(phase);
        GracefulFollower();
        GracefulLeader(3);
        GracefulLeader(8);
        AbruptRecoverySkipsOfflineCandidate();
        AbruptLeader(2);
        AbruptLeader(3);
        AbruptLeader(8);
        OutsidersCannotRemoveMembers();
        MinorityPartitionsFailClosed();
        DistinctNamesFullGroup();
        Console.WriteLine($"PASS: quiet presence and automatic recovery (17 scenarios, {assertions} assertions)");
    }

    private static void QuietIdleAndFreshStart()
    {
        using var scene = new Scene();
        var host = scene.Add("Host Player@World", 700_000);
        var guest = scene.Add("Guest Player@World", -800_000);
        host.Client.CreateGroup(); scene.Join(host, guest); scene.WaitForClocks(); scene.Advance(5_000);
        var before = scene.Packets.Count;
        scene.Advance(610_000);
        var idle = scene.Packets.Skip(before).ToArray();
        Check(idle.All(p => p.Packet.Type == PresenceKinds.Pulse), "idle groups send only compact one-way presence");
        Check(idle.Length is >= 95 and <= 104, "strict 30 s detection uses bounded one-way presence every 12 s per peer");
        Check(!guest.Client.ClockReady && host.Client.Members.Length == 2 && guest.Client.GroupId == host.Client.GroupId,
            "clock can age without losing group membership or starting a ping loop");
        host.Client.PrepareReceived += p => host.Client.SetReady(p.RunId!, true);
        guest.Client.PrepareReceived += p => guest.Client.SetReady(p.RunId!, true);
        var restart = scene.Packets.Count;
        host.Client.Start(21, 3);
        scene.Until(() => host.Schedules.Count == 1 && guest.Schedules.Count == 1, "fresh timing before a run after long idle");
        Check(scene.Packets.Skip(restart).Count(p => p.Packet.Type == Kind.Ping) is >= 2 and <= 4, "on-demand clock exchange is bounded");
        Check(guest.Client.ClockReady && Math.Abs(guest.Client.LeaderNowUnixMs - host.Client.LeaderNowUnixMs) <= 100, "on-demand sync restores independent clocks");
        Console.WriteLine($"  Idle pair: {idle.Length} one-way presence packets / 610 s; no idle Ping/Pong");
    }

    private static void StrictFollowerTimeout()
    {
        using var scene = new Scene();
        var host = scene.Add("Host Player@World"); var guest = scene.Add("Guest Player@World");
        host.Client.CreateGroup(); scene.Join(host, guest); scene.WaitForClocks(); scene.Advance(5_000);
        guest.Active = false; scene.Dropped.Add(guest.Name);
        scene.Until(() => host.Client.Members.Length == 1, "30 s follower timeout", 31_000);
        var elapsed = scene.Now - host.ReceivedAt[guest.Name];
        Check(elapsed is >= 30_000 and <= 30_100, "established missing follower is removed exactly 30 s after its last packet");
    }

    private static void FullGroupIdleAndFreshStart(long phase = 0)
    {
        using var scene = new Scene();
        var host = scene.Add("Host Player@World"); host.Client.CreateGroup();
        foreach (var name in new[] { "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf" })
            scene.Join(host, scene.Add($"{name} Player@World", name.Length * -12_345));
        scene.WaitForClocks(); scene.Advance(610_000 + phase);
        Check(scene.Active.All(n => n.Client.Members.Length == 8 && n.Client.GroupId == host.Client.GroupId && !n.Client.IsRecoveringGroup),
            "eight-member presence stays connected with the 30 s timeout during ten idle minutes");
        foreach (var node in scene.Active) node.Client.PrepareReceived += p => node.Client.SetReady(p.RunId!, true);
        host.Client.Start(21, 3);
        scene.Until(() => scene.Active.All(n => n.Schedules.Count == 1), $"fresh full-group timing after long idle at phase {phase}", 120_000);
        Check(scene.Active.All(n => n.Client.Members.Length == 8 && n.Client.ClockReady), "full-group clock burst does not falsely expire members");
    }

    private static void AbruptLeader(int size)
    {
        using var scene = new Scene();
        var host = scene.Add("Host Player@World"); host.Client.CreateGroup();
        foreach (var name in new[] { "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf" }.Take(size - 1))
            scene.Join(host, scene.Add($"{name} Player@World", name.Length * -10_000));
        scene.WaitForClocks(); scene.Advance(5_000);
        var previous = host.Client.GroupId;
        var disconnectedAt = scene.Now;
        host.Active = false; scene.Dropped.Add(host.Name);
        scene.Until(() => scene.Active.All(n => n.Client.LeaderId == "Alpha Player@World" && !n.Client.IsRecoveringGroup), $"{size}-member abrupt succession", 90_000);
        Check(scene.Active.All(n => n.Client.GroupId != previous && n.Client.Members.Length == size - 1), "abrupt succession replaces epoch and removes former leader: " +
            string.Join(" | ", scene.Active.Select(n => $"{n.Name} group={n.Client.GroupId}, members={string.Join(',',n.Client.Members.Select(m=>m.Id))}")));
        Check(scene.Active.Count(n => n.Client.IsLeader) == 1, "connected survivors share one recovered leader");
        Check(scene.Active.All(n => n.FirstRecoveryAt != null && n.FirstRecoveryAt - n.ReceivedAt[host.Name] is >= 30_000 and <= 30_100),
            "leader loss is detected at 30 s after each follower's last authenticated leader packet");
        Console.WriteLine($"  Abrupt leader, {size} members: all survivors ready after {scene.Now - disconnectedAt} ms");
    }

    private static void GracefulFollower()
    {
        using var scene = new Scene();
        var host = scene.Add("Host Player@World"); var guest = scene.Add("Guest Player@World");
        host.Client.CreateGroup(); scene.Join(host, guest); scene.WaitForClocks();
        var notices = guest.Client.PrepareDeparture();
        Check(notices.Count == 1 && notices[0].Recipient == host.Name && notices[0].Packet.Type == Kind.Leave, "follower needs one targeted leave notice");
        guest.Client.Dispose(); guest.Active = false; scene.DrainDeparture(guest.Name, notices);
        scene.Until(() => host.Client.Members.Length == 1, "disabled follower is removed", 15_000);
        Check(host.Client.IsLeader && guest.Client.GroupId == null, "departure clears local state and preserves remaining leader");
    }

    private static void GracefulLeader(int size)
    {
        using var scene = new Scene();
        var host = scene.Add("Host Player@World"); host.Client.CreateGroup();
        foreach (var name in new[] { "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf" }.Take(size - 1))
            scene.Join(host, scene.Add($"{name} Player@World"));
        scene.WaitForClocks(); scene.Advance(5_000);
        var oldGroup = host.Client.GroupId;
        var notices = host.Client.PrepareDeparture();
        Check(notices.Count == size - 1 && notices[0].Recipient == "Alpha Player@World", "designated successor is notified first");
        host.Client.Dispose(); host.Active = false; scene.DrainDeparture(host.Name, notices);
        scene.Until(() => scene.Active.All(n => n.Client.LeaderId == "Alpha Player@World" && n.Client.Members.Length == size - 1 && !n.Client.IsRecoveringGroup), "graceful leader replacement", 90_000);
        Check(scene.Active.All(n => n.Client.GroupId == oldGroup && n.Client.Members.All(m => m.Id != host.Name)), "graceful succession removes former leader and preserves group");
        Check(scene.Active.Count(n => n.Client.IsLeader) == 1, "graceful succession has one leader");
    }

    private static void AbruptRecoverySkipsOfflineCandidate()
    {
        using var scene = new Scene();
        var host = scene.Add("Host Player@World", 1_000_000); host.Client.CreateGroup();
        foreach (var name in new[] { "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf" })
            scene.Join(host, scene.Add($"{name} Player@World", name.Length * 100_000));
        scene.WaitForClocks(); scene.Advance(10_000);
        var previous = host.Client.GroupId;
        host.Active = false; scene.Dropped.Add(host.Name);
        var alpha = scene.Nodes.Single(n => n.Name == "Alpha Player@World"); alpha.Active = false; scene.Dropped.Add(alpha.Name);
        scene.Until(() => scene.Active.All(n => n.Client.LeaderId == "Bravo Player@World" && !n.Client.IsRecoveringGroup), "recovery skips an additional offline candidate", 400_000);
        Check(scene.Active.Select(n => n.Client.GroupId).Distinct().Count() == 1 && scene.Active.All(n => n.Client.GroupId != previous), "abrupt recovery establishes a fresh shared group identifier");
        Check(scene.Active.All(n => n.Client.Members.Length == 6 && n.Client.Members.All(m => m.Id != host.Name && m.Id != alpha.Name)), "unreachable old leader and candidate are excluded");
        Check(scene.Active.Count(n => n.Client.IsLeader) == 1, "surviving quorum elects one leader");
        // A delayed snapshot or start from the old group cannot affect recovered membership.
        foreach (var node in scene.Active)
            host.Transport.SendTo(node.Name, new Packet { Type = Kind.Group, GroupId = previous, LeaderId = host.Name, Members = [new(host.Name, host.Name), new(node.Name, node.Name)], Revision = 999 }, true);
        scene.Advance(5_000);
        Check(scene.Active.All(n => n.Client.LeaderId == "Bravo Player@World"), "old-epoch authority cannot reclaim recovered group");
    }

    private static void OutsidersCannotRemoveMembers()
    {
        using var scene = new Scene();
        var host = scene.Add("Host Player@World"); var guest = scene.Add("Guest Player@World"); var outsider = scene.Add("Other Player@World");
        host.Client.CreateGroup(); scene.Join(host, guest); scene.WaitForClocks();
        outsider.Transport.SendTo(guest.Name, new Packet { Type = PresenceKinds.Departed, GroupId = host.Client.GroupId, LeaderId = guest.Name, Revision = 3, Nonce = Guid.NewGuid().ToString("N") }, true);
        outsider.Transport.SendTo(guest.Name, new Packet { Type = PresenceKinds.RecoveryCommit, GroupId = host.Client.GroupId, LeaderId = outsider.Name, Nonce = Guid.NewGuid().ToString("N"), NewGroupId = Guid.NewGuid().ToString("N") }, true);
        scene.Advance(8_000);
        Check(guest.Client.LeaderId == host.Name && guest.Client.Members.Length == 2, "uninvited sender cannot force departure or election");
        var runCount = guest.Schedules.Count;
        scene.Advance(250_000);
        Check(guest.Client.LeaderId == host.Name && guest.Schedules.Count == runCount, "quiet presence keeps connected group alive without gameplay");
    }

    private static void MinorityPartitionsFailClosed()
    {
        using var scene = new Scene();
        var host = scene.Add("Host Player@World"); host.Client.CreateGroup();
        foreach (var name in new[] { "Alpha", "Bravo", "Charlie", "Delta" }) scene.Join(host, scene.Add($"{name} Player@World"));
        scene.WaitForClocks(); scene.Advance(5_000);
        host.Active = false; scene.Dropped.Add(host.Name);
        var first = new[] { "Alpha Player@World", "Bravo Player@World" };
        var second = new[] { "Charlie Player@World", "Delta Player@World" };
        foreach (var a in first) foreach (var b in second) { scene.Blocked.Add((a, b)); scene.Blocked.Add((b, a)); }
        scene.Advance(400_000);
        Check(scene.Active.All(n => !n.Client.IsLeader && n.Client.GroupId == null), "two minority partitions cannot both elect a recovered leader");
    }

    private static void DistinctNamesFullGroup()
    {
        using var scene = new Scene();
        var names = new[] { "Aurelia Dawnwhisper@Sophia", "Bastian Nightbloom@Ravana", "Cerys Silverthorne@Sephirot",
            "Darian Emberwatch@Bismarck", "Evelyn Starfall@Zurvan", "Freyja Moonlark@Halicarnassus",
            "Grayson Ashenvale@Maduin", "Helena Frostwind@Seraph" };
        var host = scene.Add(names[0]); host.Client.CreateGroup();
        foreach (var name in names.Skip(1)) scene.Join(host, scene.Add(name, name.Length * -13_000));
        scene.WaitForClocks();
        Check(scene.Active.All(n => n.Client.Members.Length == 8 && n.Client.GroupId == host.Client.GroupId), "distinct-name eight-member roster survives paced join snapshots: " +
            string.Join(" | ",scene.Active.Select(n=>$"{n.Name}={n.Client.Members.Length}/{n.Client.LeaderId}, {n.Client.Status}")));
        scene.Advance(6_000);
        host.Client.TransferLeadership(names[1]);
        scene.Until(() => scene.Active.All(n => n.Client.LeaderId == names[1] && !n.Client.IsTransferringLeadership), "distinct-name manual succession", 115_000);
        scene.WaitForClocks();
        Check(scene.Active.All(n => n.Client.Members.Length == 8 && !n.Client.IsRecoveringGroup), "distinct-name handoff and clock work preserve all eight members");
        var previous = host.Client.GroupId;
        var leader = scene.Nodes.Single(n => n.Name == names[1]);
        var disconnectedAt = scene.Now;
        leader.Active = false; scene.Dropped.Add(leader.Name);
        scene.Until(() => scene.Active.All(n => n.Client.LeaderId == names[0] && !n.Client.IsRecoveringGroup), "distinct-name abrupt succession", 100_000);
        var membershipElapsed = scene.Now - disconnectedAt;
        Check(scene.Active.All(n => n.Client.GroupId != previous && n.Client.Members.Length == 7), "larger recovery commits reach all distinct-name survivors without false expiry");
        scene.WaitForClocks(); scene.Advance(45_000);
        Check(scene.Active.All(n => n.Client.Members.Length == 7 && n.Client.LeaderId == names[0]), "distinct-name recovered group remains stable after clocks settle");
        Console.WriteLine($"  Distinct-name8 membership: {membershipElapsed} ms; clocks: {scene.Now-disconnectedAt-45_000} ms; stable45 s afterwards");
    }

    private static void Check(bool value, string name) { assertions++; if (!value) throw new InvalidOperationException("Presence test failed: " + name); }
    private sealed record Node(string Name, SimulatedTransport Transport, GroupClient Client)
    {
        public bool Active { get; set; } = true; public List<Packet> Schedules { get; } = [];
        public Dictionary<string, long> ReceivedAt { get; } = new(StringComparer.OrdinalIgnoreCase);
        public long? FirstRecoveryAt { get; set; }
    }
    private sealed class Scene : IDisposable
    {
        public long Now { get; private set; } = 1_800_000_000_000;
        public List<Node> Nodes { get; } = [];
        public IEnumerable<Node> Active => Nodes.Where(n => n.Active);
        public HashSet<string> Dropped { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<(string, string)> Blocked { get; } = [];
        public List<(string Sender, string Recipient, Packet Packet, long At)> Packets { get; } = [];
        private readonly List<(string Sender, string Recipient, string Text, long At)> network = [];
        private readonly Dictionary<string, ChatFrameAssembler> inspectors = new(StringComparer.OrdinalIgnoreCase);
        public Node Add(string name, long skew = 0)
        {
            var transport = new SimulatedTransport(this, () => Now + skew);
            var client = new GroupClient(transport, () => Now + skew); client.SetIdentity(name);
            var node = new Node(name, transport, client); client.ScheduledReceived += p => node.Schedules.Add(p);
            transport.MessageReceived += e => node.ReceivedAt[e.Sender] = Now;
            Nodes.Add(node); return node;
        }
        public void Join(Node host, Node guest)
        {
            host.Client.Invite(guest.Name); Until(() => guest.Client.Invitation != null, "invite " + guest.Name); guest.Client.RespondInvite(true);
            Until(() => guest.Client.GroupId == host.Client.GroupId, "join " + guest.Name);
        }
        public void WaitForClocks()
        {
            var joined = Active.Where(n => n.Client.GroupId != null).ToArray();
            Until(() => joined.All(n => n.Client.GroupId != null && n.Client.ClockReady), "initial clocks", 180_000);
        }
        public void DrainDeparture(string sender, IReadOnlyList<DepartureNotice> notices)
        {
            long delay = 0;
            foreach (var notice in notices) foreach (var frame in ChatCodec.Encode(notice.Packet))
            { network.Add((sender, notice.Recipient, frame, Now + delay + 100)); delay += 1_500; }
        }
        public void Advance(long duration)
        {
            var end = Now + duration;
            while (Now < end)
            {
                Now += 100;
                foreach (var frame in network.Where(f => f.At <= Now).ToArray())
                {
                    network.Remove(frame);
                    Nodes.FirstOrDefault(n => n.Active && n.Name == frame.Recipient)?.Transport.Inbox.Enqueue((frame.Sender, frame.Text));
                }
                foreach (var node in Active)
                {
                    var previousGroup = node.Client.GroupId;
                    node.Client.Poll();
                    if (node.FirstRecoveryAt == null && (node.Client.IsRecoveringGroup || previousGroup != null && previousGroup != node.Client.GroupId))
                        node.FirstRecoveryAt = Now;
                }
            }
        }
        public void Until(Func<bool> condition, string description, long timeout = 90_000)
        {
            var end = Now + timeout; while (Now < end) { if (condition()) return; Advance(100); }
            throw new InvalidOperationException($"Timeout: {description}; " + string.Join(" | ", Active.Select(n => $"{n.Name} lead={n.Client.LeaderId}; recovery={n.Client.IsRecoveringGroup}; {n.Client.Status}; queue={n.Transport.PendingFrames}")));
        }
        public void Send(string sender, string recipient, string text)
        {
            var key = sender + ">" + recipient;
            if (!inspectors.TryGetValue(key, out var inspector)) inspectors[key] = inspector = new ChatFrameAssembler();
            if (inspector.TryAccept(sender, text, Now, out var packet) && packet != null) Packets.Add((sender, recipient, packet, Now));
            if (!Dropped.Contains(sender) && !Dropped.Contains(recipient) && !Blocked.Contains((sender, recipient))) network.Add((sender, recipient, text, Now + 100));
        }
        public void Dispose() { foreach (var node in Nodes) node.Client.Dispose(); }
    }
    private sealed class SimulatedTransport : PacedChatTransport
    {
        private readonly Scene scene; private readonly Func<long> clock;
        public SimulatedTransport(Scene scene, Func<long> clock) : base(clock) { this.scene = scene; this.clock = clock; }
        public Queue<(string Sender, string Text)> Inbox { get; } = [];
        public override void Poll() { while (Inbox.TryDequeue(out var frame)) ReceiveFrame(frame.Sender, frame.Text, clock()); base.Poll(); }
        protected override void SendFrame(string recipient, string text) => scene.Send(LocalIdentity!, recipient, text);
    }
}
