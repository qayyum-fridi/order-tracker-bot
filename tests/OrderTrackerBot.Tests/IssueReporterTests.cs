using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.Alerts;
using OrderTrackerBot.Infrastructure.Persistence;
using OrderTrackerBot.Infrastructure.WhatsApp;
using Xunit;

namespace OrderTrackerBot.Tests;

public class IssueReporterTests : IDisposable
{
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly CapturingHandler _handler = new();
    private readonly ServiceProvider _services;

    public IssueReporterTests()
    {
        var services = new ServiceCollection();
        services.AddScoped<IAppDbContext>(_ => _dbFactory.CreateContext());
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _dbFactory.Dispose();
    }

    private IssueReporter CreateReporter(string? webhookUrl = "http://alerts.test/hook") =>
        new(new HttpClient(_handler), Options.Create(new FounderAlertOptions { WebhookUrl = webhookUrl }),
            _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<IssueReporter>.Instance);

    [Fact]
    public async Task Report_PostsCodeSellerAndError_ToWebhook()
    {
        using (var db = _dbFactory.CreateContext())
        {
            db.Sellers.Add(new Seller { WhatsAppPhoneNumber = "923001110001", BusinessName = "Ali Store" });
            await db.SaveChangesAsync();
        }

        await CreateReporter().ReportAsync(IssueCodes.InboundMessageFailed, "923001110001", null, new InvalidOperationException("boom"));

        var body = Assert.Single(_handler.Bodies);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal("OTB-1001", root.GetProperty("code").GetString());
        Assert.Equal("923001110001", root.GetProperty("seller_phone").GetString());
        Assert.Equal("Ali Store", root.GetProperty("business_name").GetString());
        Assert.True(root.GetProperty("seller_id").GetInt32() > 0);
        var message = root.GetProperty("message").GetString()!;
        Assert.Contains("[OTB-1001]", message);
        Assert.Contains("Ali Store", message);
        Assert.Contains("InvalidOperationException: boom", message);
    }

    [Fact]
    public async Task Report_SameCodeAndSellerWithinCooldown_PostsOnce()
    {
        var reporter = CreateReporter();
        await reporter.ReportAsync(IssueCodes.ScreenshotFailed, "923001110002");
        await reporter.ReportAsync(IssueCodes.ScreenshotFailed, "923001110002");
        await reporter.ReportAsync(IssueCodes.ScreenshotFailed, "923001110003");

        Assert.Equal(2, _handler.Bodies.Count);
    }

    [Fact]
    public async Task Report_WithoutWebhookUrl_DoesNotPostOrThrow()
    {
        await CreateReporter(webhookUrl: null).ReportAsync(IssueCodes.ScheduledJobFailed, null, "x", new Exception("y"));
        Assert.Empty(_handler.Bodies);
    }

    [Fact]
    public async Task Report_WhenWebhookFails_DoesNotThrow()
    {
        _handler.Throw = true;
        await CreateReporter().ReportAsync(IssueCodes.OpenAiAnalysisFailed, null, "Seller: X");
    }

    [Fact]
    public void IssueCodes_AreUnique()
    {
        var codes = typeof(IssueCodes).GetFields().Select(f => ((IssueCode)f.GetValue(null)!).Code).ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches(@"^OTB-\d{4}$", c));
    }

    [Fact]
    public async Task WhatsAppSender_Rejected401_ReportsTokenRejected_And500ReportsRejected()
    {
        var issues = new Mock<IIssueReporter>();
        var handler = new CapturingHandler { Status = HttpStatusCode.Unauthorized, Response = "{\"error\":\"bad token\"}" };
        var sender = new WhatsAppSender(new HttpClient(handler), Options.Create(new WhatsAppOptions { AccessToken = "t", PhoneNumberId = "1" }),
            Mock.Of<IOptionsMonitor<WhatsAppTemplatesOptions>>(), NullLogger<WhatsAppSender>.Instance, issues.Object);

        await sender.SendTextMessageAsync("923001110004", "hi");
        issues.Verify(i => i.ReportAsync(IssueCodes.WhatsAppTokenRejected, "923001110004", It.Is<string>(d => d.Contains("401")), null, It.IsAny<CancellationToken>()), Times.Once);

        handler.Status = HttpStatusCode.BadRequest;
        await sender.SendTextMessageAsync("923001110004", "hi");
        issues.Verify(i => i.ReportAsync(IssueCodes.WhatsAppSendRejected, "923001110004", It.IsAny<string>(), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();
        public bool Throw { get; set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Response { get; set; } = "{}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Throw) throw new HttpRequestException("down");
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(Status) { Content = new StringContent(Response) };
        }
    }
}
