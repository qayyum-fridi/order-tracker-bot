using System.Text.RegularExpressions;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>The WhatsApp login code carries the dashboard address as a tappable link when one is configured.</summary>
public class ClientLoginLinkTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly List<string> _sent = new();

    public ClientLoginLinkTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text))
            .Returns(Task.CompletedTask);
    }

    public void Dispose() => _dbFactory.Dispose();

    private ConversationEngine CreateEngine(AppDbContext db, string? dashboardUrl) =>
        new(db, _ai.Object, _sender.Object, _founderAlerts.Object, dashboard: new DashboardOptions { Url = dashboardUrl });

    private async Task OnboardAsync(ConversationEngine engine)
    {
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "Kurti - 1800", "done" })
            await engine.HandleIncomingMessageAsync(Phone, step, default);
        _sent.Clear();
    }

    [Fact]
    public async Task LoginWithADashboardUrl_SendsTheCodeAndTheLink()
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db, "https://dashboard.example.com");
        await OnboardAsync(engine);

        await engine.HandleIncomingMessageAsync(Phone, "login", default);

        var message = Assert.Single(_sent);
        var code = Regex.Match(message, @"\*(\d{6})\*").Groups[1].Value;
        Assert.NotEmpty(code);
        Assert.Contains("Dashboard kholen: https://dashboard.example.com", message);
        // The link itself never carries the code.
        Assert.DoesNotContain(code, "https://dashboard.example.com");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dashboard.example.com")]
    [InlineData("http://dashboard.example.com")]
    [InlineData("javascript:alert(1)")]
    public async Task LoginWithoutAUsableUrl_SendsTheCodeWithoutALink(string? url)
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db, url);
        await OnboardAsync(engine);

        await engine.HandleIncomingMessageAsync(Phone, "login", default);

        var message = Assert.Single(_sent);
        Assert.Matches(@"\*\d{6}\*", message);
        Assert.DoesNotContain("Dashboard kholen", message);
        Assert.DoesNotContain("http", message);
    }
}
