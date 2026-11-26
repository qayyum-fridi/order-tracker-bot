using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

// Scored choice: the model says how sure it is (0 to 10). Not sure, or two actions close: the bot asks which one with taps instead of running a guess;
// a tap runs the model again with that action confirmed. Money written from a loose sentence waits for YES. The model's answers are scripted.
public class ScoredChoiceTests : IDisposable
{
    private const string Phone = "923001234567";
    private const string Rent = "dukaan ka kiraya 15000";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly List<string> _sent = new();
    private readonly List<IReadOnlyList<string>> _buttons = new();
    private TurnAction? _first;
    private readonly Dictionary<string, TurnAction> _confirmed = new();
    private readonly List<string?> _confirmedAsked = new();

    public void Dispose() => _dbFactory.Dispose();

    private async Task<(AppDbContext Db, Func<string, Task> Type)> ShopAsync()
    {
        var db = _dbFactory.CreateContext();
        db.Sellers.Add(new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collection", BusinessType = "Clothing", OnboardingComplete = true,
            Session = new ConversationSession { State = ConversationState.Idle } });
        await db.SaveChangesAsync();

        var sender = new Mock<IWhatsAppSender>();
        sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _sent.Add(t)).Returns(Task.CompletedTask);
        sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, b, labels, _) => { _sent.Add(b); _buttons.Add(labels); }).Returns(Task.CompletedTask);
        sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, body, _, _, _) => _sent.Add(body)).Returns(Task.CompletedTask);
        var older = new Mock<IAiOrderAssistant>();
        older.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true });
        var turn = new Mock<ITurnInterpreter>();
        turn.Setup(t => t.UnderstandAsync(It.IsAny<TurnContext>(), It.IsAny<string>(), It.IsAny<AiImageInput?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TurnContext context, string _, AiImageInput? _, CancellationToken _) =>
            {
                if (context.ConfirmedAction is null) return _first;
                _confirmedAsked.Add(context.ConfirmedAction);
                return _confirmed.GetValueOrDefault(context.ConfirmedAction);
            });
        var features = new FeatureOptions { ModelFirst = "on", MvpOnly = true, ShortcutButtons = false, SuggestionButtons = false };
        ConversationEngine Engine() => new(db, older.Object, sender.Object, Mock.Of<IFounderAlertNotifier>(), features: features, turn: turn.Object);
        return (db, async text => await Engine().HandleIncomingMessageAsync(Phone, text, default));
    }

    private static TurnAction Expense(string action, int score, params ActionGuess[] alternatives) => new()
    {
        Action = action, Value = "kiraya", Amount = 15000, Score = score, Alternatives = alternatives.ToList(),
    };

    // ----- the rule -----

    [Theory]
    [InlineData(10, 0, 0, false)]    // sure (1.0), nothing else close: select
    [InlineData(9, 0, 0, false)]     // 0.9 with no rival: select
    [InlineData(9, 8, 0, true)]      // rival 0.8, margin 0.1 under 0.15: clarify
    [InlineData(9, 6, 0, false)]     // rival 0.6, margin 0.3: select
    [InlineData(8, 0, 0, true)]      // 0.8 under the 0.85 select score: clarify
    [InlineData(5, 0, 0, true)]      // 0.5 under the 0.55 floor: reject, which asks too
    public void NeedsActionChoice_FollowsTheScoreAndTheGap(int score, int alternative, int unused, bool expected)
    {
        var action = new TurnAction { Action = TurnAction.RecordExpense, Score = score };
        if (alternative > 0) action.Alternatives.Add(new ActionGuess(TurnAction.MonthlyExpense, alternative));
        Assert.Equal(expected, ConversationEngine.NeedsActionChoice(action));
    }

    [Fact]
    public void NoScore_IsTrusted_AsBefore() =>
        Assert.False(ConversationEngine.NeedsActionChoice(new TurnAction { Action = TurnAction.ShowSummary }));

    [Fact]
    public void EveryTapLabel_FitsAButton_IsDistinct_AndBelongsToARealAction()
    {
        var labels = ConversationEngine.ActionLabels;
        Assert.All(labels, p => { Assert.Contains(p.Key, TurnAction.All); Assert.InRange(p.Value.Length, 1, 20); });
        Assert.Equal(labels.Count, labels.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(ConversationEngine.ActionNoneLabel, labels.Values);
    }

    // ----- the conversation -----

    [Fact]
    public async Task NotSure_AsksWhichOne_WithTaps_AndRunsNothing()
    {
        var (db, type) = await ShopAsync();
        using var _ = db;
        _first = Expense(TurnAction.MonthlyExpense, 5, new ActionGuess(TurnAction.RecordExpense, 4));

        await type(Rent);

        Assert.Contains(_sent, m => m == MessageTemplates.Get("model-first.pick"));
        Assert.Equal(new[] { "Mahana kharcha", "Kharcha likhna", "Kuch aur" }, _buttons.Single());
        Assert.Equal(ConversationState.AwaitingClarificationChoice, (await db.Sessions.AsNoTracking().SingleAsync()).State);
        Assert.Empty(await db.Expenses.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ATap_RunsThatAction_WhichAsksYesBeforeSavingMoney()
    {
        var (db, type) = await ShopAsync();
        using var _ = db;
        _first = Expense(TurnAction.MonthlyExpense, 5, new ActionGuess(TurnAction.RecordExpense, 4));
        _confirmed[TurnAction.RecordExpense] = Expense(TurnAction.RecordExpense, 10);
        await type(Rent);

        await type("Kharcha likhna");

        Assert.Equal(new[] { TurnAction.RecordExpense }, _confirmedAsked);   // the model ran again with the tapped action confirmed
        Assert.Contains(_sent, m => m == MessageTemplates.Get("model-expense.001", "kiraya", Formatters.Money(15000)));
        Assert.Equal(ConversationState.AwaitingModelStatusConfirmation, (await db.Sessions.AsNoTracking().SingleAsync()).State);
        Assert.Empty(await db.Expenses.AsNoTracking().ToListAsync());   // nothing saved before YES

        await type("yes");
        var saved = await db.Expenses.AsNoTracking().SingleAsync();
        Assert.Equal(15000, saved.Amount);
    }

    [Fact]
    public async Task ATapOnTheMonthlyOption_AsksYesToAddItEveryMonth()
    {
        var (db, type) = await ShopAsync();
        using var _ = db;
        _first = Expense(TurnAction.MonthlyExpense, 5, new ActionGuess(TurnAction.RecordExpense, 4));
        _confirmed[TurnAction.MonthlyExpense] = Expense(TurnAction.MonthlyExpense, 10);
        await type(Rent);

        await type("1");   // a number works like the tap

        Assert.Contains(_sent, m => m == MessageTemplates.Get("model-expense.002", "kiraya", Formatters.Money(15000)));
        await type("yes");
        var ctx = SessionContextData.FromJson((await db.Sessions.AsNoTracking().SingleAsync()).ContextJson);
        var monthly = Assert.Single(ctx.RecurringExpenses!);
        Assert.Equal(15000, monthly.Amount);
    }

    [Fact]
    public async Task KuchAur_SaysItWasNotUnderstood_AndRunsNothing()
    {
        var (db, type) = await ShopAsync();
        using var _ = db;
        _first = Expense(TurnAction.MonthlyExpense, 5, new ActionGuess(TurnAction.RecordExpense, 4));
        await type(Rent);

        await type("Kuch aur");

        Assert.Contains(_sent, m => m.Contains("samajh nahi aayi"));
        Assert.Equal(ConversationState.Idle, (await db.Sessions.AsNoTracking().SingleAsync()).State);
        Assert.Empty(_confirmedAsked);
    }

    [Fact]
    public async Task Sure_RunsWithoutAsking_ButMoneyStillWaitsForYes()
    {
        var (db, type) = await ShopAsync();
        using var _ = db;
        _first = Expense(TurnAction.RecordExpense, 9);

        await type(Rent);

        Assert.DoesNotContain(_buttons, b => b.Contains(ConversationEngine.ActionNoneLabel));   // no "which one?" taps (the YES / No buttons are the money question)
        Assert.DoesNotContain(_sent, m => m == MessageTemplates.Get("model-first.pick"));
        Assert.Contains(_sent, m => m == MessageTemplates.Get("model-expense.001", "kiraya", Formatters.Money(15000)));
        Assert.Empty(await db.Expenses.AsNoTracking().ToListAsync());
    }
}
