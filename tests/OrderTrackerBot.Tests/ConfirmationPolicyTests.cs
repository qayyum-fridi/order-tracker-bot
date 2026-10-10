using System.Text.RegularExpressions;
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

// What counts as approval, per pending question. "ok" and a thumb agree to the order-placement question only; an explicit yes is needed elsewhere.
public class ConfirmationPolicyTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();

    public ConfirmationPolicyTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<AiAnalysisContext, string, CancellationToken>((_, m, _) => Task.FromResult(Analyze(m)));
    }

    private ConversationEngine CreateEngine(AppDbContext db) => new(db, _ai.Object, _sender.Object, _founderAlerts.Object);

    // Only "ORDER Kurti <n>" becomes an order draft; everything else is "unclear".
    private static AiMessageAnalysis Analyze(string m)
    {
        var order = Regex.Match(m, @"^ORDER Kurti (\d+)$");
        if (order.Success)
            return new AiMessageAnalysis
            {
                Intent = "new_order",
                IsOrderAttempt = true,
                Order = new AiOrderDraft
                {
                    CustomerName = "Sara",
                    Phone = "03001112222",
                    Items = { new AiOrderItemDraft { ProductName = "Kurti", Quantity = int.Parse(order.Groups[1].Value) } },
                },
            };
        return new AiMessageAnalysis { Intent = "unclear", IsOrderAttempt = false };
    }

    private async Task OnboardAsync(ConversationEngine engine)
    {
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "10 ke qareeb", "Kurti - 1800", "done" })
            await engine.HandleIncomingMessageAsync(Phone, step, default);
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("theek hai")]
    [InlineData("hanji")]
    [InlineData("Haan")]
    [InlineData("✅")]
    public async Task OrderPlacementQuestion_AcceptsYesAndAgreement(string reply)
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        await OnboardAsync(engine);
        await engine.HandleIncomingMessageAsync(Phone, "ORDER Kurti 2", default);

        await engine.HandleIncomingMessageAsync(Phone, reply, default);

        Assert.Equal(1, await db.Orders.CountAsync());
    }

    [Theory]
    [InlineData("haan lekin quantity do kar do")]   // a condition: no approval
    [InlineData("cancel mat karna")]                // a refusal
    [InlineData("nahi")]                            // a refusal (cancels the unsaved draft)
    public async Task OrderPlacementQuestion_DoesNotSaveOnAConditionOrARefusal(string reply)
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        await OnboardAsync(engine);
        await engine.HandleIncomingMessageAsync(Phone, "ORDER Kurti 2", default);

        await engine.HandleIncomingMessageAsync(Phone, reply, default);

        Assert.Equal(0, await db.Orders.CountAsync());
    }

    [Fact]
    public async Task CancelQuestion_OkIsNotApproval()
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        await OnboardAsync(engine);
        await engine.HandleIncomingMessageAsync(Phone, "ORDER Kurti 1", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        await engine.HandleIncomingMessageAsync(Phone, "cancel order 1", default);

        await engine.HandleIncomingMessageAsync(Phone, "ok", default);

        Assert.Equal(OrderStatus.Pending, (await db.Orders.SingleAsync()).Status);
    }

    [Fact]
    public async Task CancelQuestion_ExplicitYesCancels()
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        await OnboardAsync(engine);
        await engine.HandleIncomingMessageAsync(Phone, "ORDER Kurti 1", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        await engine.HandleIncomingMessageAsync(Phone, "cancel order 1", default);

        await engine.HandleIncomingMessageAsync(Phone, "hanji", default);

        Assert.Equal(OrderStatus.Cancelled, (await db.Orders.SingleAsync()).Status);
    }

    [Theory]
    [InlineData("Nahi bhai, cancel karo")]          // a refusal followed by an instruction to cancel: cancels the draft
    [InlineData("nahi cancel kar do")]
    public async Task RefusalThenCancelInstruction_CancelsTheDraft(string reply)
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        await OnboardAsync(engine);
        await engine.HandleIncomingMessageAsync(Phone, "ORDER Kurti 2", default);

        await engine.HandleIncomingMessageAsync(Phone, reply, default);

        Assert.Equal(0, await db.Orders.CountAsync());
        Assert.Equal(ConversationState.Idle, (await db.Sessions.FirstAsync()).State);
    }

    [Fact]
    public async Task CancelWithMat_IsNotACancel()
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        await OnboardAsync(engine);
        await engine.HandleIncomingMessageAsync(Phone, "ORDER Kurti 2", default);

        await engine.HandleIncomingMessageAsync(Phone, "Nahi bhai, cancel mat karo", default);

        Assert.Equal(ConversationState.AwaitingOrderConfirmation, (await db.Sessions.FirstAsync()).State);
        Assert.Equal(0, await db.Orders.CountAsync());
    }

    public void Dispose() => _dbFactory.Dispose();
}
