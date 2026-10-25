namespace EmoteTogether.Protocol;

public enum ControlFrameKind { None, Current, Legacy }

public static class ControlFrameClassifier
{
    public static ControlFrameKind Classify(string? text)
    {
        if (text == null) return ControlFrameKind.None;
        if (ChatCodec.IsFrame(text)) return ControlFrameKind.Current;
        if ((text.StartsWith("[ET2]", StringComparison.Ordinal) || text.StartsWith("[ET3]", StringComparison.Ordinal)) &&
            ChatCodec.IsFrame(ChatCodec.Prefix + text[5..])) return ControlFrameKind.Legacy;
        return ControlFrameKind.None;
    }
}
