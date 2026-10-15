using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Api.Controllers;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Auth;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

// "login" on WhatsApp gives a one-time code. The code reaches the chat and the dashboard, but never the message log.
public class ClientLoginEngineTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly List<string> _sent = new();

    public ClientLoginEngineTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text))
            .Returns(Task.CompletedTask);
    }

    public void Dispose() => _dbFactory.Dispose();

    private ConversationEngine CreateEngine(AppDbContext db) => new(db, _ai.Object, _sender.Object, _founderAlerts.Object);

    private async Task OnboardAsync(ConversationEngine engine)
    {
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "10 ke qareeb", "Kurti - 1800", "done" })
            await engine.HandleIncomingMessageAsync(Phone, step, default);
    }

    [Fact]
    public async Task Login_sends_a_code_that_the_dashboard_accepts_once_and_keeps_it_out_of_the_log()
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        await OnboardAsync(engine);
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "login", default);

        var code = Regex.Match(Assert.Single(_sent), @"\*(\d{6})\*").Groups[1].Value;
        var seller = await db.Sellers.SingleAsync();
        Assert.Equal(ClientLoginCodes.Hash(seller.Id, code), seller.ClientLoginCodeHash);

        var logs = await db.MessageLogs.ToListAsync();
        Assert.Contains(logs, l => l.Direction == "outbound" && l.RawText == "[dashboard login code]");
        Assert.DoesNotContain(logs, l => l.RawText.Contains(code));

        Assert.IsType<OkObjectResult>(await new ClientAuthController(db).Verify(new(Phone, code), default));
        Assert.IsType<UnauthorizedObjectResult>(await new ClientAuthController(db).Verify(new(Phone, code), default));
    }

    [Fact]
    public async Task A_second_login_within_a_minute_keeps_the_same_code()
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        await OnboardAsync(engine);
        await engine.HandleIncomingMessageAsync(Phone, "login", default);
        var hashBefore = (await db.Sellers.SingleAsync()).ClientLoginCodeHash;
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "login", default);

        Assert.Equal(hashBefore, (await db.Sellers.SingleAsync()).ClientLoginCodeHash);
        var reply = Assert.Single(_sent);
        Assert.Contains("abhi bheja ja chuka hai", reply);
        Assert.Matches(@"\b(?:[1-5]?\d|60) second baad", reply);
    }
}
