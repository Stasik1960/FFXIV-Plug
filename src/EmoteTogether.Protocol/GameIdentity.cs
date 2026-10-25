using System.Text;

namespace EmoteTogether.Protocol;

public static class GameIdentity
{
    // Never interpolate user text into a game command without this validation.
    public static bool TryNormalize(string? value, out string identity)
    {
        identity = string.Empty;
        if (value is null || value.Length is < 5 or > 96 || value.Any(char.IsControl)) return false;
        string[] parts;
        try { parts = value.Trim().Normalize(NormalizationForm.FormC).Split('@'); }
        catch (ArgumentException) { return false; }
        if (parts.Length != 2 || parts[1].Length is < 2 or > 32 ||
            !parts[1].All(c => char.IsLetterOrDigit(c) || c == '-')) return false;
        var names = parts[0].Split(' ');
        if (names.Length != 2 || names.Any(n => n.Length is < 2 or > 24 ||
            !n.All(c => char.IsLetter(c) || c is '\'' or '-'))) return false;
        identity = $"{names[0]} {names[1]}@{parts[1]}";
        return true;
    }
}
