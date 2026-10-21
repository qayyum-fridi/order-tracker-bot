using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Infrastructure.Ai;
using Xunit;

namespace OrderTrackerBot.Tests;

public class OpenAiOrderAssistantTests
{
    private sealed class FakeOpenAi(string content) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            var body = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    private static (OpenAiOrderAssistant Assistant, FakeOpenAi Handler) Create(object analysis)
    {
        var handler = new FakeOpenAi(JsonSerializer.Serialize(analysis));
        var assistant = new OpenAiOrderAssistant(new HttpClient(handler), Options.Create(new OpenAiOptions { ApiKey = "k" }),
            NullLogger<OpenAiOrderAssistant>.Instance, Mock.Of<IIssueReporter>());
        return (assistant, handler);
    }

    private static object Analysis(object? deliveryCharge) => new
    {
        intent = "new_order", is_order_attempt = true,
        orders = new[]
        {
            new
            {
                customer_name = "Sara", phone = "03001234567", address = (string?)null, payment_method = (string?)null,
                discount_code = (string?)null, order_source = (string?)null, delivery_charge = deliveryCharge,
                missing_required_fields = Array.Empty<string>(),
                items = new[] { new { product_name = "Kurti", matched_catalog_product_name = "Kurti", quantity = 1 } }
            }
        }
    };

    private static readonly AiAnalysisContext Context = new() { BusinessName = "Shop", Catalog = new List<AiCatalogItem>() };

    [Fact]
    public async Task ReadsDeliveryCharge_AndAsksForItInTheSchema()
    {
        var (assistant, handler) = Create(Analysis(250));

        var result = await assistant.AnalyzeMessageAsync(Context, "Sara 1 kurti delivery 250");

        Assert.Equal(250m, result.Order!.DeliveryCharge);
        Assert.Contains("\"delivery_charge\"", handler.RequestBody);
    }

    [Fact]
    public async Task MissingOrNullDeliveryCharge_StaysNull()
    {
        var (assistant, _) = Create(Analysis(null));

        var result = await assistant.AnalyzeMessageAsync(Context, "Sara 1 kurti");

        Assert.Null(result.Order!.DeliveryCharge);
    }
}

public class OpenAiRetryTests
{
    private sealed class Sequence(params HttpStatusCode?[] steps) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var step = steps[Math.Min(Calls++, steps.Length - 1)];
            if (step is null) throw new HttpRequestException("connection reset");
            var body = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = "{\"same\": true}" } } } });
            return Task.FromResult(new HttpResponseMessage(step.Value) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    private static (OpenAiOrderAssistant, Sequence) Create(params HttpStatusCode?[] steps)
    {
        var handler = new Sequence(steps);
        return (new OpenAiOrderAssistant(new HttpClient(handler), Options.Create(new OpenAiOptions { ApiKey = "k" }),
            NullLogger<OpenAiOrderAssistant>.Instance, Mock.Of<IIssueReporter>()) { RetryDelay = TimeSpan.Zero }, handler);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ServerError_IsRetriedOnce_AndTheSecondAnswerIsUsed(HttpStatusCode first)
    {
        var (assistant, handler) = Create(first, HttpStatusCode.OK);

        Assert.True(await assistant.ConfirmSamePhraseAsync("aaj ki sale", "aj ki sale dikhao"));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task DroppedConnection_IsRetriedOnce()
    {
        var (assistant, handler) = Create(null, HttpStatusCode.OK);

        Assert.True(await assistant.ConfirmSamePhraseAsync("a", "b"));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task StillFailing_GivesUpAfterTwoTries_AndFallsBack()
    {
        var (assistant, handler) = Create(HttpStatusCode.ServiceUnavailable);

        Assert.Null(await assistant.ConfirmSamePhraseAsync("a", "b"));
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task ClientError_IsNotRetried(HttpStatusCode status)
    {
        var (assistant, handler) = Create(status, HttpStatusCode.OK);

        Assert.Null(await assistant.ConfirmSamePhraseAsync("a", "b"));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task AnalysisThatFailsTwice_StillReturnsTheUnavailableFallback()
    {
        var (assistant, handler) = Create(HttpStatusCode.InternalServerError);

        var result = await assistant.AnalyzeMessageAsync(new AiAnalysisContext { BusinessName = "Shop", Catalog = new List<AiCatalogItem>() }, "kuch bhi");

        Assert.True(result.AiUnavailable);
        Assert.Equal(2, handler.Calls);
    }
}
