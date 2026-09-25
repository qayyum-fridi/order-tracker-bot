using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Instagram;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>Mockup v4 screens 5d-5..5d-10 (support queries, Instagram comment leads) plus the product/discount reports.</summary>
public class SupportAndInstagramTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly Mock<IInstagramClient> _instagram = new();
    private readonly List<string> _sent = new();
    private readonly List<(string Body, IReadOnlyList<string> Buttons)> _buttons = new();

    public SupportAndInstagramTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text))
            .Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, body, buttons, _) => { _buttons.Add((body, buttons)); _sent.Add(body); })
            .Returns(Task.CompletedTask);
        _instagram.SetupGet(i => i.IsConfigured).Returns(true);
        _instagram.Setup(i => i.BuildConnectLink(It.IsAny<int>())).Returns("https://bot.test/instagram/connect/abc");
    }

    public void Dispose() => _dbFactory.Dispose();

    private ConversationEngine Engine(AppDbContext db) =>
        new(db, _ai.Object, _sender.Object, _founderAlerts.Object, instagram: _instagram.Object);

    private async Task<ConversationEngine> OnboardAsync(AppDbContext db)
    {
        var engine = Engine(db);
        foreach (var m in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "10", "Lawn Suit - 3500", "Kurti - 1800", "done" })
            await engine.HandleIncomingMessageAsync(Phone, m, default);
        _sent.Clear();
        _buttons.Clear();
        return engine;
    }

    private void AiReturnsOrder(string name, string product, string phone)
    {
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis
            {
                Intent = "new_order",
                IsOrderAttempt = true,
                Order = new AiOrderDraft
                {
                    CustomerName = name, Phone = phone,
                    Items = { new AiOrderItemDraft { ProductName = product, MatchedCatalogProductName = product, Quantity = 1 } }
                }
            });
    }

    private async Task<Order> SaveOrderAsync(ConversationEngine engine, AppDbContext db, string name, string product)
    {
        AiReturnsOrder(name, product, "03001112222");
        await engine.HandleIncomingMessageAsync(Phone, $"{name}, 1 {product}, 03001112222", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        _sent.Clear();
        _buttons.Clear();
        return await db.Orders.Include(o => o.Customer).OrderByDescending(o => o.Id).FirstAsync();
    }

    private static async Task ConnectInstagramAsync(AppDbContext db)
    {
        var seller = await db.Sellers.FirstAsync();
        db.InstagramConnections.Add(new InstagramConnection { SellerId = seller.Id, IgUserId = "ig-1", Username = "ayesha.collections", AccessToken = "tok" });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ForwardedQuery_UsesOrderAndTracking_AndYesGivesAForwardableReply()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        var order = await SaveOrderAsync(engine, db, "Bilal", "Kurti");
        order.Status = OrderStatus.Shipped;
        order.TrackingCourier = "Leopards";
        order.TrackingNumber = "LC998877";
        await db.SaveChangesAsync();

        await engine.HandleIncomingMessageAsync(Phone, "mera order kab tak aayega? — Bilal ne poocha", default);

        var prompt = Assert.Single(_buttons);
        Assert.Contains($"Bilal (Order #{order.Id}, Kurti, SHIPPED, tracking: Leopards LC998877)", prompt.Body);
        Assert.Contains("Tracking: Leopards LC998877", prompt.Body);
        Assert.Equal(new[] { "Yes", "Edit" }, prompt.Buttons);

        await engine.HandleIncomingMessageAsync(Phone, "Yes", default);

        Assert.Contains(_sent, m => m.Contains("Reply save ho gaya — Bilal ko forward kar dein"));
        Assert.StartsWith("Aapka order raste mein hai", _sent[^1]);
        var query = await db.SupportQueries.SingleAsync();
        Assert.Equal("replied", query.Status);
        Assert.Equal(order.Id, query.LinkedOrderId);
    }

    [Fact]
    public async Task ForwardedQuery_Edit_SavesTheSellersOwnText()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "Ayesha ne poocha: Karachi bhejte hain?", default);
        await engine.HandleIncomingMessageAsync(Phone, "Edit", default);
        await engine.HandleIncomingMessageAsync(Phone, "Ji haan, Karachi bhi deliver karte hain — COD available hai.", default);

        var query = await db.SupportQueries.SingleAsync();
        Assert.Equal("Ji haan, Karachi bhi deliver karte hain — COD available hai.", query.SuggestedReply);
        Assert.Equal("replied", query.Status);
        Assert.Equal("Ji haan, Karachi bhi deliver karte hain — COD available hai.", _sent[^1]);
    }

    [Fact]
    public async Task SupportQueries_ListsOpenOnes_NumberReopensDraft_AndMarkResolvedClosesIt()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "order kab aayega — Bilal ne poocha", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        await engine.HandleIncomingMessageAsync(Phone, "query: Ayesha, Karachi bhejte hain?", default);
        await engine.HandleIncomingMessageAsync(Phone, "baad mein", default);
        _sent.Clear();
        _buttons.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "support queries", default);
        var list = _sent[^1];
        Assert.True(list.Contains("Open Queries (2)"), list);
        Assert.True(list.Contains("1. Bilal - \"order kab aayega\" - REPLIED"), list);
        Assert.True(list.Contains("2. Ayesha - \"Karachi bhejte hain?\" - OPEN"), list);

        await engine.HandleIncomingMessageAsync(Phone, "2", default);
        Assert.Contains("Karachi bhejte hain?", _buttons.Single().Body);
        await engine.HandleIncomingMessageAsync(Phone, "mark 2 resolved", default);

        Assert.Equal("resolved", (await db.SupportQueries.SingleAsync(q => q.Number == 2)).Status);
        Assert.Contains(_sent, m => m.Contains("Query #2 (Ayesha) RESOLVED"));
    }

    [Fact]
    public async Task AiSupportQueryIntent_IsRoutedToTheSupportFlow()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "support_query", SupportQuery = new AiSupportQuery { CustomerName = "Sara", Question = "size M hai?" } });
        _ai.Setup(a => a.DraftSupportReplyAsync(It.IsAny<string>(), "size M hai?", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Ji, size M available hai!");

        await engine.HandleIncomingMessageAsync(Phone, "sara pooch rahi thi size M hai kya", default);

        Assert.Contains("\"Ji, size M available hai!\"", _buttons.Single().Body);
        Assert.Equal("whatsapp", (await db.SupportQueries.SingleAsync()).Source);
    }

    [Fact]
    public async Task ConnectInstagram_SendsLink_OrExplainsWhenNotConfigured()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "connect instagram", default);
        Assert.Contains("https://bot.test/instagram/connect/abc", _sent[^1]);

        _instagram.SetupGet(i => i.IsConfigured).Returns(false);
        await engine.HandleIncomingMessageAsync(Phone, "connect instagram", default);
        Assert.Contains("screenshot forward", _sent[^1]);
    }

    [Fact]
    public async Task InstagramOrderComment_NotifiesOnce_ThenLeadConvertsWithTheNextOrder()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await ConnectInstagramAsync(db);

        const string comment = "yeh suit kitne ka hai, order karna hai! 03211234567";
        await engine.HandleInstagramCommentAsync("ig-1", "c-100", "u-9", "sana.k", comment, "m-1");
        await engine.HandleInstagramCommentAsync("ig-1", "c-100", "u-9", "sana.k", comment, "m-1"); // redelivery
        await engine.HandleInstagramCommentAsync("ig-1", "c-101", "ig-1", "ayesha.collections", "Shukriya!", "m-1"); // own reply

        Assert.Single(_sent);
        Assert.Contains("Naya comment mila (@sana.k)", _sent[0]);
        Assert.Contains("order interest lagta hai", _sent[0]);
        Assert.Equal(1, await db.CommentLeads.CountAsync());

        await engine.HandleIncomingMessageAsync(Phone, "comment leads", default);
        Assert.Contains("1. @sana.k - \"yeh suit kitne ka hai, order…\" - NEW", _sent[^1]);

        await engine.HandleIncomingMessageAsync(Phone, "lead 1 converted", default);
        AiReturnsOrder("Sana", "Lawn Suit", "03211234567");
        await engine.HandleIncomingMessageAsync(Phone, "Sana, 1 Lawn Suit, 03211234567", default);
        Assert.Contains("Source: Instagram comment lead #1", _sent[^1]);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.Contains("Lead #1 marked CONVERTED", _sent[^1]);
        var lead = await db.CommentLeads.SingleAsync();
        var order = await db.Orders.SingleAsync();
        Assert.Equal("converted_to_order", lead.Status);
        Assert.Equal(order.Id, lead.OrderId);
        Assert.Equal("instagram", order.OrderSource);
    }

    [Fact]
    public async Task InstagramQuestionComment_ReplyButtonPostsThePublicReply()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await ConnectInstagramAsync(db);
        _ai.Setup(a => a.ClassifyCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiCommentClassification { Intent = "support_query", SuggestedReply = "Ji haan, hum Karachi bhi deliver karte hain — COD available hai." });
        _instagram.Setup(i => i.ReplyToCommentAsync(It.IsAny<InstagramConnection>(), "c-200", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await engine.HandleInstagramCommentAsync("ig-1", "c-200", "u-7", "zara.b", "aap Karachi bhejte hain?", "m-2");
        var prompt = _buttons.Single();
        Assert.Equal(new[] { "Reply bhejein", "Edit karein" }, prompt.Buttons);

        await engine.HandleIncomingMessageAsync(Phone, "Reply bhejein", default);

        _instagram.Verify(i => i.ReplyToCommentAsync(It.IsAny<InstagramConnection>(), "c-200",
            "Ji haan, hum Karachi bhi deliver karte hain — COD available hai.", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("Comment ka reply bhej diya gaya (IG par)", _sent[^1]);
        Assert.Equal("followed_up", (await db.CommentLeads.SingleAsync()).Status);
        Assert.Equal("replied", (await db.SupportQueries.SingleAsync()).Status);
    }

    [Fact]
    public async Task InstagramQuestionComment_WhileSellerIsMidFlow_DoesNotHijackTheChat()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await ConnectInstagramAsync(db);
        AiReturnsOrder("Bilal", "Kurti", "03001112222");
        await engine.HandleIncomingMessageAsync(Phone, "Bilal, 1 kurti, 03001112222", default); // now awaiting YES

        await engine.HandleInstagramCommentAsync("ig-1", "c-300", "u-7", "zara.b", "delivery kitne din mein hoti hai?", "m-2");
        Assert.Contains("\"reply 1\" likh kar IG par bhej dein", _sent[^1]);

        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        Assert.Equal(1, await db.Orders.CountAsync());
    }

    [Theory]
    [InlineData("check out my page https://spam.example", "spam")]
    [InlineData("price kya hai?", "order_interest")]
    [InlineData("order karna hai 03211234567", "order_interest")]
    [InlineData("aap Karachi bhejte hain?", "support_query")]
    [InlineData("beautiful 😍", "unclear")]
    public async Task KeywordFallback_ClassifiesCommentsWithoutAi(string text, string expectedIntent)
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await ConnectInstagramAsync(db);

        await engine.HandleInstagramCommentAsync("ig-1", "c-1", "u-1", "someone", text, null);

        Assert.Equal(expectedIntent, (await db.CommentLeads.SingleAsync()).ClassifiedIntent);
    }

    [Fact]
    public async Task ProductReport_And_DiscountPerformance_AreComputedFromOrders()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await SaveOrderAsync(engine, db, "Bilal", "Kurti");
        await SaveOrderAsync(engine, db, "Sara", "Kurti");
        var discounted = await db.Orders.OrderBy(o => o.Id).FirstAsync();
        discounted.DiscountCode = "EID20";
        discounted.DiscountAmount = 360;
        discounted.Total = 1440;
        db.Discounts.Add(new Discount { SellerId = discounted.SellerId, Code = "EID20", Type = DiscountType.Percent, Value = 20 });
        await db.SaveChangesAsync();

        await engine.HandleIncomingMessageAsync(Phone, "kurti ka report", default);
        Assert.Contains("Last 30 din: 2 orders, 2 units, Rs.3,600", _sent[^1]);
        Assert.Contains("Customers: 2", _sent[^1]);

        await engine.HandleIncomingMessageAsync(Phone, "discount performance", default);
        Assert.Contains("• EID20 — 1 orders, sales Rs.1,440, discount diya Rs.360", _sent[^1]);
    }

    [Theory]
    [InlineData("mera order kab tak aayega? — Bilal ne poocha", "Bilal", "mera order kab tak aayega?")]
    [InlineData("mera order kab tak aayega - Bilal ne poocha", "Bilal", "mera order kab tak aayega")]
    [InlineData("mera order kab tak aayega? Bilal ne pucha", "Bilal", "mera order kab tak aayega")]
    [InlineData("Ayesha ne poocha: Karachi bhejte hain?", "Ayesha", "Karachi bhejte hain?")]
    [InlineData("query: Sara Khan, size M hai?", "Sara Khan", "size M hai?")]
    public void Parser_RecognisesForwardedQueries(string message, string name, string question)
    {
        var cmd = CommandParser.TryParse(message);
        Assert.Equal(CommandKind.ForwardedQuery, cmd?.Kind);
        Assert.Equal(name, cmd!.Text);
        Assert.Equal(question, cmd.Text2);
    }

    [Fact]
    public async Task NewOrder_TypedAlone_ShowsHowToWithoutCallingAi()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        string? body = null;
        _sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, b, _, _, _) => body = b)
            .Returns(Task.CompletedTask);

        foreach (var text in new[] { "New order", "naya order", "order add karna hai" })
        {
            body = null;
            await engine.HandleIncomingMessageAsync(Phone, text, default);
            Assert.Contains("Naya order darj karne ke 3 aasaan tareeqay", body);
        }
        _ai.Verify(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ClarificationPrompt_ThenAnActualOrder_IsProcessedNotRejected()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), "wo waala", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear" });
        await engine.HandleIncomingMessageAsync(Phone, "wo waala", default);
        Assert.Contains("Mujhe samajh nahi aaya", _sent[^1]);

        AiReturnsOrder("Bilal", "Kurti", "03001112222");
        await engine.HandleIncomingMessageAsync(Phone, "Bilal, 1 kurti, 03001112222", default);

        Assert.Contains("Confirm order", _sent[^1]);
    }

    [Fact]
    public void Parser_NewCommands()
    {
        Assert.Equal(CommandKind.ResolveSupportQuery, CommandParser.TryParse("mark 2 resolved")?.Kind);
        Assert.Equal(CommandKind.MarkStatus, CommandParser.TryParse("mark 2 shipped")?.Kind);
        Assert.Equal(CommandKind.SupportQueries, CommandParser.TryParse("support queries")?.Kind);
        Assert.Equal(CommandKind.CommentLeads, CommandParser.TryParse("comment leads")?.Kind);
        Assert.Equal(CommandKind.ConnectInstagram, CommandParser.TryParse("connect instagram")?.Kind);
        Assert.Equal(CommandKind.DiscountPerformance, CommandParser.TryParse("discount performance")?.Kind);
        Assert.Equal("Lawn Suit", CommandParser.TryParse("Lawn Suit ka report")?.Text);

        var reply = CommandParser.TryParse("reply 3: Ji haan, available hai")!;
        Assert.Equal((CommandKind.ReplySupportQuery, 3, "Ji haan, available hai"), (reply.Kind, reply.Number!.Value, reply.Text));

        var lead = CommandParser.TryParse("lead 1 converted order 21")!;
        Assert.Equal((CommandKind.LeadAction, 1, "converted", 21m), (lead.Kind, lead.Number!.Value, lead.Text, lead.Amount!.Value));
        Assert.Equal("followed_up", CommandParser.TryParse("lead 2 followed up")?.Text);
    }

    [Fact]
    public void InstagramWebhookPayload_ExtractsComments()
    {
        const string json = """
            {"object":"instagram","entry":[{"id":"17841400000","time":1,"changes":[
              {"field":"comments","value":{"from":{"id":"232323","username":"sana.k"},"media":{"id":"123123","media_product_type":"FEED"},
               "id":"17865799348089039","text":"order karna hai"}},
              {"field":"mentions","value":{"media_id":"1"}}]}]}
            """;

        var comment = Assert.Single(InstagramWebhookPayload.ExtractComments(json));
        Assert.Equal(new InstagramComment("17841400000", "17865799348089039", "232323", "sana.k", "order karna hai", "123123"), comment);
        Assert.Empty(InstagramWebhookPayload.ExtractComments("""{"object":"page","entry":[]}"""));
    }

    [Fact]
    public void InstagramConnectState_IsSignedAndExpires()
    {
        var client = new InstagramClient(new HttpClient(),
            Options.Create(new InstagramOptions { AppId = "1", AppSecret = "secret", PublicBaseUrl = "https://bot.test" }),
            NullLogger<InstagramClient>.Instance);
        var now = DateTime.UtcNow;
        var state = client.CreateState(42, now);

        Assert.True(client.TryReadState(state, now, out var sellerId));
        Assert.Equal(42, sellerId);
        Assert.False(client.TryReadState(state.Replace("42.", "43."), now, out _));
        Assert.False(client.TryReadState(state, now.AddHours(1), out _));
        Assert.StartsWith("https://bot.test/instagram/connect/", client.BuildConnectLink(42));
    }
}
