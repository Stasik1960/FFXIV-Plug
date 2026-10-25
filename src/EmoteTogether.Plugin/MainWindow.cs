using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace EmoteTogether.Plugin;

internal sealed class MainWindow : Window
{
    private static readonly Vector4 Ink = new(0.035f, 0.043f, 0.055f, 0.98f);
    private static readonly Vector4 Card = new(0.068f, 0.079f, 0.098f, 0.97f);
    private static readonly Vector4 Text = new(0.94f, 0.94f, 0.92f, 1);
    private static readonly Vector4 Muted = new(0.55f, 0.60f, 0.67f, 1);
    private static readonly Vector4 Amber = new(0.98f, 0.65f, 0.28f, 1);
    private static readonly Vector4 Green = new(0.45f, 0.81f, 0.64f, 1);
    private readonly Plugin plugin;
    private readonly TigerBackdrop backdrop = new();
    private string inviteName = string.Empty;
    private string search = string.Empty;
    private uint selectedEmote;
    internal uint SelectedEmoteId => selectedEmote;
    private bool settingsRequested;
    private bool expandRequested;
    private int colors;
    private int variables;
    private float Scale => ImGuiHelpers.GlobalScale;

    public MainWindow(Plugin plugin) : base("Emote Together###EmoteTogether")
    {
        this.plugin = plugin;
        Size = new Vector2(920, 790);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(780, 680),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Flags = ImGuiWindowFlags.None;
    }

    public void OpenSettings()
    {
        OpenExpanded();
        settingsRequested = true;
    }

    internal void OpenExpanded() { IsOpen = true; expandRequested = true; }

    public override void PreDraw()
    {
        if (expandRequested) { ImGui.SetNextWindowCollapsed(false, ImGuiCond.Always); expandRequested = false; }
        colors = 0;
        variables = 0;
        Push(ImGuiCol.WindowBg, Ink);
        Push(ImGuiCol.ChildBg, Card);
        Push(ImGuiCol.PopupBg, new Vector4(0.075f, 0.087f, 0.108f, 1));
        Push(ImGuiCol.Border, new Vector4(0.3f, 0.35f, 0.42f, 0.38f));
        Push(ImGuiCol.Text, Text);
        Push(ImGuiCol.TextDisabled, Muted);
        Push(ImGuiCol.FrameBg, new Vector4(0.105f, 0.124f, 0.15f, 1));
        Push(ImGuiCol.FrameBgHovered, new Vector4(0.15f, 0.18f, 0.21f, 1));
        Push(ImGuiCol.FrameBgActive, new Vector4(0.19f, 0.21f, 0.24f, 1));
        Push(ImGuiCol.Button, new Vector4(0.14f, 0.16f, 0.19f, 1));
        Push(ImGuiCol.ButtonHovered, new Vector4(0.22f, 0.25f, 0.29f, 1));
        Push(ImGuiCol.ButtonActive, new Vector4(0.27f, 0.29f, 0.33f, 1));
        Push(ImGuiCol.Header, new Vector4(0.5f, 0.32f, 0.14f, 0.65f));
        Push(ImGuiCol.HeaderHovered, new Vector4(0.52f, 0.36f, 0.19f, 0.45f));
        Push(ImGuiCol.HeaderActive, new Vector4(0.64f, 0.41f, 0.19f, 0.75f));
        Push(ImGuiCol.SliderGrab, Amber);
        Push(ImGuiCol.SliderGrabActive, new Vector4(1, 0.79f, 0.47f, 1));
        Push(ImGuiCol.CheckMark, Amber);
        Push(ImGuiCol.Separator, new Vector4(0.3f, 0.35f, 0.42f, 0.35f));
        Push(ImGuiCol.ScrollbarBg, Vector4.Zero);
        Push(ImGuiCol.ScrollbarGrab, new Vector4(0.26f, 0.3f, 0.35f, 0.75f));
        Push(ImGuiCol.ScrollbarGrabHovered, new Vector4(0.4f, 0.45f, 0.5f, 0.9f));
        Push(ImGuiCol.ScrollbarGrabActive, Amber);
        Push(ImGuiCol.TitleBg, Ink);
        Push(ImGuiCol.TitleBgActive, Ink);
        Push(ImGuiCol.TitleBgCollapsed, Ink);
        Push(ImGuiStyleVar.WindowPadding, new Vector2(20, 16) * Scale);
        Push(ImGuiStyleVar.ItemSpacing, new Vector2(10, 8) * Scale);
        Push(ImGuiStyleVar.FramePadding, new Vector2(12, 7) * Scale);
        Push(ImGuiStyleVar.WindowRounding, 14 * Scale);
        Push(ImGuiStyleVar.ChildRounding, 12 * Scale);
        Push(ImGuiStyleVar.FrameRounding, 8 * Scale);
        Push(ImGuiStyleVar.PopupRounding, 10 * Scale);
        Push(ImGuiStyleVar.ScrollbarRounding, 8 * Scale);
        Push(ImGuiStyleVar.ScrollbarSize, 8 * Scale);
        Push(ImGuiStyleVar.ChildBorderSize, 1);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(variables);
        ImGui.PopStyleColor(colors);
    }

    public override void Draw()
    {
        backdrop.Draw(Scale, plugin.Configuration.ShowTigerBackground);
        DrawHero();
        var bodyHeight = Math.Max(390 * Scale, ImGui.GetContentRegionAvail().Y - 98 * Scale);
        if (plugin.Client.GroupId is null) DrawWelcome(bodyHeight);
        else DrawGroup(bodyHeight);
        Gap(10);
        DrawStatus();
        DrawSettingsPopup();
    }

    private void DrawHero()
    {
        Gap(4);
        Label("СИНХРОННЫЕ ЭМОЦИИ", Amber);
        ImGui.SameLine(ImGui.GetWindowSize().X - 190 * Scale);
        if (ImGui.SmallButton("Настройки")) settingsRequested = true;
        LargeText("EMOTE TOGETHER", 1.65f);
        Label("Один ритм. Одна эмоция.", Muted);
        Gap(5);
        ImGui.TextColored(plugin.Client.IsConnected ? Green : Muted,
            plugin.CharacterIdentity ?? "Войдите в игру, чтобы начать");
        Gap(17);
    }

    private void DrawWelcome(float height)
    {
        using var card = ImRaii.Child("##welcome", new Vector2(0, height), true);
        if (!card.Success) return;
        var client = plugin.Client;
        if (client.Invitation is { } invitation)
        {
            Label("ВАС ПРИГЛАШАЮТ", Amber);
            Gap(6);
            LargeText("Присоединитесь к группе", 1.25f);
            Wrapped(invitation.FromName ?? "Лидер группы", Text);
            Gap(14);
            Wrapped("Выполните одну эмоцию вместе — после общего обратного отсчёта.", Muted);
            Gap(8);
            Wrapped("Приняв приглашение, вы разрешаете лидеру запускать доступные персонажу эмоции, пока остаётесь в группе. Запуск можно отменить, из группы — выйти в любой момент.", Muted);
            Gap(14);
            if (AccentButton("Принять приглашение", new Vector2(235 * Scale, 44 * Scale))) client.RespondInvite(true);
            ImGui.SameLine();
            if (ImGui.Button("Отклонить", new Vector2(140 * Scale, 44 * Scale))) client.RespondInvite(false);
            return;
        }
        Label("НАЧНИТЕ ВМЕСТЕ", Amber);
        Gap(6);
        LargeText(client.IsJoining ? "Ждём подтверждения" : "Соберите свою группу", 1.35f);
        Wrapped(client.IsJoining ? "Лидер подтверждает состав. Это может занять несколько секунд." : "Для двоих, компании друзей или общего танца — до 8 участников.", Muted);
        Gap(18);
        Step("01", "Выберите друга", "Нажмите «Пригласить цель» — группа создастся автоматически.");
        Step("02", "Выберите эмоцию", "Используйте эмоцию, открытую у всех участников.");
        Step("03", "Поймайте общий момент", "Настройте отсчёт и запустите её вместе.");
        Gap(16);
        DrawTargetInvitation(350 * Scale, 44 * Scale, true);
        ImGui.BeginDisabled(!client.IsConnected || client.IsJoining || client.IsRecoveringGroup);
        if (ImGui.Button("Создать пустую группу", new Vector2(235 * Scale, 32 * Scale))) client.CreateGroup();
        ImGui.EndDisabled();
        if (client.IsJoining && ImGui.Button("Отменить вступление")) client.LeaveGroup();
        Gap(10);
        Wrapped("У всех должен быть установлен плагин и доступен /tell. Обычная игровая party не требуется.", Muted);
        DrawInvitePopup();
    }

    private void DrawGroup(float height)
    {
        if (!ImGui.BeginTable("##groupLayout", 2, ImGuiTableFlags.SizingFixedFit)) return;
        try
        {
            ImGui.TableSetupColumn("group", ImGuiTableColumnFlags.WidthFixed, 285 * Scale);
            ImGui.TableSetupColumn("emote", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var groupHeight = Math.Max(205 * Scale, height - 210 * Scale);
            DrawMembers(groupHeight);
            Gap(10);
            DrawDelay(height - groupHeight - 10 * Scale);
            ImGui.TableNextColumn();
            DrawEmotes(height);
        }
        finally { ImGui.EndTable(); }
    }

    private void DrawMembers(float height)
    {
        using var card = ImRaii.Child("##groupCard", new Vector2(0, height), true);
        if (!card.Success) return;
        var client = plugin.Client;
        Label("ВАША ГРУППА", Amber);
        ImGui.SameLine();
        ImGui.TextColored(Muted, $"{client.Members.Length} / 8");
        var actionHeight = (client.IsLeader ? 82 : 42) * Scale;
        using (var list = ImRaii.Child("##members", new Vector2(0, Math.Max(45 * Scale, ImGui.GetContentRegionAvail().Y - actionHeight)), false))
        {
            if (list.Success)
            {
                foreach (var member in client.Members)
                {
                    var cursor = ImGui.GetCursorPos();
                    var width = ImGui.GetContentRegionAvail().X;
                    var canTransfer = client.IsLeader && member.Id != client.MemberId;
                    var transferWidth = ImGui.CalcTextSize("Передать").X + ImGui.GetStyle().FramePadding.X * 2;
                    var nameWidth = width - (canTransfer ? transferWidth + 8 * Scale : 0);
                    var parts = member.Name.Split('@', 2);
                    ImGui.TextColored(member.Id == client.LeaderId ? Amber : Text, Elide(parts[0], nameWidth));
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(member.Name);
                    var role = member.Id == client.LeaderId ? "лидер" : member.Id == client.MemberId ? "вы" : "участник";
                    ImGui.TextColored(Muted, $"{(parts.Length == 2 ? parts[1] : string.Empty)} · {role}");
                    if (canTransfer)
                    {
                        ImGui.SetCursorPos(cursor + new Vector2(width - transferWidth, 3 * Scale));
                        ImGui.BeginDisabled(plugin.HasPendingRun || client.IsTransferringLeadership || client.IsRecoveringGroup);
                        if (ImGui.SmallButton($"Передать##{member.Id}")) client.TransferLeadership(member.Id);
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Передать лидерство этому участнику");
                        ImGui.EndDisabled();
                    }
                    ImGui.SetCursorPos(new Vector2(cursor.X, cursor.Y + 45 * Scale));
                }
            }
        }
        if (client.IsLeader)
        {
            DrawTargetInvitation(ImGui.GetContentRegionAvail().X - 36 * Scale - ImGui.GetStyle().ItemSpacing.X, 30 * Scale, false);
            if (ImGui.Button("Распустить группу", new Vector2(-1, 30 * Scale)))
            {
                if (plugin.HasPendingRun) plugin.CancelRun();
                client.LeaveGroup();
            }
        }
        else
        {
            if (ImGui.Button("Выйти из группы", new Vector2(-1, 34 * Scale)))
            {
                if (plugin.HasPendingRun) plugin.CancelRun();
                client.LeaveGroup();
            }
        }
        DrawInvitePopup();
    }

    private void DrawTargetInvitation(float width, float height, bool accent)
    {
        var client = plugin.Client;
        var targetName = GetTargetName();
        var unavailable = !client.IsConnected || client.IsJoining || plugin.HasPendingRun ||
            client.IsTransferringLeadership || client.IsRecoveringGroup || client.Members.Length >= 8 ||
            (client.GroupId is not null && !client.IsLeader);
        var validTarget = targetName is not null &&
            !string.Equals(targetName, plugin.CharacterIdentity, StringComparison.OrdinalIgnoreCase) &&
            !client.Members.Any(member => string.Equals(member.Id, targetName, StringComparison.OrdinalIgnoreCase));
        ImGui.BeginDisabled(unavailable || !validTarget);
        var clicked = accent
            ? AccentButton("Пригласить цель", new Vector2(width, height))
            : ImGui.Button("Пригласить цель", new Vector2(width, height));
        if (clicked && targetName is not null) InvitePlayer(targetName);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(targetName is null ? "Выберите персонажа друга в игре" : $"Пригласить: {targetName}");
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(unavailable);
        if (ImGui.Button("…##inviteByName", new Vector2(36 * Scale, height))) ImGui.OpenPopup("##invitePopup");
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Пригласить по имени");
        ImGui.EndDisabled();
    }

    private static string? GetTargetName()
    {
        var target = Plugin.Targets.Target as IPlayerCharacter;
        return target is not null && target.HomeWorld.IsValid
            ? $"{target.Name.TextValue}@{target.HomeWorld.Value.Name.ExtractText()}"
            : null;
    }

    private void InvitePlayer(string name)
    {
        var client = plugin.Client;
        if (client.GroupId is null) client.CreateGroup();
        if (client.IsLeader) client.Invite(name);
    }

    private void DrawInvitePopup()
    {
        ImGui.SetNextWindowSize(new Vector2(420 * Scale, 0), ImGuiCond.Always);
        if (!ImGui.BeginPopup("##invitePopup")) return;
        try
        {
            Label("ПРИГЛАСИТЬ ДРУГА", Amber);
            Wrapped("Укажите имя персонажа с домашним миром.", Muted);
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##inviteName", "Имя Фамилия@ДомашнийМир", ref inviteName, 128);
            ImGui.BeginDisabled(string.IsNullOrWhiteSpace(inviteName) || !plugin.Client.IsConnected ||
                plugin.Client.IsJoining || plugin.HasPendingRun || plugin.Client.IsTransferringLeadership ||
                plugin.Client.IsRecoveringGroup || plugin.Client.Members.Length >= 8);
            if (ImGui.Button("Пригласить по имени", new Vector2(-1, 36 * Scale)))
            {
                InvitePlayer(inviteName.Trim());
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            Wrapped("Другу нужно подтвердить приглашение. У него должен работать /tell.", Muted);
        }
        finally { ImGui.EndPopup(); }
    }

    private void DrawDelay(float height)
    {
        using var card = ImRaii.Child("##delayCard", new Vector2(0, height), true);
        if (!card.Success) return;
        var client = plugin.Client;
        Label("НАСТРОЙКИ ГРУППЫ", Amber);
        ImGui.TextUnformatted("Задержка старта");
        if (!client.IsLeader)
        {
            Wrapped(client.PendingRunId != null ? $"Лидер выбрал: {client.RequestedCountdownSeconds} с" : "Время выбирает лидер группы.", Muted);
            Wrapped("Отсчёт начнётся после готовности всех участников.", Muted);
            return;
        }
        var minimum = Math.Max(1, client.MinimumCountdownSeconds);
        ImGui.BeginDisabled(plugin.HasPendingRun || client.IsTransferringLeadership || client.IsRecoveringGroup || minimum > 60);
        foreach (var preset in new[] { 3, 5, 10 })
        {
            if (preset != 3) ImGui.SameLine();
            ImGui.BeginDisabled(preset < minimum);
            var selected = plugin.Configuration.CountdownSeconds == preset;
            if (selected) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.50f, 0.32f, 0.14f, 0.9f));
            if (ImGui.Button($"{preset} с##preset", new Vector2(68 * Scale, 30 * Scale))) plugin.SaveCountdown(preset);
            if (selected) ImGui.PopStyleColor();
            ImGui.EndDisabled();
        }
        var value = Math.Clamp(Math.Max(plugin.Configuration.CountdownSeconds, minimum), 1, 60);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderInt("##countdown", ref value, Math.Min(minimum, 60), 60, "%d сек.")) plugin.SaveCountdown(value);
        ImGui.EndDisabled();
        Wrapped(minimum > 60 ? "Очередь занята. Дождитесь отправки сообщений." : $"После готовности всех. Минимум для группы: {minimum} с.", Muted);
    }

    private void DrawEmotes(float height)
    {
        using var card = ImRaii.Child("##emoteCard", new Vector2(0, height), true);
        if (!card.Success) return;
        var client = plugin.Client;
        Label(client.IsLeader ? "ВЫБОР ЭМОЦИИ" : "ОБЩИЙ МОМЕНТ", Amber);
        if (!client.IsLeader)
        {
            Gap(10);
            LargeText(client.PendingEmoteId == 0 ? "Готовы к общей эмоции" : plugin.Emotes.NameFor(client.PendingEmoteId), 1.25f);
            Wrapped("Эмоцию и время выбирает лидер. Если персонаж занят или эмоция не открыта, запуск отменится.", Muted);
            Gap(22);
            DrawRunState();
            return;
        }
        ImGui.BeginDisabled(plugin.HasPendingRun || client.IsTransferringLeadership || client.IsRecoveringGroup);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##emoteSearch", "Найти эмоцию или команду…", ref search, 128);
        using (var list = ImRaii.Child("##emoteList", new Vector2(0, Math.Max(95 * Scale, ImGui.GetContentRegionAvail().Y - 145 * Scale)), false))
        {
            if (list.Success)
            {
                foreach (var entry in plugin.Emotes.Unlocked)
                {
                    if (!string.IsNullOrWhiteSpace(search) && !entry.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) &&
                        !entry.Command.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
                    if (ImGui.Selectable($"{entry.Name}  {entry.Command}##{entry.Id}", selectedEmote == entry.Id, ImGuiSelectableFlags.None, new Vector2(0, 28 * Scale)))
                        selectedEmote = entry.Id;
                }
                if (plugin.Emotes.Unlocked.Count == 0) Wrapped("Загружаем открытые эмоции…", Muted);
            }
        }
        ImGui.EndDisabled();
        if (plugin.HasPendingRun) { DrawRunState(); return; }
        ImGui.Separator();
        Label(selectedEmote == 0 ? "Выберите эмоцию в списке" : plugin.Emotes.NameFor(selectedEmote), Text);
        var effective = Math.Max(plugin.Configuration.CountdownSeconds, client.MinimumCountdownSeconds);
        ImGui.BeginDisabled(selectedEmote == 0 || !client.ClockReady || client.Members.Length < 2 ||
            client.IsTransferringLeadership || client.IsRecoveringGroup || effective > 60);
        if (AccentButton($"Запустить вместе · {effective} с", new Vector2(-1, 44 * Scale)))
            client.Start(selectedEmote, plugin.Configuration.CountdownSeconds);
        ImGui.EndDisabled();
        Wrapped(client.Members.Length < 2 ? "Пригласите хотя бы одного участника." : "Отсчёт начнётся, когда все будут готовы.", Muted);
    }

    private void DrawRunState()
    {
        if (plugin.CountdownSeconds is { } seconds)
        {
            Label("ДО ОБЩЕГО СТАРТА", Amber);
            LargeText($"{seconds:F1} с", 2.25f);
            Wrapped($"Общий отсчёт: {plugin.Client.EffectiveCountdownSeconds} с", Muted);
        }
        else if (plugin.HasPendingRun)
        {
            Label("ПРОВЕРЯЕМ ГОТОВНОСТЬ", Amber);
            Wrapped("Ждём участников и синхронизацию времени…", Muted);
        }
        else Wrapped("Ожидаем выбор лидера.", Muted);
        if (plugin.HasPendingRun && ImGui.Button("Отменить общий запуск", new Vector2(-1, 38 * Scale))) plugin.CancelRun();
    }

    private void DrawStatus()
    {
        if (plugin.Client.IsRecoveringGroup)
        {
            Wrapped("Восстанавливаем эмоциональную группу…", Amber);
            Wrapped(plugin.Client.Status, Muted);
        }
        else if (plugin.Client.IsTransferringLeadership)
            Wrapped($"Передаём лидерство: {plugin.Client.PendingLeaderId}…", Amber);
        else Wrapped(plugin.Client.Status, Muted);
        Wrapped(plugin.LocalStatus, Text);
        if (ImGui.CollapsingHeader("Условия совместного запуска"))
        {
            Wrapped("Участникам нужен доступный /tell; Free Trial не поддерживается. Для совместного просмотра встаньте рядом. Точность старта зависит от сети, FPS и обработки FFXIV.", Muted);
            Wrapped("У адресата без плагина служебные сообщения могут быть видны в чате.", Muted);
        }
    }

    private void DrawSettingsPopup()
    {
        if (settingsRequested)
        {
            ImGui.OpenPopup("##pluginSettings");
            ImGui.SetNextWindowPos(ImGui.GetWindowPos() + ImGui.GetWindowSize() / 2, ImGuiCond.Appearing, new Vector2(0.5f));
            settingsRequested = false;
        }
        ImGui.SetNextWindowSize(new Vector2(410 * Scale, 0), ImGuiCond.Always);
        if (!ImGui.BeginPopup("##pluginSettings")) return;
        try
        {
            Label("НАСТРОЙКИ ПЛАГИНА", Amber);
            Gap(6);
            LargeText("Внешний вид", 1.2f);
            var showTiger = plugin.Configuration.ShowTigerBackground;
            if (ImGui.Checkbox("Фон с тигром", ref showTiger)) plugin.SaveAppearance(showTiger);
            Wrapped("Показывать тигра в основном окне и на мини-панели. При отключении остаётся тёмная тема.", Muted);
            Gap(8);
            var showMiniPanel = plugin.Configuration.ShowMiniPanel;
            if (ImGui.Checkbox("Мини-панель при закрытом окне", ref showMiniPanel)) plugin.SaveMiniPanel(showMiniPanel);
            Wrapped("Показывать компактную перемещаемую панель, когда основное окно закрыто. При сворачивании до заголовка она не появляется.", Muted);
            Gap(10);
            Wrapped(plugin.PhoneFilterActive ? "Служебные сообщения скрыты из чата и телефона." :
                "Фильтр телефона недоступен. Подробности в /xllog.", plugin.PhoneFilterActive ? Muted : Amber);
            Gap(10);
            if (ImGui.Button("Готово", new Vector2(-1, 36 * Scale))) ImGui.CloseCurrentPopup();
        }
        finally { ImGui.EndPopup(); }
    }

    private void Step(string number, string title, string description)
    {
        ImGui.TextColored(Amber, number);
        ImGui.SameLine(48 * Scale);
        ImGui.TextUnformatted(title);
        ImGui.SetCursorPosX(48 * Scale);
        Wrapped(description, Muted);
        Gap(6);
    }
    private static void LargeText(string value, float factor)
    {
        ImGui.SetWindowFontScale(factor);
        ImGui.TextUnformatted(value);
        ImGui.SetWindowFontScale(1);
    }
    private static string Elide(string value, float width)
    {
        if (ImGui.CalcTextSize(value).X <= width) return value;
        while (value.Length > 1 && ImGui.CalcTextSize(value + "…").X > width) value = value[..^1];
        return value + "…";
    }
    private static void Label(string value, Vector4 color) => ImGui.TextColored(color, value);
    private static void Wrapped(string value, Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.PushTextWrapPos(0);
        ImGui.TextUnformatted(value);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }
    private void Gap(float pixels) => ImGui.Dummy(new Vector2(0, pixels * Scale));
    private static bool AccentButton(string label, Vector2 size)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Amber);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1, 0.75f, 0.39f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.88f, 0.52f, 0.19f, 1));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.10f, 0.08f, 0.05f, 1));
        var result = ImGui.Button(label, size);
        ImGui.PopStyleColor(4);
        return result;
    }
    private void Push(ImGuiCol index, Vector4 value) { ImGui.PushStyleColor(index, value); colors++; }
    private void Push(ImGuiStyleVar index, Vector2 value) { ImGui.PushStyleVar(index, value); variables++; }
    private void Push(ImGuiStyleVar index, float value) { ImGui.PushStyleVar(index, value); variables++; }
}
