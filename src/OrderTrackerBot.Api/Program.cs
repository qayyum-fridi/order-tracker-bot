using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Infrastructure;
using OrderTrackerBot.Infrastructure.Alerts;
using OrderTrackerBot.Infrastructure.Backup;
using OrderTrackerBot.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("whatsapp-templates.json", optional: true, reloadOnChange: true);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHostedService<OrderTrackerBot.Api.ScheduledMessagesService>();
builder.Services.AddHostedService<OrderTrackerBot.Api.WebhookWorkerService>(); // handles queued WhatsApp messages after the webhook has answered 200

var backupOptions = builder.Configuration.GetSection(BackupOptions.SectionName).Get<BackupOptions>();
if (backupOptions is { Enabled: true, IsConfigured: true } &&
    string.Equals(builder.Configuration["Database:Provider"], "Sqlite", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddHostedService<OrderTrackerBot.Api.BackupService>();

if (builder.Configuration.GetSection(ErrorLogOptions.SectionName).Get<ErrorLogOptions>() is { Enabled: true })
    builder.Services.AddHostedService<OrderTrackerBot.Api.ErrorLogService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Manual check of the error log / Drive / email path. Mapped only when ErrorLog:TestToken is set; wrong or missing token looks like a 404.
var testToken = app.Configuration["ErrorLog:TestToken"];
if (!string.IsNullOrWhiteSpace(testToken))
{
    app.MapPost("/internal/test-error", async (HttpRequest request, OrderTrackerBot.Application.Abstractions.IIssueReporter reporter) =>
    {
        var given = request.Headers["X-Test-Token"].ToString();
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(given), System.Text.Encoding.UTF8.GetBytes(testToken)))
            return Results.NotFound();

        await reporter.ReportAsync(OrderTrackerBot.Application.Abstractions.IssueCodes.InboundMessageFailed, null,
            "Dummy error to test the error log (triggered manually)", new InvalidOperationException("dummy test error"));
        return Results.Ok(new { reported = true, note = "Appears in the CSV/Drive within ErrorLog:FlushMinutes; emailed within ~10 minutes." });
    });

    // Sends one email right now and returns the SMTP outcome, so a wrong password or blocked port shows up immediately.
    app.MapPost("/internal/test-email", async (HttpRequest request, OrderTrackerBot.Infrastructure.Alerts.IErrorMailer? mailer) =>
    {
        var given = request.Headers["X-Test-Token"].ToString();
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(given), System.Text.Encoding.UTF8.GetBytes(testToken)))
            return Results.NotFound();
        if (mailer is null)
            return Results.Ok(new { sent = false, reason = "Email is not configured: set ErrorLog:EmailTo, ErrorLog:SmtpUser and ErrorLog:SmtpPassword." });

        try
        {
            await mailer.SendAsync("[Order Tracker] Test email", "This is a test email from the Order Tracker bot. If you can read it, email alerts work.", CancellationToken.None);
            return Results.Ok(new { sent = true });
        }
        catch (Exception ex)
        {
            var chain = new List<string>();
            for (Exception? e = ex; e is not null && chain.Count < 5; e = e.InnerException)
                chain.Add($"{e.GetType().Name}: {e.Message}");
            return Results.Ok(new { sent = false, error = chain });
        }
    });
}

await OrderTrackerBot.Api.BackupStartup.RestoreIfNeededAsync(app);

if (app.Configuration.GetValue("Database:AutoMigrate", true))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    // Sqlite (local dev) has no migrations of its own — EnsureCreated keeps the dev loop
    // migration-free; SQL Server (production) uses the real, versioned migrations.
    if (db.Database.IsSqlite())
    {
        db.Database.EnsureCreated();
        SqliteSchemaPatcher.Apply(db);
        if (app.Configuration.GetValue("Database:SqliteWal", true)) SqliteTuning.EnableWal(db);
    }
    else
        db.Database.Migrate();
}

app.Run();

public partial class Program { } // exposed for WebApplicationFactory-based integration tests
