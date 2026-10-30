namespace PartyArt.Core;

[Flags]
public enum PhraseTrigger
{
    None = 0,
    DutyEntered = 1,
    DutyStarted = 2,
    DutyCompleted = 4,
    DutyWiped = 8,
    DutyRecommenced = 16,
    CombatStarted = 32,
    CombatEnded = 64,
    PlayerDied = 128,
    PlayerRevived = 256,
    PartyJoined = 512,
    PartyMemberJoined = 1024,
    PartyMemberLeft = 2048,
    PvpEntered = 4096,
    PvpStarted = 8192,
    PvpCompleted = 16384,
}

public enum ChatChannel { Party, Alliance, Echo }

[Serializable]
public sealed class PhrasePreset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Новая заготовка";
    public bool Enabled { get; set; } = true;
    public ChatChannel Channel { get; set; } = ChatChannel.Party;
    public PhraseTrigger Triggers { get; set; }
    public List<string> Messages { get; set; } = [];
}
