using SlapYourFriend.Core;

namespace SlapYourFriend.Tests;

internal static class Program
{
    private const string Alice = "Alice Player@Twintania";
    private const string Bob = "Bobby Player@Cerberus";
    private static int assertions;
    private static int Main()
    {
        try
        {
            RepeatsInMinutes();
            RetreatDoesNotBypassTimer();
            IndependentPlayersAndGlobalSpacing();
            EditsAndDisabledRulesPreserveTimers();
            GameplayAndDisappearance();
            NativeFailureBackoff();
            IdentityAndEntityReuse();
            InvalidInputsAndClock();
            Console.WriteLine($"PASS: Slap Your Friend proximity and repeat timers (8 scenarios, {assertions} assertions). No game calls or chat messages sent.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void RepeatsInMinutes()
    {
        var gate = new ProximityTrigger();
        var rules = new[] { new PlayerRule(Alice) };
        Check(gate.Update([Near(Alice, 1, 3)], rules, 0, true) is null, "outside range");
        var first = Expect(gate.Update([Near(Alice, 1, 1.8f)], rules, 100, true), Alice, "first approach");
        gate.ReportExecution(first, true, 100);
        foreach (var time in new long[] { 101, 5_100, 30_000, 60_099 })
            Check(gate.Update([Near(Alice, 1, 1.8f)], rules, time, true) is null, "standing nearby respects minute timer");
        var repeat = Expect(gate.Update([Near(Alice, 1, 1.8f)], rules, 60_100, true), Alice, "repeat after one minute while still close");
        gate.ReportExecution(repeat, true, 60_100);
        Check(gate.Update([Near(Alice, 1, 1.8f)], rules, 60_101, true) is null, "one repeat consumes timer");
    }

    private static void RetreatDoesNotBypassTimer()
    {
        var gate = new ProximityTrigger(); var rules = new[] { new PlayerRule(Alice) };
        var first = Expect(gate.Update([Near(Alice, 1, 1)], rules, 0, true), Alice, "initial");
        gate.ReportExecution(first, true, 0);
        Check(gate.Update([Near(Alice, 1, 8)], rules, 10_000, true) is null, "retreated");
        Check(gate.Update([Near(Alice, 1, 1)], rules, 20_000, true) is null, "reentry cannot bypass timer");
        foreach (var (time, distance) in new[] { (30_000L, 2.05f), (31_000L, 1.95f), (32_000L, 3.6f), (33_000L, 1.9f) })
            Check(gate.Update([Near(Alice, 1, distance)], rules, time, true) is null, "range jitter preserves timer");
        Check(gate.Update([Near(Alice, 1, 2.1f)], rules, 60_000, true) is null, "expired timer does not expand actual trigger radius");
        Expect(gate.Update([Near(Alice, 1, 1.9f)], rules, 60_100, true), Alice, "overdue return acts immediately");
    }

    private static void IndependentPlayersAndGlobalSpacing()
    {
        var gate = new ProximityTrigger();
        var rules = new[] { new PlayerRule(Alice, 1), new PlayerRule(Bob, 2) };
        var players = new[] { Near(Bob, 2, 1.8f), Near(Alice, 1, 1.2f) };
        var alice = Expect(gate.Update(players, rules, 0, true), Alice, "closest eligible player chosen first");
        gate.ReportExecution(alice, true, 0);
        Check(gate.Update(players, rules, 1, true) is null, "no simultaneous automatic emotes");
        var bob = Expect(gate.Update(players, rules, 5_000, true), Bob, "other friend after global spacing");
        gate.ReportExecution(bob, true, 5_000);
        alice = Expect(gate.Update(players, rules, 60_000, true), Alice, "Alice one minute interval");
        gate.ReportExecution(alice, true, 60_000);
        Check(gate.Update([Near(Bob, 2, 1)], rules, 120_000, true) is null, "Bob interval measured from own last success");
        Expect(gate.Update([Near(Bob, 2, 1)], rules, 125_000, true), Bob, "Bob two minute interval");
    }

    private static void EditsAndDisabledRulesPreserveTimers()
    {
        var gate = new ProximityTrigger();
        var player = new[] { Near(Alice, 1, 1) };
        var first = Expect(gate.Update(player, [new(Alice, 3)], 0, true), Alice, "initial edited timer");
        gate.ReportExecution(first, true, 0);
        Check(gate.Update(player, [new(Alice, 3)], 60_000, true) is null, "three minutes not due");
        var shorter = Expect(gate.Update(player, [new(Alice, 1)], 60_001, true), Alice, "shortening uses elapsed time");
        gate.ReportExecution(shorter, true, 60_001);
        Check(gate.Update(player, [new(Alice, 3)], 120_001, true) is null, "lengthening uses previous success");
        Check(gate.Update(player, [], 130_000, true) is null, "disabled rule cannot act");
        Check(gate.Update(player, [new(Alice, 3)], 180_000, true) is null, "reenabling rule preserves cooldown");
        Expect(gate.Update(player, [new(Alice, 3)], 240_001, true), Alice, "edited interval expires normally");
    }

    private static void GameplayAndDisappearance()
    {
        var gate = new ProximityTrigger(); var rules = new[] { new PlayerRule(Alice, 2) };
        var player = new[] { Near(Alice, 1, 1) };
        Check(gate.Update(player, rules, 0, false) is null, "combat or loading blocks native attempt");
        var first = Expect(gate.Update(player, rules, 1_000, true), Alice, "temporary busy state does not consume first approach");
        gate.ReportExecution(first, true, 1_000);
        Check(gate.Update([], rules, 20_000, true) is null, "missing player cannot act");
        Check(gate.Update([Near(Alice, 99, 1)], rules, 30_000, true) is null, "reappearing entity retains identity timer");
        Check(gate.Update(player, rules, 61_000, false) is null, "disabled plugin preserves timer");
        Check(gate.Update(player, rules, 61_100, true) is null, "reenabled plugin does not grant early hit");
        Check(gate.Update([], rules, 121_000, true) is null, "no queued hit at expiry while absent");
        Expect(gate.Update(player, rules, 180_000, true), Alice, "overdue visible player is evaluated fresh");
        gate.Reset();
        Expect(gate.Update(player, rules, 0, true), Alice, "explicit new local session resets history");
    }

    private static void NativeFailureBackoff()
    {
        var gate = new ProximityTrigger(); var rules = new[] { new PlayerRule(Alice) }; var player = new[] { Near(Alice, 1, 1) };
        var first = Expect(gate.Update(player, rules, 0, true), Alice, "first native attempt");
        gate.ReportExecution(first, false, 0);
        Check(gate.Update(player, rules, 1_000, true) is null, "failed native attempt is not retried per frame");
        var retry = Expect(gate.Update(player, rules, 2_000, true), Alice, "bounded initial retry");
        gate.ReportExecution(retry, false, 2_000);
        Check(gate.Update(player, rules, 5_999, true) is null, "repeated failures back off");
        retry = Expect(gate.Update(player, rules, 6_000, true), Alice, "second retry");
        gate.ReportExecution(retry, true, 6_000);
        Check(gate.Update(player, rules, 60_000, true) is null, "only success starts repeat timer");
        Expect(gate.Update(player, rules, 66_000, true), Alice, "minute after successful retry");
    }

    private static void IdentityAndEntityReuse()
    {
        var gate = new ProximityTrigger();
        var rules = new[] { new PlayerRule(Alice), new PlayerRule(Bob) };
        var stale = Expect(gate.Update([Near(Alice, 77, 1)], rules, 0, true), Alice, "candidate before object reuse");
        Check(gate.Update([Near(Bob, 77, 1)], rules, 500, true) is null, "same object id can describe another player");
        gate.ReportExecution(stale, true, 500);
        Expect(gate.Update([Near(Bob, 77, 1)], rules, 1_000, true), Bob, "stale result cannot consume new player/global cooldown");

        gate = new ProximityTrigger();
        var alias = Expect(gate.Update([Near("alice player@twintania", 1, 1)], [new("  Alice Player@Twintania  ")], 0, true), Alice, "case-insensitive complete identity");
        gate.ReportExecution(alias, true, 0);
        Check(gate.Update([Near(Alice, 1, 1)], [new(Alice), new("ALICE PLAYER@TWINTANIA", 2)], 60_000, true) is null,
            "duplicate aliases use conservative interval");
        var otherWorld = "Alice Player@Cerberus";
        Expect(gate.Update([Near(otherWorld, 2, 1)], [new(Alice), new(otherWorld)], 61_000, true), otherWorld, "same name on another home world is distinct");
        Check(TargetIdentity.TryNormalize("Ame\u0301lie Player@Twintania", out var normalized) && normalized == "Amélie Player@Twintania", "canonical Unicode identity");
    }

    private static void InvalidInputsAndClock()
    {
        var gate = new ProximityTrigger(); var rules = new[] { new PlayerRule(Alice) };
        Check(gate.Update([Near(Alice, 0, 1), Near(Alice, 1, float.NaN), Near(Alice, 2, float.PositiveInfinity), Near(Alice, 3, -1), Near(Bob, 4, 1)], rules, 0, true) is null,
            "invalid geometry, unknown names and missing entity ids rejected");
        foreach (var identity in new[] { "Alice", "Alice Player", "Alice Player@World\n/logout", "Alice Player@World Extra", "Alice Player@@World" })
            Check(!TargetIdentity.TryNormalize(identity, out _), "malformed identity rejected");
        var first = Expect(gate.Update([Near(Alice, 1, 1)], rules, 10_000, true), Alice, "valid observation after invalid values");
        gate.ReportExecution(first, true, 10_000);
        Check(gate.Update([Near(Alice, 1, 1)], rules, 9_000, true) is null, "backward clock cannot bypass timers");
        Check(gate.Update([Near(Alice, 1, 1)], rules, 69_999, true) is null, "timer still intact after backward clock");
        Expect(gate.Update([Near(Alice, 1, 1)], rules, 70_000, true), Alice, "normal time resumes");
    }

    private static NearbyPlayer Near(string identity, ulong entity, float distance) => new(identity, entity, distance);
    private static SlapCandidate Expect(SlapCandidate? value, string identity, string reason)
    {
        Check(value is { } candidate && string.Equals(candidate.Identity, identity, StringComparison.OrdinalIgnoreCase), reason);
        return value!.Value;
    }
    private static void Check(bool condition, string reason)
    { assertions++; if (!condition) throw new InvalidOperationException("Slap Your Friend test failed: " + reason); }
}
