using System.Globalization;
using System.Text;

namespace PartyArt.Core;

public static class PartyChatPayload
{
    public const int MaximumCommandBytes = 500;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool TryBuildCommand(ChatChannel channel, string? line, out string command)
    {
        command = string.Empty;
        var prefix = channel switch
        {
            ChatChannel.Party => "/p ",
            ChatChannel.Alliance => "/a ",
            ChatChannel.Echo => "/echo ",
            _ => null,
        };
        if (prefix is null) return false;
        if (string.IsNullOrWhiteSpace(line) || line.AsSpan().TrimStart().StartsWith("/")) return false;
        foreach (var rune in line.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                return false;
        }
        var candidate = prefix + line;
        try
        {
            if (StrictUtf8.GetByteCount(candidate) > MaximumCommandBytes) return false;
        }
        catch (EncoderFallbackException) { return false; }
        command = candidate;
        return true;
    }
}
