using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Infrastructure.Persistence;

namespace OrderTrackerBot.Api.Admin;

/// <summary>
/// One row per voice note for a person to mark right or wrong: what was heard, what the bot understood from it, and what it answered.
/// Built from the logged "🎤 Maine suna" / "➡️ Samjha" replies, so only voice notes that got that echo appear. The Verdict and Note
/// columns are left empty on purpose: the accuracy percent comes from the person filling them in.
/// </summary>
public static class VoiceReviewCsv
{
    private static readonly Regex Heard = new("Maine suna: \"(?<t>.*?)\"(?:\\r?\\n|$)", RegexOptions.Compiled);
    private static readonly Regex Understood = new("Samjha: (?<s>[^\\r\\n]*)", RegexOptions.Compiled);
    private static readonly TimeSpan ReplyWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");

    public const string Header = "\"time_pkt\",\"seller\",\"phone\",\"heard\",\"understood\",\"bot_reply\",\"verdict (right/wrong)\",\"note\"";

    public static async Task<string> BuildAsync(AppDbContext db, int days, DateTime nowUtc, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, 90);
        var from = nowUtc.AddDays(-days);
        var logs = await db.MessageLogs.AsNoTracking()
            .Where(m => m.Direction == "outbound" && m.CreatedAt >= from)
            .OrderBy(m => m.Id)
            .Select(m => new { m.Id, m.Phone, m.RawText, m.CreatedAt })
            .ToListAsync(ct);
        var names = await db.Sellers.AsNoTracking().ToDictionaryAsync(s => s.WhatsAppPhoneNumber, s => s.BusinessName, ct);

        var sb = new StringBuilder().AppendLine(Header);
        for (var i = 0; i < logs.Count; i++)
        {
            var log = logs[i];
            var heard = Heard.Match(log.RawText);
            if (!heard.Success) continue;

            var understood = Understood.Match(log.RawText);
            // The echo is usually part of the answer itself; when it was sent alone, the next reply to that seller is the answer.
            var next = logs.Skip(i + 1).FirstOrDefault(l => l.Phone == log.Phone && l.CreatedAt - log.CreatedAt <= ReplyWindow);
            var reply = understood.Success || log.RawText.Length > heard.Length + 4 ? log.RawText : next?.RawText ?? log.RawText;
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(log.CreatedAt, DateTimeKind.Utc), Zone);
            sb.AppendLine(string.Join(",",
                Cell(local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
                Cell(names.GetValueOrDefault(log.Phone)),
                Cell(log.Phone),
                Cell(heard.Groups["t"].Value),
                Cell(understood.Success ? understood.Groups["s"].Value : ""),
                Cell(Truncate(reply, 600)),
                Cell(""), Cell("")));
        }
        return sb.ToString();
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    /// <summary>Always quoted; line breaks flattened; a leading = + - @ is neutralised so a value can't run as a spreadsheet formula.</summary>
    internal static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        var text = value.Replace("\r", " ").Replace("\n", " ");
        if (text[0] is '=' or '+' or '-' or '@' or '\t') text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
