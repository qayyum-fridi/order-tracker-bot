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

public class ConfirmButtonTests : IDisposable
{
    private const string Phone = "923001234567";
    private const string Draft = "new order: Sara Khan, Kurti 2, 03001112222, Model Town Lahore";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly List<string> _text = new();
    private readonly List<(string Body, string[] Labels)> _buttons = new();

    public ConfirmButtonTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _text.Add(t)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, b, l, _) => _buttons.Add((b, l.ToArray()))).Returns(Task.CompletedTask);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true });
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), Draft, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis
            {
                Intent = "new_order",
                IsOrderAttempt = true,
                Order = new AiOrderDraft
                {
                    CustomerName = "Sara Khan",
                    Phone = "03001112222",
                    Address = "Model Town Lahore",
                    Items = { new AiOrderItemDraft { ProductName = "Kurti", Quantity = 2 } }
                }
            });
    }

    private ConversationEngine Engine(AppDbContext db, bool buttons = true) =>
        new(db, _ai.Object, _sender.Object, Mock.Of<IFounderAlertNotifier>(), features: new FeatureOptions { ShortcutButtons = false, ConfirmButtons = buttons });

    private static async Task SeedAsync(AppDbContext db)
    {
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        db.Products.Add(new Product { SellerId = seller.Id, Name = "Kurti", Price = 1800 });
        await db.SaveChangesAsync();
    }

    private async Task<ConversationEngine> DraftAsync(AppDbContext db, bool buttons = true)
    {
        await SeedAsync(db);
        var engine = Engine(db, buttons);
        await engine.HandleIncomingMessageAsync(Phone, Draft, default);
        return engine;
    }

    private static Task<int> OrdersAsync(AppDbContext db) => db.Orders.CountAsync();

    [Fact]
    public async Task Draft_ShowsSaveEditCancelButtons_NotTheTypedHint()
    {
        using var db = _dbFactory.CreateContext();
        await DraftAsync(db);

        var draft = Assert.Single(_buttons, b => b.Body.Contains("Confirm order"));
        Assert.Equal(new[] { "✅ Save", "✏️ Edit", "❌ Cancel" }, draft.Labels);
        Assert.All(draft.Labels, l => Assert.True(l.Length <= 20));
        Assert.DoesNotContain("Reply YES", draft.Body);
        Assert.Contains("Kurti", draft.Body);
    }

    [Fact]
    public async Task TapSave_SavesTheOrder()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await DraftAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "✅ Save", default);

        Assert.Equal(1, await OrdersAsync(db));
        Assert.Contains(_text, t => t.Contains("Order saved"));
    }

    [Theory]
    [InlineData("❌ Cancel")]
    [InlineData("✏️ Edit")]
    public async Task TapCancelOrEdit_DropsTheDraft_NothingSaved(string tap)
    {
        using var db = _dbFactory.CreateContext();
        var engine = await DraftAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, tap, default);

        Assert.Equal(0, await OrdersAsync(db));
        Assert.Contains(_text, t => t.Contains("order cancel kar diya"));
        Assert.Equal(ConversationState.Idle, (await db.Sellers.AsNoTracking().Include(s => s.Session).FirstAsync()).Session!.State);
    }

    [Fact]
    public async Task TypedYes_StillWorks()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await DraftAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.Equal(1, await OrdersAsync(db));
    }

    [Theory]
    [InlineData("✅ Save")]
    [InlineData("✏️ Edit")]
    [InlineData("❌ Cancel")]
    public async Task StaleTap_WithNoDraft_SavesNothing_AndSaysSo(string tap)
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, tap, default);

        Assert.Equal(0, await OrdersAsync(db));
        Assert.Contains(_text, t => t.Contains("purane order ka tha"));
    }

    [Fact]
    public async Task UnclearReply_ReShowsTheButtons()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await DraftAsync(db);
        _buttons.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "hmm ek minute", default);

        Assert.Single(_buttons, b => b.Body.Contains("samajh nahi aaya") && b.Labels.Length == 3);
        Assert.Equal(0, await OrdersAsync(db));
    }

    [Fact]
    public async Task ButtonsOff_KeepsTheTypedPrompt()
    {
        using var db = _dbFactory.CreateContext();
        await DraftAsync(db, buttons: false);

        Assert.Empty(_buttons);
        Assert.Contains(_text, t => t.Contains("Reply YES to save"));
    }

    public void Dispose() => _dbFactory.Dispose();
}
