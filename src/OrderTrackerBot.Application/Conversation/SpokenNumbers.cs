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

    /// <summary>An amount found in a transcript: its value and the first/last token it covers (token positions, inclusive).</summary>
    public readonly record struct AmountSpan(long Value, int Start, int End);

    // "ke sath hazar" = "with a thousand", not 60,000: a particle right before "sath/saath" means it is the word "with".
    private static readonly HashSet<string> SixtyWords = new(StringComparer.OrdinalIgnoreCase) { "sath", "saath", "ساٹھ" };
    private static readonly HashSet<string> SixtyBlockers = new(StringComparer.OrdinalIgnoreCase)
    {
        "ke", "ki", "ka", "ko", "se", "mein", "me", "par", "pe", "uske", "iske", "unke", "inke", "mere", "tere", "apke", "aapke", "hamare", "humare",
        "us", "is", "un", "in", "hum", "wo", "woh", "ap", "aap", "tum", "sab"
    };

    // Words that say "an id / code / number follows", so a run of single digits is an identifier and not three separate counts.
    private static readonly HashSet<string> IdMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "order", "id", "number", "no", "nambar", "numbar", "account", "code", "tracking", "phone", "mobile", "cnic", "card", "otp", "pin", "receipt", "invoice",
        "آرڈر", "نمبر", "اکاؤنٹ", "کوڈ", "آئی"
    };

    // "410 nahi, 420": the seller took the first amount back.
    private static readonly HashSet<string> RetractionMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "nahi", "nahin", "nahee", "nai", "no", "matlab", "sorry", "galat", "نہیں", "نہی", "مطلب", "غلط"
    };

    private static string Normalize(string text) => GroupedDigits.Replace(AsciiDigits(text), "");

    private static List<string> Tokens(string text) => Token.Matches(Normalize(text)).Select(m => m.Value).ToList();

    private static List<AmountSpan> DigitSpans(IReadOnlyList<string> tokens)
    {
        var spans = new List<AmountSpan>();
        for (var i = 0; i < tokens.Count; i++)
            if (tokens[i].Length is >= 3 and <= 6 && tokens[i].All(char.IsDigit)
                && long.TryParse(tokens[i], NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v >= MinAmount)
                spans.Add(new AmountSpan(v, i, i));
        return spans;
    }

    private static List<AmountSpan> WordSpans(IReadOnlyList<string> tokens)
    {
        var spans = new List<AmountSpan>();
        decimal total = 0, current = 0;
        var sawMultiplier = false;
        var sawNumber = false;
        var half = false;
        var start = -1;
        var last = -1;
        decimal? lastPlain = null; // value of the previous plain number word (not a multiplier), to tell "twenty five" from "do paanch"

        void Flush()
        {
            if (sawMultiplier && sawNumber)
            {
                var value = total + current;
                if (value >= MinAmount && value <= MaxAmount && value == decimal.Truncate(value)) spans.Add(new AmountSpan((long)value, start, last));
            }
            total = 0; current = 0; sawMultiplier = false; sawNumber = false; half = false; lastPlain = null; start = -1;
        }

        // Two plain numbers in a row are separate numbers ("kar do paanch sau" = do, then 500), except English "twenty five".
        void StartsNewNumber(decimal value)
        {
            if (lastPlain is { } prev && !(prev >= 20 && prev < 100 && prev % 10 == 0 && value is >= 1 and < 10)) Flush();
        }

        void Take(int index) { if (start < 0) start = index; last = index; }

        for (var i = 0; i < tokens.Count; i++)
        {
            var word = tokens[i];
            if (Multipliers.TryGetValue(word, out var multiplier))
            {
                if (!sawNumber && !sawMultiplier) continue; // a bare "sau" / "hazar" says nothing
                Take(i);
                sawMultiplier = true;
                lastPlain = null;
                if (multiplier == 100) current = (current == 0 ? 1 : current) * 100;
                else { total += (current == 0 ? 1 : current) * multiplier; current = 0; }
            }
            else if (word.Equals("sadhe", StringComparison.OrdinalIgnoreCase) || word == "ساڑھے")
            {
                Take(i);
                half = true;
                sawNumber = true;
            }
            else if (SixtyWords.Contains(word)
                     && !(i + 1 < tokens.Count && Multipliers.ContainsKey(tokens[i + 1]) && !(i > 0 && SixtyBlockers.Contains(tokens[i - 1]))))
            {
                Flush(); // "sath" is 60 only as "sath hazar" and not after "ke/uske/...": otherwise it is the word "with"
            }
            else if (Words.TryGetValue(word, out var value))
            {
                StartsNewNumber(value);
                Take(i);
                current += value + (half ? 0.5m : 0);
                half = false;
                sawNumber = true;
                lastPlain = value;
            }
            else if (word.All(char.IsDigit) && word.Length <= 3 && decimal.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var digits))
            {
                StartsNewNumber(digits);
                Take(i);
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
        return spans;
    }

    /// <summary>Runs of single digits (as words or characters, with "double"/"triple" repeating the next digit): token range plus the digit string.</summary>
    private static List<(int Start, int End, string Digits)> DigitRuns(IReadOnlyList<string> tokens)
    {
        var runs = new List<(int, int, string)>();
        var digits = new System.Text.StringBuilder();
        var start = -1;

        void Flush(int end)
        {
            if (digits.Length > 0) runs.Add((start, end, digits.ToString()));
            digits.Clear();
            start = -1;
        }

        static int? Single(string token) =>
            Words.TryGetValue(token, out var w) && w is >= 0 and <= 9 && w == decimal.Truncate(w) ? (int)w
            : token.Length == 1 && char.IsDigit(token[0]) ? token[0] - '0' : null;

        for (var i = 0; i < tokens.Count; i++)
        {
            var repeat = tokens[i].Equals("double", StringComparison.OrdinalIgnoreCase) ? 2 : tokens[i].Equals("triple", StringComparison.OrdinalIgnoreCase) ? 3 : 1;
            var digitIndex = repeat > 1 ? i + 1 : i;
            if (digitIndex < tokens.Count && Single(tokens[digitIndex]) is { } digit)
            {
                if (start < 0) start = i;
                digits.Append(digit.ToString()[0], repeat);
                i = digitIndex;
            }
            else Flush(i - 1);
        }
        Flush(tokens.Count - 1);
        return runs;
    }

    private static List<AmountSpan> DictatedSpans(IReadOnlyList<string> tokens)
    {
        var spans = new List<AmountSpan>();
        foreach (var (start, end, digits) in DigitRuns(tokens))
        {
            if (digits.Length is < 3 or > 6 || !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var v) || v < MinAmount) continue;
            // "order nau nau nau" is an id; "ek do teen piece" is three separate counts and must not become 123.
            var hasMarker = Enumerable.Range(Math.Max(0, start - 3), start - Math.Max(0, start - 3)).Any(k => IdMarkers.Contains(tokens[k]));
            if (hasMarker) spans.Add(new AmountSpan(v, start, end));
        }
        return spans;
    }

    /// <summary>Amounts (100..999,999) written as 3-6 digit numbers ("3500", "3,500", Urdu digits).</summary>
    public static IReadOnlySet<long> DigitAmounts(string text) => DigitSpans(Tokens(text)).Select(s => s.Value).ToHashSet();

    /// <summary>
    /// Amounts spoken in words, e.g. "teen sau" 300, "do hazar paanch sau" 2500, "dhai hazar" 2500, "sadhe teen hazar" 3500,
    /// "three thousand five hundred", "تین ہزار پانچ سو". Only phrases containing sau/hazar/lakh (or hundred/thousand) count,
    /// so everyday words such as "kar do" are never read as a number. "sath/saath" counts as 60 only in "sath hazar" and never after
    /// "ke/uske/..." ("uske sath hazar rupay" = with a thousand rupees).
    /// </summary>
    public static IReadOnlySet<long> WordAmounts(string text) => WordSpans(Tokens(text)).Select(s => s.Value).ToHashSet();

    /// <summary>
    /// Ids dictated one digit at a time ("order nau nau nau" = 999, "order one zero five" = 105; "double"/"triple" repeat a digit):
    /// 3-6 digits, and only right after an id word (order, id, number, account, ...), so "ek do teen piece" stays three counts.
    /// These only widen what the seller is taken to have said; they are never required to survive a rewrite. Longer runs are phone/account numbers, never amounts.
    /// </summary>
    public static IReadOnlySet<long> DictatedDigitAmounts(string text) => DictatedSpans(Tokens(text)).Select(s => s.Value).ToHashSet();

    /// <summary>Every amount in the text, however it was written (digits, number words, or an id dictated digit by digit).</summary>
    public static IReadOnlySet<long> Amounts(string text)
    {
        var tokens = Tokens(text);
        var all = new HashSet<long>(DigitSpans(tokens).Select(s => s.Value));
        all.UnionWith(WordSpans(tokens).Select(s => s.Value));
        all.UnionWith(DictatedSpans(tokens).Select(s => s.Value));
        return all;
    }

    /// <summary>
    /// Amounts the seller took back: an amount followed within two words by "nahi/matlab/sorry/galat" and then, within five words, a different amount
    /// ("char sau das nahi, char sau bees"). A rewrite may leave these out. A plain "500 nahi chahiye" with no replacement is not a retraction.
    /// </summary>
    public static IReadOnlySet<long> RetractedAmounts(string text)
    {
        var tokens = Tokens(text);
        var spans = DigitSpans(tokens).Concat(WordSpans(tokens)).OrderBy(s => s.Start).ToList();
        var retracted = new HashSet<long>();
        foreach (var span in spans)
        {
            var marker = Enumerable.Range(span.End + 1, 2).FirstOrDefault(k => k < tokens.Count && RetractionMarkers.Contains(tokens[k]), -1);
            if (marker < 0) continue;
            if (spans.Any(t => t.Value != span.Value && t.Start > marker && t.Start <= marker + 5)) retracted.Add(span.Value);
        }
        return retracted;
    }

    /// <summary>The text with the given digit amounts blanked out (used so a retracted amount need not survive a rewrite).</summary>
    public static string RemoveDigitAmounts(string text, IReadOnlySet<long> amounts) =>
        amounts.Aggregate(AsciiDigits(text), (current, a) => Regex.Replace(current, $@"(?<![\d,]){a}(?![\d,])", " "));

    /// <summary>Hides phone/account numbers dictated as words ("zero three zero zero ...", "double zero triple nine ...") when 7 or more digits run together.</summary>
    public static string MaskSpokenDigits(string text, int minDigits = 7)
    {
        var matches = Token.Matches(AsciiDigits(text)).ToList();
        var runs = DigitRuns(matches.Select(m => m.Value).ToList()).Where(r => r.Digits.Length >= minDigits).OrderByDescending(r => r.Start).ToList();
        var result = text;
        foreach (var (start, end, _) in runs)
        {
            var from = matches[start].Index;
            var to = matches[end].Index + matches[end].Length;
            result = result[..from] + "<phone>" + result[to..];
        }
        return result;
    }
}
