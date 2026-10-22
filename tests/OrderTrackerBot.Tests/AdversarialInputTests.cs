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

/// <summary>Local-only attempts to break the parser and the conversation engine with hostile input. Nothing here talks to WhatsApp or OpenAI.</summary>
public class AdversarialInputTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly Mock<IWhatsAppMediaClient> _media = new();

    public AdversarialInputTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendListMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", IsOrderAttempt = false });
    }

    public void Dispose() => _dbFactory.Dispose();

    private static readonly string[] Hostile =
    {
        "", " ", "\0", "​", "‮" + "Kurti", "﷽", "😀😀😀", "١٢٣٤٥", "۰۰۰", new string('9', 500),
        "order", "order -", "order - ", "- - -", "=====", "1 = ", "1 = =", "price 1 =", "remove", "remove 999999999999999999999",
        "Kurti - 99999999999999999999999999", "Kurti - -", "Kurti - 1e9", "Kurti - NaN", "Kurti - Infinity",
        "Ayesha 2 suit, 03001234567, advance 500", "order 1 advance -500", "order 0 paid 0", "cancel -1", "mark 0 shipped",
        "delivery -200", "free delivery free delivery", "stock -5", "stock Kurti -99999999999", "expense -100 food",
        "create discount: %%% 150%", "create discount: ' OR 1=1 --", "<script>alert(1)</script>", "=SUM(A1)", "@cmd|' /C calc'!A0",
        "customer: ", "customer: Sara name ", "Sara ka phone 0300", "Sara ka phone " + new string('1', 300),
        "payout 99999999999 TCS orders 1 2 3 fee -5", "courier fee 0 -1", "walkin Kurti -2, Dupatta - -4",
        "edit order 1", "edit order -1", "edit order 99999999999999", "done", "yes", "no", "cancel", "1", "0", "-1",
        "catalog page -3", "catalog size 99999", "search ", "find \u0000", "barcode ", "scan done", "drawer close -100",
        "receipt design color #zzzzzz", "label size 1x1", "industry ../../etc/passwd", "supplier: ",
    };

    [Fact]
    public void Parser_NeverThrows_OnHostileInput()
    {
        foreach (var input in Hostile.Concat(RandomStrings(seed: 7, count: 400)))
        {
            var ex = Record.Exception(() => CommandParser.TryParse(input));
            Assert.Null(ex);
        }
    }

    [Fact]
    public void OrderEditParser_NeverThrows_OnHostileInput()
    {
        foreach (var input in Hostile.Concat(RandomStrings(seed: 11, count: 200)))
        {
            var ex = Record.Exception(() => CommandParser.TryParseOrderEdit(input, out _));
            Assert.Null(ex);
        }
    }

    [Fact]
    public async Task Engine_SurvivesHostileMessages_AndKeepsMoneyNonNegative()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);

        foreach (var input in Hostile.Concat(RandomStrings(seed: 23, count: 150)))
        {
            // Each message is independent: any failure should be answered and logged, never thrown past the engine.
            await engine.HandleIncomingMessageAsync(Phone, input, default);
        }

        Assert.True(await db.Orders.AllAsync(o => o.Total >= 0 && o.AmountPaid >= 0), "an order ended with a negative total or payment");
        Assert.True(await db.Products.AllAsync(p => p.Price >= 0), "a product ended with a negative price");
    }

    [Fact]
    public async Task Engine_HandlesHostileMessagesInEveryWaitingState()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        var probes = new[] { "Kurti 2, Dupatta", "Ayesha 2 suit, 03001234567, advance 500", "walkin Kurti 1 then yes", "Kurti - 1800, cost 1000, stock 5", "payout 500 TCS orders 1" };

        foreach (var first in probes)
        {
            foreach (var second in Hostile.Take(40))
            {
                await engine.HandleIncomingMessageAsync(Phone, first, default);
                await engine.HandleIncomingMessageAsync(Phone, second, default);
            }
        }

        Assert.True(await db.Orders.AllAsync(o => o.Total >= 0));
    }

    private static IEnumerable<string> RandomStrings(int seed, int count)
    {
        var rnd = new Random(seed);
        var pieces = new[] { "Kurti", "Lawn", "Ayesha", "order", "cancel", "-", ",", "=", "#", "0", "9", "1000000", "03001", "rs", " ", "\n", "‏", "ک", "سوٹ", "yes", "no", "done", ":", "|", ";", "(", ")", "'", "\"", "%", "𝓐" };
        for (var i = 0; i < count; i++)
        {
            var parts = rnd.Next(1, 14);
            var sb = new System.Text.StringBuilder();
            for (var p = 0; p < parts; p++) sb.Append(pieces[rnd.Next(pieces.Length)]);
            yield return sb.ToString();
        }
    }

    private async Task<ConversationEngine> OnboardAsync(AppDbContext db)
    {
        var engine = new ConversationEngine(db, _ai.Object, _sender.Object, _founderAlerts.Object, null, _media.Object);
        foreach (var m in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "Lahore, Clothing, @ayesha.collections", "10 ke qareeb", "Lawn Suit - 3500", "Kurti - 1800", "done" })
            await engine.HandleIncomingMessageAsync(Phone, m, default);
        return engine;
    }
}
