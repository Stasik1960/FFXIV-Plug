using System.Text;

namespace SlapYourFriend.Core;

public static class TargetIdentity
{
    public static bool TryNormalize(string? value, out string identity)
    {
        identity = string.Empty;
        if (value is null || value.Length is < 5 or > 96 || value.Any(char.IsControl)) return false;
        string[] pieces;
        try { pieces = value.Trim().Normalize(NormalizationForm.FormC).Split('@'); }
        catch (ArgumentException) { return false; }
        if (pieces.Length != 2 || pieces[1].Length is < 2 or > 32 ||
            !pieces[1].All(c => char.IsLetterOrDigit(c) || c == '-')) return false;
        var names = pieces[0].Split(' ');
        if (names.Length != 2 || names.Any(n => n.Length is < 2 or > 24 ||
            !n.All(c => char.IsLetter(c) || c is '\'' or '-'))) return false;
        identity = $"{names[0]} {names[1]}@{pieces[1]}";
        return true;
    }
}
