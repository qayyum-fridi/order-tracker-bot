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
