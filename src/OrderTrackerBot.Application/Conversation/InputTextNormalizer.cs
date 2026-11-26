using System.Text;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>Canonicalizes text before command parsing or model interpretation while preserving line structure and seller wording.</summary>
public static class InputTextNormalizer
{
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var text = TextHygiene.StripInvisible(raw).Replace("\\n", "\n").Normalize(NormalizationForm.FormKC);
        text = NormalizeDigits(text);
        text = text.Replace('\u2018', '\'').Replace('\u2019', '\'')
            .Replace('\u201C', '"').Replace('\u201D', '"')
            .Replace('\u2013', '-').Replace('\u2014', '-');

        var builder = new StringBuilder(text.Length);
        var previousWasSpace = false;
        foreach (var ch in text)
        {
            if (ch is ' ' or '\t')
            {
                if (!previousWasSpace) builder.Append(' ');
                previousWasSpace = true;
                continue;
            }

            builder.Append(ch);
            previousWasSpace = false;
        }

        return builder.ToString().Trim();
    }

    private static string NormalizeDigits(string text)
    {
        if (!text.Any(c => c is >= '\u0660' and <= '\u0669' or >= '\u06F0' and <= '\u06F9')) return text;
        return string.Create(text.Length, text, (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = c is >= '\u0660' and <= '\u0669' ? (char)('0' + (c - '\u0660'))
                    : c is >= '\u06F0' and <= '\u06F9' ? (char)('0' + (c - '\u06F0'))
                    : c;
            }
        });
    }
}
