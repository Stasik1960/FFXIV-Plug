using PartyArt.Core;

namespace PartyArt.Tests;

internal static class TriggerTrackerChecks
{
    private static int assertions;

    internal static int Run()
    {
        assertions = 0;
        InitialSamplesAreBaselines();
        EntryIsDeferredAndFinite();
        EntryCannotLeakAcrossSessions();
        DutyIdentityDistinguishesEntryFromInternalMovement();
        PartyTransitionsAreStable();
        MemberChangesRequireCompleteSamples();
        CombatAndDeathAreEdges();
        LoadingDoesNotInventEdges();
        return assertions;
    }

    private static void InitialSamplesAreBaselines()
    {
        var tracker = new TriggerTracker();
        Check(tracker.Observe(Sample()) == PhraseTrigger.None, "first ready snapshot is only a baseline");
        Check(tracker.Observe(Sample()) == PhraseTrigger.None, "unchanged initial snapshot is quiet");

        tracker.Reset();
        Check(tracker.Observe(Sample(ready: false, territory: 0)) == PhraseTrigger.None, "initial loading is quiet");
        Check(tracker.Observe(Sample(ready: false, territory: 200, duty: true, party: 10, members: [1, 2])) == PhraseTrigger.None, "initial login territory is quiet");
        Check(tracker.Observe(Sample(territory: 200, duty: true, party: 10, members: [1, 2], combat: true, dead: true)) == PhraseTrigger.None, "first ready login in a duty does not fake any trigger");
        Check(tracker.Observe(Sample(territory: 200, duty: true, party: 10, members: [2, 1], combat: true, dead: true)) == PhraseTrigger.None, "party display order is irrelevant");
    }

    private static void EntryIsDeferredAndFinite()
    {
        var tracker = new TriggerTracker();
        tracker.Observe(Sample());
        Check(tracker.Observe(Sample(territory: 200, ready: false, duty: false)) == PhraseTrigger.None, "entry waits for loading");
        Check(tracker.Observe(Sample(territory: 200, ready: false, duty: true)) == PhraseTrigger.None, "entry waits through a cutscene");
        Check(tracker.Observe(Sample(territory: 200, duty: true)) == PhraseTrigger.DutyEntered, "entry emits once when ready in the new duty");
        Check(tracker.Observe(Sample(territory: 200, duty: true)) == PhraseTrigger.None, "repeated duty samples do not repeat entry");
        Check(tracker.Observe(Sample(territory: 0, ready: false, duty: true)) == PhraseTrigger.None, "unavailable territory is not a transition");
        Check(tracker.Observe(Sample(territory: 200, duty: true)) == PhraseTrigger.None, "same-zone loading does not create another entry");
        Check(tracker.Observe(Sample(territory: 300, duty: true)) == PhraseTrigger.DutyEntered, "another actual duty territory can emit entry");
    }

    private static void EntryCannotLeakAcrossSessions()
    {
        var tracker = new TriggerTracker();
        tracker.Observe(Sample());
        tracker.Observe(Sample(territory: 200, ready: false, duty: true));
        Check(tracker.Observe(Sample(territory: 300, ready: false)) == PhraseTrigger.None, "new transition replaces pending entry context");
        Check(tracker.Observe(Sample(territory: 300)) == PhraseTrigger.None, "returning to a non-duty does not deliver an old entry");
        tracker.Observe(Sample(territory: 200, ready: false, duty: true));
        Check(tracker.Observe(Sample(character: 0, ready: false, territory: 0)) == PhraseTrigger.None, "logout resets a pending entry");
        Check(tracker.Observe(Sample(territory: 200, duty: true)) == PhraseTrigger.None, "reconnect in the duty is a fresh baseline");
        tracker.Observe(Sample(territory: 300, ready: false, duty: true));
        Check(tracker.Observe(Sample(character: 9, territory: 300, duty: true, party: 20, members: [9, 10], combat: true, dead: true)) == PhraseTrigger.None, "different character cannot inherit pending events");
        tracker.Reset();
        Check(tracker.Observe(Sample(character: 9, territory: 300, duty: true)) == PhraseTrigger.None, "explicit reset never fabricates an event");
    }

    private static void PartyTransitionsAreStable()
    {
        var tracker = new TriggerTracker();
        tracker.Observe(Sample());
        Check(tracker.Observe(Sample(party: 10, ready: false, members: [1, 2])) == PhraseTrigger.None, "joining a party waits until ready");
        Check(tracker.Observe(Sample(party: 10, members: [1, 2])) == PhraseTrigger.PartyJoined, "solo-to-party emits one party join");
        Check(tracker.Observe(Sample(party: 10, members: [1, 2])) == PhraseTrigger.None, "stable party does not repeat join");
        Check(tracker.Observe(Sample(party: 0, ready: false)) == PhraseTrigger.None, "party disappearing during loading is not a departure");
        Check(tracker.Observe(Sample(party: 10, members: [1, 2])) == PhraseTrigger.None, "same party after loading is not a new join");
        Check(tracker.Observe(Sample(party: 20, members: [1, 3])) == PhraseTrigger.None, "changed party ID resets member baseline");
        Check(tracker.Observe(Sample()) == PhraseTrigger.None, "leaving party does not fake member changes");
        Check(tracker.Observe(Sample(party: 30, members: [1, 4])) == PhraseTrigger.PartyJoined, "actual solo-to-party edge can trigger again");
    }

    private static void DutyIdentityDistinguishesEntryFromInternalMovement()
    {
        var tracker = new TriggerTracker();
        tracker.Observe(Sample(territory: 200, duty: true, dutyId: 50));
        Check(tracker.Observe(Sample(territory: 201, ready: false, duty: true, dutyId: 50)) == PhraseTrigger.None, "internal territory transition waits for loading");
        Check(tracker.Observe(Sample(territory: 201, duty: true, dutyId: 50)) == PhraseTrigger.None, "same duty in another territory is not a new entry");
        Check(tracker.Observe(Sample(territory: 202, duty: true, dutyId: 50)) == PhraseTrigger.None, "ready internal territory movement is also quiet");
        Check(tracker.Observe(Sample(territory: 100)) == PhraseTrigger.None, "leaving a duty establishes an outside state");
        Check(tracker.Observe(Sample(territory: 200, duty: true, dutyId: 50)) == PhraseTrigger.DutyEntered, "leaving and returning to the same duty ID is a new visit");
        Check(tracker.Observe(Sample(territory: 200, duty: true, dutyId: 50)) == PhraseTrigger.None, "same visit does not repeat entry");
        Check(tracker.Observe(Sample(territory: 300, ready: false, duty: true, dutyId: 50)) == PhraseTrigger.None, "new duty loading may temporarily retain old duty metadata");
        Check(tracker.Observe(Sample(territory: 300, duty: true, dutyId: 60)) == PhraseTrigger.DutyEntered, "different destination duty ID is evaluated after loading");
        Check(tracker.Observe(Sample(territory: 300, duty: true, dutyId: 61)) == PhraseTrigger.DutyEntered, "different duty metadata in a shared territory can establish entry");

        Check(tracker.Observe(Sample(territory: 100, ready: false)) == PhraseTrigger.None, "outside loading carries no immediate entry");
        Check(tracker.Observe(Sample(territory: 100)) == PhraseTrigger.None, "ready outside clears pending entry");
        Check(tracker.Observe(Sample(territory: 100, duty: true, dutyId: 70)) == PhraseTrigger.None, "later metadata availability cannot revive an outside entry candidate");
        Check(tracker.Observe(Sample(territory: 400, ready: false, duty: true, dutyId: 70)) == PhraseTrigger.None, "next transition records its own origin");
        Check(tracker.Observe(Sample(territory: 400, duty: true, dutyId: 80)) == PhraseTrigger.DutyEntered, "later actual duty transition still fires normally");
    }

    private static void MemberChangesRequireCompleteSamples()
    {
        var tracker = new TriggerTracker();
        tracker.Observe(Sample(party: 10, members: [1, 2]));
        Check(tracker.Observe(Sample(party: 10, members: [2, 1, 3])) == PhraseTrigger.PartyMemberJoined, "new member emits join");
        Check(tracker.Observe(Sample(party: 10, members: [1, 2, 3])) == PhraseTrigger.None, "reordered unchanged roster is quiet");
        Check(tracker.Observe(Sample(party: 10, members: [1, 0, 3])) == PhraseTrigger.None, "zero content ID does not fake a departure");
        Check(tracker.Observe(Sample(party: 10, members: [1, 3, 3])) == PhraseTrigger.None, "duplicate IDs do not fake a departure");
        Check(tracker.Observe(Sample(party: 10, members: [2, 3])) == PhraseTrigger.None, "roster missing the local character is incomplete");
        Check(tracker.Observe(Sample(party: 10, members: [1, 2, 3])) == PhraseTrigger.None, "complete original roster after invalid samples is quiet");
        Check(tracker.Observe(Sample(party: 10, members: [1, 3])) == PhraseTrigger.PartyMemberLeft, "real removed member emits leave");
        Check(tracker.Observe(Sample(party: 10, members: [1, 4])) == (PhraseTrigger.PartyMemberJoined | PhraseTrigger.PartyMemberLeft), "member replacement reports both observed edges");
        Check(tracker.Observe(Sample(party: 10, ready: false, members: [])) == PhraseTrigger.None, "unavailable game discards roster comparison baseline");
        Check(tracker.Observe(Sample(party: 10, members: [1, 5, 6])) == PhraseTrigger.None, "first roster after loading does not produce ghost joins");
    }

    private static void CombatAndDeathAreEdges()
    {
        var tracker = new TriggerTracker();
        tracker.Observe(Sample());
        Check(tracker.Observe(Sample(combat: true)) == PhraseTrigger.CombatStarted, "combat starts on a real edge");
        Check(tracker.Observe(Sample(combat: true)) == PhraseTrigger.None, "ongoing combat does not repeat start");
        Check(tracker.Observe(Sample(combat: true, dead: true)) == PhraseTrigger.PlayerDied, "death starts on a real edge");
        Check(tracker.Observe(Sample(combat: true, dead: true)) == PhraseTrigger.None, "remaining dead does not repeat death");
        Check(tracker.Observe(Sample(dead: true)) == PhraseTrigger.CombatEnded, "leaving combat does not mean duty completion");
        Check(tracker.Observe(Sample()) == PhraseTrigger.PlayerRevived, "revive follows an observed death state");
        Check(tracker.Observe(Sample()) == PhraseTrigger.None, "remaining alive does not repeat revive");
        Check(tracker.Observe(Sample(combat: true, dead: true)) == (PhraseTrigger.CombatStarted | PhraseTrigger.PlayerDied), "simultaneous real edges are combined");
    }

    private static void LoadingDoesNotInventEdges()
    {
        var tracker = new TriggerTracker();
        tracker.Observe(Sample(combat: true, dead: true));
        Check(tracker.Observe(Sample(ready: false)) == PhraseTrigger.None, "loading zeroed state is quiet");
        Check(tracker.Observe(Sample()) == PhraseTrigger.None, "first ready state is a new combat and life baseline");
        Check(tracker.Observe(Sample(combat: true)) == PhraseTrigger.CombatStarted, "real edges resume after the new baseline");
        Check(tracker.Observe(Sample(territory: 200, duty: true)) == PhraseTrigger.DutyEntered, "new territory suppresses incidental combat end");
        Check(tracker.Observe(Sample(territory: 200, duty: true, dead: true)) == PhraseTrigger.PlayerDied, "death tracking resumes in the duty");
        Check(tracker.Observe(Sample(territory: 0, ready: true, duty: true)) == PhraseTrigger.None, "zero territory cannot be treated as ready");
        Check(tracker.Observe(Sample(territory: 200, duty: true)) == PhraseTrigger.None, "transient missing territory cannot fabricate revival");
    }

    private static GameSnapshot Sample(
        ulong character = 1, long party = 0, uint territory = 100, bool ready = true,
        bool duty = false, bool combat = false, bool dead = false, ulong[]? members = null, uint dutyId = 0)
        => new(character, party, territory, ready, duty, combat, dead, members ?? [], dutyId);

    private static void Check(bool condition, string label)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException("TriggerTracker: " + label);
    }
}
