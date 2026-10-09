using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.Alerts;
using OrderTrackerBot.Infrastructure.Backup;
using Xunit;

namespace OrderTrackerBot.Tests;

public class ErrorLogTests : IDisposable
{
    private static readonly DateTime Day = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "otb-errorlog-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ErrorLogBuffer _buffer = new();
    private readonly FakeDrive _drive = new();
    private readonly FakeMailer _mailer = new();
    private DateTime _now = Day;

    private string CsvPath => Path.Combine(_dir, "errors-2026-10-09.csv");

    private ErrorLogWriter Writer(bool drive = false, bool mail = false) =>
        new(_buffer, Options.Create(new ErrorLogOptions { Enabled = true, Directory = _dir, TimeZoneId = "UTC" }),
            NullLogger<ErrorLogWriter>.Instance, drive ? _drive : null, mail ? _mailer : null) { UtcNow = () => _now };

    private static ErrorLogEntry Entry(string code = "OTB-1001", IssueSeverity severity = IssueSeverity.Error, string? detail = "boom") =>
        new(Day, code, "Inbound message processing failed", severity, "923001110001", "Ali Store", detail, "InvalidOperationException: boom");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Flush_writes_a_daily_csv_with_date_seller_and_detail()
    {
        _buffer.Add(Entry());

        await Writer().FlushAsync(default);

        var lines = File.ReadAllLines(CsvPath);
        Assert.Equal(2, lines.Length);
        Assert.Contains("Time (UTC)", lines[0]);
        Assert.Contains("\"2026-10-09 12:00:00\"", lines[1]);
        Assert.Contains("\"OTB-1001\"", lines[1]);
        Assert.Contains("\"Ali Store\"", lines[1]);
        Assert.Contains("\"923001110001\"", lines[1]);
        Assert.Contains("\"boom\"", lines[1]);
    }

    [Fact]
    public async Task A_second_flush_appends_without_repeating_the_header()
    {
        var writer = Writer();
        _buffer.Add(Entry("OTB-1001"));
        await writer.FlushAsync(default);
        _buffer.Add(Entry("OTB-2002"));
        await writer.FlushAsync(default);

        var lines = File.ReadAllLines(CsvPath);
        Assert.Equal(3, lines.Length);
        Assert.Contains("OTB-2002", lines[2]);
    }

    [Fact]
    public async Task Cells_cannot_run_as_formulas_and_quotes_are_escaped()
    {
        _buffer.Add(Entry(detail: "=HYPERLINK(\"http://x\")"));

        await Writer().FlushAsync(default);

        Assert.Contains("\"'=HYPERLINK(\"\"http://x\"\")\"", File.ReadAllText(CsvPath));
    }

    [Fact]
    public async Task The_file_is_uploaded_to_drive_and_retried_after_a_failure()
    {
        var writer = Writer(drive: true);
        _drive.FailNext = 1;
        _buffer.Add(Entry());

        await writer.FlushAsync(default);
        Assert.Empty(_drive.Uploads);

        await writer.FlushAsync(default);
        var upload = Assert.Single(_drive.Uploads);
        Assert.Equal("error-logs", upload.Folder);
        Assert.Equal("errors-2026-10-09.csv", upload.Name);
        Assert.Contains("OTB-1001", upload.Content);
    }

    [Fact]
    public async Task Only_errors_are_emailed_as_one_digest()
    {
        _buffer.Add(Entry("OTB-1001"));
        _buffer.Add(Entry("OTB-2002"));
        _buffer.Add(Entry("OTB-2001", IssueSeverity.Warning));

        await Writer(mail: true).FlushAsync(default);

        var mail = Assert.Single(_mailer.Sent);
        Assert.Contains("2 error(s)", mail.Subject);
        Assert.Contains("Ali Store (923001110001)", mail.Body);
        Assert.Contains("OTB-2002", mail.Body);
        Assert.DoesNotContain("OTB-2001", mail.Body);
        Assert.Contains("OTB-2001", File.ReadAllText(CsvPath)); // the warning is still in the file
    }

    [Fact]
    public async Task Emails_are_rate_limited_and_a_failed_send_is_retried()
    {
        var writer = Writer(mail: true);
        _mailer.FailNext = 1;
        _buffer.Add(Entry("OTB-1001"));

        await writer.FlushAsync(default);
        Assert.Empty(_mailer.Sent);

        await writer.FlushAsync(default);
        Assert.Single(_mailer.Sent);

        _now = _now.AddMinutes(1);
        _buffer.Add(Entry("OTB-2002"));
        await writer.FlushAsync(default);
        Assert.Single(_mailer.Sent); // inside the 10-minute gap

        _now = _now.AddMinutes(10);
        await writer.FlushAsync(default);
        Assert.Equal(2, _mailer.Sent.Count);
        Assert.Contains("OTB-2002", _mailer.Sent[1].Body);
    }

    [Fact]
    public async Task IssueReporter_logs_every_occurrence_even_when_the_webhook_is_in_cooldown()
    {
        using var dbFactory = new TestDbContextFactory();
        using (var db = dbFactory.CreateContext())
        {
            db.Sellers.Add(new Seller { WhatsAppPhoneNumber = "923009990009", BusinessName = "Cooldown Store" });
            await db.SaveChangesAsync();
        }
        var services = new ServiceCollection();
        services.AddScoped<IAppDbContext>(_ => dbFactory.CreateContext());
        using var provider = services.BuildServiceProvider();
        var reporter = new IssueReporter(new HttpClient(new OkHandler()), Options.Create(new FounderAlertOptions { WebhookUrl = "http://alerts.test/hook" }),
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<IssueReporter>.Instance, _buffer);

        await reporter.ReportAsync(IssueCodes.InboundMessageFailed, "923009990009", "first");
        await reporter.ReportAsync(IssueCodes.InboundMessageFailed, "923009990009", "second");

        var entries = _buffer.Drain();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal("Cooldown Store", e.BusinessName));
        Assert.Equal(new[] { "first", "second" }, entries.Select(e => e.Detail));
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private sealed class FakeDrive : IDriveLogStore
    {
        public int FailNext { get; set; }
        public List<(string Folder, string Name, string Content)> Uploads { get; } = new();

        public Task UploadOrReplaceAsync(string folderName, string fileName, string localPath, CancellationToken cancellationToken)
        {
            if (FailNext > 0) { FailNext--; throw new HttpRequestException("Drive is down"); }
            Uploads.Add((folderName, fileName, File.ReadAllText(localPath)));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMailer : IErrorMailer
    {
        public int FailNext { get; set; }
        public List<(string Subject, string Body)> Sent { get; } = new();

        public Task SendAsync(string subject, string body, CancellationToken cancellationToken)
        {
            if (FailNext > 0) { FailNext--; throw new InvalidOperationException("SMTP is down"); }
            Sent.Add((subject, body));
            return Task.CompletedTask;
        }
    }
}
