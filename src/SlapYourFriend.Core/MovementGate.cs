using System.Numerics;

namespace SlapYourFriend.Core;

/// <summary>Requires a stable local position before allowing an automatic emote.</summary>
public sealed class MovementGate
{
    public const long SettleMs = 500;
    public const float JitterTolerance = 0.02f;
    private Vector3? anchor;
    private long stableSince;
    private long? lastTime;

    public bool IsStationary { get; private set; }

    /// <summary>
    /// Position changes accumulate against a fixed anchor, so small running steps cannot each
    /// disappear below the tolerance. Loading, invalid positions and a native movement flag
    /// require a fresh stationary window. This gate never modifies player repeat timers.
    /// </summary>
    public bool Update(Vector3? position, long nowMs, bool nativeMoving = false)
    {
        if (nowMs < 0)
        {
            Reset();
            return false;
        }
        if (lastTime is { } previous && nowMs < previous)
        {
            anchor = null;
            IsStationary = false;
        }
        lastTime = nowMs;
        if (nativeMoving || position is not { } current || !Finite(current))
        {
            anchor = null;
            IsStationary = false;
            return false;
        }
        if (anchor is not { } original || Vector3.DistanceSquared(original, current) >= JitterTolerance * JitterTolerance)
        {
            anchor = current;
            stableSince = nowMs;
            IsStationary = false;
            return false;
        }
        IsStationary = nowMs >= stableSince && nowMs - stableSince >= SettleMs;
        return IsStationary;
    }

    public void Reset()
    {
        anchor = null;
        stableSince = 0;
        lastTime = null;
        IsStationary = false;
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
