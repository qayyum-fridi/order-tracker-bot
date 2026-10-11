using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

// The reset claim is a single conditional UPDATE. Two independent connections to one database file stand in for two app instances.
// This verifies the database primitive only; the full message flow across instances is still untested (issue #40).
public class ClaimAcrossConnectionsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"otb-claim-{Guid.NewGuid():N}.db");

    private AppDbContext Open()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder($"Data Source={_path}") { DefaultTimeout = 10 }.ToString())
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task TwoConnections_ClaimingTheSamePrompt_OneWins()
    {
        int sessionId;
        const string generation = "0123456789abcdef0123456789abcdef";
        using (var seed = Open())
        {
            seed.Database.EnsureCreated();
            var seller = new Seller { WhatsAppPhoneNumber = "923000000001" };
            seed.Sellers.Add(seller);
            await seed.SaveChangesAsync();
            var session = new ConversationSession
            {
                SellerId = seller.Id,
                State = ConversationState.AwaitingResetConfirmation,
                ContextJson = new SessionContextData { ResetGenerationId = generation }.ToJson()
            };
            seed.Sessions.Add(session);
            await seed.SaveChangesAsync();
            sessionId = session.Id;
        }

        using var first = Open();
        using var second = Open();
        var results = await Task.WhenAll(
            ResetConfirmation.TryClaimAsync(first, sessionId, generation, default),
            ResetConfirmation.TryClaimAsync(second, sessionId, generation, default));

        Assert.Equal(1, results.Count(won => won));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path)) File.Delete(_path);
    }
}
