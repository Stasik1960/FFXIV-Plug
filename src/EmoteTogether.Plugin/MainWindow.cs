using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace EmoteTogether.Plugin;

internal sealed class MainWindow : Window
{
    private readonly Plugin plugin;
    private string inviteName = string.Empty;
    private string search = string.Empty;
    private uint selectedEmote;

    public MainWindow(Plugin plugin) : base("Emote Together — эмоциональная группа###EmoteTogether")
    {
        this.plugin = plugin;
        Size = new Vector2(650, 680);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(490, 480),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        var client = plugin.Client;
        ImGui.TextUnformatted("EMOTE TOGETHER");
        Wrapped("Одна эмоция. Общий обратный отсчёт. До 8 участников.");
        ImGui.Separator();

        ImGui.TextUnformatted(plugin.CharacterIdentity ?? "Войдите в игру для создания группы.");
        Wrapped("Обмен через личные сообщения FFXIV (/tell). Создайте группу и пригласите друга — дополнительная настройка не нужна.");
        if (client.GroupId is not null)
            ImGui.TextUnformatted(client.MemberId == client.LeaderId ? "Вы — лидер группы" : client.ClockReady
                ? $"Время с лидером синхронизировано · RTT {client.RoundTripMs:F0} мс"
                : "Синхронизация времени с лидером…");
        Wrapped(client.Status);
        ImGui.Separator();

        if (client.Invitation is { } invitation)
        {
            ImGui.TextColored(new Vector4(0.45f, 0.85f, 1, 1), "Приглашение в группу");
            Wrapped($"{invitation.FromName} приглашает вас синхронно проигрывать эмоции.");
            Wrapped("Приняв приглашение, вы разрешаете лидеру запускать доступные вашему персонажу эмоции, пока вы в группе. Можно отменить запуск или выйти в любой момент.");
            if (ImGui.Button("Принять приглашение")) client.RespondInvite(true);
            ImGui.SameLine();
            if (ImGui.Button("Отклонить")) client.RespondInvite(false);
            ImGui.Separator();
        }

        if (client.GroupId is null)
        {
            ImGui.BeginDisabled(!client.IsConnected || client.IsJoining);
            if (ImGui.Button("Создать эмоциональную группу")) client.CreateGroup();
            ImGui.EndDisabled();
            if (client.IsJoining)
            {
                Wrapped("Ожидаем подтверждение вступления от лидера…");
                if (ImGui.Button("Отменить вступление")) client.LeaveGroup();
            }
            Wrapped("У обоих игроков должен быть установлен плагин и доступен /tell друг другу. Выберите персонажа в игре или укажите его имя с домашним миром. Обычная игровая party не требуется.");
        }
        else
        {
            var leader = client.MemberId == client.LeaderId;
            ImGui.TextUnformatted($"Участники: {client.Members.Length}/8");
            foreach (var member in client.Members)
            {
                ImGui.Bullet();
                ImGui.SameLine();
                ImGui.TextUnformatted($"{member.Name}{(member.Id == client.LeaderId ? " · лидер" : string.Empty)}{(member.Id == client.MemberId ? " · вы" : string.Empty)}");
            }
            if (ImGui.Button(leader ? "Распустить группу" : "Выйти из группы"))
            {
                if (plugin.HasPendingRun) plugin.CancelRun();
                client.LeaveGroup();
            }
            ImGui.Separator();

            if (leader)
            {
                DrawInvitations();
                ImGui.Separator();
                DrawEmotes();
            }
            else
            {
                Wrapped("Эмоцию выбирает лидер группы. Если у кого-то она не открыта или персонаж занят, общий запуск отменится.");
                if (client.PendingEmoteId != 0)
                    ImGui.TextUnformatted($"Выбрано: {plugin.Emotes.NameFor(client.PendingEmoteId)}");
            }

            if (plugin.CountdownSeconds is { } seconds)
                ImGui.TextColored(new Vector4(0.45f, 1f, 0.65f, 1), $"До запуска: {seconds:F1} с");
            else if (plugin.HasPendingRun)
                ImGui.TextUnformatted("Ждём готовности всех участников…");

            if (plugin.HasPendingRun && ImGui.Button("Отменить общий запуск")) plugin.CancelRun();
        }

        ImGui.Separator();
        Wrapped(plugin.LocalStatus);
        Wrapped("Нужен доступный /tell; Free Trial не поддерживается. Между мирами действуют ограничения личных сообщений.");
        Wrapped("Служебные сообщения скрываются у игроков с плагином. Если у адресата плагина нет, он может увидеть их в /tell.");
        Wrapped("Для совместного просмотра находитесь рядом в игре. Точность старта зависит от сети, FPS и обработки эмоций FFXIV.");
    }

    private void DrawInvitations()
    {
        var client = plugin.Client;
        ImGui.BeginDisabled(plugin.HasPendingRun || client.Members.Length >= 8);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##inviteName", "Имя Фамилия@ДомашнийМир", ref inviteName, 128);
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(inviteName));
        if (ImGui.Button("Пригласить по имени")) client.Invite(inviteName.Trim());
        ImGui.EndDisabled();
        var target = Plugin.Targets.Target as IPlayerCharacter;
        var targetName = target is not null && target.HomeWorld.IsValid
            ? $"{target.Name.TextValue}@{target.HomeWorld.Value.Name.ExtractText()}"
            : null;
        ImGui.SameLine();
        ImGui.BeginDisabled(targetName is null || targetName == plugin.CharacterIdentity);
        if (ImGui.Button("Пригласить цель") && targetName is not null)
        {
            inviteName = targetName;
            client.Invite(targetName);
        }
        ImGui.EndDisabled();
        ImGui.EndDisabled();
    }

    private void DrawEmotes()
    {
        var client = plugin.Client;
        ImGui.TextUnformatted("Выберите открытую эмоцию");
        ImGui.BeginDisabled(plugin.HasPendingRun);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##emoteSearch", "Поиск по названию или команде", ref search, 128);
        var selectedName = selectedEmote == 0 ? "Эмоция ещё не выбрана" : plugin.Emotes.NameFor(selectedEmote);
        ImGui.TextUnformatted(selectedName);

        using (var child = ImRaii.Child("##emoteList", new Vector2(0, 180 * ImGuiHelpers.GlobalScale), true))
        {
            if (child.Success)
            {
                foreach (var entry in plugin.Emotes.Unlocked)
                {
                    if (!string.IsNullOrWhiteSpace(search) &&
                        !entry.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) &&
                        !entry.Command.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
                    if (ImGui.Selectable($"{entry.Name}  {entry.Command}##{entry.Id}", selectedEmote == entry.Id))
                        selectedEmote = entry.Id;
                }
                if (plugin.Emotes.Unlocked.Count == 0) Wrapped("Список открытых эмоций загружается…");
            }
        }
        ImGui.BeginDisabled(selectedEmote == 0 || !client.ClockReady || client.Members.Length < 2);
        if (ImGui.Button("Запустить вместе")) client.Start(selectedEmote);
        ImGui.EndDisabled();
        ImGui.EndDisabled();
        if (client.Members.Length < 2) Wrapped("Пригласите хотя бы одного участника.");
        if (!client.ClockReady) Wrapped("Дождитесь синхронизации времени. Отсчёт учитывает очередь личных сообщений и размер группы.");
    }

    private static void Wrapped(string value)
    {
        ImGui.PushTextWrapPos(0);
        ImGui.TextUnformatted(value);
        ImGui.PopTextWrapPos();
    }
}
