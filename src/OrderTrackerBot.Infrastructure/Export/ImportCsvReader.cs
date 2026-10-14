using System.Text;
using OrderTrackerBot.Application.Abstractions;

namespace OrderTrackerBot.Infrastructure.Export;

/// <summary>
/// CSV / TSV / delimited text: UTF-8 (with or without BOM), UTF-16 (Excel's "Unicode Text") or, failing that, Latin-1; the delimiter (comma, semicolon,
/// tab or pipe, or an Excel "sep=" line) is detected from the first lines; quoted cells may hold delimiters, doubled quotes and line breaks.
/// A text file is one sheet, named after the file. Binary data is refused.
/// </summary>
internal static class ImportCsvReader
{
    private static readonly char[] Delimiters = { ',', ';', '\t', '|' };
    private const int SniffLines = 5;

    public static IReadOnlyList<ImportGridSheet>? Read(byte[] file, string? fileName)
    {
        var text = Decode(file);
        if (text is null) return null;

        var delimiter = Sniff(ref text);
        var builder = new ImportGridBuilder(SheetName(fileName));
        var number = 0;
        foreach (var record in Parse(text, delimiter))
            builder.Add(++number, record);
        return new[] { builder.Build() };
    }

    private static string SheetName(string? fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName ?? "").Trim();
        return name.Length == 0 ? "Sheet1" : name;
    }

    private static string? Decode(byte[] file)
    {
        string text;
        if (file.Length >= 3 && file[0] == 0xEF && file[1] == 0xBB && file[2] == 0xBF) text = Encoding.UTF8.GetString(file, 3, file.Length - 3);
        else if (file.Length >= 2 && file[0] == 0xFF && file[1] == 0xFE) text = Encoding.Unicode.GetString(file, 2, file.Length - 2);
        else if (file.Length >= 2 && file[0] == 0xFE && file[1] == 0xFF) text = Encoding.BigEndianUnicode.GetString(file, 2, file.Length - 2);
        else
        {
            try { text = new UTF8Encoding(false, true).GetString(file); }
            catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(file); }
        }

        // A real delimited file has no control characters besides tab and line breaks; anything else is a binary file (PDF, image, Word, ...).
        foreach (var ch in text)
            if (ch < ' ' && ch is not ('\t' or '\r' or '\n')) return null;
        return text;
    }

    private static char Sniff(ref string text)
    {
        // Excel writes "sep=;" as the first line when the list separator isn't a comma.
        if (text.StartsWith("sep=", StringComparison.OrdinalIgnoreCase) && text.Length >= 5 && Delimiters.Contains(text[4]))
        {
            var delimiter = text[4];
            var newline = text.IndexOf('\n');
            text = newline < 0 ? "" : text[(newline + 1)..];
            return delimiter;
        }

        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).Take(SniffLines).ToList();
        var best = ',';
        (int First, int Total) bestScore = (0, 0);
        foreach (var candidate in Delimiters)
        {
            var counts = lines.Select(l => CountOutsideQuotes(l, candidate)).ToList();
            var score = (counts.Count > 0 ? counts[0] : 0, counts.Sum());
            if (score.Item1 > bestScore.First || (score.Item1 == bestScore.First && score.Item2 > bestScore.Total)) { best = candidate; bestScore = score; }
        }
        return best;
    }

    private static int CountOutsideQuotes(string line, char delimiter)
    {
        var count = 0;
        var quoted = false;
        foreach (var ch in line)
        {
            if (ch == '"') quoted = !quoted;
            else if (ch == delimiter && !quoted) count++;
        }
        return count;
    }

    private static IEnumerable<List<string>> Parse(string text, char delimiter)
    {
        var record = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var any = false; // something was read on this line (an empty line still counts as a row for numbering)

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                }
                else cell.Append(ch);
            }
            else if (ch == '"' && cell.Length == 0) { quoted = true; any = true; }
            else if (ch == delimiter) { record.Add(cell.ToString().Trim()); cell.Clear(); any = true; }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                record.Add(cell.ToString().Trim());
                cell.Clear();
                yield return record;
                record = new List<string>();
                any = false;
            }
            else { cell.Append(ch); any = true; }
        }

        if (any || record.Count > 0)
        {
            record.Add(cell.ToString().Trim());
            yield return record;
        }
    }
}
