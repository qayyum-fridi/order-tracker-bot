using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

// The top report questions asked by voice ("aaj kitna bika", "kis ka paisa baqi", "courier ke paas kitna") are answered aloud in one sentence
// with the numbers, not with the generic "first line + the rest is in the text" summary of the long typed report.
public class SpokenReportAnswersTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly List<string> _sent = new();
    private readonly List<string> _spoken = new();
    private readonly Dictionary<string, TurnAction> _model = new();
    private string _transcript = "";
    private int _notes;

    public void Dispose() => _dbFactory.Dispose();

    private async Task<(AppDbContext Db, Func<string, Task> Type, Func<string, Task> Speak)> ShopAsync()
    {
        var db = _dbFactory.CreateContext();
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collection", BusinessType = "Clothing", OnboardingComplete = true,
            Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        var sara = new Customer { SellerId = seller.Id, Name = "Sara Khan", Phone = "03004445566" };
        var bilal = new Customer { SellerId = seller.Id, Name = "Bilal Ahmed", Phone = "03215557788" };
        db.Customers.AddRange(sara, bilal);
        await db.SaveChangesAsync();
        // Today: Sara's 5000 COD parcel was delivered by TCS (TCS has not paid yet); Bilal's 3000 order is still pending. Nothing is paid.
        var delivered = new Order { SellerId = seller.Id, CustomerId = sara.Id, Status = OrderStatus.Delivered, PaymentMethod = OrderPaymentMethod.Cod,
            TrackingCourier = "TCS", Subtotal = 5000, Total = 5000, CreatedAt = DateTime.UtcNow.AddMinutes(-30), DeliveredAt = DateTime.UtcNow.AddMinutes(-10) };
        var pending = new Order { SellerId = seller.Id, CustomerId = bilal.Id, PaymentMethod = OrderPaymentMethod.Cod,
            Subtotal = 3000, Total = 3000, CreatedAt = DateTime.UtcNow.AddMinutes(-20) };
        foreach (var o in new[] { delivered, pending }) o.Items.Add(new OrderItem { ProductNameSnapshot = "Lawn Suit", UnitPrice = o.Total, Quantity = 1 });
        db.Orders.AddRange(delivered, pending);
        await db.SaveChangesAsync();

        var sender = new Mock<IWhatsAppSender>();
        sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _sent.Add(t)).Returns(Task.CompletedTask);
        sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, b, _, _) => _sent.Add(b)).Returns(Task.CompletedTask);
        sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, body, _, _, _) => _sent.Add(body)).Returns(Task.CompletedTask);
        sender.Setup(s => s.SendVoiceNoteAsync(Phone, It.IsAny<byte[]>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var older = new Mock<IAiOrderAssistant>();
        older.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true });
        var turn = new Mock<ITurnInterpreter>();
        turn.Setup(t => t.UnderstandAsync(It.IsAny<TurnContext>(), It.IsAny<string>(), It.IsAny<AiImageInput?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TurnContext _, string text, AiImageInput? _, CancellationToken _) => _model.GetValueOrDefault(text));
        var media = new Mock<IWhatsAppMediaClient>();
        media.Setup(m => m.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ((byte[] Bytes, string MimeType)?)(System.Text.Encoding.UTF8.GetBytes($"note-{++_notes}" + new string('.', 4096)), "audio/ogg"));
        var transcriber = new Mock<IAudioTranscriber>();
        transcriber.Setup(t => t.IsConfigured).Returns(true);
        transcriber.Setup(t => t.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _transcript);
        var speech = new Mock<ISpeechSynthesizer>();
        speech.Setup(s => s.IsConfigured).Returns(true);
        speech.Setup(s => s.SynthesizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((t, _) => _spoken.Add(t)).ReturnsAsync(new byte[] { 1, 2, 3 });
        var features = new FeatureOptions { ModelFirst = "on", MvpOnly = true, Payouts = true, SpokenReplies = true, ShortcutButtons = false, SuggestionButtons = false };

        ConversationEngine Engine() => new(db, older.Object, sender.Object, Mock.Of<IFounderAlertNotifier>(), media: media.Object, features: features,
            transcriber: transcriber.Object, turn: turn.Object, speech: speech.Object);
        return (db,
            async text => await Engine().HandleIncomingMessageAsync(Phone, text, default),
            async transcript => { _transcript = transcript; await Engine().HandleAudioMessageAsync(Phone, $"media-{_notes}", $"wamid-{_notes}-{transcript.Length}"); });
    }

    [Fact]
    public async Task TodaysSales_AskedByVoice_IsHeardAsTheOrderCountAndTheSales()
    {
        var (db, _, speak) = await ShopAsync();
        using var _ = db;
        _model["aaj kitna bika"] = new TurnAction { Action = TurnAction.ShowSummary, Period = "today" };

        await speak("aaj kitna bika");

        Assert.Equal(MessageTemplates.Get("spoken.summary.today", 2, "Rs.8,000"), Assert.Single(_spoken));
    }

    [Fact]
    public async Task WhoOwesMoney_AskedByVoice_IsHeardAsTheTotalAndTheBiggestDebtor()
    {
        var (db, _, speak) = await ShopAsync();
        using var _ = db;

        await speak("unpaid orders");

        Assert.Equal(MessageTemplates.Get("spoken.unpaid", 2, "Rs.8,000", "Sara Khan", "Rs.5,000"), Assert.Single(_spoken));
    }

    [Fact]
    public async Task WhatCouriersHold_AskedByVoice_NamesTheTotalAndTheCourier()
    {
        var (db, _, speak) = await ShopAsync();
        using var _ = db;
        _model["courier ke paas kitna paisa hai"] = new TurnAction { Action = TurnAction.CodPending };

        await speak("courier ke paas kitna paisa hai");

        var spoken = Assert.Single(_spoken);
        Assert.StartsWith("Courier walon ke paas", spoken);
        Assert.Contains("TCS", spoken);
    }

    [Fact]
    public async Task TheSameReport_Typed_GetsNoVoiceNote()
    {
        var (db, type, _) = await ShopAsync();
        using var _ = db;

        await type("unpaid orders");

        Assert.Contains(_sent, m => m.Contains("Sara Khan"));
        Assert.Empty(_spoken);
    }
}
