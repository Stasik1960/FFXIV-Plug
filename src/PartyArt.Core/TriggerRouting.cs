namespace PartyArt.Core;

/// <summary>Separates duty lifecycle events by the destination's mode while preserving saved trigger values.</summary>
public static class TriggerRouting
{
    public static PhraseTrigger KnownTriggers { get; } = Enum.GetValues<PhraseTrigger>()
        .Aggregate(PhraseTrigger.None, (known, trigger) => known | trigger);

    /// <summary>Accepts one duty event. PvP match completion is independent of the player's result.</summary>
    public static PhraseTrigger RouteDutyEvent(PhraseTrigger trigger, bool isPvP) => (trigger, isPvP) switch
    {
        (PhraseTrigger.DutyEntered, true) => PhraseTrigger.PvpEntered,
        (PhraseTrigger.DutyStarted, true) => PhraseTrigger.PvpStarted,
        (PhraseTrigger.DutyCompleted, true) => PhraseTrigger.PvpCompleted,
        (PhraseTrigger.DutyEntered or PhraseTrigger.DutyStarted or PhraseTrigger.DutyCompleted or
            PhraseTrigger.DutyWiped or PhraseTrigger.DutyRecommenced, false) => trigger,
        _ => PhraseTrigger.None,
    };
}
