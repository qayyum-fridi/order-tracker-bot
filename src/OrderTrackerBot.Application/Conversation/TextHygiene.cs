namespace OrderTrackerBot.Application.Conversation;

public static class TextHygiene
{
    /// <summary>
    /// Removes characters that are invisible but make two identical-looking words different: zero-width space, left/right marks,
    /// bidi embedding/isolate controls, word joiner, BOM. Copy-pasting from Urdu apps brings them along. The zero-width (non-)joiners
    /// U+200C/U+200D are kept: Urdu/Persian spelling uses them.
    /// </summary>
    public static string StripInvisible(string text)
    {
        if (text.Length == 0) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is '\u200B' or '\u200E' or '\u200F' or '\u2060' or '\uFEFF' or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069')) continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
