namespace PartyArt.Core;

/// <summary>A single local-character sample. Members contains content IDs, including the local character.</summary>
public sealed record GameSnapshot(
    ulong CharacterId,
    long PartyId,
    uint Territory,
    bool Ready,
    bool InDuty,
    bool InCombat,
    bool IsDead,
    ulong[] Members,
    uint DutyId = 0);

/// <summary>Detects observed edges without interpreting an initial or temporarily unavailable sample as an event.</summary>
public sealed class TriggerTracker
{
    private ulong characterId;
    private uint territory;
    private bool armed;
    private bool pendingEntry;
    private bool wasInDuty;
    private uint dutyId;
    private bool pendingOriginInDuty;
    private uint pendingOriginDutyId;
    private long stablePartyId;
    private bool? previousCombat;
    private bool? previousDead;
    private HashSet<ulong>? previousMembers;

    public PhraseTrigger Observe(GameSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.CharacterId == 0)
        {
            Reset();
            return PhraseTrigger.None;
        }

        if (characterId != snapshot.CharacterId)
        {
            Reset();
            characterId = snapshot.CharacterId;
        }

        var ready = snapshot.Ready && snapshot.Territory != 0;
        if (!armed)
        {
            // Loading the plugin, logging in and changing characters establish a baseline.
            // Until the first usable sample, a territory number is only initialization data.
            if (snapshot.Territory != 0) territory = snapshot.Territory;
            if (ready)
            {
                armed = true;
                stablePartyId = snapshot.PartyId;
                previousCombat = snapshot.InCombat;
                previousDead = snapshot.IsDead;
                previousMembers = ReadMembers(snapshot);
                wasInDuty = snapshot.InDuty;
                dutyId = snapshot.InDuty ? snapshot.DutyId : 0;
            }
            return PhraseTrigger.None;
        }

        // Zero is an unavailable territory, rather than a visit to a new zone.
        if (snapshot.Territory != 0 && snapshot.Territory != territory)
        {
            territory = snapshot.Territory;
            pendingEntry = true;
            pendingOriginInDuty = wasInDuty;
            pendingOriginDutyId = dutyId;
            ResetTransientBaselines();
        }

        if (!ready)
        {
            ResetTransientBaselines();
            return PhraseTrigger.None;
        }

        var result = PhraseTrigger.None;
        if (pendingEntry)
        {
            // Multiple territories can belong to one duty. Judge the destination only
            // after loading, because its metadata may still describe the previous area.
            var sameDuty = pendingOriginInDuty && pendingOriginDutyId != 0 &&
                snapshot.DutyId != 0 && pendingOriginDutyId == snapshot.DutyId;
            if (snapshot.InDuty && !sameDuty) result |= PhraseTrigger.DutyEntered;
            pendingEntry = false;
        }
        else if (wasInDuty && snapshot.InDuty && dutyId != 0 && snapshot.DutyId != 0 && dutyId != snapshot.DutyId)
            result |= PhraseTrigger.DutyEntered;
        wasInDuty = snapshot.InDuty;
        dutyId = snapshot.InDuty ? snapshot.DutyId : 0;

        if (stablePartyId != snapshot.PartyId)
        {
            if (stablePartyId == 0 && snapshot.PartyId != 0) result |= PhraseTrigger.PartyJoined;
            stablePartyId = snapshot.PartyId;
            previousMembers = null;
        }

        if (previousCombat is { } combat && combat != snapshot.InCombat)
            result |= snapshot.InCombat ? PhraseTrigger.CombatStarted : PhraseTrigger.CombatEnded;
        if (previousDead is { } dead && dead != snapshot.IsDead)
            result |= snapshot.IsDead ? PhraseTrigger.PlayerDied : PhraseTrigger.PlayerRevived;
        previousCombat = snapshot.InCombat;
        previousDead = snapshot.IsDead;

        var members = ReadMembers(snapshot);
        if (members is not null)
        {
            if (previousMembers is not null)
            {
                if (members.Except(previousMembers).Any()) result |= PhraseTrigger.PartyMemberJoined;
                if (previousMembers.Except(members).Any()) result |= PhraseTrigger.PartyMemberLeft;
            }
            previousMembers = members;
        }
        else if (snapshot.PartyId == 0) previousMembers = null;
        return result;
    }

    public void Reset()
    {
        characterId = 0;
        territory = 0;
        armed = false;
        pendingEntry = false;
        wasInDuty = false;
        dutyId = 0;
        pendingOriginInDuty = false;
        pendingOriginDutyId = 0;
        stablePartyId = 0;
        ResetTransientBaselines();
    }

    private void ResetTransientBaselines()
    {
        previousCombat = null;
        previousDead = null;
        previousMembers = null;
    }

    private static HashSet<ulong>? ReadMembers(GameSnapshot snapshot)
    {
        if (snapshot.PartyId == 0 || snapshot.Members is null || snapshot.Members.Length == 0 ||
            snapshot.Members.Any(id => id == 0)) return null;
        var members = snapshot.Members.ToHashSet();
        if (members.Count != snapshot.Members.Length || !members.Contains(snapshot.CharacterId)) return null;
        return members;
    }
}
