using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Api.Client;
using OrderTrackerBot.Api.Controllers;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Auth;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>Dashboard session safety: the login rate limit, the sign-in notice on WhatsApp, and logout that revokes the token.</summary>
public sealed class ClientSecurityTests : IDisposable
{
    private const string Phone = "923000000020";
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public ClientSecurityTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void Limiter_allows_ten_checks_per_window_then_refuses_until_the_window_passes()
    {
        var now = new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);
        var limiter = new ClientLoginLimiter { Clock = () => now };

        for (var i = 0; i < ClientLoginLimiter.MaxChecksPerWindow; i++)
            Assert.True(limiter.TryAcquire("3000000020"));
        Assert.False(limiter.TryAcquire("3000000020"));
        Assert.True(limiter.TryAcquire("3111111111"), "another number has its own window");

        now = now.Add(ClientLoginLimiter.Window);
        Assert.True(limiter.TryAcquire("3000000020"));
    }

    [Fact]
    public async Task Verify_refuses_with_429_when_the_phone_is_over_its_limit()
    {
        await SellerWithLoginCodeAsync("123456");
        var limiter = new ClientLoginLimiter();
        for (var i = 0; i < ClientLoginLimiter.MaxChecksPerWindow; i++) limiter.TryAcquire(ClientLoginCodes.SubscriberDigits(Phone)!);

        var result = await new ClientAuthController(_db, limiter).Verify(new(Phone, "123456"), default);

        var refused = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, refused.StatusCode);
        Assert.Null((await _db.Sellers.SingleAsync()).ClientApiTokenHash);
    }

    [Fact]
    public async Task Verify_tells_the_seller_on_whatsapp_that_the_dashboard_was_signed_into()
    {
        var seller = await SellerWithLoginCodeAsync("123456");
        var sender = new Mock<IWhatsAppSender>();
        sender.Setup(s => s.SendTextMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var result = await new ClientAuthController(_db, null, sender.Object).Verify(new(Phone, "123456"), default);

        Assert.IsType<OkObjectResult>(result);
        sender.Verify(
            s => s.SendTextMessageAsync(seller.WhatsAppPhoneNumber, It.Is<string>(t => t.Contains("dashboard logout")), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Verify_still_logs_in_when_the_notice_cannot_be_sent()
    {
        await SellerWithLoginCodeAsync("123456");
        var sender = new Mock<IWhatsAppSender>();
        sender.Setup(s => s.SendTextMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("WhatsApp is down"));

        var result = await new ClientAuthController(_db, null, sender.Object).Verify(new(Phone, "123456"), default);

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull((await _db.Sellers.SingleAsync()).ClientApiTokenHash);
    }

    [Fact]
    public async Task Logout_revokes_the_dashboard_token()
    {
        var seller = await SellerWithLoginCodeAsync(null);
        seller.ClientApiTokenHash = ClientTokens.Hash("otc_live_session");
        await _db.SaveChangesAsync();
        var controller = new ClientAuthController(_db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.HttpContext.Items[ClientAuthFilter.SellerIdItem] = seller.Id;

        var result = await controller.Logout(default);

        Assert.IsType<NoContentResult>(result);
        Assert.Null((await _db.Sellers.SingleAsync()).ClientApiTokenHash);
    }

    [Theory]
    [InlineData("dashboard logout", CommandKind.ClientLogout)]
    [InlineData("logout all", CommandKind.ClientLogout)]
    [InlineData("sign out", CommandKind.ClientLogout)]
    [InlineData("login", CommandKind.ClientLogin)]
    public void Dashboard_logout_is_its_own_command_and_login_is_unchanged(string message, CommandKind kind)
    {
        Assert.Equal(kind, CommandParser.TryParse(message)?.Kind);
    }

    /// <summary>A seller who asked for a login code: the code is stored as its hash, as the bot does.</summary>
    private async Task<Seller> SellerWithLoginCodeAsync(string? code)
    {
        var seller = new Seller
        {
            WhatsAppPhoneNumber = Phone,
            BusinessName = "Shop " + Phone,
            OnboardingComplete = true,
            Status = SellerStatus.Active,
            CreatedAt = DateTime.UtcNow.AddDays(-30),
        };
        _db.Sellers.Add(seller);
        await _db.SaveChangesAsync();
        if (code is not null)
        {
            seller.ClientLoginCodeHash = ClientLoginCodes.Hash(seller.Id, code);
            seller.ClientLoginCodeExpiresAt = DateTime.UtcNow.AddMinutes(5);
            await _db.SaveChangesAsync();
        }
        return seller;
    }
}
