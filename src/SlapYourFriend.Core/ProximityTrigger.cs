namespace SlapYourFriend.Core;

public sealed record PlayerRule(string Identity, int IntervalMinutes = 1);
public readonly record struct NearbyPlayer(string Identity, ulong EntityId, float Distance);
public readonly record struct SlapCandidate(string Identity, ulong EntityId, float Distance, long Token);

/// <summary>Per-character repeat timers and proximity checks; contains no game or network calls.</summary>
public sealed class ProximityTrigger
{
    public const long GlobalCooldownMs = 5_000;
    public const float ExitMargin = 1.5f;
    private const long AttemptSpacingMs = 1_000;
    private const long InitialRetryMs = 2_000;
    private readonly Dictionary<string, PlayerState> states = new(StringComparer.OrdinalIgnoreCase);
    private long lastTime = -1;
    private long? lastGlobalSuccess;
    private long? lastGlobalAttempt;
    private long token;

    /// <summary>
    /// Observe every frame, including while gameplay prevents an emote. Timers survive disabled
    /// rules, missing objects and range changes. Report a returned candidate immediately.
    /// </summary>
    public SlapCandidate? Update(IEnumerable<NearbyPlayer> visible, IEnumerable<PlayerRule> rules,
        long nowMs, bool canAct, float triggerRadius = 2f)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nowMs);
        ArgumentNullException.ThrowIfNull(visible);
        ArgumentNullException.ThrowIfNull(rules);
        if (nowMs < lastTime) return null;
        lastTime = nowMs;
        if (!float.IsFinite(triggerRadius)) triggerRadius = 2f;
        triggerRadius = Math.Clamp(triggerRadius, 0.5f, 5f);

        var allowed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            if (rule is null || !TargetIdentity.TryNormalize(rule.Identity, out var identity)) continue;
            var minutes = Math.Clamp(rule.IntervalMinutes, 1, 60);
            // Duplicate configuration entries must not shorten the intended per-player timer.
            allowed[identity] = Math.Max(minutes, allowed.GetValueOrDefault(identity));
        }
        var observed = new Dictionary<string, NearbyPlayer>(StringComparer.OrdinalIgnoreCase);
        foreach (var player in visible)
        {
            if (!TargetIdentity.TryNormalize(player.Identity, out var identity) || !allowed.ContainsKey(identity) ||
                player.EntityId == 0 || !float.IsFinite(player.Distance) || player.Distance < 0) continue;
            var candidate = player with { Identity = identity };
            if (!observed.TryGetValue(identity, out var old) || candidate.Distance < old.Distance ||
                (candidate.Distance == old.Distance && candidate.EntityId < old.EntityId)) observed[identity] = candidate;
        }

        foreach (var state in states.Values)
        {
            state.Visible = false;
            state.ActiveToken = 0;
        }
        foreach (var (identity, player) in observed)
        {
            if (!states.TryGetValue(identity, out var state)) states[identity] = state = new PlayerState();
            state.Visible = true;
            state.EntityId = player.EntityId;
            if (player.Distance <= triggerRadius) state.Close = true;
            else if (player.Distance >= triggerRadius + ExitMargin) state.Close = false;
        }
        if (!canAct || !Elapsed(nowMs, lastGlobalAttempt, AttemptSpacingMs) ||
            !Elapsed(nowMs, lastGlobalSuccess, GlobalCooldownMs)) return null;

        foreach (var player in observed.Values.OrderBy(p => p.Distance).ThenBy(p => p.Identity, StringComparer.OrdinalIgnoreCase))
        {
            var state = states[player.Identity];
            var interval = allowed[player.Identity] * 60_000L;
            // Hysteresis tracks the encounter; an actual attempt still obeys the selected radius.
            if (!state.Close || player.Distance > triggerRadius || nowMs < state.RetryAt || !Elapsed(nowMs, state.LastSuccess, interval)) continue;
            lastGlobalAttempt = nowMs;
            state.SelectedAt = nowMs;
            state.ActiveToken = ++token;
            state.RetryAt = Add(nowMs, InitialRetryMs);
            return new SlapCandidate(player.Identity, player.EntityId, player.Distance, state.ActiveToken);
        }
        return null;
    }

    /// <summary>Only the most recent candidate from the current observation can affect timers.</summary>
    public void ReportExecution(SlapCandidate candidate, bool success, long nowMs)
    {
        if (nowMs < 0 || !TargetIdentity.TryNormalize(candidate.Identity, out var identity) ||
            !states.TryGetValue(identity, out var state) || !state.Visible || candidate.Token == 0 ||
            state.ActiveToken != candidate.Token || state.EntityId != candidate.EntityId ||
            nowMs < state.SelectedAt || nowMs - state.SelectedAt > InitialRetryMs) return;
        state.ActiveToken = 0;
        if (success)
        {
            state.LastSuccess = nowMs;
            lastGlobalSuccess = nowMs;
            state.Failures = 0;
            state.RetryAt = nowMs;
            return;
        }
        state.Failures = Math.Min(5, state.Failures + 1);
        state.RetryAt = Add(nowMs, Math.Min(30_000, InitialRetryMs << (state.Failures - 1)));
    }

    /// <summary>Clear history for a new local character/session; zone changes should preserve timers.</summary>
    public void Reset()
    {
        states.Clear();
        lastGlobalAttempt = null;
        lastGlobalSuccess = null;
        lastTime = -1;
    }

    private static bool Elapsed(long now, long? since, long duration) => since is null || now >= since.Value && now - since.Value >= duration;
    private static long Add(long now, long duration) => now > long.MaxValue - duration ? long.MaxValue : now + duration;
    private sealed class PlayerState
    {
        public bool Visible;
        public bool Close;
        public ulong EntityId;
        public long? LastSuccess;
        public long RetryAt;
        public int Failures;
        public long ActiveToken;
        public long SelectedAt;
    }
}
