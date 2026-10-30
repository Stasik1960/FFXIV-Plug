using PartyArt.Core;

namespace PartyArt.Tests;

internal static class PvpTriggerChecks
{
    private static int assertions;

    internal static int Run()
    {
        assertions = 0;
        DutyLifecycleRouting();
        SavedFlagValuesRemainStable();
        EntriesStayInTheirOwnMode();
        LoadingAndInitialSamplesStayQuiet();
        GenericEventsRemainGeneric();
        return assertions;
    }

    private static void DutyLifecycleRouting()
    {
        foreach (var (duty, pvp) in new[]
        {
            (PhraseTrigger.DutyEntered, PhraseTrigger.PvpEntered),
            (PhraseTrigger.DutyStarted, PhraseTrigger.PvpStarted),
            (PhraseTrigger.DutyCompleted, PhraseTrigger.PvpCompleted),
            (PhraseTrigger.DutyWiped, PhraseTrigger.None),
            (PhraseTrigger.DutyRecommenced, PhraseTrigger.None),
        })
        {
            Check(TriggerRouting.RouteDutyEvent(duty, false) == duty, "PvE native event retains its existing trigger");
            Check(TriggerRouting.RouteDutyEvent(duty, true) == pvp, "PvP native event cannot activate a PvE duty trigger");
        }
        foreach (var invalid in new[]
        {
            PhraseTrigger.None, PhraseTrigger.CombatEnded, PhraseTrigger.PvpEntered,
            PhraseTrigger.PvpStarted, PhraseTrigger.PvpCompleted,
            PhraseTrigger.DutyStarted | PhraseTrigger.DutyCompleted, (PhraseTrigger)(1 << 25),
        })
        {
            Check(TriggerRouting.RouteDutyEvent(invalid, false) == PhraseTrigger.None, "routing refuses a non-duty or combined PvE input");
            Check(TriggerRouting.RouteDutyEvent(invalid, true) == PhraseTrigger.None, "routing refuses a non-duty or combined PvP input");
        }
    }

    private static void SavedFlagValuesRemainStable()
    {
        var flags = new[]
        {
            PhraseTrigger.DutyEntered, PhraseTrigger.DutyStarted, PhraseTrigger.DutyCompleted,
            PhraseTrigger.DutyWiped, PhraseTrigger.DutyRecommenced, PhraseTrigger.CombatStarted,
            PhraseTrigger.CombatEnded, PhraseTrigger.PlayerDied, PhraseTrigger.PlayerRevived,
            PhraseTrigger.PartyJoined, PhraseTrigger.PartyMemberJoined, PhraseTrigger.PartyMemberLeft,
            PhraseTrigger.PvpEntered, PhraseTrigger.PvpStarted, PhraseTrigger.PvpCompleted,
        };
        for (var index = 0; index < flags.Length; index++)
            Check((int)flags[index] == 1 << index, "existing saved flags keep their original numeric values and new flags are appended");
        var savedPvE = (PhraseTrigger)5;
        Check((savedPvE & TriggerRouting.KnownTriggers) == (PhraseTrigger.DutyEntered | PhraseTrigger.DutyCompleted), "old entry and completion selections survive normalization");
        Check((savedPvE & (PhraseTrigger.PvpEntered | PhraseTrigger.PvpCompleted)) == PhraseTrigger.None, "old selections do not silently opt into PvP");
        var savedPvP = PhraseTrigger.PvpEntered | PhraseTrigger.PvpStarted | PhraseTrigger.PvpCompleted;
        Check((savedPvP & TriggerRouting.KnownTriggers) == savedPvP, "new PvP selections survive normalization");
        Check(((PhraseTrigger)(1 << 25) & TriggerRouting.KnownTriggers) == PhraseTrigger.None, "unknown future flag is excluded");
        Check(Enum.GetValues<PhraseTrigger>().All(trigger => (TriggerRouting.KnownTriggers & trigger) == trigger), "known mask includes every defined trigger");
    }

    private static void EntriesStayInTheirOwnMode()
    {
        var tracker = new TriggerTracker();
        tracker.Observe(Sample());
        Check(tracker.Observe(Sample(territory: 200, dutyId: 50)) == PhraseTrigger.DutyEntered, "entering PvE fires only the existing entry trigger");
        Check(tracker.Observe(Sample(territory: 100)) == PhraseTrigger.None, "leaving PvE does not fabricate another entry");
        Check(tracker.Observe(Sample(territory: 300, dutyId: 60, pvp: true)) == PhraseTrigger.PvpEntered, "entering PvP fires only the PvP entry trigger");
        Check(tracker.Observe(Sample(territory: 300, dutyId: 60, pvp: true)) == PhraseTrigger.None, "same PvP match does not repeat entry");
        Check(tracker.Observe(Sample(territory: 200, dutyId: 50)) == PhraseTrigger.DutyEntered, "direct PvP-to-PvE transition returns to PvE entry");
        Check(tracker.Observe(Sample(territory: 300, dutyId: 60, pvp: true)) == PhraseTrigger.PvpEntered, "direct PvE-to-PvP transition never leaks a dungeon greeting");
        Check(tracker.Observe(Sample(territory: 300, dutyId: 61, pvp: true)) == PhraseTrigger.PvpEntered, "changed duty metadata in a shared PvP territory routes to PvP");
        Check(tracker.Observe(Sample(territory: 100, pvp: true)) == PhraseTrigger.None, "PvP zone without a duty does not invent a match entry");
    }

    private static void LoadingAndInitialSamplesStayQuiet()
    {
        var tracker = new TriggerTracker();
        Check(tracker.Observe(Sample(territory: 300, ready: false, dutyId: 60, pvp: true)) == PhraseTrigger.None, "initial loading in PvP is quiet");
        Check(tracker.Observe(Sample(territory: 300, dutyId: 60, pvp: true)) == PhraseTrigger.None, "loading plugin or reconnecting inside PvP is a baseline");
        tracker.Observe(Sample());
        Check(tracker.Observe(Sample(territory: 300, ready: false, dutyId: 0, pvp: false)) == PhraseTrigger.None, "unready destination may not yet expose its PvP classification");
        Check(tracker.Observe(Sample(territory: 300, ready: false, dutyId: 60, pvp: true)) == PhraseTrigger.None, "known PvP mode still waits through loading");
        Check(tracker.Observe(Sample(territory: 300, dutyId: 60, pvp: true)) == PhraseTrigger.PvpEntered, "deferred entry uses the destination mode after readiness");
        Check(tracker.Observe(Sample(territory: 301, ready: false, dutyId: 60, pvp: true)) == PhraseTrigger.None, "internal PvP territory loading is quiet");
        Check(tracker.Observe(Sample(territory: 301, dutyId: 60, pvp: true)) == PhraseTrigger.None, "same PvP duty in another territory does not repeat greeting");
        tracker.Observe(Sample(territory: 200, ready: false, dutyId: 60, pvp: true));
        Check(tracker.Observe(Sample(territory: 200, dutyId: 50, pvp: false)) == PhraseTrigger.DutyEntered, "stale PvP loading metadata cannot redirect the next PvE entry");
    }

    private static void GenericEventsRemainGeneric()
    {
        var tracker = new TriggerTracker();
        tracker.Observe(Sample(territory: 300, dutyId: 60, pvp: true));
        Check(tracker.Observe(Sample(territory: 300, dutyId: 60, pvp: true, combat: true)) == PhraseTrigger.CombatStarted, "PvP combat remains a generic combat event");
        Check(tracker.Observe(Sample(territory: 300, dutyId: 60, pvp: true, combat: true, dead: true)) == PhraseTrigger.PlayerDied, "PvP death remains a generic character event");
        Check(tracker.Observe(Sample(territory: 300, dutyId: 60, pvp: true, combat: true)) == PhraseTrigger.PlayerRevived, "PvP revival remains a generic character event");
        Check(tracker.Observe(Sample(territory: 300, dutyId: 60, pvp: true)) == PhraseTrigger.CombatEnded, "leaving PvP combat is not a match result or a PvE completion");
    }

    private static GameSnapshot Sample(uint territory = 100, bool ready = true, uint dutyId = 0,
        bool pvp = false, bool combat = false, bool dead = false)
        => new(1, 0, territory, ready, dutyId != 0, combat, dead, [], dutyId, pvp);

    private static void Check(bool condition, string label)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException("PvP triggers: " + label);
    }
}
