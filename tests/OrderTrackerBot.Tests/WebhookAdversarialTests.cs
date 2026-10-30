using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OrderTrackerBot.Api.Admin;
using OrderTrackerBot.Api.Controllers;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.Instagram;
using OrderTrackerBot.Infrastructure.WhatsApp;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>
/// Hostile HTTP input at the webhook and admin edges: malformed JSON, bad or missing signatures, oversized text, odd Unicode,
/// and out-of-range paging. Called directly on the controllers (no server, no Meta, no OpenAI).
/// </summary>
public class WebhookAdversarialTests : IDisposable
{
    private const string Secret = "test-app-secret";
    private const string VerifyToken = "verify-me";
    private const string Phone = "923001234567";

    private readonly TestDbContextFactory _dbFactory = new();
    private readonly ServiceProvider _services;
    private readonly WebhookInbox _inbox;

    public WebhookAdversarialTests()
    {
        var services = new ServiceCollection();
        services.AddScoped<IAppDbContext>(_ => _dbFactory.CreateContext());
        _services = services.BuildServiceProvider();
        _inbox = new WebhookInbox(_services.GetRequiredService<IServiceScopeFactory>(), new WebhookWorkQueue());
    }

    public void Dispose()
    {
        _services.Dispose();
        _dbFactory.Dispose();
    }

    private WhatsAppWebhookController WhatsApp(string secret = Secret, bool development = false)
    {
        var controller = new WhatsAppWebhookController(
            Options.Create(new WhatsAppOptions { AppSecret = secret, VerifyToken = VerifyToken }),
            NullLogger<WhatsAppWebhookController>.Instance, _inbox, Environment(development));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static IHostEnvironment Environment(bool development)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(development ? "Development" : "Production");
        return environment.Object;
    }

    private static async Task<IActionResult> PostAsync(WhatsAppWebhookController controller, byte[] body, string? signature)
    {
        controller.HttpContext.Request.Body = new MemoryStream(body);
        if (signature is not null) controller.HttpContext.Request.Headers["X-Hub-Signature-256"] = signature;
        return await controller.Receive(CancellationToken.None);
    }

    private static string Sign(string secret, byte[] body) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();

    private static byte[] TextPayload(string text, string id = "wamid.1") => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        entry = new[] { new { changes = new[] { new { value = new { messages = new[] { new { id, from = Phone, type = "text", text = new { body = text } } } } } } } }
    }));

    private async Task<List<string>> PendingIdsAsync()
    {
        using var db = _dbFactory.CreateContext();
        return await db.PendingWebhookMessages.OrderBy(m => m.CreatedAt).Select(m => m.MessageId).ToListAsync();
    }

    [Fact]
    public async Task Malformed_json_with_a_valid_signature_is_a_400_not_an_exception()
    {
        var body = Encoding.UTF8.GetBytes("{\"entry\":[{\"changes\":");
        var result = await PostAsync(WhatsApp(), body, Sign(Secret, body));
        Assert.IsType<BadRequestResult>(result);
    }

    [Fact]
    public async Task Type_mismatched_json_with_a_valid_signature_is_a_400_not_an_exception()
    {
        var body = Encoding.UTF8.GetBytes("{\"entry\":5}");
        var result = await PostAsync(WhatsApp(), body, Sign(Secret, body));
        Assert.IsType<BadRequestResult>(result);
    }

    [Fact]
    public async Task Missing_or_wrong_signature_is_rejected_and_nothing_is_stored()
    {
        var body = TextPayload("Kurti 2");
        Assert.IsType<UnauthorizedResult>(await PostAsync(WhatsApp(), body, null));
        Assert.IsType<UnauthorizedResult>(await PostAsync(WhatsApp(), body, "sha256=00"));
        Assert.IsType<UnauthorizedResult>(await PostAsync(WhatsApp(), body, Sign("another-secret", body)));
        Assert.Empty(await PendingIdsAsync());
    }

    [Fact]
    public async Task A_valid_signature_over_bytes_that_are_not_utf8_is_accepted()
    {
        // Meta signs the raw bytes. The controller must hash those bytes, not a re-encoded copy: 0xFF is not valid UTF-8,
        // and decoding then re-encoding it changes the bytes, so the signature check would wrongly fail.
        var body = Encoding.UTF8.GetBytes("{\"entry\":[],\"x\":\"").Concat(new byte[] { 0xFF }).Concat(Encoding.UTF8.GetBytes("\"}")).ToArray();
        var result = await PostAsync(WhatsApp(), body, Sign(Secret, body));
        Assert.IsNotType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task Text_longer_than_whatsapps_4096_limit_is_not_queued()
    {
        // Meta can never deliver more than 4096 characters, so a longer text is not genuine. It must not reach the queue,
        // the AI call or the database row.
        var body = TextPayload(new string('a', 5000));
        await PostAsync(WhatsApp(), body, Sign(Secret, body));
        Assert.Empty(await PendingIdsAsync());
    }

    [Fact]
    public async Task Odd_unicode_text_is_queued_as_is()
    {
        var body = TextPayload("‮Kurti​ ۱۲۳ 😀 ک");
        Assert.IsType<OkResult>(await PostAsync(WhatsApp(), body, Sign(Secret, body)));
        Assert.Equal(new[] { "wamid.1" }, await PendingIdsAsync());
    }

    [Fact]
    public async Task Missing_app_secret_refuses_unsigned_posts_outside_development()
    {
        // With no AppSecret configured, production must not accept unsigned posts (it would take fake orders for any phone).
        var body = TextPayload("Kurti 2");
        Assert.IsType<UnauthorizedResult>(await PostAsync(WhatsApp(secret: ""), body, null));
        Assert.Empty(await PendingIdsAsync());
    }

    [Fact]
    public async Task Missing_app_secret_still_accepts_unsigned_posts_in_development()
    {
        // The local curl loop in CLAUDE.md posts without a signature; that stays open in Development only.
        var body = TextPayload("Kurti 2");
        Assert.IsType<OkResult>(await PostAsync(WhatsApp(secret: "", development: true), body, null));
    }

    [Fact]
    public void Verify_with_an_empty_configured_token_and_an_empty_token_is_refused()
    {
        var controller = new WhatsAppWebhookController(
            Options.Create(new WhatsAppOptions { AppSecret = Secret, VerifyToken = "" }),
            NullLogger<WhatsAppWebhookController>.Instance, _inbox, Environment(development: false));
        var result = controller.Verify("subscribe", "", "12345");
        Assert.IsNotType<ContentResult>(result);
    }

    [Fact]
    public void Verify_with_the_right_token_echoes_the_challenge()
    {
        var result = WhatsApp().Verify("subscribe", VerifyToken, "12345");
        Assert.Equal("12345", Assert.IsType<ContentResult>(result).Content);
    }

    [Fact]
    public void Instagram_payload_with_a_string_entry_yields_no_comments_instead_of_throwing()
    {
        var json = "{\"object\":\"instagram\",\"entry\":[\"x\"]}";
        Assert.Empty(InstagramWebhookPayload.ExtractComments(json));
    }

    [Fact]
    public void Instagram_payload_with_a_numeric_field_yields_no_comments_instead_of_throwing()
    {
        var json = "{\"object\":\"instagram\",\"entry\":[{\"id\":\"17841\",\"changes\":[{\"field\":5,\"value\":{}}]}]}";
        Assert.Empty(InstagramWebhookPayload.ExtractComments(json));
    }

    [Fact]
    public void Instagram_comment_whose_text_is_an_object_is_skipped_instead_of_throwing()
    {
        var json = "{\"object\":\"instagram\",\"entry\":[{\"id\":\"17841\",\"changes\":[{\"field\":\"comments\",\"value\":{\"id\":\"c1\",\"text\":{\"a\":1}}}]}]}";
        Assert.Empty(InstagramWebhookPayload.ExtractComments(json));
    }

    [Fact]
    public async Task Admin_page_number_that_overflows_the_skip_returns_an_empty_page()
    {
        // (page - 1) * pageSize overflows int for this page and goes negative; a negative offset must not return rows.
        using var db = _dbFactory.CreateContext();
        db.Sellers.Add(new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections" });
        await db.SaveChangesAsync();

        var controller = new AdminReportsController(db);
        var result = await controller.Setup(status: null, page: 85_899_348, pageSize: 25);

        var page = Assert.IsType<PagedResultDto<SellerSetupDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task Admin_seller_list_with_an_overflowing_page_returns_an_empty_page()
    {
        using var db = _dbFactory.CreateContext();
        db.Sellers.Add(new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections" });
        await db.SaveChangesAsync();

        var controller = new AdminController(db, NullLogger<AdminController>.Instance, new BillingOptions(), Microsoft.Extensions.Options.Options.Create(new FeatureOptions()));
        var result = await controller.ListSellers(search: null, page: 85_899_348, pageSize: 25);

        var page = Assert.IsType<PagedResultDto<SellerListItemDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Empty(page.Items);
    }

    [Fact]
    public void Admin_api_key_filter_fails_closed_when_no_key_is_configured()
    {
        var filter = new AdminApiKeyFilter(Options.Create(new AdminOptions { ApiKey = "" }));
        var context = AuthorizationContext("anything");
        filter.OnAuthorizationAsync(context).Wait();
        Assert.Equal(503, Assert.IsType<ObjectResult>(context.Result).StatusCode);
    }

    [Fact]
    public void Admin_api_key_filter_rejects_a_wrong_key_and_accepts_the_right_one()
    {
        var filter = new AdminApiKeyFilter(Options.Create(new AdminOptions { ApiKey = "k-123" }));

        var wrong = AuthorizationContext("k-124");
        filter.OnAuthorizationAsync(wrong).Wait();
        Assert.IsType<UnauthorizedObjectResult>(wrong.Result);

        var right = AuthorizationContext("k-123");
        filter.OnAuthorizationAsync(right).Wait();
        Assert.Null(right.Result);
    }

    private static AuthorizationFilterContext AuthorizationContext(string key)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers[AdminApiKeyFilter.HeaderName] = key;
        var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
        return new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());
    }
}
