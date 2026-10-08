using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using OrderTrackerBot.Application.Conversation;
using Xunit;
using Xunit.Abstractions;

namespace OrderTrackerBot.Tests;

/// <summary>Runs only when the named environment variable is set; otherwise the test is reported as skipped and does nothing.</summary>
public sealed class LocalOnlyFactAttribute : FactAttribute
{
    public LocalOnlyFactAttribute(string environmentVariable)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(environmentVariable)))
            Skip = $"Local-only report: set {environmentVariable} to run it.";
    }
}

internal sealed record VoiceSample(string Transcript, string? Rewrite);

/// <summary>What real speech-to-text output looks like to the number guard. Pure functions over (transcript, rewrite) samples.</summary>
internal static class VoiceTranscriptAnalyzer
{
    private static readonly Regex Echo = new("^🎤 Maine suna: \"(?<t>.*?)\"[ \\t]*(?:\\r?\\n|$)", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Understood = new("➡️ Samjha: (?<s>[^\\r\\n]*)", RegexOptions.Compiled);
    private static readonly Regex Word = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);
    private static readonly Regex LongDigits = new(@"\d{7,}", RegexOptions.Compiled);

    /// <summary>One bot message ("🎤 Maine suna: "…"" plus an optional "➡️ Samjha: …" line) -> the raw transcript and the rewrite the bot used; null if it is not a voice echo.</summary>
    public static VoiceSample? ParseEcho(string raw)
    {
        var m = Echo.Match(raw.TrimStart());
        if (!m.Success) return null;
        string? rewrite = null;
        if (Understood.Match(raw) is { Success: true } s)
        {
            var steps = s.Groups["s"].Value.Split("\"  →  ", StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().Trim('"')).Where(x => x.Length > 0);
            rewrite = string.Join("\n", steps);
            if (rewrite.Length == 0) rewrite = null;
        }
        return new VoiceSample(m.Groups["t"].Value.Trim(), rewrite);
    }

    /// <summary>Phone-length digit runs are hidden so a report can be shared without customer numbers.</summary>
    public static string Mask(string text) => SpokenNumbers.MaskSpokenDigits(LongDigits.Replace(text, "<phone>"));

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        for (var j = 1; j <= b.Length; j++)
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    private static void Bump(Dictionary<string, int> map, string key) => map[key] = map.GetValueOrDefault(key) + 1;

    private static string Top(Dictionary<string, int> map, int take = 25) =>
        map.Count == 0 ? "  (none)" : string.Join("\n", map.OrderByDescending(x => x.Value).ThenBy(x => x.Key).Take(take).Select(x => $"  {x.Value,4} × {x.Key}"));

    public static string Report(IReadOnlyList<VoiceSample> samples)
    {
        int digitsOnly = 0, wordsOnly = 0, both = 0, neither = 0, withNumeric = 0, dictated = 0;
        int digitAmounts = 0, wordAmounts = 0;
        var multipliers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var suspectMultipliers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var beforeMultiplier = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var nearMiss = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var unparsed = new List<string>();
        var guard = new List<string>();
        var knownMultipliers = SpokenNumbers.MultiplierWords.Where(w => w.Length >= 5).ToList();
        var knownNumberWords = SpokenNumbers.NumberWords.Where(w => w.Length >= 4 && w.All(c => c < 128)).ToList();
        var withRewrite = 0;

        foreach (var sample in samples)
        {
            var t = sample.Transcript;
            var digits = SpokenNumbers.DigitAmounts(t);
            var words = SpokenNumbers.WordAmounts(t);
            digitAmounts += digits.Count;
            wordAmounts += words.Count;
            if (SpokenNumbers.DictatedDigitAmounts(t).Count > 0) dictated++;
            if (digits.Count > 0 && words.Count > 0) both++;
            else if (digits.Count > 0) digitsOnly++;
            else if (words.Count > 0) wordsOnly++;
            else neither++;

            var tokens = Word.Matches(t).Select(m => m.Value).ToList();
            var hasMultiplier = tokens.Any(SpokenNumbers.IsMultiplier);
            if (digits.Count > 0 || hasMultiplier || tokens.Any(SpokenNumbers.IsNumberWord)) withNumeric++;

            for (var i = 0; i < tokens.Count; i++)
            {
                var tok = tokens[i].ToLowerInvariant();
                if (SpokenNumbers.IsMultiplier(tok))
                {
                    Bump(multipliers, tok);
                    if (i > 0)
                    {
                        var prev = tokens[i - 1].ToLowerInvariant();
                        if (prev.Length > 2 && !SpokenNumbers.IsNumberWord(prev) && !SpokenNumbers.IsMultiplier(prev) && !prev.All(char.IsDigit)) Bump(beforeMultiplier, prev);
                    }
                }
                else if (tok.Length >= 4 && tok.All(c => c < 128) && !SpokenNumbers.IsNumberWord(tok))
                {
                    if (knownMultipliers.FirstOrDefault(k => Distance(tok, k.ToLowerInvariant()) <= 1) is { } nearest) Bump(suspectMultipliers, $"{tok} (looks like {nearest})");
                    else if (hasMultiplier && knownNumberWords.Where(k => Distance(tok, k.ToLowerInvariant()) <= 2).ToList() is { Count: > 0 and <= 3 } near)
                        Bump(nearMiss, $"{tok} (close to {string.Join("/", near.Select(n => $"{n}={ValueOf(n)}"))})");
                }
            }

            if (hasMultiplier && digits.Count == 0 && words.Count == 0 && SpokenNumbers.DictatedDigitAmounts(t).Count == 0)
                unparsed.Add(Mask(t));

            if (sample.Rewrite is { } rewrite)
            {
                withRewrite++;
                if (ConversationEngine.WhyUnfaithful(t, rewrite) is { } why)
                    guard.Add($"{why}\n      transcript: {Mask(t)}\n      rewrite   : {Mask(rewrite.Replace("\n", " | "))}");
            }
        }

        var n = Math.Max(1, samples.Count);
        string Pct(int x) => $"{x} ({100.0 * x / n:0.0}%)";
        var sb = new StringBuilder();
        sb.AppendLine("================ Voice transcript report (output is local; phone-length numbers are masked) ================");
        sb.AppendLine($"Samples: {samples.Count}   with a logged rewrite: {withRewrite}   with any numeric token: {Pct(withNumeric)}");
        sb.AppendLine();
        sb.AppendLine("1) Digits vs words (an 'amount' is 100..999,999)");
        sb.AppendLine($"   digits only : {Pct(digitsOnly)}");
        sb.AppendLine($"   words only  : {Pct(wordsOnly)}");
        sb.AppendLine($"   both        : {Pct(both)}");
        sb.AppendLine($"   no amount   : {Pct(neither)}");
        sb.AppendLine($"   amounts found as digits: {digitAmounts}   as words: {wordAmounts}   transcripts with ids dictated digit by digit: {dictated}");
        sb.AppendLine($"   digits : words ratio (amounts) = {digitAmounts} : {wordAmounts}");
        sb.AppendLine();
        sb.AppendLine("2) Multiplier spellings the model produced (recognised by the parser)");
        sb.AppendLine(Top(multipliers));
        sb.AppendLine("   Words that look like a misspelt multiplier (NOT recognised — candidates to add):");
        sb.AppendLine(Top(suspectMultipliers));
        sb.AppendLine();
        sb.AppendLine("3) Lexicon gaps where the guard is blind or rejects");
        sb.AppendLine("   Words right before a multiplier that are not known number words (strongest signal):");
        sb.AppendLine(Top(beforeMultiplier));
        sb.AppendLine("   Unknown words close to known number words, in transcripts with a multiplier (a human must confirm the value; never auto-map):");
        sb.AppendLine(Top(nearMiss));
        sb.AppendLine($"   Transcripts with a multiplier but NO amount parsed: {unparsed.Count}");
        foreach (var u in unparsed.Take(15)) sb.AppendLine($"      \"{u}\"");
        sb.AppendLine($"   Logged rewrites the guard rejects (known order ids/prices are not available offline, so some invented-amount hits may be legitimate): {guard.Count}");
        foreach (var g in guard.Take(15)) sb.AppendLine($"    - {g}");
        sb.AppendLine("=============================================================================================================");
        return sb.ToString();
    }

    private static string ValueOf(string numberWord) =>
        SpokenNumbers.WordAmounts($"{numberWord} sau").FirstOrDefault() is var v and > 0 ? (v / 100).ToString() : "?";
}

public class VoiceTranscriptReport(ITestOutputHelper output)
{
    private const string EnvVar = "VOICE_TRANSCRIPTS";

    private static IEnumerable<VoiceSample> FromLines(IEnumerable<string> lines)
    {
        foreach (var line in lines.Select(l => l.Trim()).Where(l => l.Length > 0))
            yield return VoiceTranscriptAnalyzer.ParseEcho(line) ?? new VoiceSample(line.Trim('"'), null);
    }

    private static List<VoiceSample> Load(string path)
    {
        if (Directory.Exists(path))
            return Directory.GetFiles(path, "*.txt").OrderBy(f => f).SelectMany(f => FromLines(File.ReadAllLines(f))).ToList();
        if (path.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase))
        {
            // The app's own database, opened read-only: the bot echoes every transcript back to the seller and logs that message.
            using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT RawText FROM MessageLogs WHERE Direction = 'outbound' AND RawText LIKE '🎤 Maine suna:%' ORDER BY Id";
            using var reader = command.ExecuteReader();
            var samples = new List<VoiceSample>();
            while (reader.Read())
                if (VoiceTranscriptAnalyzer.ParseEcho(reader.GetString(0)) is { } sample) samples.Add(sample);
            return samples;
        }
        return FromLines(File.ReadAllLines(path)).ToList();
    }

    /// <summary>
    /// Local only. VOICE_TRANSCRIPTS = a folder of .txt files (one transcript per line; "🎤 Maine suna:" lines are cleaned),
    /// a single text file, or the app's .db file (reads MessageLogs, read-only). Optional VOICE_TRANSCRIPTS_REPORT = file to also write the report to.
    /// Run with: dotnet test --filter VoiceTranscriptReport --logger "console;verbosity=detailed"
    /// </summary>
    [LocalOnlyFact(EnvVar)]
    public void Report_OnRealTranscripts()
    {
        var path = Environment.GetEnvironmentVariable(EnvVar)!;
        var samples = Load(path);
        Assert.True(samples.Count > 0, $"No transcripts found in {path}.");

        var report = VoiceTranscriptAnalyzer.Report(samples);
        output.WriteLine(report);
        if (Environment.GetEnvironmentVariable("VOICE_TRANSCRIPTS_REPORT") is { Length: > 0 } outFile) File.WriteAllText(outFile, report);
    }

    // ---- the harness itself, on made-up lines (these say nothing about real ASR behaviour) ----

    [Fact]
    public void ParseEcho_StripsThePrefix_AndReadsTheRewriteLine()
    {
        var sample = VoiceTranscriptAnalyzer.ParseEcho("🎤 Maine suna: \"price teen sau kar do\"\n➡️ Samjha: \"price 1 = 300\"  →  \"done\"")!;
        Assert.Equal("price teen sau kar do", sample.Transcript);
        Assert.Equal("price 1 = 300\ndone", sample.Rewrite);

        var plain = VoiceTranscriptAnalyzer.ParseEcho("🎤 Maine suna: \"orders today\"")!;
        Assert.Equal("orders today", plain.Transcript);
        Assert.Null(plain.Rewrite);

        Assert.Null(VoiceTranscriptAnalyzer.ParseEcho("Order #12 saved"));
    }

    [Fact]
    public void Report_CountsDigitsWordsAndMultiplierSpellings_AndFlagsGaps()
    {
        var report = VoiceTranscriptAnalyzer.Report(new[]
        {
            new VoiceSample("price 3500 kar do", null),
            new VoiceSample("price teen hazar paanch sau", null),
            new VoiceSample("kurti pachpanx hazar ki", null),            // unknown word before a multiplier -> no amount parsed
            new VoiceSample("kurti hazr ki", null),                       // misspelt multiplier
            new VoiceSample("orders today", null),
            new VoiceSample("order 03001234567 ka price teen sau", "order 03001234567 price 300 3000"), // rewrite invents an extra amount
        });

        Assert.Contains("Samples: 6", report);
        Assert.Contains("digits only : 1 (16.7%)", report);
        Assert.Contains("words only  : 2 (33.3%)", report);
        Assert.Contains("hazar", report);
        Assert.Contains("pachpanx", report);                              // listed before the multiplier
        Assert.Contains("hazr (looks like hazar)", report);
        Assert.Contains("NO amount parsed: 1", report);
        Assert.Contains("amount 3000 appears in the rewrite but was not said", report);
        Assert.DoesNotContain("03001234567", report);                     // phone numbers are masked
        Assert.Contains("<phone>", report);
    }
}
