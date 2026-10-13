namespace OrderTrackerBot.Application.Conversation;

/// <summary>Edit distance for typo tolerance ("Ploo Shirt" is probably "Polo Shirt", "shiped" is "shipped").</summary>
public static class TextDistance
{
    /// <summary>Levenshtein distance, ignoring case.</summary>
    public static int Levenshtein(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    /// <summary>
    /// The candidate that is a near miss of <paramref name="text"/> (at most <paramref name="maxDistance"/> edits and at most a third of its length), or null.
    /// An exact match is not a typo and returns null; with two equally close candidates the first one wins.
    /// </summary>
    public static string? Closest(string text, IEnumerable<string> candidates, int maxDistance = 2)
    {
        var wanted = text.Trim();
        if (wanted.Length < 4) return null;
        var allowed = Math.Min(maxDistance, Math.Max(1, wanted.Length / 3));

        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || Math.Abs(candidate.Length - wanted.Length) > allowed) continue;
            var distance = Levenshtein(wanted, candidate);
            if (distance == 0) return null;
            if (distance <= allowed && distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }
        return best;
    }
}
