using System.Diagnostics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using EmoteTogether.Client;
using EmoteTogether.Protocol;

namespace EmoteTogether.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager Commands { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IDataManager Data { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable Objects { get; private set; } = null!;
    [PluginService] internal static ICondition Conditions { get; private set; } = null!;
    [PluginService] internal static ITargetManager Targets { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static ITextureProvider Textures { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider Interop { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    internal Configuration Configuration { get; }
    internal GroupClient Client { get; }
    internal GameEmotes Emotes { get; }
    internal string LocalStatus { get; private set; } = "Создайте эмоциональную группу и пригласите друга.";
    internal bool HasPendingRun => Client.PendingRunId is not null;
    internal uint SelectedEmoteId => window.SelectedEmoteId;
    internal bool PhoneFilterActive => transport.PhoneFilterActive;
    internal bool CanStartSelectedEmote => Client.IsLeader && Client.Members.Length >= 2 && SelectedEmoteId != 0 &&
        !HasPendingRun && !Client.IsTransferringLeadership && !Client.IsRecoveringGroup && Client.ClockReady;
    internal double? CountdownSeconds => scheduledTicks is { } ticks
        ? Math.Max(0, (ticks - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency)
        : null;

    private readonly WindowSystem windows = new("EmoteTogether");
    private readonly MainWindow window;
    private readonly MiniWindow miniWindow;
    private readonly GameChatTransport transport;
    private readonly MonotonicClock clock = new();
    private string? activeCharacter;
    private string? preparedRunId;
    private uint preparedEmoteId;
    private long? scheduledTicks;
    private bool readinessSent;
    private long lastCatalogRefresh;
    private string? lastInvitationId;
    private bool disposed;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new();
        Configuration.Version = 4;
        Configuration.CountdownSeconds = Math.Clamp(Configuration.CountdownSeconds, 1, 60);
        transport = new GameChatTransport(Chat, Interop, () => clock.UnixMilliseconds, Log);
        Client = new GroupClient(transport, () => clock.UnixMilliseconds);
        Emotes = new GameEmotes(Data, ClientState, Objects, Conditions);
        window = new MainWindow(this);
        windows.AddWindow(window);
        miniWindow = new MiniWindow(this);
        windows.AddWindow(miniWindow);
        Commands.AddHandler("/emotegroup", new CommandInfo(OnCommand)
        {
            HelpMessage = "Открыть Emote Together: приглашения и синхронные эмоции группы.",
        });
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenWindow;
        PluginInterface.UiBuilder.OpenConfigUi += OpenSettings;
        Framework.Update += OnUpdate;
        Client.PrepareReceived += OnPrepare;
        Client.ScheduledReceived += OnScheduled;
        Client.Cancelled += OnCancelled;
        Client.Disconnected += OnDisconnected;
    }

    internal string? CharacterIdentity => ClientState.IsLoggedIn && PlayerState.IsLoaded && PlayerState.HomeWorld.IsValid
        ? $"{PlayerState.CharacterName}@{PlayerState.HomeWorld.Value.Name.ExtractText()}"
        : null;

    internal void CancelRun()
    {
        Client.Cancel();
        ClearRun();
        LocalStatus = "Запуск отменён.";
    }

    internal void SaveCountdown(int seconds)
    {
        Configuration.CountdownSeconds = Math.Clamp(seconds, 1, 60);
        PluginInterface.SavePluginConfig(Configuration);
    }

    internal void SaveAppearance(bool showTiger)
    {
        Configuration.ShowTigerBackground = showTiger;
        PluginInterface.SavePluginConfig(Configuration);
    }

    internal void SaveMiniPanel(bool enabled)
    {
        Configuration.ShowMiniPanel = enabled;
        PluginInterface.SavePluginConfig(Configuration);
        UpdateMiniVisibility();
    }

    internal void OpenMainMenu() => window.OpenExpanded();
    internal void StartSelectedEmote()
    {
        if (CanStartSelectedEmote) Client.Start(SelectedEmoteId, Configuration.CountdownSeconds);
    }

    private void UpdateMiniVisibility() => miniWindow.IsOpen = Configuration.ShowMiniPanel && Client.IsConnected && !window.IsOpen;

    private void OnPrepare(Packet packet)
    {
        if (packet.RunId is null || packet.RunId == preparedRunId) return;
        if (!Emotes.CanPlay(packet.EmoteId, out var reason))
        {
            Client.SetReady(packet.RunId, false, reason);
            LocalStatus = reason;
            return;
        }
        preparedRunId = packet.RunId;
        preparedEmoteId = packet.EmoteId;
        scheduledTicks = null;
        readinessSent = false;
        if (Client.ClockReady)
        {
            readinessSent = true;
            LocalStatus = $"Проверяем готовность группы: {Emotes.NameFor(packet.EmoteId)}.";
            Client.SetReady(packet.RunId, true);
        }
        else LocalStatus = "Синхронизируем время с лидером…";
    }

    private void OnScheduled(Packet packet)
    {
        if (packet.RunId != preparedRunId || packet.EmoteId != preparedEmoteId || scheduledTicks is not null) return;
        var remaining = packet.StartAtUnixMs - Client.LeaderNowUnixMs;
        if (!Client.ClockReady || remaining < 500 || remaining > 120_000)
        {
            Client.SetReady(packet.RunId!, false, "Время старта пришло слишком поздно или часы не синхронизированы.");
            ClearRun();
            LocalStatus = "Запуск отменён: не удалось надёжно получить время старта.";
            return;
        }
        // Freeze the local monotonic deadline: later NTP samples cannot move a running countdown.
        scheduledTicks = Stopwatch.GetTimestamp() + (long)(remaining / 1000d * Stopwatch.Frequency);
        LocalStatus = $"Готовимся: {Emotes.NameFor(preparedEmoteId)}.";
    }

    private void OnUpdate(IFramework framework)
    {
        try
        {
            // Identity is reset before processing messages from the previous character's group.
            var identity = CharacterIdentity;
            if (identity == null && ClientState.IsLoggedIn) identity = activeCharacter;
            if (!string.Equals(identity, activeCharacter, StringComparison.OrdinalIgnoreCase))
            {
                ClearRun();
                Client.SetIdentity(identity);
                activeCharacter = identity;
                lastInvitationId = null;
                LocalStatus = identity is null ? "Войдите в игру для создания группы." : "Создайте группу или примите приглашение друга.";
            }
            // Transport sends at most one paced frame; all game calls remain on the framework thread.
            Client.Poll();
            UpdateMiniVisibility();
            if (Client.Invitation is { } invitation && invitation.InviteId != lastInvitationId)
            {
                lastInvitationId = invitation.InviteId;
                window.OpenExpanded();
            }
            if (Stopwatch.GetElapsedTime(lastCatalogRefresh).TotalSeconds >= 2)
            {
                Emotes.RefreshUnlocked();
                lastCatalogRefresh = Stopwatch.GetTimestamp();
            }
            if (preparedRunId is null) return;
            var gameReason = string.Empty;
            if (!Client.IsConnected || !Emotes.CanPlay(preparedEmoteId, out gameReason))
            {
                var reason = Client.IsConnected ? gameReason : "Связь с лидером потеряна.";
                Client.SetReady(preparedRunId, false, reason);
                ClearRun();
                LocalStatus = reason;
                return;
            }
            if (!Client.ClockReady)
            {
                if (!readinessSent && scheduledTicks is null)
                {
                    LocalStatus = "Синхронизируем время с лидером…";
                    return;
                }
                Client.SetReady(preparedRunId, false, "Синхронизация времени с лидером потеряна.");
                ClearRun();
                LocalStatus = "Запуск отменён: синхронизация времени потеряна.";
                return;
            }
            if (!readinessSent)
            {
                readinessSent = true;
                LocalStatus = $"Проверяем готовность группы: {Emotes.NameFor(preparedEmoteId)}.";
                Client.SetReady(preparedRunId, true);
            }
            if (scheduledTicks is not { } deadline || Stopwatch.GetTimestamp() < deadline) return;
            var lateMs = Stopwatch.GetElapsedTime(deadline).TotalMilliseconds;
            var emote = preparedEmoteId;
            if (lateMs > 250)
            {
                Client.Cancel();
                ClearRun();
                LocalStatus = "Кадр опоздал более чем на 250 мс; запуск пропущен.";
                return;
            }
            // Consume the local scheduler before native code; keep the wire run ID until failure is reported.
            preparedRunId = null;
            preparedEmoteId = 0;
            scheduledTicks = null;
            readinessSent = false;
            var played = Emotes.Play(emote, out var reasonText);
            if (!played) Client.Cancel();
            Client.CompleteLocalRun();
            LocalStatus = played ? $"Эмоция запущена: {Emotes.NameFor(emote)}." : reasonText;
        }
        catch (Exception ex)
        {
            ClearRun();
            LocalStatus = "Ошибка плагина; запуск отменён. Подробности в /xllog.";
            Log.Error(ex, "EmoteTogether framework update failed");
            Client.SetIdentity(null);
            activeCharacter = null;
        }
    }

    private void OnCancelled(string reason)
    {
        ClearRun();
        LocalStatus = $"Запуск отменён: {reason}";
    }

    private void OnDisconnected()
    {
        ClearRun();
    }

    private void ClearRun()
    {
        preparedRunId = null;
        preparedEmoteId = 0;
        scheduledTicks = null;
        readinessSent = false;
        Client.CompleteLocalRun();
    }

    private void OnCommand(string command, string arguments) => window.Toggle();
    private void OpenWindow() => OpenMainMenu();
    private void OpenSettings() => window.OpenSettings();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Framework.Update -= OnUpdate;
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;
        Commands.RemoveHandler("/emotegroup");
        Client.PrepareReceived -= OnPrepare;
        Client.ScheduledReceived -= OnScheduled;
        Client.Cancelled -= OnCancelled;
        Client.Disconnected -= OnDisconnected;
        DrainDepartureBeforeUnload();
        Client.Dispose();
        windows.RemoveAllWindows();
    }

    private void DrainDepartureBeforeUnload()
    {
        if (Framework.IsFrameworkUnloading) return;
        var drainGate = new object();
        var drainClosed = false;
        try
        {
            void Prepare()
            {
                lock (drainGate)
                {
                    if (drainClosed || !Client.IsConnected || !ClientState.IsLoggedIn || Conditions[ConditionFlag.LoggingOut]) return;
                    transport.QueueDeparture(Client.PrepareDeparture());
                }
            }
            if (Framework.IsInFrameworkUpdateThread)
            {
                Prepare();
                if (ClientState.IsLoggedIn && !Conditions[ConditionFlag.LoggingOut]) transport.Poll();
                return;
            }
            // Dalamud runs unload on a worker (CanUnloadAsync). Native sends stay on Framework.
            Framework.RunOnFrameworkThread(Prepare).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            var deadline = Stopwatch.StartNew();
            while (transport.PendingFrames > 0 && deadline.Elapsed < TimeSpan.FromSeconds(20) && !Framework.IsFrameworkUnloading)
            {
                var sent = Framework.RunOnFrameworkThread(() =>
                {
                    lock (drainGate)
                    {
                        if (drainClosed || !ClientState.IsLoggedIn || Conditions[ConditionFlag.LoggingOut]) return false;
                        transport.Poll();
                        return true;
                    }
                }).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                if (!sent) break;
                if (transport.PendingFrames > 0) Thread.Sleep(100);
            }
        }
        catch (Exception error) { Log.Debug(error, "Departure could not be fully delivered before unload"); }
        finally
        {
            // A timed-out framework task may still be queued. It must not use the disposed client later.
            lock (drainGate) drainClosed = true;
        }
    }
}
