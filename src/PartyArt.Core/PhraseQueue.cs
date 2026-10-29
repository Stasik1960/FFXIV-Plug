namespace PartyArt.Core;

/// <summary>One paced stream for all presets. Callers cancel it when the destination changes.</summary>
public sealed class PhraseQueue
{
    public const int MinimumIntervalMilliseconds = 1_500;
    public const int MaximumIntervalMilliseconds = 10_000;
    public const int DefaultIntervalMilliseconds = 1_500;
    public const int PresetCooldownMilliseconds = 15_000;
    public const int PendingLifetimeMilliseconds = 30_000;
    public const int MaximumPendingJobs = 32;

    private readonly Queue<Job> pending = new();
    private readonly Dictionary<Guid, long> lastEnqueued = new();
    private Job? active;
    private long previousTime = -1;
    private long? lastAttempt;
    private int intervalMilliseconds = DefaultIntervalMilliseconds;
    private bool updating;
    private string progressName = string.Empty;
    private int submittedMessages;
    private int totalMessages;

    public bool IsSending => active is not null || pending.Count != 0;
    public string ActiveName => active?.Name ?? (pending.TryPeek(out var job) ? job.Name : progressName);
    public int SubmittedMessages => active?.Submitted ?? (pending.Count != 0 ? 0 : submittedMessages);
    public int TotalMessages => active?.Commands.Length ?? (pending.TryPeek(out var job) ? job.Commands.Length : totalMessages);
    public int PendingJobs => pending.Count;
    public string? LastError { get; private set; }
    public int IntervalMilliseconds
    {
        get => intervalMilliseconds;
        set => intervalMilliseconds = Math.Clamp(value, MinimumIntervalMilliseconds, MaximumIntervalMilliseconds);
    }

    /// <summary>Copies all messages before accepting a job. Enabled is an automatic-trigger setting.</summary>
    public bool Enqueue(PhrasePreset preset, long nowMs, out string reason)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (updating) return Reject("Дождитесь завершения отправки сообщения.", out reason);
        if (!ObserveTime(nowMs)) return Reject(LastError!, out reason);
        if (preset.Id == Guid.Empty) return Reject("У заготовки отсутствует идентификатор.", out reason);
        if (active?.Id == preset.Id || pending.Any(job => job.Id == preset.Id))
            return Reject("Эта заготовка уже отправляется или ожидает своей очереди.", out reason);
        if (lastEnqueued.TryGetValue(preset.Id, out var last) && nowMs - last < PresetCooldownMilliseconds)
            return Reject("Повтор этой заготовки доступен через 15 секунд после запуска.", out reason);
        if (pending.Count >= MaximumPendingJobs)
            return Reject("Очередь заполнена. Дождитесь отправки или остановите её.", out reason);
        if (preset.Messages is not { Count: > 0 }) return Reject("Добавьте хотя бы одно сообщение.", out reason);

        var commands = new string[preset.Messages.Count];
        for (var index = 0; index < commands.Length; index++)
        {
            if (!PartyChatPayload.TryBuildCommand(preset.Channel, preset.Messages[index], out commands[index]))
                return Reject($"Сообщение {index + 1}: пустая строка, команда, запрещённый символ или превышен лимит 500 байт.", out reason);
        }
        var name = string.IsNullOrWhiteSpace(preset.Name) ? "Без названия" : preset.Name;
        pending.Enqueue(new Job(preset.Id, name, preset.Channel, commands, nowMs));
        lastEnqueued[preset.Id] = nowMs;
        // Old cooldowns do not need to survive forever after presets have been removed.
        foreach (var id in lastEnqueued.Where(entry => nowMs - entry.Value >= PresetCooldownMilliseconds).Select(entry => entry.Key).ToArray())
            lastEnqueued.Remove(id);
        LastError = null;
        reason = string.Empty;
        return true;
    }

    /// <summary>Submits at most one command. Local acceptance does not confirm server delivery.</summary>
    public void Update(long nowMs, Func<ChatChannel, string, bool> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        if (updating || !ObserveTime(nowMs)) return;
        if (active is null)
        {
            while (pending.TryDequeue(out var next))
            {
                if (nowMs - next.EnqueuedAt > PendingLifetimeMilliseconds)
                {
                    LastError = "Заготовка отменена: ожидание в очереди превысило 30 секунд.";
                    continue;
                }
                active = next;
                LastError = null;
                progressName = next.Name;
                submittedMessages = 0;
                totalMessages = next.Commands.Length;
                break;
            }
        }
        var job = active;
        if (job is null || lastAttempt is { } last && nowMs - last < IntervalMilliseconds) return;
        updating = true;
        lastAttempt = nowMs;
        try
        {
            bool accepted;
            try { accepted = sender(job.Channel, job.Commands[job.Submitted]); }
            catch (Exception) { Fail("Не удалось передать сообщение игровому чату."); return; }
            if (!accepted) { Fail("Игровой чат не принял сообщение. Отправка остановлена."); return; }
            job.Submitted++;
            submittedMessages = job.Submitted;
            if (ReferenceEquals(active, job) && job.Submitted == job.Commands.Length) active = null;
        }
        finally { updating = false; }
    }

    /// <summary>Discards queued work while preserving global pacing and preset cooldowns.</summary>
    public void Cancel(string reason = "Отправка остановлена.")
    {
        active = null;
        pending.Clear();
        LastError = reason;
    }

    private bool ObserveTime(long nowMs)
    {
        if (nowMs < 0 || nowMs < previousTime)
        {
            Fail("Отправка остановлена: монотонные часы изменились.");
            return false;
        }
        previousTime = nowMs;
        return true;
    }

    private void Fail(string message) => Cancel(message);

    private static bool Reject(string message, out string reason) { reason = message; return false; }

    private sealed class Job(Guid id, string name, ChatChannel channel, string[] commands, long enqueuedAt)
    {
        public Guid Id { get; } = id;
        public string Name { get; } = name;
        public ChatChannel Channel { get; } = channel;
        public string[] Commands { get; } = commands;
        public long EnqueuedAt { get; } = enqueuedAt;
        public int Submitted { get; set; }
    }
}
