using System.Numerics;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using SlapYourFriend.Core;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace SlapYourFriend.Plugin;

// Accessed only from Framework.Update.
internal sealed unsafe class GameSlap
{
    private readonly IClientState clientState;
    private readonly IObjectTable objects;
    private readonly ICondition conditions;
    private readonly ushort slapId;

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
        ConditionFlag.OccupiedInQuestEvent, ConditionFlag.OccupiedSummoningBell, ConditionFlag.LoggingOut,
    ];

    public GameSlap(IDataManager data, IClientState clientState, IObjectTable objects, ICondition conditions)
    {
        this.clientState = clientState;
        this.objects = objects;
        this.conditions = conditions;
        slapId = (ushort)data.GetExcelSheet<Emote>(ClientLanguage.English)
            .Where(row => row.RowId is > 0 and <= ushort.MaxValue && row.TextCommand.IsValid &&
                row.TextCommand.Value.Command.ExtractText().Equals("/slap", StringComparison.OrdinalIgnoreCase))
            .Select(row => row.RowId).FirstOrDefault();
    }

    public bool CanPlay(out string reason)
    {
        if (!clientState.IsLoggedIn || objects.LocalPlayer is not { } player || !player.IsValid())
            return Fail("Войдите в игру, чтобы начать.", out reason);
        if (player.IsDead || player.CurrentHp == 0)
            return Fail("Ожидаем, пока персонаж сможет выполнить эмоцию.", out reason);
        if (clientState.IsGPosing || clientState.IsPvP || BlockedConditions.Any(flag => conditions[flag]))
            return Fail("Пауза: бой, переход, диалог или другое занятие.", out reason);
        if (((NativeCharacter*)player.Address)->EmoteController.IsEmoting())
            return Fail("Ожидаем завершения текущей эмоции.", out reason);
        if (slapId == 0)
            return Fail("Не удалось найти эмоцию /slap в данных игры.", out reason);
        var ui = UIState.Instance();
        var agent = AgentEmote.Instance();
        var manager = EmoteManager.Instance();
        if (ui == null || agent == null || manager == null || EmoteController.PlayEmoteOption.StaticVirtualTablePointer == null)
            return Fail("Система эмоций ещё не готова.", out reason);
        if (!ui->IsEmoteUnlocked(slapId))
            return Fail("Эмоция /slap недоступна персонажу.", out reason);
        if (!agent->CanUseEmote(slapId) || !manager->CanExecuteEmote(slapId))
            return Fail("Ожидаем, пока игра разрешит эмоцию.", out reason);
        reason = string.Empty;
        return true;
    }

    public bool Play(IPlayerCharacter target, float maxDistance, out string reason)
    {
        if (!CanPlay(out reason)) return false;
        var player = objects.LocalPlayer!;
        if (!float.IsFinite(maxDistance) || maxDistance is < 1 or > 4 || !target.IsValid() ||
            !TryIdentity(target, out var identity) || objects.SearchById(target.GameObjectId) is not IPlayerCharacter live ||
            live.Address != target.Address || !live.IsValid() || !TryIdentity(live, out var currentIdentity) ||
            !identity.Equals(currentIdentity, StringComparison.OrdinalIgnoreCase))
            return Fail("Игрок больше не доступен рядом.", out reason);
        if (live.GameObjectId == player.GameObjectId || !live.IsTargetable || live.IsDead || live.CurrentHp == 0)
            return Fail("Эта цель сейчас недоступна.", out reason);
        var distanceSquared = Vector3.DistanceSquared(player.Position, live.Position);
        if (!float.IsFinite(distanceSquared) || distanceSquared > maxDistance * maxDistance)
            return Fail("Игрок уже отошёл слишком далеко.", out reason);

        var options = new EmoteController.PlayEmoteOption
        {
            VirtualTable = EmoteController.PlayEmoteOption.StaticVirtualTablePointer,
            TargetId = live.GameObjectId,
            DisableLogMessage = true,
        };
        if (EmoteManager.Instance()->ExecuteEmote(slapId, &options))
        {
            reason = string.Empty;
            return true;
        }
        return Fail("Игра отклонила пощёчину; попробуем позже.", out reason);
    }

    private static bool TryIdentity(IPlayerCharacter player, out string identity)
    {
        identity = string.Empty;
        return player.HomeWorld.IsValid && TargetIdentity.TryNormalize(
            $"{player.Name.TextValue}@{player.HomeWorld.Value.Name.ExtractText()}", out identity);
    }

    private static bool Fail(string message, out string reason)
    {
        reason = message;
        return false;
    }
}
