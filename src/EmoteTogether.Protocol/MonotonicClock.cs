using System.Diagnostics;

namespace EmoteTogether.Protocol;

// Wall-clock changes after construction cannot move a pending performance's deadline.
public sealed class MonotonicClock
{
    private readonly long originTicks = Stopwatch.GetTimestamp();
    private readonly long originUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long UnixMilliseconds => originUnixMs + (long)Stopwatch.GetElapsedTime(originTicks).TotalMilliseconds;
}
