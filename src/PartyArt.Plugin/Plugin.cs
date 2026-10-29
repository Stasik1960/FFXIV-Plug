using System.Diagnostics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
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
    [PluginService] internal static ICondition Conditions { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly object sync = new();
    private readonly ArtSequence sequence = new();
    private readonly WindowSystem windows = new("PartyArt");
    private readonly MainWindow window;
    private readonly long started = Stopwatch.GetTimestamp();
    private bool disposed;
    private bool canStart;
    private Request pending;
    private ChatContext activeContext;
    private string status = "Вступите в группу и нажмите «Отправить в /p».";

    internal Configuration Configuration { get; }
    internal string Status { get { lock (sync) return status; } }
    internal bool IsSending { get { lock (sync) return sequence.IsRunning || pending == Request.Start; } }
    internal bool CanStart { get { lock (sync) return !disposed && canStart && !sequence.IsRunning && pending == Request.None; } }
    internal int SentLines { get { lock (sync) return sequence.SubmittedLines; } }
    internal int TotalLines => sequence.TotalLines;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new();
        Configuration.LineDelaySeconds = NormalizeDelay(Configuration.LineDelaySeconds);
        window = new MainWindow(this);
        windows.AddWindow(window);
        Commands.AddHandler("/partyart", new CommandInfo(OnCommand)
        {
            HelpMessage = "Party Art. /partyart send — рисунок в /p; /partyart cancel — остановить.",
        });
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenWindow;
        PluginInterface.UiBuilder.OpenConfigUi += OpenWindow;
        Framework.Update += OnUpdate;
    }

    internal void SaveConfiguration()
    {
        lock (sync)
        {
            if (disposed) return;
            Configuration.LineDelaySeconds = NormalizeDelay(Configuration.LineDelaySeconds);
            PluginInterface.SavePluginConfig(Configuration);
        }
    }

    internal void StartSending()
    {
        lock (sync)
        {
            if (disposed || sequence.IsRunning || pending == Request.Start) return;
            pending = Request.Start;
        }
    }

    internal void CancelSending()
    {
        lock (sync)
        {
            if (!disposed) pending = Request.Cancel;
        }
    }

    private void OnUpdate(IFramework framework)
    {
        lock (sync)
        {
            if (disposed) return;
            try
            {
                var request = pending;
                pending = Request.None;
                var ready = TryGetContext(out var context, out var reason);
                canStart = ready;
                if (request == Request.Cancel)
                {
                    if (sequence.IsRunning) { sequence.Cancel(); status = "Отправка остановлена."; }
                    return;
                }
                if (sequence.IsRunning && (!ready || context != activeContext))
                {
                    sequence.Cancel();
                    status = ready ? "Отправка остановлена: группа или локация изменились." : $"Отправка остановлена. {reason}";
                    return;
                }
                var now = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (request == Request.Start && !sequence.IsRunning)
                {
                    if (!ready) { status = reason; return; }
                    activeContext = context;
                    var interval = (int)Math.Round(NormalizeDelay(Configuration.LineDelaySeconds) * 1000);
                    if (!sequence.TryStart(now, interval)) { status = "Не удалось начать отправку."; return; }
                }
                if (!sequence.IsRunning)
                {
                    if (sequence.State == SequenceState.Idle) status = ready ? "Готово к отправке в чат группы /p." : reason;
                    return;
                }
                sequence.Update(now, SubmitCommand);
                status = sequence.State switch
                {
                    SequenceState.Completed => $"Все {sequence.TotalLines} строк переданы игре.",
                    SequenceState.Failed => "Отправка остановлена: игровой чат не принял команду. Подробности в /xllog.",
                    _ => $"Передано игре {sequence.SubmittedLines} из {sequence.TotalLines} строк…",
                };
            }
            catch (Exception error)
            {
                sequence.Cancel();
                canStart = false;
                pending = Request.None;
                status = "Отправка остановлена из-за ошибки. Подробности в /xllog.";
                Log.Error(error, "PartyArt update failed");
            }
        }
    }

    private static unsafe bool TryGetContext(out ChatContext context, out string reason)
    {
        context = default;
        if (!ClientState.IsLoggedIn || !PlayerState.IsLoaded || PlayerState.ContentId == 0)
        { reason = "Войдите в игру."; return false; }
        if (ClientState.IsGPosing || Conditions.Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51,
                ConditionFlag.LoggingOut, ConditionFlag.WatchingCutscene, ConditionFlag.WatchingCutscene78,
                ConditionFlag.OccupiedInCutSceneEvent))
        { reason = "Дождитесь завершения перехода или ролика."; return false; }
        if (Party.Length < 2 || Party.PartyId == 0)
        { reason = "Вступите в группу минимум из двух игроков."; return false; }
        if (UIModule.Instance() == null)
        { reason = "Игровой чат ещё не готов."; return false; }
        context = new ChatContext(PlayerState.ContentId, Party.PartyId, ClientState.TerritoryType);
        reason = string.Empty;
        return true;
    }

    private unsafe bool SubmitCommand(string text)
    {
        if (disposed || !Framework.IsInFrameworkUpdateThread || !TryGetContext(out var context, out _) || context != activeContext)
            return false;
        if (!text.StartsWith("/p ", StringComparison.Ordinal) ||
            !PartyChatPayload.TryBuildCommand(text[3..], out var checkedCommand) || checkedCommand != text)
            return false;
        var module = UIModule.Instance();
        if (module == null) return false;
        try
        {
            using var command = new Utf8String(text);
            module->ProcessChatBoxEntry(&command, 0, false);
            return true;
        }
        catch (Exception error)
        {
            Log.Error(error, "PartyArt could not submit a party chat line");
            return false;
        }
    }

    private static double NormalizeDelay(double value) => double.IsFinite(value) ? Math.Clamp(value, 1, 5) : 1.5;

    private void OpenWindow() => window.OpenExpanded();
    private void OnCommand(string command, string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "send": OpenWindow(); StartSending(); break;
            case "cancel": case "stop": CancelSending(); break;
            default: OpenWindow(); break;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            pending = Request.None;
            sequence.Cancel();
        }
        Framework.Update -= OnUpdate;
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenWindow;
        Commands.RemoveHandler("/partyart");
        windows.RemoveAllWindows();
    }

    private enum Request { None, Start, Cancel }
    private readonly record struct ChatContext(ulong ContentId, long PartyId, uint TerritoryType);
}
