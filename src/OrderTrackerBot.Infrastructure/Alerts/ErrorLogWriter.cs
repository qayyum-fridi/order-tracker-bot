using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Infrastructure.Backup;

namespace OrderTrackerBot.Infrastructure.Alerts;

/// <summary>
/// Drains the <see cref="ErrorLogBuffer"/>: appends rows to the day's CSV (the source of truth), mirrors changed files to Drive,
/// and emails errors as one digest at most every 10 minutes. A failed upload or email is retried on the next flush and never
/// reported through <see cref="IIssueReporter"/> (that would feed this very log).
/// </summary>
public sealed class ErrorLogWriter
{
    private static readonly TimeSpan MinEmailGap = TimeSpan.FromMinutes(10);
    private const int MaxPendingEmail = 200;
    private const int MaxEmailRows = 30;

    private readonly ErrorLogBuffer _buffer;
    private readonly ErrorLogOptions _options;
    private readonly ILogger<ErrorLogWriter> _logger;
    private readonly IDriveLogStore? _drive;
    private readonly IErrorMailer? _mailer;
    private readonly TimeZoneInfo _zone;
    private readonly HashSet<string> _pendingUploads = new();
    private readonly List<ErrorLogEntry> _pendingEmail = new();
    private DateTime _lastEmailUtc = DateTime.MinValue;

    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public ErrorLogWriter(ErrorLogBuffer buffer, IOptions<ErrorLogOptions> options, ILogger<ErrorLogWriter> logger,
        IDriveLogStore? drive = null, IErrorMailer? mailer = null)
    {
        _buffer = buffer;
        _options = options.Value;
        _logger = logger;
        _drive = drive;
        _mailer = mailer;
        _zone = ResolveZone(_options.TimeZoneId);
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        var batch = _buffer.Drain();
        if (batch.Count > 0)
        {
            foreach (var day in batch.GroupBy(e => LocalDate(e.OccurredUtc)))
            {
                var path = PathFor(day.Key);
                try
                {
                    Append(path, day);
                    _pendingUploads.Add(path);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not write error log {Path}", path);
                }
            }

            _pendingEmail.AddRange(batch.Where(e => e.Severity == IssueSeverity.Error));
            if (_pendingEmail.Count > MaxPendingEmail) _pendingEmail.RemoveRange(0, _pendingEmail.Count - MaxPendingEmail);
        }

        await UploadPendingAsync(cancellationToken);
        await EmailPendingAsync(cancellationToken);
    }

    private async Task UploadPendingAsync(CancellationToken cancellationToken)
    {
        if (_drive is null) return;
        foreach (var path in _pendingUploads.ToList())
        {
            try
            {
                await _drive.UploadOrReplaceAsync(_options.DriveFolderName, Path.GetFileName(path), path, cancellationToken);
                _pendingUploads.Remove(path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not upload error log {Path} to Drive; will retry", path);
            }
        }
    }

    private async Task EmailPendingAsync(CancellationToken cancellationToken)
    {
        if (_mailer is null || _pendingEmail.Count == 0 || UtcNow() - _lastEmailUtc < MinEmailGap) return;
        try
        {
            var (subject, body) = BuildEmail(_pendingEmail, _zone);
            await _mailer.SendAsync(subject, body, cancellationToken);
            _pendingEmail.Clear();
            _lastEmailUtc = UtcNow();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not email the error digest; will retry");
        }
    }

    public static (string Subject, string Body) BuildEmail(IReadOnlyList<ErrorLogEntry> entries, TimeZoneInfo zone)
    {
        var codes = entries.Select(e => e.Code).Distinct().Take(4);
        var subject = $"[Order Tracker] {entries.Count} error(s): {string.Join(", ", codes)}";

        var body = new StringBuilder();
        body.AppendLine($"{entries.Count} error(s) on the Order Tracker bot.").AppendLine();
        foreach (var e in entries.Take(MaxEmailRows))
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(e.OccurredUtc, DateTimeKind.Utc), zone);
            body.AppendLine($"[{local:yyyy-MM-dd HH:mm:ss}] {e.Code} {e.Title}");
            body.AppendLine($"  Seller: {e.BusinessName ?? "unknown"}{(e.SellerPhone is null ? "" : $" ({e.SellerPhone})")}");
            if (!string.IsNullOrEmpty(e.Detail)) body.AppendLine($"  Detail: {e.Detail}");
            if (!string.IsNullOrEmpty(e.Error)) body.AppendLine($"  Error: {e.Error}");
            body.AppendLine();
        }
        if (entries.Count > MaxEmailRows)
            body.AppendLine($"...and {entries.Count - MaxEmailRows} more. The full list is in the error-logs folder on Google Drive.");
        return (subject, body.ToString());
    }

    private DateOnly LocalDate(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _zone));

    private string PathFor(DateOnly day) =>
        Path.Combine(Path.GetFullPath(_options.Directory), $"errors-{day:yyyy-MM-dd}.csv");

    private void Append(string path, IEnumerable<ErrorLogEntry> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var isNew = !File.Exists(path);
        using var writer = new StreamWriter(path, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        if (isNew) writer.WriteLine(ErrorLogCsv.Header);
        foreach (var row in rows) writer.WriteLine(ErrorLogCsv.Row(row, _zone));
    }

    private static TimeZoneInfo ResolveZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception) { return TimeZoneInfo.Utc; }
    }
}
