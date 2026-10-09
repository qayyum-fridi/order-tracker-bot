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
