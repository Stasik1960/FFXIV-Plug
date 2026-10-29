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
            ModelAndChannels();
            PayloadBoundaries();
            PayloadInjection();
            EmptyQueueAndSnapshot();
            WholeJobValidation();
            GlobalPaceAndNoCatchup();
            DuplicateAndCooldown();
            CancellationPreservesPacing();
            TerminalSenderFailures();
            PendingExpiryAndCapacity();
            ManyMessages();
            TimeAndIntervalBoundaries();
            Reentrancy();
            var triggerAssertions = TriggerTrackerChecks.Run();
            assertions += triggerAssertions;
            Console.WriteLine($"PASS: Party Art trigger tracking ({triggerAssertions} assertions).");
            Console.WriteLine($"PASS: Party Art presets, payloads, paced queue and trigger tracking (21 scenarios, {assertions} assertions). No game calls or chat messages sent.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static PhrasePreset Preset(params string[] messages) => new() { Name = "Заготовка", Messages = messages.ToList() };

    private static void ModelAndChannels()
    {
        var first = new PhrasePreset();
        var second = new PhrasePreset();
        Check(first.Id != Guid.Empty && first.Id != second.Id, "new presets have distinct identities");
        Check(first.Enabled && first.Triggers == PhraseTrigger.None && first.Messages.Count == 0, "new preset has no automatic trigger");
        var triggers = PhraseTrigger.DutyEntered | PhraseTrigger.DutyCompleted | PhraseTrigger.PlayerRevived;
        Check(triggers.HasFlag(PhraseTrigger.DutyEntered) && !triggers.HasFlag(PhraseTrigger.DutyWiped), "independent trigger combinations");
        foreach (var (channel, prefix) in new[] { (ChatChannel.Party, "/p "), (ChatChannel.Alliance, "/a "), (ChatChannel.Echo, "/echo ") })
            Check(PartyChatPayload.TryBuildCommand(channel, "Привет!", out var command) && command == prefix + "Привет!", "explicit destination prefix");
        Check(!PartyChatPayload.TryBuildCommand((ChatChannel)99, "x", out _), "unknown channel rejected");
    }

    private static void PayloadBoundaries()
    {
        Check(PartyChatPayload.TryBuildCommand(ChatChannel.Party, new string('x', 497), out var ascii), "497 ASCII bytes plus prefix fit");
        Check(Encoding.UTF8.GetByteCount(ascii) == 500, "party command exactly byte limit");
        Check(!PartyChatPayload.TryBuildCommand(ChatChannel.Party, new string('x', 498), out _), "overlong ASCII rejected");
        Check(PartyChatPayload.TryBuildCommand(ChatChannel.Party, new string('¶', 248) + "_", out var unicode), "multibyte bytes include prefix");
        Check(Encoding.UTF8.GetByteCount(unicode) == 500, "multibyte command exactly byte limit");
        Check(!PartyChatPayload.TryBuildCommand(ChatChannel.Party, new string('¶', 249), out _), "multibyte over limit rejected");
        Check(PartyChatPayload.TryBuildCommand(ChatChannel.Echo, new string('x', 494), out var echo) && Encoding.UTF8.GetByteCount(echo) == 500, "echo longer prefix included");
        Check(!PartyChatPayload.TryBuildCommand(ChatChannel.Echo, new string('x', 495), out _), "echo overlong command rejected");
        Check(PartyChatPayload.TryBuildCommand(ChatChannel.Party, "  Привет!  ", out var preserved) && preserved == "/p   Привет!  ", "phrase spacing preserved");
        Check(!PartyChatPayload.TryBuildCommand(ChatChannel.Party, "\ud800", out _), "unpaired high surrogate rejected");
        Check(!PartyChatPayload.TryBuildCommand(ChatChannel.Party, "\udfff", out _), "unpaired low surrogate rejected");
    }

    private static void PayloadInjection()
    {
        foreach (var invalid in new string?[] { null, "", "   ", "/say x", " /logout", "_\n/say x", "_\r_", "_\0_", "_\t_", "_\u0085_", "_\u2028_", "_\u2029_", "_\u202e_" })
            Check(!PartyChatPayload.TryBuildCommand(ChatChannel.Party, invalid, out var command) && command == "", "invalid text cannot build a command");
        Check(PartyChatPayload.TryBuildCommand(ChatChannel.Party, "x /say text", out var plain) && plain == "/p x /say text", "inline slash stays message text");
    }

    private static void EmptyQueueAndSnapshot()
    {
        var queue = new PhraseQueue();
        var submitted = new List<string>();
        queue.Update(100, (_, text) => { submitted.Add(text); return true; });
        Check(!queue.IsSending && submitted.Count == 0, "empty queue sends nothing");
        var preset = Preset("Привет", "Удачи");
        preset.Name = "Вход";
        preset.Channel = ChatChannel.Alliance;
        preset.Enabled = false;
        Check(queue.Enqueue(preset, 100, out _), "manual request may use disabled automatic preset");
        preset.Messages[0] = "changed";
        preset.Messages.Clear();
        preset.Channel = ChatChannel.Echo;
        preset.Name = "renamed";
        queue.Update(100, (channel, text) => { Check(channel == ChatChannel.Alliance, "snapshot channel retained"); submitted.Add(text); return true; });
        Check(queue.ActiveName == "Вход" && queue.TotalMessages == 2 && queue.SubmittedMessages == 1, "snapshot metadata retained");
        queue.Update(1_600, (_, text) => { submitted.Add(text); return true; });
        Check(submitted.SequenceEqual(new[] { "/a Привет", "/a Удачи" }) && !queue.IsSending, "snapshot messages retained in order");
        queue.Update(50_000, (_, text) => { submitted.Add(text); return true; });
        Check(submitted.Count == 2, "completed queue never repeats automatically");
    }

    private static void WholeJobValidation()
    {
        var queue = new PhraseQueue();
        Check(!queue.Enqueue(Preset(), 0, out var empty) && empty.Length > 0, "empty preset rejected");
        Check(!queue.Enqueue(Preset("safe", "/logout"), 0, out var invalid) && invalid.Contains("2"), "bad later message rejects whole preset");
        var noId = Preset("x");
        noId.Id = Guid.Empty;
        Check(!queue.Enqueue(noId, 0, out _), "invalid identity rejected");
        var wrongChannel = Preset("x");
        wrongChannel.Channel = (ChatChannel)999;
        Check(!queue.Enqueue(wrongChannel, 0, out _), "invalid channel rejected");
        queue.Update(0, (_, _) => throw new InvalidOperationException("must not send"));
        Check(!queue.IsSending, "partially validated preset is never queued");
        var fixedPreset = Preset("safe", "/logout");
        Check(!queue.Enqueue(fixedPreset, 0, out _), "invalid preset not accepted");
        fixedPreset.Messages[1] = "fixed";
        Check(queue.Enqueue(fixedPreset, 0, out _), "validation failure does not consume cooldown");
    }

    private static void GlobalPaceAndNoCatchup()
    {
        var queue = new PhraseQueue { IntervalMilliseconds = 2_000 };
        queue.Enqueue(Preset("a", "b"), 0, out _);
        var second = Preset("c");
        second.Channel = ChatChannel.Echo;
        queue.Enqueue(second, 0, out _);
        var sent = new List<string>();
        bool Send(ChatChannel _, string text) { sent.Add(text); return true; }
        queue.Update(0, Send);
        queue.Update(1_999, Send);
        Check(sent.Count == 1, "configured global interval respected");
        queue.Update(20_000, Send);
        queue.Update(20_000, Send);
        Check(sent.SequenceEqual(new[] { "/p a", "/p b" }), "long stall sends one message without catchup");
        queue.Update(21_999, Send);
        Check(sent.Count == 2, "next job cannot bypass previous job spacing");
        queue.Update(22_000, Send);
        Check(sent.SequenceEqual(new[] { "/p a", "/p b", "/echo c" }) && !queue.IsSending, "queued channels share one paced sender");
    }

    private static void DuplicateAndCooldown()
    {
        var queue = new PhraseQueue();
        var preset = Preset("a");
        Check(queue.Enqueue(preset, 0, out _), "initial request accepted");
        Check(!queue.Enqueue(preset, 0, out _), "pending duplicate rejected");
        queue.Update(0, (_, _) => true);
        Check(!queue.Enqueue(preset, 14_999, out _), "repeat within 15seconds rejected");
        Check(queue.Enqueue(preset, 15_000, out _), "repeat at cooldown boundary accepted");
        queue.Update(15_000, (_, _) => true);
        var longPreset = Preset("a", "b");
        Check(queue.Enqueue(longPreset, 15_000, out _), "different preset accepted");
        queue.Update(16_500, (_, _) => true);
        Check(!queue.Enqueue(longPreset, 31_500, out _), "active duplicate rejected after cooldown expires");
        queue.Cancel();
        Check(queue.Enqueue(longPreset, 31_500, out _), "active duplicate may restart after cancellation and cooldown");
    }

    private static void CancellationPreservesPacing()
    {
        var queue = new PhraseQueue();
        var first = Preset("one", "unused");
        queue.Enqueue(first, 0, out _);
        var sent = new List<string>();
        bool Send(ChatChannel _, string text) { sent.Add(text); return true; }
        queue.Update(0, Send);
        queue.Cancel("context changed");
        Check(!queue.IsSending && queue.LastError == "context changed", "cancel clears work and records reason");
        Check(!queue.Enqueue(first, 0, out _), "cancel preserves preset cooldown");
        Check(queue.Enqueue(Preset("two"), 0, out _), "different explicit request after cancel accepted");
        queue.Update(0, Send);
        queue.Update(1_499, Send);
        Check(sent.SequenceEqual(new[] { "/p one" }), "cancel/restart cannot bypass global pacing");
        queue.Update(1_500, Send);
        Check(sent.SequenceEqual(new[] { "/p one", "/p two" }), "new request sends when global interval ends");
        queue.Update(20_000, Send);
        Check(sent.Count == 2, "cancelled leftovers never resume");
    }

    private static void TerminalSenderFailures()
    {
        foreach (var throwing in new[] { false, true })
        {
            var queue = new PhraseQueue();
            var failed = Preset("one", "two");
            queue.Enqueue(failed, 0, out _);
            queue.Enqueue(Preset("three"), 0, out _);
            var calls = 0;
            bool Fail(ChatChannel _, string __) { calls++; if (throwing) throw new InvalidOperationException(); return false; }
            queue.Update(0, Fail);
            queue.Update(50_000, Fail);
            Check(!queue.IsSending && queue.LastError is not null, "sender failure clears entire queue");
            Check(calls == 1 && queue.SubmittedMessages == 0, "failed message not counted or retried");
            Check(queue.Enqueue(failed, 50_000, out _), "new explicit request can retry after cooldown");
            Check(queue.LastError is null, "new accepted request clears old error");
        }
    }

    private static void PendingExpiryAndCapacity()
    {
        var queue = new PhraseQueue();
        queue.Enqueue(Preset("old"), 0, out _);
        var calls = 0;
        queue.Update(30_001, (_, _) => { calls++; return true; });
        Check(calls == 0 && !queue.IsSending && queue.LastError is not null, "stale pending job expires without submission");
        queue.Enqueue(Preset("boundary"), 30_001, out _);
        queue.Update(60_001, (_, _) => { calls++; return true; });
        Check(calls == 1, "job at exact pending lifetime boundary remains valid");
        var activeQueue = new PhraseQueue();
        activeQueue.Enqueue(Preset("first", "second"), 0, out _);
        activeQueue.Update(0, (_, _) => true);
        activeQueue.Update(100_000, (_, _) => true);
        Check(activeQueue.SubmittedMessages == 2 && !activeQueue.IsSending, "active job has no duration cap");
        var full = new PhraseQueue();
        for (var index = 0; index < PhraseQueue.MaximumPendingJobs; index++)
            Check(full.Enqueue(Preset(index.ToString()), 0, out _), "pending queue accepts available slot");
        Check(!full.Enqueue(Preset("overflow"), 0, out _) && full.PendingJobs == 32, "pending queue bounds protect against event storms");
        full.Cancel();
        Check(!full.IsSending && full.PendingJobs == 0, "cancel discards every pending job");
    }

    private static void ManyMessages()
    {
        var queue = new PhraseQueue();
        var preset = new PhrasePreset { Messages = Enumerable.Range(0, 1_000).Select(index => $"Сообщение {index}").ToList() };
        Check(queue.Enqueue(preset, 0, out _), "preset supports many messages without fixed size");
        var calls = 0;
        for (var index = 0; index < 1_000; index++)
            queue.Update(index * 1_500L, (_, text) => { Check(text == $"/p Сообщение {calls}", "long preset order preserved"); calls++; return true; });
        Check(calls == 1_000 && queue.SubmittedMessages == 1_000 && !queue.IsSending, "long preset completes without truncation or repetition");
    }

    private static void TimeAndIntervalBoundaries()
    {
        var queue = new PhraseQueue { IntervalMilliseconds = -5 };
        Check(queue.IntervalMilliseconds == 1_500, "short interval clamped");
        queue.IntervalMilliseconds = int.MaxValue;
        Check(queue.IntervalMilliseconds == 10_000, "long interval clamped");
        Check(!queue.Enqueue(Preset("x"), -1, out _), "negative time rejected");
        Check(queue.Enqueue(Preset("x", "y"), 100, out _), "valid request after invalid time accepted");
        queue.Update(100, (_, _) => true);
        queue.Update(99, (_, _) => true);
        Check(!queue.IsSending && queue.LastError is not null && queue.SubmittedMessages == 1, "backwards clock clears queue");
        Check(!queue.Enqueue(Preset("x"), 99, out _), "clock must recover before another request");
        var limit = new PhraseQueue();
        Check(limit.Enqueue(Preset("one", "two"), long.MaxValue, out _), "largest monotonic time accepted");
        var calls = 0;
        limit.Update(long.MaxValue, (_, _) => { calls++; return true; });
        limit.Update(long.MaxValue, (_, _) => { calls++; return true; });
        Check(calls == 1, "time arithmetic cannot overflow into duplicate submission");
    }

    private static void Reentrancy()
    {
        var queue = new PhraseQueue();
        queue.Enqueue(Preset("one", "two"), 0, out _);
        var calls = 0;
        queue.Update(0, (_, _) =>
        {
            calls++;
            queue.Update(50_000, (_, _) => { calls++; return true; });
            Check(!queue.Enqueue(Preset("nested"), 0, out _), "sender cannot enqueue reentrantly");
            queue.Cancel();
            return true;
        });
        Check(calls == 1 && !queue.IsSending && queue.SubmittedMessages == 1, "reentrant cancel cannot resurrect stream");
        Check(queue.Enqueue(Preset("fresh"), 1_500, out _), "skipped reentrant update does not advance clock");
    }

    private static void Check(bool condition, string label)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(label);
    }
}
