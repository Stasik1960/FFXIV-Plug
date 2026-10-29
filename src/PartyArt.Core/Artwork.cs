namespace PartyArt.Core;

public static class Artwork
{
    public static IReadOnlyList<string> Lines { get; } = ReadLines();

    private static IReadOnlyList<string> ReadLines()
    {
        using var stream = typeof(Artwork).Assembly.GetManifestResourceStream("PartyArt.Core.Artwork.txt")
            ?? throw new InvalidOperationException("Artwork resource is missing.");
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        if (lines.Count == 0) throw new InvalidOperationException("Artwork resource is empty.");
        return lines.AsReadOnly();
    }
}
