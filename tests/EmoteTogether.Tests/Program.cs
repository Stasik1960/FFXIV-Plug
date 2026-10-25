using EmoteTogether.Client;

namespace EmoteTogether.Tests;

internal static class Program
{
    private static int Main()
    {
        try
        {
            ClockTests();
            ChatCodecTests.Run();
            ControlFrameClassifierTests.Run();
            TransportTests.Run();
            ServerlessTests.Run();
            LeadershipTests.Run();
            PresenceTests.Run();
            Console.WriteLine("PASS: all serverless checks completed. No real game chat was sent.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void ClockTests()
    {
        var sync = new ClockSynchronizer();
        Check(sync.Observe(10_000, 11_020, 11_025, 10_045), "valid sample");
        Check(sync.OffsetMs == 1_000 && sync.RoundTripMs == 40, "known offset");
        Check(!sync.IsReady(10_045), "one sample insufficient");
        Check(sync.Observe(11_000, 12_010, 42_010, 41_020), "queue delay excluded");
        Check(sync.IsReady(41_020), "two samples ready");
        Check(sync.OffsetMs == 1_000 && sync.RoundTripMs == 20, "best RTT wins");
        Check(!sync.Observe(1_000, 2_000, 1_999, 1_001), "server time backwards");
        Check(!sync.Observe(1_000, 2_000, 2_000, 999), "client time backwards");
        Check(!sync.Observe(1_000, 2_000, 2_000, 7_001), "excessive RTT");
        Check(!sync.Observe(1_000, long.MinValue, long.MaxValue, 1_100), "overflow rejected");
        Check(!sync.IsReady(641_020), "stale synchronization expires");
        sync.Reset();
        Check(!sync.IsReady(41_020) && sync.OffsetMs == 0, "reset");
        Console.WriteLine("PASS: clock synchronization (12 assertions)");
    }
    private static void Check(bool condition, string name)
    { if (!condition) throw new InvalidOperationException($"Clock test failed: {name}"); }
}
