using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Infrastructure.Alerts;
using Xunit;

namespace OrderTrackerBot.Tests;

public class NewSellerNotifierTests
{
    [Fact]
    public async Task Only_the_first_message_from_a_new_number_registers_a_seller()
    {
        using var dbFactory = new TestDbContextFactory();
        using var db = dbFactory.CreateContext();
        var notifier = new Mock<INewSellerNotifier>();
        var engine = new ConversationEngine(db, Mock.Of<IAiOrderAssistant>(), Mock.Of<IWhatsAppSender>(), Mock.Of<IFounderAlertNotifier>(),
            newSellers: notifier.Object);

        await engine.HandleIncomingMessageAsync("923001110001", "start", default);
        await engine.HandleIncomingMessageAsync("923001110001", "hello again", default);
        await engine.HandleIncomingMessageAsync("923001110002", "start", default);

        notifier.Verify(n => n.SellerRegisteredAsync("923001110001", 1, It.IsAny<CancellationToken>()), Times.Once);
        notifier.Verify(n => n.SellerRegisteredAsync("923001110002", 2, It.IsAny<CancellationToken>()), Times.Once);
        notifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task The_email_carries_the_phone_time_and_total()
    {
        var mailer = new Mock<IErrorMailer>();
        var notifier = new NewSellerEmailNotifier(mailer.Object, Options.Create(new ErrorLogOptions { TimeZoneId = "UTC" }),
            NullLogger<NewSellerEmailNotifier>.Instance);

        await notifier.SendAsync("923001110001", 7, new DateTime(2026, 10, 9, 10, 30, 0, DateTimeKind.Utc), default);

        mailer.Verify(m => m.SendAsync(
            It.Is<string>(s => s == "[Order Tracker] New seller: 923001110001"),
            It.Is<string>(b => b.Contains("923001110001") && b.Contains("2026-10-09 10:30:00") && b.Contains("Sellers in total: 7")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_mail_failure_never_throws()
    {
        var mailer = new Mock<IErrorMailer>();
        mailer.Setup(m => m.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("SMTP down"));
        var notifier = new NewSellerEmailNotifier(mailer.Object, Options.Create(new ErrorLogOptions()), NullLogger<NewSellerEmailNotifier>.Instance);

        await notifier.SendAsync("923001110001", 1, DateTime.UtcNow, default);
    }
}
