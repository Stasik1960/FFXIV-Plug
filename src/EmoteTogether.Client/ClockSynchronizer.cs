namespace EmoteTogether.Client;

/// <summary>NTP-style four-timestamp estimate, anchored to monotonic clocks on both peers.</summary>
public sealed class ClockSynchronizer
{
    private readonly List<Sample> samples = [];
    public double OffsetMs { get; private set; }
    public double RoundTripMs { get; private set; }
    public bool IsReady(long localNow) => samples.Count >= 2 && localNow - samples[^1].ReceivedAt < 600_000;

    public bool Observe(long t1, long t2, long t3, long t4)
    {
        if (t4 < t1 || t3 < t2) return false;
        double serverProcessing = (double)t3 - t2;
        // A chat reply may wait behind paced messages; that processing time is excluded from RTT.
        if (serverProcessing > 120_000) return false;
        double roundTrip = ((double)t4 - t1) - serverProcessing;
        if (roundTrip is < 0 or > 5_000) return false;
        double offset = ((double)t2 - t1 + (double)t3 - t4) / 2;
        samples.Add(new Sample(t4, roundTrip, offset));
        samples.RemoveAll(s => t4 - s.ReceivedAt > 600_000);
        if (samples.Count > 12) samples.RemoveAt(0);
        var best = samples.MinBy(s => s.RoundTrip)!;
        OffsetMs = best.Offset;
        RoundTripMs = best.RoundTrip;
        return true;
    }

    public void Reset()
    {
        samples.Clear();
        OffsetMs = 0;
        RoundTripMs = 0;
    }

    private sealed record Sample(long ReceivedAt, double RoundTrip, double Offset);
}
