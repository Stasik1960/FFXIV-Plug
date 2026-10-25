using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace SlapYourFriend.Plugin;

internal sealed class MainWindow : Window
{
    private static readonly Vector4 Ink = new(0.055f, 0.060f, 0.080f, 1);
    private static readonly Vector4 Text = new(0.96f, 0.94f, 0.91f, 1);
    private static readonly Vector4 Muted = new(0.57f, 0.60f, 0.67f, 1);
    private static readonly Vector4 Coral = new(1, 0.56f, 0.46f, 1);
    private readonly Plugin plugin;
    private readonly Backdrop backdrop = new();
    private string typedName = string.Empty;
    private string friendSearch = string.Empty;
    private bool expandRequested;
    private bool settingsRequested;
    private int colors;
    private int variables;
    private float Scale => ImGuiHelpers.GlobalScale;

    public MainWindow(Plugin plugin) : base("Slap Your Friend###SlapYourFriend")
    {
        this.plugin = plugin;
        Size = new Vector2(540, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(490, 580),
            MaximumSize = new Vector2(840, float.MaxValue),
        };
        Flags = ImGuiWindowFlags.None;
    }

    internal void OpenExpanded() { IsOpen = true; expandRequested = true; }
    internal void OpenSettings() { OpenExpanded(); settingsRequested = true; }

    public override void PreDraw()
    {
        if (expandRequested) { ImGui.SetNextWindowCollapsed(false, ImGuiCond.Always); expandRequested = false; }
        colors = 0;
        variables = 0;
        Push(ImGuiCol.WindowBg, Ink);
        Push(ImGuiCol.ChildBg, new Vector4(0, 0, 0, 0));
        Push(ImGuiCol.PopupBg, new Vector4(0.080f, 0.087f, 0.110f, 1));
        Push(ImGuiCol.Text, Text);
        Push(ImGuiCol.TextDisabled, Muted);
        Push(ImGuiCol.Border, new Vector4(0.30f, 0.32f, 0.38f, 0.35f));
        Push(ImGuiCol.FrameBg, new Vector4(0.11f, 0.12f, 0.15f, 1));
        Push(ImGuiCol.FrameBgHovered, new Vector4(0.17f, 0.18f, 0.22f, 1));
        Push(ImGuiCol.FrameBgActive, new Vector4(0.22f, 0.21f, 0.25f, 1));
        Push(ImGuiCol.Button, new Vector4(0.13f, 0.14f, 0.17f, 1));
        Push(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.21f, 0.23f, 1));
        Push(ImGuiCol.ButtonActive, new Vector4(0.36f, 0.25f, 0.26f, 1));
        Push(ImGuiCol.Header, new Vector4(0.45f, 0.26f, 0.25f, 0.65f));
        Push(ImGuiCol.HeaderHovered, new Vector4(0.48f, 0.28f, 0.28f, 0.70f));
        Push(ImGuiCol.HeaderActive, new Vector4(0.60f, 0.34f, 0.31f, 0.8f));
        Push(ImGuiCol.CheckMark, Coral);
        Push(ImGuiCol.SliderGrab, Coral);
        Push(ImGuiCol.SliderGrabActive, new Vector4(1, 0.72f, 0.60f, 1));
        Push(ImGuiCol.Separator, new Vector4(0.30f, 0.32f, 0.38f, 0.35f));
        Push(ImGuiCol.ScrollbarBg, Vector4.Zero);
        Push(ImGuiCol.ScrollbarGrab, new Vector4(0.28f, 0.30f, 0.36f, 0.7f));
        Push(ImGuiCol.ScrollbarGrabHovered, Muted);
        Push(ImGuiCol.ScrollbarGrabActive, Coral);
        Push(ImGuiCol.TitleBg, Ink);
        Push(ImGuiCol.TitleBgActive, Ink);
        Push(ImGuiCol.TitleBgCollapsed, Ink);
        Push(ImGuiStyleVar.WindowPadding, new Vector2(22, 17) * Scale);
        Push(ImGuiStyleVar.ItemSpacing, new Vector2(8, 7) * Scale);
        Push(ImGuiStyleVar.FramePadding, new Vector2(11, 7) * Scale);
        Push(ImGuiStyleVar.WindowRounding, 13 * Scale);
        Push(ImGuiStyleVar.FrameRounding, 7 * Scale);
        Push(ImGuiStyleVar.PopupRounding, 10 * Scale);
        Push(ImGuiStyleVar.ScrollbarRounding, 8 * Scale);
        Push(ImGuiStyleVar.ScrollbarSize, 7 * Scale);
    }

    public override void PostDraw() { ImGui.PopStyleVar(variables); ImGui.PopStyleColor(colors); }

    public override void Draw()
    {
        backdrop.Draw(Scale, plugin.Configuration.ShowBackground);
        DrawHeader();
        var enabled = plugin.Configuration.Enabled;
        if (ImGui.Checkbox("Автопощёчина", ref enabled)) plugin.SetEnabled(enabled);
        ImGui.SameLine();
        ImGui.TextColored(enabled ? Coral : Muted, enabled ? "ВКЛЮЧЕНА" : "ПАУЗА");
        Gap(5);
        DrawAddControls();
        Gap(5);
        var labelStart = ImGui.GetCursorPosX();
        var labelWidth = ImGui.GetContentRegionAvail().X;
        ImGui.TextColored(Muted, $"ВЫБРАННЫЕ ИГРОКИ  ·  {plugin.Configuration.Players.Count}");
        ImGui.SameLine(labelStart + labelWidth - 127 * Scale);
        ImGui.TextColored(Muted, "ПОВТОР");
        DrawPlayers(Math.Max(105 * Scale, ImGui.GetContentRegionAvail().Y - 132 * Scale));
        ImGui.Separator();
        DrawRadius();
        Gap(3);
        Wrapped(plugin.Status, plugin.Configuration.Enabled ? Text : Muted);
        DrawFriendsPopup();
        DrawSettingsPopup();
    }

    private void DrawHeader()
    {
        ImGui.TextColored(Coral, "ПРИВЕТСТВИЕ С ХАРАКТЕРОМ");
        ImGui.SameLine(ImGui.GetWindowSize().X - 130 * Scale);
        if (ImGui.SmallButton("Настройки")) settingsRequested = true;
        Gap(4);
        ImGui.SetWindowFontScale(1.38f);
        ImGui.TextUnformatted("SLAP YOUR FRIEND");
        ImGui.SetWindowFontScale(1);
        ImGui.TextColored(Muted, "Подойди ближе. Остальное — /slap.");
        Gap(19);
    }

    private void DrawAddControls()
    {
        var target = plugin.CurrentTargetIdentity;
        var alreadyAdded = target is not null && plugin.Configuration.Players.Any(p => string.Equals(p.Identity, target, StringComparison.OrdinalIgnoreCase));
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.BeginDisabled(target is null || alreadyAdded);
        if (AccentButton("Добавить цель", new Vector2(width * 0.62f, 34 * Scale)) && target is not null) plugin.AddPlayer(target);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(target is null ? "Выберите персонажа в игре" : alreadyAdded ? $"Уже в списке: {target}" : target);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Из друзей…", new Vector2(-1, 34 * Scale)))
        {
            friendSearch = string.Empty;
            ImGui.OpenPopup("##friends");
        }
        ImGui.SetNextItemWidth(width - 42 * Scale);
        var enter = ImGui.InputTextWithHint("##playerName", "Имя Фамилия@ДомашнийМир", ref typedName, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(typedName));
        var clicked = ImGui.Button("+##addName", new Vector2(-1, 0));
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Добавить по имени");
        if ((enter || clicked) && !string.IsNullOrWhiteSpace(typedName) && plugin.AddPlayer(typedName)) typedName = string.Empty;
    }

    private void DrawPlayers(float height)
    {
        using var list = ImRaii.Child("##players", new Vector2(0, height), false);
        if (!list.Success) return;
        if (plugin.Configuration.Players.Count == 0)
        {
            Gap(17);
            ImGui.TextUnformatted("Кому передать привет?");
            Wrapped("Выберите друга или добавьте имя персонажа.", Muted);
            return;
        }
        foreach (var player in plugin.Configuration.Players.ToArray())
        {
            ImGui.PushID(player.Identity);
            try
            {
                var row = ImGui.GetCursorPos();
                var width = ImGui.GetContentRegionAvail().X;
                var controlsWidth = 127 * Scale;
                var parts = player.Identity.Split('@', 2);
                ImGui.TextUnformatted(Elide(parts[0], width - controlsWidth - 8 * Scale));
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(player.Identity);
                ImGui.TextColored(Muted, Elide(parts.Length == 2 ? parts[1] : string.Empty, width - controlsWidth - 8 * Scale));
                ImGui.SetCursorPos(row + new Vector2(width - controlsWidth, 4 * Scale));
                var minutes = player.IntervalMinutes;
                ImGui.SetNextItemWidth(87 * Scale);
                if (ImGui.DragInt("##interval", ref minutes, 1, 1, 60, "%d мин."))
                {
                    player.IntervalMinutes = Math.Clamp(minutes, 1, 60);
                    plugin.SaveConfiguration();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Интервал повтора для этого игрока. Потяните значение или нажмите Ctrl + клик для ввода.\nПервый раз — при сближении; затем по таймеру, пока вы рядом.\nОтход и возвращение не сбрасывают интервал.");
                ImGui.SameLine();
                if (ImGui.Button("×##remove", new Vector2(31 * Scale, 0))) plugin.RemovePlayer(player.Identity);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Убрать из списка");
                ImGui.SetCursorPos(new Vector2(row.X, row.Y + 48 * Scale));
            }
            finally { ImGui.PopID(); }
        }
    }

    private void DrawRadius()
    {
        ImGui.TextColored(Muted, "ДИСТАНЦИЯ СРАБАТЫВАНИЯ");
        var radius = plugin.Configuration.Radius;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("##radius", ref radius, 1, 4, "%.1f м"))
        {
            plugin.Configuration.Radius = Math.Clamp(radius, 1, 4);
            plugin.SaveConfiguration();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Расстояние между персонажами для автоматической эмоции.");
    }

    private void DrawFriendsPopup()
    {
        ImGui.SetNextWindowSize(new Vector2(420 * Scale, 0), ImGuiCond.Always);
        if (!ImGui.BeginPopup("##friends")) return;
        try
        {
            ImGui.TextColored(Coral, "ИЗ СПИСКА ДРУЗЕЙ");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##friendSearch", "Найти друга…", ref friendSearch, 96);
            var friends = plugin.Friends;
            using var list = ImRaii.Child("##friendList", new Vector2(0, 245 * Scale), false);
            if (!list.Success) return;
            if (friends.Count == 0) Wrapped("Откройте список друзей в игре, чтобы загрузить его.", Muted);
            foreach (var identity in friends)
            {
                if (!string.IsNullOrWhiteSpace(friendSearch) && !identity.Contains(friendSearch, StringComparison.CurrentCultureIgnoreCase)) continue;
                var added = plugin.Configuration.Players.Any(p => string.Equals(p.Identity, identity, StringComparison.OrdinalIgnoreCase));
                ImGui.BeginDisabled(added);
                if (ImGui.Selectable($"{identity}{(added ? "  · добавлен" : string.Empty)}", false, ImGuiSelectableFlags.DontClosePopups, new Vector2(0, 28 * Scale)))
                    plugin.AddPlayer(identity);
                ImGui.EndDisabled();
            }
        }
        finally { ImGui.EndPopup(); }
    }

    private void DrawSettingsPopup()
    {
        if (settingsRequested)
        {
            ImGui.OpenPopup("##settings");
            settingsRequested = false;
        }
        ImGui.SetNextWindowSize(new Vector2(355 * Scale, 0), ImGuiCond.Always);
        if (!ImGui.BeginPopup("##settings")) return;
        try
        {
            ImGui.TextColored(Coral, "ОФОРМЛЕНИЕ");
            var show = plugin.Configuration.ShowBackground;
            if (ImGui.Checkbox("Фоновая картинка", ref show))
            {
                plugin.Configuration.ShowBackground = show;
                plugin.SaveConfiguration();
            }
            Wrapped("Отключение картинки сохраняет тёмную тему окна.", Muted);
            Gap(5);
            ImGui.TextColored(Muted, "Ringoto");
            if (ImGui.Button("Готово", new Vector2(-1, 31 * Scale))) ImGui.CloseCurrentPopup();
        }
        finally { ImGui.EndPopup(); }
    }

    private static string Elide(string value, float width)
    {
        if (ImGui.CalcTextSize(value).X <= width) return value;
        while (value.Length > 1 && ImGui.CalcTextSize(value + "…").X > width) value = value[..^1];
        return value + "…";
    }

    private static void Wrapped(string value, Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.PushTextWrapPos(0);
        ImGui.TextUnformatted(value);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }

    private static bool AccentButton(string label, Vector2 size)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Coral);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1, 0.68f, 0.57f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.87f, 0.44f, 0.36f, 1));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.10f, 0.07f, 0.08f, 1));
        var result = ImGui.Button(label, size);
        ImGui.PopStyleColor(4);
        return result;
    }

    private void Gap(float value) => ImGui.Dummy(new Vector2(0, value * Scale));
    private void Push(ImGuiCol index, Vector4 value) { ImGui.PushStyleColor(index, value); colors++; }
    private void Push(ImGuiStyleVar index, Vector2 value) { ImGui.PushStyleVar(index, value); variables++; }
    private void Push(ImGuiStyleVar index, float value) { ImGui.PushStyleVar(index, value); variables++; }
}
