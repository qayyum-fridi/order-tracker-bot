using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Infrastructure;
using OrderTrackerBot.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddInfrastructure(builder.Configuration);

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
    }
    else
        db.Database.Migrate();
}

app.Run();

public partial class Program { } // exposed for WebApplicationFactory-based integration tests
