using System.Globalization;
using System.Text.RegularExpressions;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// Reads amounts out of a voice transcript whether the speech model wrote them as digits ("3500") or as words ("teen hazar paanch sau",
/// "three thousand five hundred", "تین ہزار پانچ سو"). Used to check that an AI rewrite of a voice note neither loses nor invents an amount.
/// It is a safety check, not a translator: an unknown number word simply means "no amount found", and the caller then fails safe.
/// </summary>
public static class SpokenNumbers
{
    public const long MinAmount = 100;
    public const long MaxAmount = 999_999;

    private static readonly Dictionary<string, decimal> Words = BuildWords();
    private static readonly Dictionary<string, long> Multipliers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sau"] = 100, ["so"] = 100, ["hundred"] = 100, ["سو"] = 100,
        ["hazar"] = 1_000, ["hazaar"] = 1_000, ["hajar"] = 1_000, ["thousand"] = 1_000, ["ہزار"] = 1_000,
        ["lakh"] = 100_000, ["lac"] = 100_000, ["lakhs"] = 100_000, ["لاکھ"] = 100_000
    };

    private static Dictionary<string, decimal> BuildWords()
    {
        var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        void Add(decimal value, params string[] spellings) { foreach (var s in spellings) map[s] = value; }

        // English
        string[] english = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen",
            "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen" };
        for (var i = 0; i < english.Length; i++) Add(i, english[i]);
        string[] englishTens = { "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety" };
        for (var i = 0; i < englishTens.Length; i++) Add((i + 2) * 10, englishTens[i]);

        // Roman Urdu 0-99 (spellings vary; the common ones)
        Add(0, "sifar", "sifr");
        Add(1, "ek", "aik"); Add(2, "do"); Add(3, "teen"); Add(4, "char", "chaar"); Add(5, "paanch", "panch"); Add(6, "chhe", "chay", "chhay", "che", "chah");
        Add(7, "saat"); Add(8, "aath", "ath"); Add(9, "nau"); Add(10, "das");
        Add(11, "gyarah", "gyara"); Add(12, "barah", "bara"); Add(13, "terah", "tera"); Add(14, "chaudah", "chaudha"); Add(15, "pandrah", "pandra");
        Add(16, "solah", "sola"); Add(17, "satrah", "satra"); Add(18, "atharah", "athara"); Add(19, "unnees", "unnis", "unees");
        Add(20, "bees"); Add(21, "ikkees", "ikees"); Add(22, "baees", "bais"); Add(23, "teis", "tayees", "teyis"); Add(24, "chaubees", "chobees");
        Add(25, "pachees", "pachchis", "pacheess"); Add(26, "chhabbees", "chabbees"); Add(27, "sattaees", "sataees"); Add(28, "attaees", "athaees"); Add(29, "untees", "unatees");
        Add(30, "tees", "tis"); Add(31, "ikatees", "iktees"); Add(32, "battees"); Add(33, "taintees", "tentees"); Add(34, "chauntees"); Add(35, "paintees", "pentees");
        Add(36, "chhattees", "chattees"); Add(37, "saintees"); Add(38, "artees"); Add(39, "unchalees");
        Add(40, "chalees", "chalis"); Add(41, "iktalees"); Add(42, "bayalees"); Add(43, "taintalees", "tentalees"); Add(44, "chawalees"); Add(45, "paintalees", "pentalees");
        Add(46, "chhiyalees"); Add(47, "saintalees"); Add(48, "artalees"); Add(49, "unchaas");
        Add(50, "pachaas", "pachas", "pachas"); Add(51, "ikyawan"); Add(52, "bawan"); Add(53, "tirpan"); Add(54, "chauwan"); Add(55, "pachpan"); Add(56, "chhappan");
        Add(57, "sattawan"); Add(58, "atthawan"); Add(59, "unsath");
        Add(60, "saath", "sath"); Add(61, "iksath"); Add(62, "basath"); Add(63, "tirsath"); Add(64, "chausath"); Add(65, "painsath"); Add(66, "chhiyasath");
        Add(67, "sarsath"); Add(68, "arsath"); Add(69, "unhattar");
        Add(70, "sattar"); Add(71, "ikhattar"); Add(72, "bahattar"); Add(73, "tihattar"); Add(74, "chauhattar"); Add(75, "pachhattar"); Add(76, "chhihattar");
        Add(77, "satattar"); Add(78, "athattar"); Add(79, "unasi");
        Add(80, "assi"); Add(81, "ikyasi"); Add(82, "bayasi"); Add(83, "tirasi"); Add(84, "chaurasi"); Add(85, "pachasi"); Add(86, "chhiyasi"); Add(87, "sattasi");
        Add(88, "atthasi"); Add(89, "nawasi");
        Add(90, "nabbe"); Add(91, "ikyanwe"); Add(92, "bayanwe"); Add(93, "tiranwe"); Add(94, "chauranwe"); Add(95, "pachanwe"); Add(96, "chhiyanwe");
        Add(97, "sattanwe"); Add(98, "atthanwe"); Add(99, "ninnanwe", "ninyanwe");

        // "one and a half / two and a half": dedh sau = 150, dhai hazar = 2500
        Add(1.5m, "dedh", "derh"); Add(2.5m, "dhai", "dhaai");

        // Urdu script 0-10 and the round tens
        Add(0, "صفر"); Add(1, "ایک"); Add(2, "دو"); Add(3, "تین"); Add(4, "چار"); Add(5, "پانچ"); Add(6, "چھ"); Add(7, "سات"); Add(8, "آٹھ"); Add(9, "نو"); Add(10, "دس");
        Add(20, "بیس"); Add(30, "تیس"); Add(40, "چالیس"); Add(50, "پچاس"); Add(60, "ساٹھ"); Add(70, "ستر"); Add(80, "اسی"); Add(90, "نوے");
        Add(1.5m, "ڈیڑھ"); Add(2.5m, "ڈھائی");
        return map;
    }

    public static bool IsMultiplier(string word) => Multipliers.ContainsKey(word);
    public static bool IsNumberWord(string word) => Words.ContainsKey(word) || word.Equals("sadhe", StringComparison.OrdinalIgnoreCase) || word == "ساڑھے";
    public static IEnumerable<string> NumberWords => Words.Keys;
    public static IEnumerable<string> MultiplierWords => Multipliers.Keys;

    private static readonly Regex Token = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);
    private static readonly Regex GroupedDigits = new(@"(?<=\d),(?=\d{3}(?!\d))", RegexOptions.Compiled);

    private static string AsciiDigits(string s) =>
        new(s.Select(c => char.IsDigit(c) ? (char)('0' + (int)char.GetNumericValue(c)) : c).ToArray());

    /// <summary>Amounts (100..999,999) written as 3-6 digit numbers ("3500", "3,500", Urdu digits).</summary>
    public static IReadOnlySet<long> DigitAmounts(string text)
    {
        var result = new HashSet<long>();
        foreach (Match m in Regex.Matches(GroupedDigits.Replace(AsciiDigits(text), ""), @"(?<!\d)\d{3,6}(?!\d)"))
            if (long.TryParse(m.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v is >= MinAmount and <= MaxAmount) result.Add(v);
        return result;
    }

    /// <summary>
    /// Amounts spoken in words, e.g. "teen sau" 300, "do hazar paanch sau" 2500, "dhai hazar" 2500, "sadhe teen hazar" 3500,
    /// "three thousand five hundred", "تین ہزار پانچ سو". Only phrases containing sau/hazar/lakh (or hundred/thousand) count,
    /// so everyday words such as "kar do" are never read as a number.
    /// </summary>
    public static IReadOnlySet<long> WordAmounts(string text)
    {
        var result = new HashSet<long>();
        decimal total = 0, current = 0;
        var sawMultiplier = false;
        var sawNumber = false;
        var half = false;
        decimal? lastPlain = null; // value of the previous plain number word (not a multiplier), to tell "twenty five" from "do paanch"

        void Flush()
        {
            if (sawMultiplier && sawNumber)
            {
                var value = total + current;
                if (value >= MinAmount && value <= MaxAmount && value == decimal.Truncate(value)) result.Add((long)value);
            }
            total = 0; current = 0; sawMultiplier = false; sawNumber = false; half = false; lastPlain = null;
        }

        // Two plain numbers in a row are separate numbers ("kar do paanch sau" = do, then 500), except English "twenty five".
        void StartsNewNumber(decimal value)
        {
            if (lastPlain is { } last && !(last >= 20 && last < 100 && last % 10 == 0 && value is >= 1 and < 10)) Flush();
        }

        foreach (Match m in Token.Matches(AsciiDigits(text)))
        {
            var word = m.Value;
            if (Multipliers.TryGetValue(word, out var multiplier))
            {
                if (!sawNumber && !sawMultiplier) continue; // a bare "sau" / "hazar" says nothing
                sawMultiplier = true;
                lastPlain = null;
                if (multiplier == 100) current = (current == 0 ? 1 : current) * 100;
                else { total += (current == 0 ? 1 : current) * multiplier; current = 0; }
            }
            else if (word.Equals("sadhe", StringComparison.OrdinalIgnoreCase) || word == "ساڑھے")
            {
                half = true;
                sawNumber = true;
            }
            else if (Words.TryGetValue(word, out var value))
            {
                StartsNewNumber(value);
                current += value + (half ? 0.5m : 0);
                half = false;
                sawNumber = true;
                lastPlain = value;
            }
            else if (word.All(char.IsDigit) && word.Length <= 3 && decimal.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var digits))
            {
                StartsNewNumber(digits);
                current += digits;
                sawNumber = true;
                lastPlain = digits;
            }
            else
            {
                Flush();
            }
        }
        Flush();
        return result;
    }

    /// <summary>
    /// Ids dictated one digit at a time ("nau nau nau" = 999, "one zero five" = 105): three to six single-digit words in a row.
    /// These only widen what the seller is taken to have said; they are never required to survive a rewrite.
    /// </summary>
    public static IReadOnlySet<long> DictatedDigitAmounts(string text)
    {
        var result = new HashSet<long>();
        var run = new System.Text.StringBuilder();

        void Flush()
        {
            if (run.Length is >= 3 and <= 6 && long.TryParse(run.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v >= MinAmount) result.Add(v);
            run.Clear();
        }

        foreach (Match m in Token.Matches(AsciiDigits(text)))
        {
            if (Words.TryGetValue(m.Value, out var w) && w is >= 0 and <= 9 && w == decimal.Truncate(w)) run.Append((int)w);
            else if (m.Value.Length == 1 && char.IsDigit(m.Value[0])) run.Append(m.Value[0]);
            else Flush();
        }
        Flush();
        return result;
    }

    /// <summary>Every amount in the text, however it was written (digits, number words, or dictated digit by digit).</summary>
    public static IReadOnlySet<long> Amounts(string text)
    {
        var all = new HashSet<long>(DigitAmounts(text));
        all.UnionWith(WordAmounts(text));
        all.UnionWith(DictatedDigitAmounts(text));
        return all;
    }
}
