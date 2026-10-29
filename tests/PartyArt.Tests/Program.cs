using System.Text;
using PartyArt.Core;

namespace PartyArt.Tests;

internal static class Program
{
    private static int assertions;

    private static int Main()
    {
        try
        {
            EmbeddedArtwork();
            PayloadBoundaries();
            PayloadInjection();
            RequestOnlyAndExactOrder();
            PaceAndNoCatchup();
            CancellationAndRestart();
            FailuresAreTerminal();
            InvalidTimeAndSettings();
            ReentrantSender();
            Console.WriteLine($"PASS: Party Art payload, request-only sequence and cancellation (9 scenarios, {assertions} assertions). No game calls or chat messages sent.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void EmbeddedArtwork()
    {
        Check(Artwork.Lines.Count == 45, "all supplied artwork rows embedded");
        Check(Artwork.Lines[0] == "_____________________¶¶¶¶¶¶¶", "first row preserved");
        Check(Artwork.Lines[^1] == "_¶_____¶¶¶________¶_____¶___¶___¶¶¶__¶¶¶ ", "last row including trailing space preserved");
        foreach (var line in Artwork.Lines)
        {
            Check(line.All(c => c is '_' or '¶' or ' '), "only supplied artwork characters");
            Check(PartyChatPayload.TryBuildCommand(line, out var command), "every supplied row valid");
            Check(command == "/p " + line, "party prefix preserves full row");
        }
    }

    private static void PayloadBoundaries()
    {
        Check(PartyChatPayload.TryBuildCommand(new string('x', 497), out var ascii), "497 ASCII bytes plus prefix fit");
        Check(Encoding.UTF8.GetByteCount(ascii) == 500, "command exactly byte limit");
        Check(!PartyChatPayload.TryBuildCommand(new string('x', 498), out _), "overlong ASCII rejected");
        Check(PartyChatPayload.TryBuildCommand(new string('¶', 248) + "_", out var unicode), "multibyte bytes counted with prefix");
        Check(Encoding.UTF8.GetByteCount(unicode) == 500, "multibyte command exactly byte limit");
        Check(!PartyChatPayload.TryBuildCommand(new string('¶', 249), out _), "multibyte over limit rejected");
        Check(PartyChatPayload.TryBuildCommand("__¶_ ", out var preserved) && preserved == "/p __¶_ ", "no trim changes artwork");
        Check(!PartyChatPayload.TryBuildCommand("\ud800", out _), "unpaired high surrogate rejected");
        Check(!PartyChatPayload.TryBuildCommand("\udfff", out _), "unpaired low surrogate rejected");
    }

    private static void PayloadInjection()
    {
        foreach (var invalid in new string?[] { null, "", "   ", "/say x", " /logout", "_\n/say x", "_\r_", "_\0_", "_\t_", "_\u0085_", "_\u2028_", "_\u2029_", "_\u202e_" })
            Check(!PartyChatPayload.TryBuildCommand(invalid, out var command) && command == "", "invalid content cannot build a command");
        Check(PartyChatPayload.TryBuildCommand("x /say text", out var plain) && plain == "/p x /say text", "inline text stays within party command");
        Throws<ArgumentException>(() => new ArtSequence(["_", "/say hi"]), "invalid row prevents any sequence construction");
        Throws<ArgumentException>(() => new ArtSequence([]), "empty sequence rejected");
    }

    private static void RequestOnlyAndExactOrder()
    {
        var sequence = new ArtSequence(["___", "_¶_", "¶¶¶ "]);
        var sent = new List<string>();
        sequence.Update(100_000, command => { sent.Add(command); return true; });
        Check(sent.Count == 0 && sequence.State == SequenceState.Idle, "loading alone never submits");
        Check(sequence.TryStart(100_000), "explicit request starts");
        Check(!sequence.TryStart(100_001), "repeat click cannot queue overlapping run");
        sequence.Update(100_000, command => { sent.Add(command); return true; });
        Check(sequence.SubmittedLines == 1 && sequence.IsRunning, "first row immediate");
        sequence.Update(101_500, command => { sent.Add(command); return true; });
        sequence.Update(103_000, command => { sent.Add(command); return true; });
        Check(sent.SequenceEqual(new[] { "/p ___", "/p _¶_", "/p ¶¶¶ " }), "rows submitted verbatim in order");
        Check(sequence.State == SequenceState.Completed && sequence.SubmittedLines == 3, "finite sequence ends");
        sequence.Update(500_000, command => { sent.Add(command); return true; });
        Check(sent.Count == 3, "no automatic repeat");
    }

    private static void PaceAndNoCatchup()
    {
        var sequence = new ArtSequence(["1", "2", "3", "4"]);
        var count = 0;
        bool Send(string _) { count++; return true; }
        Check(sequence.TryStart(0, 2_000), "custom delay accepted");
        sequence.Update(0, Send);
        sequence.Update(1_999, Send);
        Check(count == 1, "delay respected before second row");
        sequence.Update(20_000, Send);
        Check(count == 2, "long frame sends just one overdue row");
        sequence.Update(20_000, Send);
        sequence.Update(21_999, Send);
        Check(count == 2, "missed deadlines do not become a burst");
        sequence.Update(22_000, Send);
        Check(count == 3, "next row paced from actual last submission");
    }

    private static void CancellationAndRestart()
    {
        var sequence = new ArtSequence(["1", "2", "3"]);
        var sent = new List<string>();
        bool Send(string command) { sent.Add(command); return true; }
        Check(sequence.TryStart(0), "start first request");
        sequence.Update(0, Send);
        sequence.Cancel();
        sequence.Update(20_000, Send);
        Check(sequence.State == SequenceState.Cancelled && sent.Count == 1, "cancel discards remaining rows immediately");
        sequence.Cancel();
        Check(sequence.TryStart(30_000, 1_000), "new explicit request after cancellation");
        sequence.Update(30_000, Send);
        Check(sent.SequenceEqual(new[] { "/p 1", "/p 1" }) && sequence.SubmittedLines == 1, "restart begins at first row, never resumes old rows");
        sequence.Update(31_000, Send);
        sequence.Update(32_000, Send);
        sequence.Cancel();
        Check(sequence.State == SequenceState.Completed, "late cancellation cannot undo completed result");
    }

    private static void FailuresAreTerminal()
    {
        foreach (var throwing in new[] { false, true })
        {
            var sequence = new ArtSequence(["1", "2", "3"]);
            var calls = 0;
            bool Submit(string _) { calls++; if (throwing) throw new InvalidOperationException("transport"); return false; }
            sequence.TryStart(0);
            sequence.Update(0, Submit);
            sequence.Update(50_000, Submit);
            Check(sequence.State == SequenceState.Failed && sequence.Error is not null, "sender failure stops stream");
            Check(calls == 1 && sequence.SubmittedLines == 0, "failed row neither counted nor retried");
            Check(sequence.TryStart(60_000), "explicit retry permits a fresh request");
            sequence.Update(60_000, _ => true);
            Check(sequence.SubmittedLines == 1 && sequence.Error is null, "new request clears previous error");
        }
    }

    private static void InvalidTimeAndSettings()
    {
        var sequence = new ArtSequence(["1", "2"]);
        Check(!sequence.TryStart(-1), "negative start rejected");
        Check(!sequence.TryStart(0, 999), "too-short interval rejected");
        Check(!sequence.TryStart(0, 5_001), "too-long interval rejected");
        Check(sequence.TryStart(100, 5_000), "maximum interval accepted");
        sequence.Update(100, _ => true);
        sequence.Update(99, _ => true);
        Check(sequence.State == SequenceState.Failed && sequence.SubmittedLines == 1, "backwards clock cannot run early");
        Check(sequence.TryStart(long.MaxValue), "large monotonic time accepted");
        sequence.Update(long.MaxValue, _ => true);
        Check(sequence.State == SequenceState.Failed && sequence.SubmittedLines == 1, "unrepresentable next deadline fails without overflow burst");
    }

    private static void ReentrantSender()
    {
        var sequence = new ArtSequence(["1", "2"]);
        var calls = 0;
        sequence.TryStart(0);
        sequence.Update(0, _ =>
        {
            calls++;
            sequence.Update(10_000, _ => { calls++; return true; });
            sequence.Cancel();
            Check(!sequence.TryStart(10_000), "sender cannot restart before previous submission returns");
            return true;
        });
        Check(calls == 1 && sequence.State == SequenceState.Cancelled && sequence.SubmittedLines == 1, "reentrancy cannot submit duplicate rows or override cancellation");
    }

    private static void Throws<T>(Action action, string label) where T : Exception
    {
        try { action(); }
        catch (T) { assertions++; return; }
        throw new InvalidOperationException(label);
    }

    private static void Check(bool condition, string label)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(label);
    }
}
