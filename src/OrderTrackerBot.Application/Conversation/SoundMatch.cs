using System.Text;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// Matching by sound, across scripts. A voice note is transcribed in Urdu script ("کرتی", "لان سوٹ") while the catalog is usually typed in Latin
/// letters ("Kurti", "Lawn Suit"), and a seller who spells by ear writes "kurtee" or "lon soot". Each word is reduced to its consonant skeleton in
/// one alphabet ("krt", "ln st"), so the same word said, typed or transcribed gives the same key. Plain code: no model call and no word list.
/// A key is deliberately coarse (Kurta and Kurti share one), so it only ever proposes candidates or picks the one product that fits.
/// </summary>
public static class SoundMatch
{
    /// <summary>The sound key of a phrase: one consonant skeleton per word, joined by spaces ("Lawn Suit" and "لان سوٹ" are both "ln st").</summary>
    public static string Key(string? text) => string.Join(' ', Keys(text));

    /// <summary>The sound key of each word, in order; words that leave nothing (only vowels) are dropped.</summary>
    public static IReadOnlyList<string> Keys(string? text)
    {
        var keys = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return keys;
        var word = new StringBuilder();
        foreach (var raw in text + " ")
        {
            if (char.IsLetterOrDigit(raw))
            {
                word.Append(raw);
                continue;
            }
            // Vowel marks written above or below a letter belong to the word they sit in.
            if (char.GetUnicodeCategory(raw) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (word.Length == 0) continue;
            var key = WordKey(word.ToString());
            if (key.Length > 0) keys.Add(key);
            word.Clear();
        }
        return keys;
    }

    /// <summary>True when one text is in Urdu / Arabic script and the other is not: there is no spelling to compare, only sound.</summary>
    public static bool CrossScript(string a, string b) => HasArabic(a) != HasArabic(b);

    private static bool HasArabic(string text) => text.Any(c => c is >= '؀' and <= 'ۿ');

    /// <summary>How alike two phrases sound, 0 to 1: the share of words whose keys match (a key one letter off counts for less).</summary>
    public static double Score(string heard, string label)
    {
        var a = Keys(heard);
        var b = Keys(label);
        if (a.Count == 0 || b.Count == 0) return 0;
        var used = new bool[b.Count];
        double total = 0;
        foreach (var x in a)
        {
            var best = -1;
            double bestScore = 0;
            for (var j = 0; j < b.Count; j++)
            {
                if (used[j]) continue;
                var s = Same(x, b[j]);
                if (s > bestScore) (best, bestScore) = (j, s);
            }
            if (best < 0) continue;
            used[best] = true;
            total += bestScore;
        }
        return total / Math.Max(a.Count, b.Count);
    }

    private static double Same(string x, string y) =>
        x == y ? 1 : x.Length >= 3 && y.Length >= 3 && TextDistance.Levenshtein(x, y) == 1 ? 0.6 : 0;

    /// <summary>Up to <paramref name="take"/> labels that sound like <paramref name="heard"/>, best first, ties in catalog order.</summary>
    public static IReadOnlyList<string> Nearest(string heard, IEnumerable<string> labels, int take = 3, double minScore = 0.5) =>
        labels.Where(l => !string.IsNullOrWhiteSpace(l))
            .Select((label, order) => (label, order, score: Score(heard, label)))
            .Where(x => x.score >= minScore)
            .OrderByDescending(x => x.score).ThenBy(x => x.order)
            .Select(x => x.label).Distinct(StringComparer.OrdinalIgnoreCase).Take(take).ToList();

    /// <summary>The one label whose whole sound key equals that of <paramref name="heard"/>; null when none or several do.</summary>
    public static string? Unique(string heard, IEnumerable<string> labels)
    {
        var key = Key(heard);
        if (key.Length == 0) return null;
        var same = labels.Where(l => Key(l) == key).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
        return same.Count == 1 ? same[0] : null;
    }

    /// <summary>
    /// The items most likely named in a whole message: those sharing a word (by sound) with it, best first, at most <paramref name="take"/>.
    /// When few match, the list is filled from the start of <paramref name="items"/> up to <paramref name="pad"/>, so the reader still sees what kind of shop it is.
    /// </summary>
    public static IReadOnlyList<T> Shortlist<T>(string message, IReadOnlyList<T> items, Func<T, string> label, int take = 60, int pad = 15)
    {
        if (items.Count <= take) return items;
        var said = Keys(message).ToHashSet(StringComparer.Ordinal);
        var ranked = items.Select((item, order) =>
            {
                var keys = Keys(label(item));
                // A one-letter key ("Ghee" is "g") matches many filler words, so it counts for little.
                var hit = keys.Count == 0 ? 0 : keys.Sum(k => said.Contains(k) ? (k.Length >= 2 ? 1.0 : 0.3) : 0) / keys.Count;
                return (item, order, hit);
            })
            .Where(x => x.hit > 0).OrderByDescending(x => x.hit).ThenBy(x => x.order).Take(take).Select(x => x.item).ToList();
        foreach (var item in items)
        {
            if (ranked.Count >= pad) break;
            if (!ranked.Contains(item)) ranked.Add(item);
        }
        return ranked;
    }

    private static string WordKey(string word)
    {
        var w = word.ToLowerInvariant();
        var sb = new StringBuilder();
        void Add(char c)
        {
            // A doubled sound ("dupatta", "sharara" once its vowels are gone) is one sound.
            if (sb.Length == 0 || sb[^1] != c) sb.Append(c);
        }

        for (var i = 0; i < w.Length; i++)
        {
            var c = w[i];
            var next = i + 1 < w.Length ? w[i + 1] : '\0';
            var first = i == 0;

            if (c is >= '0' and <= '9') { sb.Append(c); continue; }
            if (c is >= '۰' and <= '۹') { sb.Append((char)('0' + (c - '۰'))); continue; }   // Urdu digits
            if (c is >= '٠' and <= '٩') { sb.Append((char)('0' + (c - '٠'))); continue; }   // Arabic digits

            if (c is >= 'a' and <= 'z')
            {
                // Two letters that write one sound in Roman Urdu / English.
                if (next == 'h')
                {
                    var joined = c switch { 's' => 's', 'c' => 'c', 'k' => 'k', 'g' => 'g', 'p' => 'f', 't' => 't', 'd' => 'd', 'b' => 'b', 'j' => 'j', 'r' => 'r', _ => '\0' };
                    if (joined != '\0') { Add(joined); i++; continue; }
                }
                switch (c)
                {
                    case 'a' or 'e' or 'i' or 'o' or 'u': break;
                    case 'y': if (first) Add('y'); break;
                    case 'w' or 'v': if (first) Add('w'); break;
                    case 'h': if (first) Add('h'); break;
                    case 'q': Add('k'); break;
                    case 'c': Add(next is 'e' or 'i' or 'y' ? 's' : 'k'); break;
                    case 'x': Add('k'); Add('s'); break;
                    default: Add(c); break;
                }
                continue;
            }

            var mapped = c switch
            {
                'ب' => 'b', 'پ' => 'p', 'ت' or 'ٹ' or 'ط' => 't', 'ث' or 'س' or 'ش' or 'ص' => 's', 'ج' => 'j', 'چ' => 'c',
                'خ' or 'ق' or 'ک' or 'ك' => 'k', 'د' or 'ڈ' => 'd', 'ذ' or 'ز' or 'ژ' or 'ض' or 'ظ' => 'z', 'ر' or 'ڑ' => 'r',
                'غ' or 'گ' => 'g', 'ف' => 'f', 'ل' => 'l', 'م' => 'm', 'ن' => 'n',
                'و' => first ? 'w' : '\0',
                'ی' or 'ي' or 'ے' or 'ئ' => first ? 'y' : '\0',
                'ہ' or 'ه' or 'ھ' or 'ح' => first ? 'h' : '\0',
                _ => '\0'   // alif, ain, hamza, nasal noon and anything else carry no consonant
            };
            if (mapped != '\0') Add(mapped);
        }
        return sb.ToString();
    }
}
