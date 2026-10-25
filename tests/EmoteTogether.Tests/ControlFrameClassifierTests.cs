using EmoteTogether.Protocol;

namespace EmoteTogether.Tests;

internal static class ControlFrameClassifierTests
{
    public static void Run()
    {
        var frame = ChatCodec.Encode(new Packet { Type = PresenceKinds.Pulse, GroupId = Guid.NewGuid().ToString("N") }).Single();
        Check(ControlFrameClassifier.Classify(frame) == ControlFrameKind.Current, "current protocol frame");
        foreach (var prefix in new[] { "[ET2]", "[ET3]" })
            Check(ControlFrameClassifier.Classify(prefix + frame[5..]) == ControlFrameKind.Legacy, "legacy control frame is hidden but not current");
        string?[] ordinary = [null, "", "Привет!", "[ET4] обычное сообщение", "[ET3] не протокол",
            "[ET1]" + frame[5..], "[ET5]" + frame[5..], " " + frame,
            "[ET4]zzzzzzzzzzzz.1.1.AA", "[ET4]123456789abc.0.1.AA", "[ET4]123456789abc.1.9.AA",
            "[ET4]123456789abc.1.2.AA", frame + "\n", frame + new string('A', ChatCodec.MaxFrameLength)];
        foreach (var text in ordinary)
            Check(ControlFrameClassifier.Classify(text) == ControlFrameKind.None, "ordinary or malformed chat remains visible");
        Console.WriteLine($"PASS: control frame classification ({ordinary.Length + 3} assertions)");
    }

    private static void Check(bool condition, string name)
    { if (!condition) throw new InvalidOperationException($"Control frame test failed: {name}"); }
}
