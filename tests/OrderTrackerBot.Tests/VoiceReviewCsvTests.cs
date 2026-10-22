using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Api.Admin;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

public class VoiceReviewCsvTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    public VoiceReviewCsvTests()
    {
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    private void Out(string phone, string text, double minutesAgo) =>
        _db.MessageLogs.Add(new MessageLog { Phone = phone, Direction = "outbound", RawText = text, CreatedAt = Now.AddMinutes(-minutesAgo) });

    [Fact]
    public async Task OneRowPerVoiceNote_WithHeardUnderstoodAndReply_AndEmptyVerdict()
    {
        _db.Sellers.Add(new Seller { WhatsAppPhoneNumber = "923001111111", BusinessName = "Ayesha" });
        Out("923001111111", "🎤 Maine suna: \"order char sau\"\n➡️ Samjha: \"order 400\"\n\nOrder #400 nahi mila.", 10);
        Out("923001111111", "Hello, yeh awaaz nahi hai", 9);
        await _db.SaveChangesAsync();

        var csv = await VoiceReviewCsv.BuildAsync(_db, 7, Now, default);

        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"Ayesha\"", lines[1]);
        Assert.Contains("\"order char sau\"", lines[1]);
        Assert.Contains("\"\"order 400\"\"", lines[1]);
        Assert.Contains("nahi mila", lines[1]);
        Assert.EndsWith("\"\",\"\"", lines[1].TrimEnd('\r'));
    }

    [Fact]
    public async Task EchoSentAlone_UsesTheNextReply_AndFormulasAreNeutralised()
    {
        Out("923002222222", "🎤 Maine suna: \"=SUM(A1)\"", 5);
        Out("923002222222", "Maaf kijiye, samajh nahi aaya", 4.5);
        await _db.SaveChangesAsync();

        var csv = await VoiceReviewCsv.BuildAsync(_db, 7, Now, default);

        Assert.Contains("\"'=SUM(A1)\"", csv);
        Assert.Contains("samajh nahi aaya", csv);
    }

    [Fact]
    public async Task OldAndNonVoiceMessages_AreLeftOut()
    {
        Out("923003333333", "🎤 Maine suna: \"purana\"", 60 * 24 * 30);
        Out("923003333333", "Koi aam jawab", 1);
        await _db.SaveChangesAsync();

        var csv = await VoiceReviewCsv.BuildAsync(_db, 7, Now, default);

        Assert.Single(csv.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
