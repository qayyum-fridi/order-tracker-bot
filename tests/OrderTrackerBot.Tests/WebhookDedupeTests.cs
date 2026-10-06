using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Tests;

public class WebhookDedupeTests
{
    [Fact]
    public async Task DuplicateMessageId_ViolatesPrimaryKey()
    {
        var factory = new TestDbContextFactory();
        using var db = factory.CreateContext();
        db.ProcessedWebhookMessages.Add(new ProcessedWebhookMessage { MessageId = "wamid.1" });
        await db.SaveChangesAsync();

        using var db2 = factory.CreateContext();
        db2.ProcessedWebhookMessages.Add(new ProcessedWebhookMessage { MessageId = "wamid.1" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }
}
