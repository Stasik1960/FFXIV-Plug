using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace EmoteTogether.Plugin;

internal sealed record EmoteEntry(uint Id, string Name, string Command);

// All methods accessing game state are called on the game's framework thread.
internal sealed unsafe class GameEmotes
{
    private readonly IClientState clientState;
    private readonly IObjectTable objects;
    private readonly ICondition conditions;
    private readonly Dictionary<uint, EmoteEntry> catalog;
    public IReadOnlyList<EmoteEntry> Unlocked { get; private set; } = [];

    private static readonly ConditionFlag[] BlockedConditions =
    [
        ConditionFlag.InCombat, ConditionFlag.Mounted, ConditionFlag.RidingPillion,
        ConditionFlag.Mounting, ConditionFlag.Mounting71, ConditionFlag.MountOrOrnamentTransition,
        ConditionFlag.Crafting, ConditionFlag.ExecutingCraftingAction, ConditionFlag.PreparingToCraft,
        ConditionFlag.Casting, ConditionFlag.Casting87,
        ConditionFlag.WatchingCutscene, ConditionFlag.WatchingCutscene78,
        ConditionFlag.OccupiedInCutSceneEvent, ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51,
        ConditionFlag.Occupied, ConditionFlag.Occupied30, ConditionFlag.Occupied33,
        ConditionFlag.Occupied38, ConditionFlag.Occupied39, ConditionFlag.OccupiedInEvent,
        ConditionFlag.OccupiedInQuestEvent, ConditionFlag.OccupiedSummoningBell,
    ];

    public GameEmotes(IDataManager data, IClientState clientState, IObjectTable objects, ICondition conditions)
    {
        this.clientState = clientState;
        this.objects = objects;
        this.conditions = conditions;
        catalog = data.GetExcelSheet<Emote>()
            .Where(row => row.RowId is > 0 and <= ushort.MaxValue && row.TextCommand.IsValid)
            .Select(row => new EmoteEntry(row.RowId, row.Name.ExtractText(), row.TextCommand.Value.Command.ExtractText()))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name) && entry.Command.StartsWith('/'))
            .ToDictionary(entry => entry.Id);
    }

    public string NameFor(uint id) => catalog.TryGetValue(id, out var emote) ? emote.Name : $"ID {id}";

    public void RefreshUnlocked()
    {
        if (!clientState.IsLoggedIn || objects.LocalPlayer is null)
        {
            Unlocked = [];
            return;
        }
        var ui = UIState.Instance();
        if (ui == null) return;
        Unlocked = catalog.Values.Where(entry => ui->IsEmoteUnlocked((ushort)entry.Id))
            .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public bool CanPlay(uint emoteId, out string reason)
    {
        if (!clientState.IsLoggedIn || objects.LocalPlayer is not { } player)
            return Fail("Персонаж не загружен.", out reason);
        if (player.IsDead || player.CurrentHp == 0)
            return Fail("Персонаж не может выполнить эмоцию.", out reason);
        if (clientState.IsGPosing || clientState.IsPvP)
            return Fail("Групповой запуск недоступен в GPose и PvP.", out reason);
        if (BlockedConditions.Any(flag => conditions[flag]))
            return Fail("Завершите бой, переход, диалог, каст или езду верхом.", out reason);
        if (!catalog.ContainsKey(emoteId) || emoteId > ushort.MaxValue)
            return Fail("Неизвестная эмоция.", out reason);
        var ui = UIState.Instance();
        var agent = AgentEmote.Instance();
        var manager = EmoteManager.Instance();
        if (ui == null || agent == null || manager == null)
            return Fail("Система эмоций ещё не готова.", out reason);
        var id = (ushort)emoteId;
        if (!ui->IsEmoteUnlocked(id))
            return Fail("У персонажа не открыта выбранная эмоция.", out reason);
        if (!agent->CanUseEmote(id) || !manager->CanExecuteEmote(id))
            return Fail("Игра сейчас не разрешает эту эмоцию.", out reason);
        reason = string.Empty;
        return true;
    }

    public bool Play(uint emoteId, out string reason)
    {
        if (!CanPlay(emoteId, out reason)) return false;
        if (EmoteManager.Instance()->ExecuteEmote((ushort)emoteId)) return true;
        return Fail("Игра отклонила запуск эмоции.", out reason);
    }

    private static bool Fail(string message, out string reason)
    {
        reason = message;
        return false;
    }
}
