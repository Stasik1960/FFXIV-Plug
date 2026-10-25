using System.Diagnostics;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using SlapYourFriend.Core;

namespace SlapYourFriend.Plugin;

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
    [PluginService] internal static ITextureProvider Textures { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    internal Configuration Configuration { get; }
    internal string Status { get; private set; } = "Добавьте друзей и подойдите ближе.";
    internal IReadOnlyList<string> Friends => friends;
    internal string? CurrentTargetIdentity => Targets.Target is IPlayerCharacter target && target.GameObjectId != Objects.LocalPlayer?.GameObjectId
        ? IdentityOf(target) : null;

    private readonly WindowSystem windows = new("SlapYourFriend");
    private readonly MainWindow window;
    private readonly GameSlap slap;
    private readonly ProximityTrigger trigger = new();
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly Dictionary<string, string> worlds;
    private volatile PlayerRule[] rules = [];
    private volatile string[] friends = [];
    private string? activeIdentity;
    private long nextScan;
    private long nextFriends;
    private long lastAction = -10_000;
    private bool friendReadError;
    private volatile bool disposed;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new();
        worlds = Data.GetExcelSheet<World>().Select(w => w.Name.ExtractText()).Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(n => n, n => n, StringComparer.OrdinalIgnoreCase);
        NormalizeConfiguration();
        slap = new GameSlap(Data, ClientState, Objects, Conditions);
        window = new MainWindow(this);
        windows.AddWindow(window);
        Commands.AddHandler("/slapfriend", new CommandInfo(OnCommand)
        {
            HelpMessage = "Настройки Slap Your Friend. /slapfriend off — пауза, /slapfriend on — включить.",
        });
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenWindow;
        PluginInterface.UiBuilder.OpenConfigUi += OpenSettings;
        Framework.Update += OnUpdate;
    }

    internal bool AddPlayer(string name)
    {
        var value = name.Trim();
        if (!value.Contains('@'))
        {
            var matches = friends.Where(n => string.Equals(n.Split('@')[0], value, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1) { Status = "Уточните домашний мир: Имя Фамилия@Мир."; return false; }
            if (matches.Length == 1) value = matches[0];
            else if (PlayerState.IsLoaded && PlayerState.HomeWorld.IsValid) value += "@" + PlayerState.HomeWorld.Value.Name.ExtractText();
        }
        if (!TargetIdentity.TryNormalize(value, out var identity)) { Status = "Укажите Имя Фамилия@ДомашнийМир."; return false; }
        var parts = identity.Split('@');
        if (!worlds.TryGetValue(parts[1], out var world)) { Status = "Домашний мир не найден. Проверьте написание после @."; return false; }
        identity = parts[0] + "@" + world;
        if (string.Equals(identity, IdentityOf(Objects.LocalPlayer), StringComparison.OrdinalIgnoreCase)) { Status = "Себя добавлять не нужно."; return false; }
        if (Configuration.Players.Any(p => string.Equals(p.Identity, identity, StringComparison.OrdinalIgnoreCase))) { Status = "Этот игрок уже добавлен."; return false; }
        if (Configuration.Players.Count >= 200) { Status = "В списке уже 200 игроков."; return false; }
        Configuration.Players.Add(new PlayerSettings { Identity = identity });
        SaveConfiguration();
        Status = $"Добавлен {identity}. Интервал — 1 минута.";
        lastAction = Now;
        return true;
    }

    internal void RemovePlayer(string identity)
    {
        Configuration.Players.RemoveAll(p => string.Equals(p.Identity, identity, StringComparison.OrdinalIgnoreCase));
        SaveConfiguration();
        Status = "Игрок убран из списка.";
        lastAction = Now;
    }

    internal void SaveConfiguration()
    {
        Configuration.Radius = float.IsFinite(Configuration.Radius) ? Math.Clamp(Configuration.Radius, 1, 4) : 2;
        foreach (var player in Configuration.Players) player.IntervalMinutes = Math.Clamp(player.IntervalMinutes, 1, 60);
        rules = Configuration.Players.Select(p => new PlayerRule(p.Identity, p.IntervalMinutes)).ToArray();
        PluginInterface.SavePluginConfig(Configuration);
    }

    internal void SetEnabled(bool enabled)
    {
        Configuration.Enabled = enabled;
        SaveConfiguration();
        Status = enabled ? "Автопощёчина включена." : "Автопощёчина на паузе.";
        lastAction = Now;
    }

    private void NormalizeConfiguration()
    {
        Configuration.Version = 1;
        Configuration.Players ??= [];
        var clean = new List<PlayerSettings>();
        foreach (var player in Configuration.Players)
        {
            if (player is null || !TargetIdentity.TryNormalize(player.Identity, out var identity)) continue;
            var existing = clean.FirstOrDefault(p => string.Equals(p.Identity, identity, StringComparison.OrdinalIgnoreCase));
            var interval = Math.Clamp(player.IntervalMinutes, 1, 60);
            if (existing is not null) existing.IntervalMinutes = Math.Max(existing.IntervalMinutes, interval);
            else if (clean.Count < 200) clean.Add(new PlayerSettings { Identity = identity, IntervalMinutes = interval });
        }
        Configuration.Players = clean;
        Configuration.Radius = float.IsFinite(Configuration.Radius) ? Math.Clamp(Configuration.Radius, 1, 4) : 2;
        rules = clean.Select(p => new PlayerRule(p.Identity, p.IntervalMinutes)).ToArray();
    }

    private long Now => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private void OnUpdate(IFramework framework)
    {
        if (disposed) return;
        var now = Now;
        if (now < nextScan) return;
        nextScan = now + 200;
        try
        {
            var snapshot = rules;
            var local = Objects.LocalPlayer;
            if (!ClientState.IsLoggedIn)
            {
                if (activeIdentity is not null) trigger.Reset();
                activeIdentity = null;
                friends = [];
                Status = "Войдите в игру для автопощёчины.";
                return;
            }
            var identity = IdentityOf(local);
            if (identity is null || !PlayerState.IsLoaded)
            {
                trigger.Update([], snapshot, now, false, Configuration.Radius);
                Status = "Ждём загрузку персонажа…";
                return;
            }
            if (!string.Equals(identity, activeIdentity, StringComparison.OrdinalIgnoreCase))
            {
                trigger.Reset();
                activeIdentity = identity;
                friends = [];
                nextFriends = 0;
            }
            if (now >= nextFriends)
            {
                nextFriends = now + 2_000;
                try { friends = GameFriends.Read(Data).Where(n => !string.Equals(n, identity, StringComparison.OrdinalIgnoreCase)).ToArray(); }
                catch (Exception error)
                {
                    friends = [];
                    if (!friendReadError) { friendReadError = true; Log.Warning(error, "Не удалось прочитать список друзей."); }
                }
            }
            var visible = new List<NearbyPlayer>();
            if (snapshot.Length != 0)
            {
                var allowed = snapshot.Select(p => p.Identity).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var obj in Objects)
                {
                    if (obj is not IPlayerCharacter player || player.GameObjectId == local!.GameObjectId || player.IsDead || !player.IsTargetable) continue;
                    var targetIdentity = IdentityOf(player);
                    if (targetIdentity is null || !allowed.Contains(targetIdentity)) continue;
                    visible.Add(new NearbyPlayer(targetIdentity, player.GameObjectId, Vector3.Distance(local.Position, player.Position)));
                }
            }
            var reason = string.Empty;
            var canAct = Configuration.Enabled && snapshot.Length != 0 && slap.CanPlay(out reason);
            var candidate = trigger.Update(visible, snapshot, now, canAct, Configuration.Radius);
            if (candidate is { } selected)
            {
                var target = Objects.OfType<IPlayerCharacter>().FirstOrDefault(p => p.GameObjectId == selected.EntityId &&
                    string.Equals(IdentityOf(p), selected.Identity, StringComparison.OrdinalIgnoreCase));
                var stillAllowed = ReferenceEquals(snapshot, rules) && snapshot.Any(p => string.Equals(p.Identity, selected.Identity, StringComparison.OrdinalIgnoreCase));
                var success = !disposed && Configuration.Enabled && stillAllowed && target is not null &&
                    slap.Play(target, Configuration.Radius, out reason);
                trigger.ReportExecution(selected, success, now);
                Status = success ? $"Пощёчина для {selected.Identity.Split('@')[0]}." : string.IsNullOrEmpty(reason) ? "Цель уже недоступна." : reason;
                lastAction = now;
                return;
            }
            if (!Configuration.Enabled) Status = "Автопощёчина на паузе. Таймеры сохраняются.";
            else if (snapshot.Length == 0) Status = "Добавьте друзей или имена персонажей.";
            else if (!canAct) Status = reason;
            else if (now - lastAction >= 5_000) Status = $"Ждём выбранных игроков в радиусе {Configuration.Radius:F1} м. Повтор — по их таймерам.";
        }
        catch (Exception error)
        {
            Configuration.Enabled = false;
            Status = "Автопощёчина остановлена из-за ошибки. Подробности в /xllog.";
            Log.Error(error, "SlapYourFriend framework update failed");
        }
    }

    private static string? IdentityOf(IPlayerCharacter? player)
    {
        if (player is null || !player.HomeWorld.IsValid) return null;
        return TargetIdentity.TryNormalize($"{player.Name.TextValue}@{player.HomeWorld.Value.Name.ExtractText()}", out var identity) ? identity : null;
    }

    private void OpenWindow() => window.OpenExpanded();
    private void OpenSettings() => window.OpenSettings();
    private void OnCommand(string command, string arguments)
    {
        if (arguments.Trim().Equals("off", StringComparison.OrdinalIgnoreCase)) SetEnabled(false);
        else if (arguments.Trim().Equals("on", StringComparison.OrdinalIgnoreCase)) SetEnabled(true);
        else OpenWindow();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Framework.Update -= OnUpdate;
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;
        Commands.RemoveHandler("/slapfriend");
        windows.RemoveAllWindows();
    }
}
