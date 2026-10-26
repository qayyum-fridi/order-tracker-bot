using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

// Two engines on two connections to one database file stand in for two app instances handling the same YES.
// Instance B has already loaded the session (still waiting for the YES) when instance A completes the reset, which is the real race.
public class ResetRaceAcrossInstancesTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"otb-race-{Guid.NewGuid():N}.db");
    private readonly List<string> _sent = new();

    private AppDbContext Open()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder($"Data Source={_path}") { DefaultTimeout = 10 }.ToString())
            .Options;
        return new AppDbContext(options);
    }

    private ConversationEngine CreateEngine(AppDbContext db)
    {
        var sender = new Mock<IWhatsAppSender>();
        sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text))
            .Returns(Task.CompletedTask);
        return new ConversationEngine(db, new Mock<IAiOrderAssistant>().Object, sender.Object, new Mock<IFounderAlertNotifier>().Object);
    }

    [Fact]
    public async Task LosingInstance_DoesNotOverwriteTheWinnersOnboardingState()
    {
        using var a = Open();
        using var b = Open();
        a.Database.EnsureCreated();
        var engineA = CreateEngine(a);
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "Kurti - 1800", "done" })
            await engineA.HandleIncomingMessageAsync(Phone, step, default);
        await engineA.HandleIncomingMessageAsync(Phone, "reset account", default);

        // Instance B reads the session while it is still waiting for the YES.
        await b.Sellers.Include(s => s.Session).FirstAsync();
        var engineB = CreateEngine(b);

        await engineA.HandleIncomingMessageAsync(Phone, "yes", default);
        var winnerState = (await a.Sessions.AsNoTracking().FirstAsync()).State;
        Assert.Equal(ConversationState.OnboardingLanguage, winnerState);

        await engineB.HandleIncomingMessageAsync(Phone, "yes", default);

        using var check = Open();
        var session = await check.Sessions.AsNoTracking().FirstAsync();
        Assert.Equal(winnerState, session.State);
        Assert.Single(_sent, m => m.Contains("Account reset ho gaya"));
        Assert.Contains(_sent, m => m.Contains("pehle hi use ho chuki"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path)) File.Delete(_path);
    }
}
