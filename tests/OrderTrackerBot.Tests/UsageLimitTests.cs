using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

public class UsageLimitTests
{
    private const string Phone = "923001110009";
    private static readonly DateTime Start = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Count_cap_refuses_once_the_hour_is_full_for_that_kind_only()
    {
        var limiter = new MediaRateLimiter(new MediaLimitOptions { TextPerHour = 2, VoicePerHour = 5 }) { UtcNow = () => Start };

        Assert.Equal(UsageDecision.Allowed, limiter.Consume(Phone, MediaKind.Text, 0, out _));
        Assert.Equal(UsageDecision.Allowed, limiter.Consume(Phone, MediaKind.Text, 0, out _));
        Assert.Equal(UsageDecision.CountLimit, limiter.Consume(Phone, MediaKind.Text, 0, out var wait));
        Assert.Equal(TimeSpan.FromHours(1), wait);
        Assert.Equal(UsageDecision.Allowed, limiter.Consume(Phone, MediaKind.Voice, 0, out _));
    }

    [Fact]
    public void Budget_refuses_when_the_hours_spend_would_pass_the_cap_even_with_counts_left()
    {
        var options = new MediaLimitOptions { TextPerHour = 0, VoicePerHour = 0, ImagePerHour = 0, VoiceUsd = 0.015m };
        var limiter = new MediaRateLimiter(options) { UtcNow = () => Start };

        Assert.Equal(UsageDecision.Allowed, limiter.Consume(Phone, MediaKind.Voice, 0.03m, out _));
        Assert.Equal(UsageDecision.Allowed, limiter.Consume(Phone, MediaKind.Voice, 0.03m, out _));
        Assert.Equal(UsageDecision.BudgetLimit, limiter.Consume(Phone, MediaKind.Voice, 0.03m, out _));
    }

    [Fact]
    public void Budget_is_shared_across_text_voice_and_images()
    {
        var options = new MediaLimitOptions { TextPerHour = 0, VoicePerHour = 0, ImagePerHour = 0, TextUsd = 0.4m, VoiceUsd = 0.5m };
        var limiter = new MediaRateLimiter(options) { UtcNow = () => Start };

        Assert.Equal(UsageDecision.Allowed, limiter.Consume(Phone, MediaKind.Text, 1.0m, out _));   // 0.4
        Assert.Equal(UsageDecision.Allowed, limiter.Consume(Phone, MediaKind.Voice, 1.0m, out _));  // 0.9
        Assert.Equal(UsageDecision.BudgetLimit, limiter.Consume(Phone, MediaKind.Text, 1.0m, out _)); // would be 1.3
    }

    [Fact]
    public void Zero_budget_means_no_budget_check()
    {
        var options = new MediaLimitOptions { TextPerHour = 0, VoicePerHour = 0, VoiceUsd = 100m };
        var limiter = new MediaRateLimiter(options) { UtcNow = () => Start };

        Assert.Equal(UsageDecision.Allowed, limiter.Consume(Phone, MediaKind.Voice, 0, out _));
        Assert.Equal(UsageDecision.Allowed, limiter.Consume(Phone, MediaKind.Voice, 0, out _));
    }

    [Fact]
    public void Uses_expire_after_an_hour()
    {
        var now = Start;
        var limiter = new MediaRateLimiter(new MediaLimitOptions { TextPerHour = 1 }) { UtcNow = () => now };

        Assert.Equal(UsageDecision.Allowed, limiter.Consume(Phone, MediaKind.Text, 0, out _));
        Assert.Equal(UsageDecision.CountLimit, limiter.Consume(Phone, MediaKind.Text, 0, out _));

        now = Start.AddMinutes(61);
        Assert.Equal(UsageDecision.Allowed, limiter.Consume(Phone, MediaKind.Text, 0, out _));
    }

    [Fact]
    public void Free_and_paid_budgets_default_to_one_and_five_dollars_an_hour()
    {
        var options = new MediaLimitOptions();

        Assert.Equal(1.00m, options.FreeUsdPerHour);
        Assert.Equal(5.00m, options.PaidUsdPerHour);
    }
}
