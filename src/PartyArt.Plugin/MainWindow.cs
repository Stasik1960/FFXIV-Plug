using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using PartyArt.Core;

namespace PartyArt.Plugin;

internal sealed class MainWindow : Window
{
    private static readonly Vector4 Ink = new(0.035f, 0.052f, 0.079f, 1);
    private static readonly Vector4 Text = new(0.91f, 0.95f, 0.98f, 1);
    private static readonly Vector4 Muted = new(0.50f, 0.61f, 0.70f, 1);
    private static readonly Vector4 Ice = new(0.45f, 0.83f, 0.94f, 1);
    private static readonly Vector4 Warning = new(1, 0.66f, 0.47f, 1);
    private static readonly (PhraseTrigger Trigger, string Name)[] DutyTriggers =
    [
        (PhraseTrigger.DutyEntered, "Вход в подземелье / задание"),
        (PhraseTrigger.DutyStarted, "Начало задания"),
        (PhraseTrigger.DutyCompleted, "Победа / завершение задания"),
        (PhraseTrigger.DutyWiped, "Вайп группы"),
        (PhraseTrigger.DutyRecommenced, "Повторная попытка после вайпа"),
    ];
    private static readonly (PhraseTrigger Trigger, string Name)[] PvpTriggers =
    [
        (PhraseTrigger.PvpEntered, "Вход на PvP-карту"),
        (PhraseTrigger.PvpStarted, "Начало PvP-матча"),
        (PhraseTrigger.PvpCompleted, "Завершение PvP-матча"),
    ];
    private static readonly (PhraseTrigger Trigger, string Name)[] CharacterTriggers =
    [
        (PhraseTrigger.CombatStarted, "Вход в бой"), (PhraseTrigger.CombatEnded, "Выход из боя"),
        (PhraseTrigger.PlayerDied, "Мой персонаж погиб"), (PhraseTrigger.PlayerRevived, "Мой персонаж воскрес"),
    ];
    private static readonly (PhraseTrigger Trigger, string Name)[] PartyTriggers =
    [
        (PhraseTrigger.PartyJoined, "Я вступил в группу"),
        (PhraseTrigger.PartyMemberJoined, "В группу пришёл игрок"),
        (PhraseTrigger.PartyMemberLeft, "Игрок покинул группу"),
    ];
    private readonly Plugin plugin;
    private readonly Backdrop backdrop = new();
    private Guid selectedId;
    private Guid editingId;
    private Guid deleteRequested;
    private string messageEditor = string.Empty;
    private byte[] messageBytes = GC.AllocateArray<byte>(4096, pinned: true);
    private string search = string.Empty;
    private bool expandRequested;
    private int colors;
    private int variables;
    private float Scale => ImGuiHelpers.GlobalScale;

    internal MainWindow(Plugin plugin) : base("Party Art###PartyArt")
    {
        this.plugin = plugin;
        Size = new Vector2(980, 800);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(850, 680), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
        Flags = ImGuiWindowFlags.None;
    }

    internal void OpenExpanded() { IsOpen = true; expandRequested = true; }

    public override void PreDraw()
    {
        if (expandRequested) { ImGui.SetNextWindowCollapsed(false, ImGuiCond.Always); expandRequested = false; }
        colors = 0; variables = 0;
        Push(ImGuiCol.WindowBg, Ink);
        Push(ImGuiCol.ChildBg, new Vector4(0.045f, 0.068f, 0.10f, 0.94f));
        Push(ImGuiCol.PopupBg, new Vector4(0.045f, 0.068f, 0.10f, 1));
        Push(ImGuiCol.Text, Text); Push(ImGuiCol.TextDisabled, Muted);
        Push(ImGuiCol.Border, new Vector4(0.20f, 0.34f, 0.43f, 0.38f));
        Push(ImGuiCol.FrameBg, new Vector4(0.07f, 0.11f, 0.16f, 0.95f));
        Push(ImGuiCol.FrameBgHovered, new Vector4(0.10f, 0.18f, 0.24f, 1));
        Push(ImGuiCol.FrameBgActive, new Vector4(0.12f, 0.22f, 0.29f, 1));
        Push(ImGuiCol.Button, new Vector4(0.08f, 0.14f, 0.20f, 1));
        Push(ImGuiCol.ButtonHovered, new Vector4(0.13f, 0.25f, 0.33f, 1));
        Push(ImGuiCol.ButtonActive, new Vector4(0.17f, 0.32f, 0.41f, 1));
        Push(ImGuiCol.Header, new Vector4(0.13f, 0.31f, 0.40f, 0.82f));
        Push(ImGuiCol.HeaderHovered, new Vector4(0.17f, 0.35f, 0.45f, 0.9f));
        Push(ImGuiCol.HeaderActive, new Vector4(0.21f, 0.42f, 0.52f, 1));
        Push(ImGuiCol.CheckMark, Ice); Push(ImGuiCol.SliderGrab, Ice);
        Push(ImGuiCol.SliderGrabActive, new Vector4(0.66f, 0.92f, 1, 1));
        Push(ImGuiCol.PlotHistogram, Ice);
        Push(ImGuiCol.Separator, new Vector4(0.20f, 0.34f, 0.43f, 0.38f));
        Push(ImGuiCol.ScrollbarBg, Vector4.Zero);
        Push(ImGuiCol.ScrollbarGrab, new Vector4(0.23f, 0.38f, 0.48f, 0.7f));
        Push(ImGuiCol.ScrollbarGrabHovered, Muted); Push(ImGuiCol.ScrollbarGrabActive, Ice);
        Push(ImGuiCol.TitleBg, Ink); Push(ImGuiCol.TitleBgActive, Ink); Push(ImGuiCol.TitleBgCollapsed, Ink);
        Push(ImGuiStyleVar.WindowPadding, new Vector2(20, 16) * Scale);
        Push(ImGuiStyleVar.ItemSpacing, new Vector2(9, 8) * Scale);
        Push(ImGuiStyleVar.FramePadding, new Vector2(10, 6) * Scale);
        Push(ImGuiStyleVar.WindowRounding, 13 * Scale); Push(ImGuiStyleVar.ChildRounding, 9 * Scale);
        Push(ImGuiStyleVar.FrameRounding, 6 * Scale); Push(ImGuiStyleVar.ScrollbarRounding, 8 * Scale);
        Push(ImGuiStyleVar.ScrollbarSize, 8 * Scale);
    }

    public override void PostDraw() { ImGui.PopStyleVar(variables); ImGui.PopStyleColor(colors); }

    public override void Draw()
    {
        backdrop.Draw(Scale, plugin.Configuration.ShowBackground);
        DrawHeader(); Gap(7);
        var bodyHeight = Math.Max(230 * Scale, ImGui.GetContentRegionAvail().Y - 100 * Scale);
        DrawLibrary(bodyHeight); ImGui.SameLine();
        using (var editor = ImRaii.Child("##presetEditor", new Vector2(0, bodyHeight), true))
            if (editor.Success) DrawEditor();
        Gap(5); DrawStatus();
    }

    private void DrawHeader()
    {
        ImGui.TextColored(Ice, "ВАШИ СЛОВА. В НУЖНЫЙ МОМЕНТ.");
        ImGui.SetWindowFontScale(1.45f); ImGui.TextUnformatted("PARTY ART"); ImGui.SetWindowFontScale(1);
        ImGui.TextColored(Muted, "Заготовки сообщений и игровые события"); Gap(5);
        var automation = plugin.Configuration.AutomationEnabled;
        if (ImGui.Checkbox("Автоматическая отправка", ref automation))
        { plugin.Configuration.AutomationEnabled = automation; plugin.SaveConfiguration(); }
        ImGui.SameLine();
        if (ImGui.Button("Настройки")) ImGui.OpenPopup("##settings");
        DrawSettings();
    }

    private void DrawSettings()
    {
        ImGui.SetNextWindowSize(new Vector2(420 * Scale, 0), ImGuiCond.Always);
        if (!ImGui.BeginPopup("##settings")) return;
        try
        {
            ImGui.TextColored(Ice, "ОТПРАВКА И ОФОРМЛЕНИЕ");
            var delay = (float)plugin.Configuration.LineDelaySeconds;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderFloat("##delay", ref delay, 1.5f, 10, "Пауза: %.1f сек."))
            { plugin.Configuration.LineDelaySeconds = Math.Clamp(delay, 1.5f, 10); plugin.SaveConfiguration(); }
            Wrapped("Одна общая очередь для всех заготовок. Сообщения идут по порядку, без отправки пачкой.", Muted);
            var background = plugin.Configuration.ShowBackground;
            if (ImGui.Checkbox("Волк на фоне", ref background))
            { plugin.Configuration.ShowBackground = background; plugin.SaveConfiguration(); }
            Gap(4); Wrapped("Закрытие окна сохраняет автоматическую отправку.", Muted);
            ImGui.TextColored(Muted, "Ringoto");
            if (ImGui.Button("Готово", new Vector2(-1, 0))) ImGui.CloseCurrentPopup();
        }
        finally { ImGui.EndPopup(); }
    }

    private void DrawLibrary(float height)
    {
        using var library = ImRaii.Child("##presetLibrary", new Vector2(230 * Scale, height), true);
        if (!library.Success) return;
        ImGui.TextColored(Muted, $"ЗАГОТОВКИ  ·  {plugin.Configuration.Presets.Count}");
        if (AccentButton("+ Новая заготовка", new Vector2(-1, 31 * Scale)))
        {
            var preset = new PhrasePreset();
            plugin.Configuration.Presets.Add(preset); selectedId = preset.Id; plugin.SaveConfiguration();
        }
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##search", "Найти заготовку…", ref search, 256);
        ImGui.Separator();
        using var list = ImRaii.Child("##presets", Vector2.Zero, false);
        if (!list.Success) return;
        if (plugin.Configuration.Presets.Count == 0)
            Wrapped("Создайте первую заготовку: добавьте сообщения и выберите события.", Muted);
        foreach (var preset in plugin.Configuration.Presets)
        {
            if (search.Length > 0 && !preset.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)) continue;
            ImGui.PushID(preset.Id.ToString());
            try
            {
                var label = string.IsNullOrWhiteSpace(preset.Name) ? "Без названия" : preset.Name;
                if (ImGui.Selectable(Elide(label, ImGui.GetContentRegionAvail().X - 5 * Scale), preset.Id == selectedId,
                        ImGuiSelectableFlags.None, new Vector2(0, 28 * Scale)))
                { selectedId = preset.Id; deleteRequested = Guid.Empty; }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(label);
                var mode = preset.Enabled && preset.Triggers != PhraseTrigger.None ? "по событиям" : "вручную";
                ImGui.TextColored(Muted, $"{preset.Messages.Count} сообщ.  ·  {mode}"); Gap(4);
            }
            finally { ImGui.PopID(); }
        }
    }

    private void DrawEditor()
    {
        var preset = plugin.Configuration.Presets.FirstOrDefault(p => p.Id == selectedId);
        if (preset is null) { preset = plugin.Configuration.Presets.FirstOrDefault(); selectedId = preset?.Id ?? Guid.Empty; }
        if (preset is null)
        {
            Gap(30); ImGui.TextColored(Ice, "СЛОВА ДЛЯ ВАШЕЙ ГРУППЫ");
            Wrapped("Приветствие перед данжем, поддержка после вайпа, благодарность за победу. Создайте заготовку слева.", Text);
            return;
        }
        if (editingId != preset.Id)
        {
            editingId = preset.Id;
            messageEditor = string.Join('\n', preset.Messages);
            var required = checked(Encoding.UTF8.GetByteCount(messageEditor) + 1);
            if (messageBytes.Length < required) messageBytes = GC.AllocateArray<byte>(required, pinned: true);
            var written = Encoding.UTF8.GetBytes(messageEditor, messageBytes);
            messageBytes[written] = 0;
        }
        var name = preset.Name;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##name", "Название заготовки", ref name, 1024))
        { preset.Name = name; plugin.SaveConfiguration(); }
        var enabled = preset.Enabled;
        if (ImGui.Checkbox("Запускать по выбранным событиям", ref enabled))
        { preset.Enabled = enabled; plugin.SaveConfiguration(); }
        ImGui.SetNextItemWidth(180 * Scale);
        var channel = (int)preset.Channel;
        if (ImGui.Combo("Канал", ref channel, "Группа · /p\0Альянс · /a\0Только мне · /echo\0"))
        { preset.Channel = (ChatChannel)channel; plugin.SaveConfiguration(); }
        Gap(3); ImGui.TextColored(Muted, "СООБЩЕНИЯ  ·  ОДНА СТРОКА = ОДНО СООБЩЕНИЕ");
        var editorHeight = Math.Clamp(ImGui.GetContentRegionAvail().Y * 0.32f, 110 * Scale, 245 * Scale);
        if (ImGui.InputTextMultiline("##messages", messageBytes.AsSpan(), new Vector2(-1, editorHeight),
                ImGuiInputTextFlags.CallbackResize, ResizeMessages))
        {
            var length = messageBytes.AsSpan().IndexOf((byte)0);
            messageEditor = Encoding.UTF8.GetString(length < 0 ? messageBytes : messageBytes.AsSpan(0, length));
            preset.Messages = messageEditor.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
                .Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
            plugin.SaveConfiguration();
        }
        var invalid = preset.Messages.Select((line, i) => (line, i)).FirstOrDefault(item => !PartyChatPayload.TryBuildCommand(preset.Channel, item.line, out _));
        var hasInvalid = invalid.line is not null;
        if (hasInvalid) Wrapped($"Проверьте сообщение {invalid.i + 1}: максимум 500 байт вместе с командой канала; без команд и управляющих символов.", Warning);
        else ImGui.TextColored(Muted, $"{preset.Messages.Count} сообщ.  ·  интервал {plugin.Configuration.LineDelaySeconds:0.0} сек.");
        ImGui.BeginDisabled(preset.Messages.Count == 0 || hasInvalid);
        if (AccentButton("Отправить сейчас", new Vector2(180 * Scale, 32 * Scale))) plugin.SendPreset(preset.Id);
        ImGui.EndDisabled(); ImGui.SameLine();
        if (ImGui.Button("Копия", new Vector2(75 * Scale, 32 * Scale))) Duplicate(preset);
        ImGui.SameLine();
        if (ImGui.Button("Удалить", new Vector2(78 * Scale, 32 * Scale))) deleteRequested = preset.Id;
        if (deleteRequested == preset.Id)
        {
            ImGui.TextColored(Warning, "Удалить эту заготовку?"); ImGui.SameLine();
            if (ImGui.SmallButton("Да, удалить"))
            {
                plugin.Configuration.Presets.Remove(preset); selectedId = editingId = deleteRequested = Guid.Empty;
                plugin.SaveConfiguration(); return;
            }
            ImGui.SameLine(); if (ImGui.SmallButton("Отмена")) deleteRequested = Guid.Empty;
        }
        Gap(3); ImGui.Separator(); ImGui.TextColored(Muted, "КОГДА ОТПРАВЛЯТЬ");
        if (preset.Triggers == PhraseTrigger.None) Wrapped("События не выбраны — доступна ручная отправка.", Muted);
        DrawTriggerGroup(preset, "Подземелья и задания · PvE", DutyTriggers);
        DrawTriggerGroup(preset, "PvP", PvpTriggers);
        DrawTriggerGroup(preset, "Бой и персонаж · PvE и PvP", CharacterTriggers);
        DrawTriggerGroup(preset, "Группа", PartyTriggers);
    }

    private void DrawTriggerGroup(PhrasePreset preset, string name, (PhraseTrigger Trigger, string Name)[] triggers)
    {
        if (!ImGui.CollapsingHeader(name, ImGuiTreeNodeFlags.DefaultOpen)) return;
        ImGui.Indent(5 * Scale);
        try
        {
            foreach (var (trigger, label) in triggers)
            {
                var selected = (preset.Triggers & trigger) != 0;
                if (ImGui.Checkbox(label, ref selected))
                { preset.Triggers = selected ? preset.Triggers | trigger : preset.Triggers & ~trigger; plugin.SaveConfiguration(); }
                if (ImGui.IsItemHovered())
                {
                    if (trigger == PhraseTrigger.DutyCompleted)
                        ImGui.SetTooltip("Только PvE: подтверждённое завершение всего задания, обычно после последнего босса. В PvP не срабатывает.");
                    else if (DutyTriggers.Any(item => item.Trigger == trigger))
                        ImGui.SetTooltip("Только подземелья и задания PvE. Для PvP выберите отдельное событие ниже.");
                    else if (trigger == PhraseTrigger.PvpCompleted)
                        ImGui.SetTooltip("Завершение PvP-матча при любом результате: победе или поражении.");
                    else if (PvpTriggers.Any(item => item.Trigger == trigger))
                        ImGui.SetTooltip("Только PvP-матчи. Приветствия для PvE назначаются отдельно.");
                    else if (trigger == PhraseTrigger.CombatEnded)
                        ImGui.SetTooltip("Любой выход из боя в PvE и PvP, включая поражение. Завершение задания и матча выбирается отдельно.");
                    else if (CharacterTriggers.Any(item => item.Trigger == trigger))
                        ImGui.SetTooltip("Это общее событие персонажа: действует в PvE и PvP.");
                }
            }
        }
        finally { ImGui.Unindent(5 * Scale); }
    }

    private void DrawStatus()
    {
        var sending = plugin.IsSending;
        if (ImGui.Button("Остановить очередь", new Vector2(170 * Scale, 29 * Scale))) plugin.StopSending();
        ImGui.SameLine();
        ImGui.TextColored(sending ? Ice : Muted, sending ? Elide(plugin.ActiveName, ImGui.GetContentRegionAvail().X - 10 * Scale) : "Ringoto  ·  /partyart");
        if (sending)
        {
            var total = plugin.TotalLines; var sent = plugin.SentLines;
            ImGui.ProgressBar(total > 0 ? (float)sent / total : 0, new Vector2(-1, 17 * Scale), $"Передано игре: {sent} / {total}  ·  в очереди: {plugin.PendingJobs}");
        }
        Wrapped(plugin.Status, sending ? Text : Muted);
    }

    private void Duplicate(PhrasePreset preset)
    {
        var copy = new PhrasePreset { Name = preset.Name + " — копия", Enabled = false, Channel = preset.Channel, Triggers = preset.Triggers, Messages = [.. preset.Messages] };
        plugin.Configuration.Presets.Add(copy); selectedId = copy.Id; plugin.SaveConfiguration();
    }

    private unsafe int ResizeMessages(scoped ref ImGuiInputTextCallbackData data)
    {
        if (data.EventFlag != ImGuiInputTextFlags.CallbackResize) return 0;
        if (data.BufSize > messageBytes.Length)
        {
            var capacity = (int)Math.Max(data.BufSize, Math.Min(Array.MaxLength, (long)messageBytes.Length * 2));
            var replacement = GC.AllocateArray<byte>(capacity, pinned: true);
            messageBytes.CopyTo(replacement, 0);
            messageBytes = replacement;
        }
        // The callback may replace ImGui's buffer; pinned storage stays stable after this scope ends.
        fixed (byte* pointer = messageBytes) data.Buf = pointer;
        data.BufSize = messageBytes.Length;
        return 0;
    }

    private static string Elide(string value, float width)
    {
        if (ImGui.CalcTextSize(value).X <= width) return value;
        while (value.Length > 1 && ImGui.CalcTextSize(value + "…").X > width) value = value[..^1];
        return value + "…";
    }

    private static void Wrapped(string value, Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color); ImGui.PushTextWrapPos(0);
        try { ImGui.TextUnformatted(value); } finally { ImGui.PopTextWrapPos(); ImGui.PopStyleColor(); }
    }

    private static bool AccentButton(string label, Vector2 size)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Ice);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.65f, 0.92f, 1, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.31f, 0.66f, 0.79f, 1));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.025f, 0.08f, 0.11f, 1));
        try { return ImGui.Button(label, size); } finally { ImGui.PopStyleColor(4); }
    }

    private void Gap(float value) => ImGui.Dummy(new Vector2(0, value * Scale));
    private void Push(ImGuiCol index, Vector4 value) { ImGui.PushStyleColor(index, value); colors++; }
    private void Push(ImGuiStyleVar index, Vector2 value) { ImGui.PushStyleVar(index, value); variables++; }
    private void Push(ImGuiStyleVar index, float value) { ImGui.PushStyleVar(index, value); variables++; }
}
