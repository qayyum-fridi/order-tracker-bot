using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

public class OnboardingCustomerTests : IDisposable
{
    private const string Phone = "923001234567";
    private const string Message = "naya client hai Muhammad Ali, address Model Town Lahore, phone baad mein";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly List<string> _sent = new();

    public OnboardingCustomerTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text))
            .Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, body, _, _) => _sent.Add(body))
            .Returns(Task.CompletedTask);
    }

    private ConversationEngine CreateEngine(AppDbContext db) => new(db, _ai.Object, _sender.Object, _founderAlerts.Object);

    private async Task OnboardAsync(AppDbContext db)
    {
        var engine = CreateEngine(db);
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "Kurti - 1800" })
            await engine.HandleIncomingMessageAsync(Phone, step, default);
        _sent.Clear();
    }

    private void AnalysisIs(AiMessageAnalysis analysis) =>
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(analysis);

    [Fact]
    public async Task ClientNamedDuringSetup_WithoutItems_IsSavedAsCustomer()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        AnalysisIs(new AiMessageAnalysis
        {
            Intent = "add_customer",
            NewCustomer = new AiNewCustomer { Name = "Muhammad Ali", Address = "Model Town Lahore" }
        });

        await CreateEngine(db).HandleIncomingMessageAsync(Phone, Message, default);

        var saved = await db.Customers.SingleAsync(c => c.Name == "Muhammad Ali");
        Assert.Equal("Model Town Lahore", saved.Address);
        Assert.Null(saved.Phone);
        Assert.Contains(_sent, m => m.Contains("Muhammad Ali") && m.Contains("customer list"));
        Assert.DoesNotContain(_sent, m => m.Contains("samajh nahi aaya"));
    }

    [Fact]
    public async Task ClientAlreadySaved_IsNotDuplicated()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        AnalysisIs(new AiMessageAnalysis
        {
            Intent = "add_customer",
            NewCustomer = new AiNewCustomer { Name = "Muhammad Ali", Address = "Model Town Lahore" }
        });
        var engine = CreateEngine(db);
        await engine.HandleIncomingMessageAsync(Phone, Message, default);
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, Message, default);

        Assert.Equal(1, await db.Customers.CountAsync(c => c.Name == "Muhammad Ali"));
        Assert.Contains(_sent, m => m.Contains("pehle se customer list mein"));
    }

    [Fact]
    public async Task ImplausiblePhone_IsDropped_AndTheCustomerStillSaved()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        AnalysisIs(new AiMessageAnalysis
        {
            Intent = "add_customer",
            NewCustomer = new AiNewCustomer { Name = "Sara", Phone = "030011122" }
        });

        await CreateEngine(db).HandleIncomingMessageAsync(Phone, "Sara, phone 030011122", default);

        var saved = await db.Customers.SingleAsync(c => c.Name == "Sara");
        Assert.Null(saved.Phone);
    }

    public void Dispose() => _dbFactory.Dispose();
}
