using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using PartyArt.Core;

namespace PartyArt.Plugin;

internal sealed class MainWindow : Window
{
    private static readonly Vector4 Ink = new(0.055f, 0.060f, 0.080f, 1);
    private static readonly Vector4 Text = new(0.96f, 0.94f, 0.91f, 1);
    private static readonly Vector4 Muted = new(0.57f, 0.60f, 0.67f, 1);
    private static readonly Vector4 Coral = new(1, 0.56f, 0.46f, 1);
    private static readonly string Preview = string.Join('\n', Artwork.Lines);
    private readonly Plugin plugin;
    private bool expandRequested;
    private int colors;
    private int variables;
    private float Scale => ImGuiHelpers.GlobalScale;

    internal MainWindow(Plugin plugin) : base("Party Art###PartyArt")
    {
        this.plugin = plugin;
        Size = new Vector2(620, 760);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(490, 600),
            MaximumSize = new Vector2(1000, float.MaxValue),
        };
        Flags = ImGuiWindowFlags.None;
    }

    internal void OpenExpanded()
    {
        IsOpen = true;
        expandRequested = true;
    }

    public override void OnClose() => plugin.CancelSending();

    public override void PreDraw()
    {
        if (expandRequested)
        {
            ImGui.SetNextWindowCollapsed(false, ImGuiCond.Always);
            expandRequested = false;
        }
        colors = 0;
        variables = 0;
        Push(ImGuiCol.WindowBg, Ink);
        Push(ImGuiCol.ChildBg, new Vector4(0.075f, 0.082f, 0.105f, 1));
        Push(ImGuiCol.Text, Text);
        Push(ImGuiCol.TextDisabled, Muted);
        Push(ImGuiCol.Border, new Vector4(0.30f, 0.32f, 0.38f, 0.35f));
        Push(ImGuiCol.FrameBg, new Vector4(0.11f, 0.12f, 0.15f, 1));
        Push(ImGuiCol.FrameBgHovered, new Vector4(0.17f, 0.18f, 0.22f, 1));
        Push(ImGuiCol.FrameBgActive, new Vector4(0.22f, 0.21f, 0.25f, 1));
        Push(ImGuiCol.Button, new Vector4(0.13f, 0.14f, 0.17f, 1));
        Push(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.21f, 0.23f, 1));
        Push(ImGuiCol.ButtonActive, new Vector4(0.36f, 0.25f, 0.26f, 1));
        Push(ImGuiCol.SliderGrab, Coral);
        Push(ImGuiCol.SliderGrabActive, new Vector4(1, 0.72f, 0.60f, 1));
        Push(ImGuiCol.PlotHistogram, Coral);
        Push(ImGuiCol.Separator, new Vector4(0.30f, 0.32f, 0.38f, 0.35f));
        Push(ImGuiCol.ScrollbarBg, Vector4.Zero);
        Push(ImGuiCol.ScrollbarGrab, new Vector4(0.28f, 0.30f, 0.36f, 0.7f));
        Push(ImGuiCol.ScrollbarGrabHovered, Muted);
        Push(ImGuiCol.ScrollbarGrabActive, Coral);
        Push(ImGuiCol.TitleBg, Ink);
        Push(ImGuiCol.TitleBgActive, Ink);
        Push(ImGuiCol.TitleBgCollapsed, Ink);
        Push(ImGuiStyleVar.WindowPadding, new Vector2(22, 18) * Scale);
        Push(ImGuiStyleVar.ItemSpacing, new Vector2(9, 8) * Scale);
        Push(ImGuiStyleVar.FramePadding, new Vector2(11, 7) * Scale);
        Push(ImGuiStyleVar.WindowRounding, 13 * Scale);
        Push(ImGuiStyleVar.ChildRounding, 9 * Scale);
        Push(ImGuiStyleVar.FrameRounding, 7 * Scale);
        Push(ImGuiStyleVar.ScrollbarRounding, 8 * Scale);
        Push(ImGuiStyleVar.ScrollbarSize, 8 * Scale);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(variables);
        ImGui.PopStyleColor(colors);
    }

    public override void Draw()
    {
        ImGui.TextColored(Coral, "КАРТИНКА ДЛЯ СВОИХ");
        Gap(3);
        ImGui.SetWindowFontScale(1.4f);
        ImGui.TextUnformatted("PARTY ART");
        ImGui.SetWindowFontScale(1);
        ImGui.TextColored(Muted, "Один клик. Рисунок в чате группы /p.");
        Gap(9);

        var sending = plugin.IsSending;
        if (sending)
        {
            if (AccentButton("Остановить отправку", new Vector2(-1, 39 * Scale)))
                plugin.CancelSending();
        }
        else
        {
            ImGui.BeginDisabled(!plugin.CanStart);
            if (AccentButton("Отправить в группу", new Vector2(-1, 39 * Scale)))
                plugin.StartSending();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(plugin.CanStart ? "Отправить рисунок по строкам в чат группы /p." : plugin.Status);
            ImGui.EndDisabled();
        }

        Gap(2);
        var delay = (float)plugin.Configuration.LineDelaySeconds;
        ImGui.TextColored(Muted, "ПАУЗА МЕЖДУ СТРОКАМИ");
        ImGui.BeginDisabled(sending);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("##lineDelay", ref delay, 1, 5, "%.1f сек."))
        {
            plugin.Configuration.LineDelaySeconds = Math.Clamp(delay, 1, 5);
            plugin.SaveConfiguration();
        }
        ImGui.EndDisabled();

        var duration = Math.Max(0, plugin.TotalLines - 1) * plugin.Configuration.LineDelaySeconds;
        ImGui.TextColored(Muted, $"{plugin.TotalLines} строк  ·  около {duration:0} сек.");
        if (sending)
        {
            var progress = plugin.TotalLines == 0 ? 0 : (float)plugin.SentLines / plugin.TotalLines;
            ImGui.ProgressBar(progress, new Vector2(-1, 20 * Scale), $"Передано игре: {plugin.SentLines} / {plugin.TotalLines}");
        }
        Wrapped(plugin.Status, sending ? Coral : Text);
        Gap(4);
        ImGui.Separator();
        Gap(3);
        ImGui.TextColored(Muted, "ПРЕДПРОСМОТР");
        DrawPreview(Math.Max(140 * Scale, ImGui.GetContentRegionAvail().Y - 55 * Scale));
        Gap(2);
        ImGui.TextColored(Muted, "/partyart send  ·  /partyart cancel");
        ImGui.TextColored(Muted, "Закрытие окна останавливает отправку.  ·  Ringoto");
    }

    private static void DrawPreview(float height)
    {
        using var preview = ImRaii.Child("##artPreview", new Vector2(0, height), true, ImGuiWindowFlags.HorizontalScrollbar);
        if (!preview.Success) return;
        using var font = Plugin.PluginInterface.UiBuilder.MonoFontHandle.Push();
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        try { ImGui.TextUnformatted(Preview); }
        finally { ImGui.PopStyleVar(); }
    }

    private static void Wrapped(string value, Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.PushTextWrapPos(0);
        try { ImGui.TextUnformatted(value); }
        finally { ImGui.PopTextWrapPos(); ImGui.PopStyleColor(); }
    }

    private static bool AccentButton(string label, Vector2 size)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Coral);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1, 0.68f, 0.57f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.87f, 0.44f, 0.36f, 1));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.10f, 0.07f, 0.08f, 1));
        try { return ImGui.Button(label, size); }
        finally { ImGui.PopStyleColor(4); }
    }

    private void Gap(float value) => ImGui.Dummy(new Vector2(0, value * Scale));
    private void Push(ImGuiCol index, Vector4 value) { ImGui.PushStyleColor(index, value); colors++; }
    private void Push(ImGuiStyleVar index, Vector2 value) { ImGui.PushStyleVar(index, value); variables++; }
    private void Push(ImGuiStyleVar index, float value) { ImGui.PushStyleVar(index, value); variables++; }
}
