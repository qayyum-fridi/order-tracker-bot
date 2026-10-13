using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

public class DateRangeParserTests
{
    [Theory]
    [InlineData("1 January se aaj tak", 1, 1)]
    [InlineData("1 January se ab tak", 1, 1)]
    [InlineData("1 jan se abhi tak", 1, 1)]
    [InlineData("since 1 January", 1, 1)]
    [InlineData("1 January to now", 1, 1)]
    [InlineData("15 March se aaj tak", 15, 3)]
    [InlineData("یکم January se ab tak", 1, 1)]
    [InlineData("yakum January se aaj tak", 1, 1)]
    [InlineData("1st January se ab tak.", 1, 1)]
    public void OpenRange_ReadsTheStartDate(string text, int day, int month)
    {
        Assert.True(ConversationEngine.TryParseOpenRange(text, out var from));
        Assert.Equal((day, month), (from.Day, from.Month));
        Assert.True(from <= DateTime.UtcNow);
    }

    [Theory]
    [InlineData("1 May se 15 May")]
    [InlineData("pichle 3 mahine")]
    [InlineData("summary last week")]
    [InlineData("aaj tak")]
    [InlineData("kuch bhi se aaj tak")]
    public void NotAnOpenRange_IsNotTaken(string text) => Assert.False(ConversationEngine.TryParseOpenRange(text, out _));

    [Fact]
    public void YakumIsOne_SoARewriteWith1IsFaithful() =>
        Assert.Null(ConversationEngine.WhyUnfaithful("یکم جنوری سے اب تک", "1 January se aaj tak"));
}

public class DateRangeEngineTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly List<string> _text = new();

    public DateRangeEngineTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _text.Add(t)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, b, _, _) => _text.Add(b)).Returns(Task.CompletedTask);
    }

    private async Task<(ConversationEngine Engine, AppDbContext Db)> WaitingForDatesAsync()
    {
        var db = _dbFactory.CreateContext();
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        var customer = new Customer { SellerId = seller.Id, Name = "Sara", Phone = "03001112222" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        var order = new Order { SellerId = seller.Id, CustomerId = customer.Id, Status = OrderStatus.Delivered, CreatedAt = DateTime.UtcNow.AddDays(-2) };
        order.Items.Add(new OrderItem { ProductNameSnapshot = "Lawn Suit", UnitPrice = 3500, Quantity = 1 });
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        var ai = Mock.Of<IAiOrderAssistant>(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()) == Task.FromResult(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true }));
        var engine = new ConversationEngine(db, ai, _sender.Object, Mock.Of<IFounderAlertNotifier>(), features: new FeatureOptions { ShortcutButtons = false, ConfirmButtons = false });
        await engine.HandleIncomingMessageAsync(Phone, "trending products", default);
        await engine.HandleIncomingMessageAsync(Phone, "Custom Dates", default);
        _text.Clear();
        Assert.Equal(ConversationState.AwaitingCustomDateRange, (await db.Sellers.AsNoTracking().Include(s => s.Session).FirstAsync()).Session!.State);
        return (engine, db);
    }

    [Fact]
    public async Task FromADateUntilNow_ShowsTrendingForThatWindow()
    {
        var (engine, db) = await WaitingForDatesAsync();
        using var db2 = db;

        await engine.HandleIncomingMessageAsync(Phone, "1 January se aaj tak", default);

        var reply = Assert.Single(_text);
        Assert.Contains("Trending", reply);
        Assert.Contains("Lawn Suit", reply);
        Assert.Contains("se aaj tak", reply);
        Assert.Equal(ConversationState.Idle, (await db.Sellers.AsNoTracking().Include(s => s.Session).FirstAsync()).Session!.State);
    }

    [Theory]
    [InlineData("summary last week", "Reconciliation")]
    [InlineData("orders today", "")]
    public async Task ARealCommand_LeavesTheDateQuestion_AndRuns(string command, string expected)
    {
        var (engine, db) = await WaitingForDatesAsync();
        using var db2 = db;

        await engine.HandleIncomingMessageAsync(Phone, command, default);

        Assert.DoesNotContain(_text, t => t.Contains("samajh nahi aayi"));
        Assert.Contains(_text, t => t.Contains(expected)); // "" = any answer: the command ran and was not taken for a date
        Assert.Equal(ConversationState.Idle, (await db.Sellers.AsNoTracking().Include(s => s.Session).FirstAsync()).Session!.State);
    }

    [Fact]
    public async Task Cancel_LeavesTheDateQuestion()
    {
        var (engine, db) = await WaitingForDatesAsync();
        using var db2 = db;

        await engine.HandleIncomingMessageAsync(Phone, "cancel", default);

        Assert.Contains(_text, t => t.Contains("band kar diya"));
        Assert.Equal(ConversationState.Idle, (await db.Sellers.AsNoTracking().Include(s => s.Session).FirstAsync()).Session!.State);
    }

    [Fact]
    public async Task GibberishAsDates_KeepsAsking_WithAClearExample()
    {
        var (engine, db) = await WaitingForDatesAsync();
        using var db2 = db;

        await engine.HandleIncomingMessageAsync(Phone, "kuch samajh nahi", default);

        var reply = Assert.Single(_text);
        Assert.Contains("tareekh samajh nahi aayi", reply);
        Assert.Contains("1 January se aaj tak", reply);
        Assert.Equal(ConversationState.AwaitingCustomDateRange, (await db.Sellers.AsNoTracking().Include(s => s.Session).FirstAsync()).Session!.State);
    }

    public void Dispose() => _dbFactory.Dispose();
}
