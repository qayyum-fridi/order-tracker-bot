using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

public class FreeSellerAndMediaLimitTests : IDisposable
{
    private const string First = "923001110001";
    private const string Second = "923001110002";

    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IFounderAlertNotifier> _founder = new();
    private readonly Mock<IAudioTranscriber> _transcriber = new();
    private readonly List<string> _sent = new();

    public FreeSellerAndMediaLimitTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, body, _, _) => _sent.Add(body)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendListMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, body, _, _, _) => _sent.Add(body)).Returns(Task.CompletedTask);
        _transcriber.Setup(t => t.IsConfigured).Returns(true);
    }

    public void Dispose() => _dbFactory.Dispose();

    private ConversationEngine Engine(AppDbContext db, BillingOptions? billing = null, MediaRateLimiter? limiter = null) =>
        new(db, _ai.Object, _sender.Object, _founder.Object, billing, Mock.Of<IWhatsAppMediaClient>(), transcriber: _transcriber.Object, mediaLimiter: limiter);

    private async Task OnboardAsync(ConversationEngine engine, string phone)
    {
        foreach (var m in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "Lahore, Clothing, @ayesha", "10 ke qareeb", "Lawn Suit - 3500", "done" })
            await engine.HandleIncomingMessageAsync(phone, m, default);
    }

    [Fact]
    public async Task The_first_sellers_are_free_with_no_trial_or_plan_messages()
    {
        using var db = _dbFactory.CreateContext();
        var engine = Engine(db, new BillingOptions { FreeSellerLimit = 1 });

        await OnboardAsync(engine, First);
        _sent.Add("--");
        await engine.HandleIncomingMessageAsync(First, "subscribe", default);

        var seller = await db.Sellers.FirstAsync(s => s.WhatsAppPhoneNumber == First);
        Assert.True(seller.OnboardingComplete);
        Assert.Null(seller.TrialEndsAt);
        Assert.DoesNotContain(_sent, m => m.Contains("FREE trial") || m.Contains("Basic - Rs") || m.Contains("Plans:"));
        Assert.Contains(_sent, m => m.Contains("account free hai"));
    }

    [Fact]
    public async Task A_seller_beyond_the_free_limit_still_gets_the_trial()
    {
        using var db = _dbFactory.CreateContext();
        var engine = Engine(db, new BillingOptions { FreeSellerLimit = 1 });

        await OnboardAsync(engine, First);
        await OnboardAsync(engine, Second);

        var second = await db.Sellers.FirstAsync(s => s.WhatsAppPhoneNumber == Second);
        Assert.NotNull(second.TrialEndsAt);
        Assert.Contains(_sent, m => m.Contains("14-din FREE trial"));
    }

    [Fact]
    public async Task A_free_seller_is_never_paused_even_if_a_trial_date_is_in_the_past()
    {
        using var db = _dbFactory.CreateContext();
        var engine = Engine(db, new BillingOptions { FreeSellerLimit = 5 });
        await OnboardAsync(engine, First);
        var seller = await db.Sellers.FirstAsync();
        seller.TrialEndsAt = DateTime.UtcNow.AddDays(-30);
        await db.SaveChangesAsync();
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(First, "orders today", default);

        Assert.DoesNotContain(_sent, m => m.Contains("khatam ho gaya"));
    }

    [Fact]
    public async Task Voice_notes_beyond_the_hourly_limit_are_refused_before_any_paid_call()
    {
        using var db = _dbFactory.CreateContext();
        var engine = Engine(db, new BillingOptions { Enabled = false }, new MediaRateLimiter(new MediaLimitOptions { VoicePerHour = 2 }));
        await OnboardAsync(engine, First);
        _sent.Clear();

        for (var i = 0; i < 3; i++) await engine.HandleAudioMessageAsync(First, $"voice-{i}");

        Assert.Equal(2, _sent.Count(m => m.Contains("Voice message samajh nahi aaya")));
        Assert.Single(_sent, m => m.Contains("2 voice notes ki limit"));
        _transcriber.Verify(t => t.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Screenshots_beyond_the_hourly_limit_are_refused()
    {
        using var db = _dbFactory.CreateContext();
        var engine = Engine(db, new BillingOptions { Enabled = false }, new MediaRateLimiter(new MediaLimitOptions { ImagePerHour = 1 }));
        await OnboardAsync(engine, First);
        _sent.Clear();

        await engine.HandleImageMessageAsync(First, "img-1", null);
        await engine.HandleImageMessageAsync(First, "img-2", null);

        Assert.Single(_sent, m => m.Contains("1 screenshots ki limit"));
        Assert.Single(_sent, m => m.Contains("screenshot abhi parh nahi saka"));
    }

    [Fact]
    public void The_limiter_uses_a_rolling_hour_per_seller_and_zero_means_unlimited()
    {
        var now = new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc);
        var limiter = new MediaRateLimiter(new MediaLimitOptions { VoicePerHour = 2, ImagePerHour = 0 }) { UtcNow = () => now };

        Assert.True(limiter.TryConsume(First, MediaKind.Voice, out _));
        now = now.AddMinutes(20);
        Assert.True(limiter.TryConsume(First, MediaKind.Voice, out _));
        now = now.AddMinutes(10);
        Assert.False(limiter.TryConsume(First, MediaKind.Voice, out var wait));
        Assert.Equal(TimeSpan.FromMinutes(30), wait);                        // the first use ages out at 10:00 + 1h
        Assert.True(limiter.TryConsume(Second, MediaKind.Voice, out _));       // another seller is unaffected
        for (var i = 0; i < 50; i++) Assert.True(limiter.TryConsume(First, MediaKind.Image, out _));

        now = now.AddMinutes(31);
        Assert.True(limiter.TryConsume(First, MediaKind.Voice, out _));
    }
}
