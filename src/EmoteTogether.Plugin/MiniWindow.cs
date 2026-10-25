using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace EmoteTogether.Plugin;

internal sealed class MiniWindow : Window
{
    private readonly Plugin plugin;
    private readonly TigerBackdrop backdrop = new();
    private float Scale => ImGuiHelpers.GlobalScale;

    public MiniWindow(Plugin plugin) : base("Emote Together mini###EmoteTogetherMini")
    {
        this.plugin = plugin;
        Size = new Vector2(232, 68);
        SizeCondition = ImGuiCond.Always;
        Position = new Vector2(60, 180);
        PositionCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        RespectCloseHotkey = false;
    }

    public override void PreDraw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(9, 7) * Scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 11 * Scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 7 * Scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6, 4) * Scale);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.035f, 0.043f, 0.055f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.08f, 0.09f, 0.11f, 0.25f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.24f, 0.19f, 0.12f, 0.8f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.34f, 0.24f, 0.12f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.96f, 0.94f, 0.90f, 1));
    }

    public override void PostDraw() { ImGui.PopStyleColor(5); ImGui.PopStyleVar(4); }

    public override void Draw()
    {
        backdrop.Draw(Scale, plugin.Configuration.ShowTigerBackground);
        var draw = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##move", new Vector2(14, 27) * Scale);
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Перетащить мини-панель");
        for (var row = 0; row < 3; row++)
            draw.AddCircleFilled(origin + new Vector2(5, 7 + row * 6) * Scale, 1.3f * Scale, Color(0.70f, 0.67f, 0.60f, 1));

        ImGui.SameLine();
        if (ImGui.Button("Emote Together", new Vector2(145, 27) * Scale)) plugin.OpenMainMenu();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Открыть полное меню");
        ImGui.SameLine();
        var buttonPosition = ImGui.GetCursorScreenPos();
        var canStart = plugin.CanStartSelectedEmote;
        ImGui.BeginDisabled(!canStart);
        var clicked = ImGui.InvisibleButton("##start", new Vector2(34, 27) * Scale);
        ImGui.EndDisabled();
        var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        draw.AddRectFilled(buttonPosition, buttonPosition + new Vector2(34, 27) * Scale,
            canStart ? Color(0.98f, hovered ? 0.76f : 0.65f, 0.28f, 0.94f) : Color(0.28f, 0.29f, 0.31f, 0.8f), 6 * Scale);
        draw.AddTriangleFilled(buttonPosition + new Vector2(13, 7) * Scale, buttonPosition + new Vector2(13, 20) * Scale,
            buttonPosition + new Vector2(23, 13.5f) * Scale, Color(0.08f, 0.07f, 0.05f, 1));
        if (hovered) ImGui.SetTooltip(canStart ? "Запустить выбранную эмоцию" : "Выберите эмоцию в полном меню. Запуск доступен лидеру готовой группы.");
        if (clicked && canStart) plugin.StartSelectedEmote();

        var status = plugin.CountdownSeconds is { } seconds ? $"До старта: {seconds:F1} с" : plugin.HasPendingRun ? "Проверяем готовность…"
            : plugin.Client.GroupId == null ? "Создать группу" : plugin.SelectedEmoteId == 0 ? $"В группе: {plugin.Client.Members.Length}"
            : plugin.Emotes.NameFor(plugin.SelectedEmoteId);
        ImGui.TextUnformatted(Elide(status, ImGui.GetContentRegionAvail().X));
    }

    private static string Elide(string value, float width)
    {
        while (value.Length > 1 && ImGui.CalcTextSize(value).X > width) value = value[..^1];
        return value;
    }
    private static uint Color(float r, float g, float b, float a) => ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, a));
}
