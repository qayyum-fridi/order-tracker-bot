using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Api.Client;
using OrderTrackerBot.Api.Controllers;
using OrderTrackerBot.Application.Auth;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;

namespace OrderTrackerBot.Tests;

public sealed class ClientLoginTests : IDisposable
{
    private const string Phone = "923124891614";
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public ClientLoginTests()
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

    [Theory]
    [InlineData("login")]
    [InlineData("Login")]
    [InlineData("dashboard login")]
    [InlineData("log in")]
    public void Login_words_parse_to_the_client_login_command(string text) =>
        Assert.Equal(CommandKind.ClientLogin, CommandParser.TryParse(text)?.Kind);

    [Theory]
    [InlineData("login kaise karun")]
    [InlineData("I cannot login to my account")]
    public void Sentences_that_mention_login_are_not_the_command(string text) =>
        Assert.NotEqual(CommandKind.ClientLogin, CommandParser.TryParse(text)?.Kind);

    [Theory]
    [InlineData("03124891614", "3124891614")]
    [InlineData("+92 312 489 1614", "3124891614")]
    [InlineData("0092-312-4891614", "3124891614")]
    [InlineData("923124891614", "3124891614")]
    public void Subscriber_digits_match_however_the_number_is_typed(string typed, string expected) =>
        Assert.Equal(expected, ClientLoginCodes.SubscriberDigits(typed));

    [Fact]
    public void Numbers_shorter_than_ten_digits_have_no_subscriber_digits() =>
        Assert.Null(ClientLoginCodes.SubscriberDigits("12345"));

    [Fact]
    public void Codes_are_six_digits_and_bound_to_the_seller()
    {
        Assert.Matches("^[0-9]{6}$", ClientLoginCodes.NewCode());
        Assert.NotEqual(ClientLoginCodes.Hash(1, "123456"), ClientLoginCodes.Hash(2, "123456"));
        Assert.NotEqual(ClientLoginCodes.Hash(1, "123456"), ClientLoginCodes.Hash(1, "123457"));
        Assert.Equal(ClientLoginCodes.Hash(1, "123456"), ClientLoginCodes.Hash(1, "123456"));
    }

    [Fact]
    public async Task Right_code_issues_a_token_replaces_the_old_one_and_clears_the_code()
    {
        var seller = await SellerWithCodeAsync();
        seller.ClientApiTokenHash = ClientTokens.Hash("otc_old_token");
        await _db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await VerifyAsync("03124891614", "123456"));
        var token = (string)ok.Value!.GetType().GetProperty("token")!.GetValue(ok.Value)!;

        Assert.StartsWith(ClientTokens.Prefix, token);
        Assert.Equal(ClientTokens.Hash(token), seller.ClientApiTokenHash);
        Assert.Null(seller.ClientLoginCodeHash);
        Assert.Null(seller.ClientLoginCodeExpiresAt);
    }

    [Fact]
    public async Task A_code_works_only_once()
    {
        await SellerWithCodeAsync();

        Assert.IsType<OkObjectResult>(await VerifyAsync(Phone, "123456"));
        Assert.IsType<UnauthorizedObjectResult>(await VerifyAsync(Phone, "123456"));
    }

    [Fact]
    public async Task Five_wrong_codes_lock_the_code_even_when_the_right_one_follows()
    {
        var seller = await SellerWithCodeAsync();

        for (var i = 0; i < ClientLoginCodes.MaxAttempts; i++)
            Assert.IsType<UnauthorizedObjectResult>(await VerifyAsync(Phone, "000000"));

        Assert.Equal(ClientLoginCodes.MaxAttempts, seller.ClientLoginAttempts);
        Assert.IsType<UnauthorizedObjectResult>(await VerifyAsync(Phone, "123456"));
    }

    [Fact]
    public async Task Expired_codes_are_refused()
    {
        await SellerWithCodeAsync(expiresAt: DateTime.UtcNow.AddSeconds(-1));

        Assert.IsType<UnauthorizedObjectResult>(await VerifyAsync(Phone, "123456"));
    }

    [Fact]
    public async Task Unknown_numbers_sellers_without_a_code_and_disabled_sellers_are_refused()
    {
        _db.Sellers.Add(new Seller { WhatsAppPhoneNumber = Phone });
        _db.Sellers.Add(new Seller { WhatsAppPhoneNumber = "923009999999", Status = SellerStatus.Disabled });
        await _db.SaveChangesAsync();

        Assert.IsType<UnauthorizedObjectResult>(await VerifyAsync(Phone, "123456"));
        Assert.IsType<UnauthorizedObjectResult>(await VerifyAsync("03000000000", "123456"));
        Assert.IsType<UnauthorizedObjectResult>(await VerifyAsync("03009999999", "123456"));
    }

    private async Task<Seller> SellerWithCodeAsync(string code = "123456", DateTime? expiresAt = null)
    {
        var seller = new Seller { WhatsAppPhoneNumber = Phone };
        _db.Sellers.Add(seller);
        await _db.SaveChangesAsync();

        seller.ClientLoginCodeHash = ClientLoginCodes.Hash(seller.Id, code);
        seller.ClientLoginCodeSentAt = DateTime.UtcNow;
        seller.ClientLoginCodeExpiresAt = expiresAt ?? DateTime.UtcNow.AddMinutes(5);
        await _db.SaveChangesAsync();
        return seller;
    }

    private Task<IActionResult> VerifyAsync(string phone, string code) =>
        new ClientAuthController(_db).Verify(new ClientAuthController.VerifyCodeRequest(phone, code), CancellationToken.None);
}
