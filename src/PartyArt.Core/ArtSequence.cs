namespace PartyArt.Core;

public enum SequenceState { Idle, Running, Completed, Cancelled, Failed }

/// <summary>A request starts one finite sequence. Callers cancel when the game context changes.</summary>
public sealed class ArtSequence
{
    public const int MinimumIntervalMilliseconds = 1_000;
    public const int MaximumIntervalMilliseconds = 5_000;
    public const int DefaultIntervalMilliseconds = 1_500;
    private readonly string[] commands;
    private long nextDue;
    private long previousTime;
    private bool updating;

    public SequenceState State { get; private set; }
    public bool IsRunning => State == SequenceState.Running;
    public int SubmittedLines { get; private set; }
    public int TotalLines => commands.Length;
    public int IntervalMilliseconds { get; private set; } = DefaultIntervalMilliseconds;
    public string? Error { get; private set; }

    public ArtSequence() : this(Artwork.Lines) { }

    public ArtSequence(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        commands = lines.Select(line => PartyChatPayload.TryBuildCommand(line, out var command)
            ? command : throw new ArgumentException("Every artwork line must be a valid party-chat payload.", nameof(lines))).ToArray();
        if (commands.Length == 0) throw new ArgumentException("Artwork must contain at least one line.", nameof(lines));
    }

    public bool TryStart(long nowMs, int intervalMilliseconds = DefaultIntervalMilliseconds)
    {
        if (IsRunning || updating || nowMs < 0 || intervalMilliseconds is < MinimumIntervalMilliseconds or > MaximumIntervalMilliseconds)
            return false;
        IntervalMilliseconds = intervalMilliseconds;
        SubmittedLines = 0;
        Error = null;
        previousTime = nextDue = nowMs;
        State = SequenceState.Running;
        return true;
    }

    /// <summary>Submits at most one command; true means accepted by the local sender, not delivered by the server.</summary>
    public void Update(long nowMs, Func<string, bool> submit)
    {
        ArgumentNullException.ThrowIfNull(submit);
        if (!IsRunning || updating) return;
        if (nowMs < previousTime) { Fail("Clock moved backwards."); return; }
        previousTime = nowMs;
        if (nowMs < nextDue) return;
        updating = true;
        try
        {
            bool accepted;
            try { accepted = submit(commands[SubmittedLines]); }
            catch (Exception) { Fail("The local sender raised an error."); return; }
            if (!accepted) { Fail("The local sender rejected the line."); return; }
            SubmittedLines++;
            if (!IsRunning) return;
            if (SubmittedLines == commands.Length) State = SequenceState.Completed;
            else if (nowMs > long.MaxValue - IntervalMilliseconds) Fail("Clock limit reached before the next line.");
            else nextDue = nowMs + IntervalMilliseconds;
        }
        finally { updating = false; }
    }

    public void Cancel()
    {
        if (IsRunning) State = SequenceState.Cancelled;
    }

    private void Fail(string error)
    {
        Error = error;
        State = SequenceState.Failed;
    }
}
