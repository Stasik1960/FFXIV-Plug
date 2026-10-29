using System.Globalization;
using System.Text;

namespace PartyArt.Core;

public static class PartyChatPayload
{
    public const int MaximumCommandBytes = 500;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool TryBuildCommand(string? line, out string command)
    {
        command = string.Empty;
        if (string.IsNullOrWhiteSpace(line) || line.AsSpan().TrimStart().StartsWith("/")) return false;
        foreach (var rune in line.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                return false;
        }
        var candidate = "/p " + line;
        try
        {
            if (StrictUtf8.GetByteCount(candidate) > MaximumCommandBytes) return false;
        }
        catch (EncoderFallbackException) { return false; }
        command = candidate;
        return true;
    }
}
