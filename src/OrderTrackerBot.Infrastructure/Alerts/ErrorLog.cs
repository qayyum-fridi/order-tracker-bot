using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using OrderTrackerBot.Application.Abstractions;

namespace OrderTrackerBot.Infrastructure.Alerts;

/// <summary>One reported issue, as kept in the daily error log (CSV) and the error email.</summary>
public sealed record ErrorLogEntry(
    DateTime OccurredUtc, string Code, string Title, IssueSeverity Severity,
    string? SellerPhone, string? BusinessName, string? Detail, string? Error);

/// <summary>
/// Section "ErrorLog". Every issue goes to a daily CSV; when Google Drive backups are configured the file is also
/// uploaded to a Drive subfolder, and when SMTP is configured errors are emailed (batched). Off unless Enabled.
/// </summary>
public sealed class ErrorLogOptions
{
    public const string SectionName = "ErrorLog";

    public bool Enabled { get; set; }
    public int FlushMinutes { get; set; } = 5;

    /// <summary>Folder for the local daily files; point it at the persistent data volume.</summary>
    public string Directory { get; set; } = "logs";

    /// <summary>Drive subfolder (inside Backup:FolderId) that receives the daily files.</summary>
    public string DriveFolderName { get; set; } = "error-logs";

    public string TimeZoneId { get; set; } = "Asia/Karachi";

    public string EmailTo { get; set; } = "";
    public string SmtpHost { get; set; } = "smtp.gmail.com";
    public int SmtpPort { get; set; } = 587;
    public string SmtpUser { get; set; } = "";
    public string SmtpPassword { get; set; } = "";

    public bool EmailConfigured =>
        !string.IsNullOrWhiteSpace(EmailTo) && !string.IsNullOrWhiteSpace(SmtpUser) && !string.IsNullOrWhiteSpace(SmtpPassword);
}

public interface IErrorLogSink
{
    void Add(ErrorLogEntry entry);
}

/// <summary>In-memory queue between the request path (cheap Add) and the timed flush. Bounded so an outage cannot eat memory.</summary>
public sealed class ErrorLogBuffer : IErrorLogSink
{
    private const int MaxQueued = 5000;
    private readonly ConcurrentQueue<ErrorLogEntry> _queue = new();

    public void Add(ErrorLogEntry entry)
    {
        _queue.Enqueue(entry);
        while (_queue.Count > MaxQueued && _queue.TryDequeue(out _)) { }
    }

    public List<ErrorLogEntry> Drain()
    {
        var items = new List<ErrorLogEntry>();
        while (_queue.TryDequeue(out var entry)) items.Add(entry);
        return items;
    }
}

public static class ErrorLogCsv
{
    public const string Header = "\"Time (UTC)\",\"Time (local)\",\"Code\",\"Severity\",\"Seller\",\"Phone\",\"Problem\",\"Detail\",\"Error\"";

    public static string Row(ErrorLogEntry e, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(e.OccurredUtc, DateTimeKind.Utc), zone);
        return string.Join(',',
            Cell(e.OccurredUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
            Cell(local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
            Cell(e.Code),
            Cell(e.Severity.ToString()),
            Cell(e.BusinessName),
            Cell(e.SellerPhone),
            Cell(e.Title),
            Cell(Truncate(e.Detail, 1000)),
            Cell(Truncate(e.Error, 500)));
    }

    /// <summary>Always quoted; line breaks flattened; a leading = + - @ is neutralised so a buyer-supplied value can't run as a spreadsheet formula.</summary>
    private static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        var text = value.Replace("\r", " ").Replace("\n", " ");
        if (text[0] is '=' or '+' or '-' or '@' or '\t') text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    private static string? Truncate(string? text, int max) => text is null || text.Length <= max ? text : text[..max] + "…";
}
