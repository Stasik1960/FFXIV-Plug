using System.Diagnostics;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.DutyState;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using PartyArt.Core;

namespace PartyArt.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager Commands { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IPartyList Party { get; private set; } = null!;
    [PluginService] internal static IObjectTable Objects { get; private set; } = null!;
    [PluginService] internal static ICondition Conditions { get; private set; } = null!;
    [PluginService] internal static IDutyState Duty { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static ITextureProvider Textures { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly object sync = new();
    private readonly PhraseQueue queue = new();
    private readonly TriggerTracker tracker = new();
    private readonly WindowSystem windows = new("PartyArt");
    private readonly MainWindow window;
    private readonly Queue<Guid> manualRequests = new();
    private readonly List<PendingEvent> pendingEvents = [];
    private readonly Dictionary<PhraseTrigger, long> lastEvents = [];
    private readonly long started = Stopwatch.GetTimestamp();
    private PhrasePreset[] presets = [];
    private ChatContext? observedContext;
    private ChatContext? queueContext;
    private bool automationEnabled;
    private bool disposed;
    private bool stopRequested;
    private long lastSubmission = -10_000;
    private string status = "Создайте заготовку и выберите события для её отправки.";

    internal Configuration Configuration { get; }
    internal string Status { get { lock (sync) return status; } }
    internal bool IsSending { get { lock (sync) return queue.IsSending; } }
    internal int SentLines { get { lock (sync) return queue.SubmittedMessages; } }
    internal int TotalLines { get { lock (sync) return queue.TotalMessages; } }
    internal string ActiveName { get { lock (sync) return queue.ActiveName; } }
    internal int PendingJobs { get { lock (sync) return queue.PendingJobs; } }
    private long Now => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new();
        NormalizeConfiguration();
        presets = Configuration.Presets.Select(Clone).ToArray();
        automationEnabled = Configuration.AutomationEnabled;
        queue.IntervalMilliseconds = (int)Math.Round(Configuration.LineDelaySeconds * 1000);
        window = new MainWindow(this);
        windows.AddWindow(window);
        Commands.AddHandler("/partyart", new CommandInfo(OnCommand)
        {
            HelpMessage = "Заготовки: /partyart send Название; /partyart cancel; /partyart on или off — автоматика.",
        });
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenWindow;
        PluginInterface.UiBuilder.OpenConfigUi += OpenWindow;
        Framework.Update += OnUpdate;
        Duty.DutyStarted += OnDutyStarted;
        Duty.DutyCompleted += OnDutyCompleted;
        Duty.DutyWiped += OnDutyWiped;
        Duty.DutyRecommenced += OnDutyRecommenced;
        Chat.LogMessage += OnLogMessage;
    }

    internal void SaveConfiguration()
    {
        lock (sync)
        {
            if (disposed) return;
            NormalizeConfiguration();
            var snapshot = Configuration.Presets.Select(Clone).ToArray();
            if (!SamePresets(presets, snapshot) || automationEnabled != Configuration.AutomationEnabled)
            {
                if (queue.IsSending) stopRequested = true;
                manualRequests.Clear();
                pendingEvents.Clear();
            }
            if (automationEnabled != Configuration.AutomationEnabled)
                status = Configuration.AutomationEnabled ? "Автоматическая отправка включена." : "Автоматическая отправка выключена.";
            presets = snapshot;
            automationEnabled = Configuration.AutomationEnabled;
            queue.IntervalMilliseconds = (int)Math.Round(Configuration.LineDelaySeconds * 1000);
            PluginInterface.SavePluginConfig(Configuration);
        }
    }

    internal void SendPreset(Guid id)
    {
        lock (sync)
        {
            if (disposed || manualRequests.Contains(id)) return;
            manualRequests.Enqueue(id);
        }
    }

    internal void StopSending()
    {
        lock (sync)
        {
            if (disposed) return;
            stopRequested = true;
            manualRequests.Clear();
            pendingEvents.Clear();
        }
    }

    private void OnUpdate(IFramework framework)
    {
        lock (sync)
        {
            if (disposed) return;
            try
            {
                var now = Now;
                var snapshot = ReadSnapshot();
                if (!snapshot.Ready && observedContext is { } stable && stable.CharacterId == snapshot.CharacterId)
                    snapshot = snapshot with
                    {
                        PartyId = snapshot.PartyId == 0 ? stable.PartyId : snapshot.PartyId,
                        Territory = snapshot.Territory == 0 ? stable.Territory : snapshot.Territory,
                    };
                var context = snapshot.CharacterId == 0 ? (ChatContext?)null :
                    new ChatContext(snapshot.CharacterId, snapshot.PartyId, snapshot.Territory);
                var flags = tracker.Observe(snapshot);
                if (context != observedContext)
                {
                    var wasSending = queue.IsSending;
                    queue.Cancel("Группа, персонаж или локация изменились.");
                    queueContext = null;
                    if (observedContext is not null) manualRequests.Clear();
                    // Native duty events can arrive just before the first observation of a new area.
                    pendingEvents.RemoveAll(e => context is null || e.Context != context.Value);
                    lastEvents.Clear();
                    observedContext = context;
                    if (wasSending) status = "Отправка остановлена: игровой контекст изменился.";
                }
                if (stopRequested)
                {
                    stopRequested = false;
                    queue.Cancel();
                    queueContext = null;
                    pendingEvents.Clear();
                    status = "Очередь остановлена. Автоматика: " + (automationEnabled ? "включена." : "выключена.");
                    return;
                }
                if (context is null)
                {
                    queue.Cancel("Персонаж не загружен.");
                    pendingEvents.Clear();
                    status = "Войдите в игру, чтобы отправлять сообщения.";
                    return;
                }
                if (automationEnabled && flags != PhraseTrigger.None)
                    AddEvent(flags, context.Value, now);
                pendingEvents.RemoveAll(e => e.Context != context.Value || now - e.CreatedAt > 300_000);
                if (!snapshot.Ready)
                {
                    if (manualRequests.Count > 0) status = "Дождитесь завершения загрузки или ролика.";
                    if (queue.IsSending)
                    {
                        queue.Cancel("Загрузка или ролик: текущие сообщения отменены.");
                        queueContext = null;
                        status = "Текущая очередь отменена на время загрузки или ролика.";
                    }
                    manualRequests.Clear();
                    return;
                }
                if (automationEnabled)
                {
                    foreach (var eventInfo in pendingEvents.ToArray())
                    {
                        foreach (var preset in presets.Where(p => p.Enabled && (p.Triggers & eventInfo.Trigger) != 0))
                            Enqueue(preset, context.Value, now, false);
                    }
                }
                pendingEvents.Clear();
                while (manualRequests.TryDequeue(out var id))
                {
                    var preset = presets.FirstOrDefault(p => p.Id == id);
                    if (preset is not null) Enqueue(preset, context.Value, now, true);
                }
                if (queue.IsSending)
                {
                    var before = queue.SubmittedMessages;
                    queue.Update(now, SubmitCommand);
                    if (queue.LastError is not null) status = queue.LastError;
                    else if (queue.IsSending) status = $"{queue.ActiveName}: передано игре {queue.SubmittedMessages}/{queue.TotalMessages}. В очереди: {queue.PendingJobs}.";
                    else if (before != queue.SubmittedMessages) status = "Сообщения переданы игре.";
                }
            }
            catch (Exception error)
            {
                queue.Cancel("Отправка остановлена из-за ошибки. Подробности в /xllog.");
                manualRequests.Clear();
                pendingEvents.Clear();
                status = queue.LastError ?? "Отправка остановлена из-за ошибки.";
                Log.Error(error, "PartyArt update failed");
            }
        }
    }

    private unsafe GameSnapshot ReadSnapshot()
    {
        if (!ClientState.IsLoggedIn || PlayerState.ContentId == 0)
            return new GameSnapshot(0, 0, 0, false, false, false, false, []);
        var local = Objects.LocalPlayer;
        var ready = PlayerState.IsLoaded && ClientState.TerritoryType != 0 && local is not null && local.IsValid() && UIModule.Instance() != null &&
            !ClientState.IsGPosing && !Conditions.Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51,
                ConditionFlag.LoggingOut, ConditionFlag.WatchingCutscene, ConditionFlag.WatchingCutscene78,
                ConditionFlag.OccupiedInCutSceneEvent);
        var members = ready && Party.Length > 0 ? Enumerable.Range(0, Party.Length).Select(i => Party[i]?.ContentId ?? 0).ToArray() : [];
        return new GameSnapshot(PlayerState.ContentId, Party.PartyId, ClientState.TerritoryType, ready,
            Duty.ContentFinderCondition.RowId != 0, Conditions[ConditionFlag.InCombat], local?.IsDead ?? false, members,
            Duty.ContentFinderCondition.RowId);
    }

    private void Enqueue(PhrasePreset preset, ChatContext context, long now, bool manual)
    {
        if (!ChannelAvailable(preset.Channel, out var reason))
        {
            if (manual) status = reason;
            return;
        }
        if (queueContext is not null && queueContext != context) queue.Cancel("Игровой контекст изменился.");
        if (queue.Enqueue(preset, now, out reason))
        {
            queueContext = context;
            status = $"«{preset.Name}» добавлена в очередь.";
        }
        else if (manual) status = reason;
    }

    private static bool ChannelAvailable(ChatChannel channel, out string reason)
    {
        reason = string.Empty;
        if (channel == ChatChannel.Echo) return true;
        if (Party.Length < 2 || Party.PartyId == 0) { reason = "Для отправки в группу вступите в неё хотя бы с одним другим игроком."; return false; }
        if (channel == ChatChannel.Alliance && !Party.IsAlliance) { reason = "Вы сейчас не в альянсе."; return false; }
        return channel is ChatChannel.Party or ChatChannel.Alliance;
    }

    private unsafe bool SubmitCommand(ChatChannel channel, string text)
    {
        if (disposed || !Framework.IsInFrameworkUpdateThread || !ChannelAvailable(channel, out _)) return false;
        var current = ReadSnapshot();
        if (!current.Ready || queueContext != new ChatContext(current.CharacterId, current.PartyId, current.Territory)) return false;
        var split = text.IndexOf(' ');
        if (split < 0 || !PartyChatPayload.TryBuildCommand(channel, text[(split + 1)..], out var checkedCommand) || checkedCommand != text)
            return false;
        var module = UIModule.Instance();
        if (module == null) return false;
        try
        {
            using var command = new Utf8String(text);
            lastSubmission = Now;
            module->ProcessChatBoxEntry(&command, 0, false);
            return true;
        }
        catch (Exception error)
        {
            Log.Error(error, "PartyArt could not submit a chat message");
            return false;
        }
    }

    private void OnDutyStarted(IDutyStateEventArgs args) => OnDutyEvent(PhraseTrigger.DutyStarted, args);
    private void OnDutyCompleted(IDutyStateEventArgs args) => OnDutyEvent(PhraseTrigger.DutyCompleted, args);
    private void OnDutyWiped(IDutyStateEventArgs args) => OnDutyEvent(PhraseTrigger.DutyWiped, args);
    private void OnDutyRecommenced(IDutyStateEventArgs args) => OnDutyEvent(PhraseTrigger.DutyRecommenced, args);

    private void OnDutyEvent(PhraseTrigger trigger, IDutyStateEventArgs args)
    {
        lock (sync)
        {
            if (disposed || !automationEnabled || !ClientState.IsLoggedIn || PlayerState.ContentId == 0) return;
            var territory = args.TerritoryType.RowId;
            if (territory == 0 || territory != ClientState.TerritoryType) return;
            var partyId = Party.PartyId;
            if (partyId == 0 && observedContext is { } stable && stable.CharacterId == PlayerState.ContentId)
                partyId = stable.PartyId;
            AddEvent(trigger, new ChatContext(PlayerState.ContentId, partyId, territory), Now);
        }
    }

    private void AddEvent(PhraseTrigger trigger, ChatContext context, long now)
    {
        foreach (var single in Enum.GetValues<PhraseTrigger>().Where(t => t != PhraseTrigger.None && (trigger & t) != 0))
        {
            if (lastEvents.TryGetValue(single, out var previous) && now - previous < 4_000) continue;
            if (pendingEvents.Any(e => e.Trigger == single && e.Context == context)) continue;
            lastEvents[single] = now;
            pendingEvents.Add(new PendingEvent(single, context, now));
        }
    }

    private void OnLogMessage(ILogMessage message)
    {
        if (message.LogMessageId is not (3810 or 3999 or 10196)) return;
        lock (sync)
        {
            if (disposed || (!queue.IsSending && Now - lastSubmission > 5_000)) return;
            queue.Cancel("Игра ограничила частоту сообщений. Очередь и автоматика остановлены; увеличьте паузу перед повторным включением.");
            pendingEvents.Clear();
            manualRequests.Clear();
            automationEnabled = Configuration.AutomationEnabled = false;
            status = queue.LastError ?? "Игра ограничила частоту сообщений.";
            try { PluginInterface.SavePluginConfig(Configuration); }
            catch (Exception error) { Log.Warning(error, "PartyArt could not save the automatic chat pause"); }
        }
    }

    private void NormalizeConfiguration()
    {
        Configuration.Version = 2;
        Configuration.LineDelaySeconds = double.IsFinite(Configuration.LineDelaySeconds) ? Math.Clamp(Configuration.LineDelaySeconds, 1.5, 10) : 1.5;
        Configuration.Presets ??= [];
        Configuration.Presets = Configuration.Presets.Where(p => p is not null).ToList();
        var ids = new HashSet<Guid>();
        foreach (var preset in Configuration.Presets)
        {
            if (preset.Id == Guid.Empty || !ids.Add(preset.Id)) { preset.Id = Guid.NewGuid(); ids.Add(preset.Id); }
            preset.Name = string.IsNullOrWhiteSpace(preset.Name) ? "Новая заготовка" : preset.Name;
            preset.Messages ??= [];
            if (!Enum.IsDefined(preset.Channel)) preset.Channel = ChatChannel.Party;
            preset.Triggers &= (PhraseTrigger)4095;
        }
    }

    private static PhrasePreset Clone(PhrasePreset source) => new()
    {
        Id = source.Id, Name = source.Name, Enabled = source.Enabled, Channel = source.Channel,
        Triggers = source.Triggers, Messages = source.Messages.ToList(),
    };

    private static bool SamePresets(PhrasePreset[] left, PhrasePreset[] right) => left.Length == right.Length &&
        left.Zip(right).All(pair => pair.First.Id == pair.Second.Id && pair.First.Name == pair.Second.Name &&
            pair.First.Enabled == pair.Second.Enabled && pair.First.Channel == pair.Second.Channel &&
            pair.First.Triggers == pair.Second.Triggers && pair.First.Messages.SequenceEqual(pair.Second.Messages));

    private void OpenWindow() => window.OpenExpanded();
    private void OnCommand(string command, string arguments)
    {
        var parts = arguments.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var action = parts.FirstOrDefault()?.ToLowerInvariant();
        switch (action)
        {
            case "send":
                OpenWindow();
                lock (sync)
                {
                    var matches = parts.Length > 1 ? presets.Where(p => p.Name.Equals(parts[1], StringComparison.OrdinalIgnoreCase) || p.Id.ToString().Equals(parts[1], StringComparison.OrdinalIgnoreCase)).ToArray() : presets;
                    if (matches.Length == 1) SendPreset(matches[0].Id);
                    else status = "Укажите уникальное название: /partyart send Название. Или выберите заготовку в окне.";
                }
                break;
            case "cancel": case "stop": StopSending(); break;
            case "on": case "off": Configuration.AutomationEnabled = action == "on"; SaveConfiguration(); break;
            default: OpenWindow(); break;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            queue.Cancel();
            manualRequests.Clear();
            pendingEvents.Clear();
        }
        Framework.Update -= OnUpdate;
        Duty.DutyStarted -= OnDutyStarted;
        Duty.DutyCompleted -= OnDutyCompleted;
        Duty.DutyWiped -= OnDutyWiped;
        Duty.DutyRecommenced -= OnDutyRecommenced;
        Chat.LogMessage -= OnLogMessage;
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenWindow;
        Commands.RemoveHandler("/partyart");
        windows.RemoveAllWindows();
    }

    private readonly record struct ChatContext(ulong CharacterId, long PartyId, uint Territory);
    private readonly record struct PendingEvent(PhraseTrigger Trigger, ChatContext Context, long CreatedAt);
}
