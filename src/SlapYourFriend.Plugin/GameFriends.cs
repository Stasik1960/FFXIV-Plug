using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using SlapYourFriend.Core;

namespace SlapYourFriend.Plugin;

internal static unsafe class GameFriends
{
    // Read only the friend list already loaded by the game. Called on Framework.Update.
    public static IReadOnlyList<string> Read(IDataManager data)
    {
        var proxy = InfoProxyFriendList.Instance();
        if (proxy == null || proxy->CharData == null || proxy->EntryCount > 200) return [];
        var worlds = data.GetExcelSheet<World>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < proxy->EntryCount; i++)
        {
            ref readonly var friend = ref proxy->CharData[i];
            if (friend.WaitingForFriendListApproval ||
                (friend.State & InfoProxyCommonList.CharacterData.OnlineStatus.WaitingForFriendListApproval) != 0 ||
                friend.HomeWorld == 0 || !worlds.TryGetRow(friend.HomeWorld, out var world)) continue;
            if (TargetIdentity.TryNormalize($"{friend.NameString}@{world.Name.ExtractText()}", out var identity)) names.Add(identity);
        }
        return names.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
