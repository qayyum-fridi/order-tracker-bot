using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderTrackerBot.Application.Ai;

namespace OrderTrackerBot.Infrastructure.Ai;

/// <summary>
/// Talks to the OpenAI Chat Completions API using a strict JSON-schema response so the
/// single free-form call (order extraction + intent classification + clarification) always
/// comes back as parseable structured data instead of prose. Screenshots go through the same
/// call with the image attached (vision input).
/// </summary>
public class OpenAiOrderAssistant : IAiOrderAssistant
{
    private readonly HttpClient _httpClient;
    private readonly OpenAiOptions _options;
    private readonly ILogger<OpenAiOrderAssistant> _logger;

    public OpenAiOrderAssistant(HttpClient httpClient, IOptions<OpenAiOptions> options, ILogger<OpenAiOrderAssistant> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public Task<AiMessageAnalysis> AnalyzeMessageAsync(AiAnalysisContext context, string message, CancellationToken cancellationToken = default) =>
        AnalyzeAsync(context, message, cancellationToken);

    public Task<AiMessageAnalysis> AnalyzeImageAsync(AiAnalysisContext context, AiImageInput image, CancellationToken cancellationToken = default)
    {
        var dataUrl = $"data:{image.MimeType};base64,{Convert.ToBase64String(image.Bytes)}";
        var content = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = "The seller forwarded this screenshot. It is either a customer's order (Instagram/TikTok/WhatsApp/Facebook " +
                           "DM or comment) or a payment receipt (JazzCash/Easypaisa/bank transfer)." +
                           (string.IsNullOrWhiteSpace(image.Caption) ? "" : $" Seller's caption: {image.Caption}")
            },
            new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = dataUrl } }
        };
        return AnalyzeAsync(context, content, cancellationToken);
    }

    private async Task<AiMessageAnalysis> AnalyzeAsync(AiAnalysisContext context, JsonNode userContent, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogWarning("OpenAI API key not configured — treating message as unclear.");
            return new AiMessageAnalysis
            {
                Intent = "unclear",
                IsOrderAttempt = false,
                ClarificationQuestion = "Mujhe samajh nahi aaya 🤔 Kya aap:",
                ClarificationOptions = { "Naya order add karna chahte hain", "Kisi order ka status update karna chahte hain" }
            };
        }

        var catalogText = context.Catalog.Count == 0
            ? "(catalog is empty)"
            : string.Join("\n", context.Catalog.Select(c => $"- {c.Name}: Rs.{c.Price}"));

        var systemPrompt =
            $"You are the order-parsing engine behind a WhatsApp bot used by a small business seller named '{context.BusinessName}'. " +
            "The seller (never the end customer) sends you freeform messages or screenshots in Roman Urdu, Urdu script, and/or English, often mixed. " +
            "Classify the intent and, for orders, extract structured fields. Never invent data that is not in the message. Match product names loosely " +
            "(typos, informal names, missing words) against this seller's catalog:\n" + catalogText + "\n\n" +
            "intent values: new_order (a customer order to record), status_update (an order was shipped/delivered etc.), customer_feedback " +
            "(the seller relays how a buyer felt about their order, e.g. \"ayesha bahut khush thi order se\" — fill feedback), off_topic (small talk " +
            "or chatter unrelated to the business, e.g. \"bohat thak gayi hoon aaj\"), unclear. " +
            "For new_order put one entry per customer in orders (two different customers in one message = two entries). Quantity is the number " +
            "of catalog units; for weight-sold items like \"15kg kaju\" use the number of kg (15). " +
            "Required order fields are customer_name and phone; if either is missing, still return the order with what you found and list the " +
            "missing ones in missing_required_fields (values: 'CustomerName', 'Phone'). Address, payment_method, discount_code and order_source " +
            "(instagram|whatsapp|tiktok|facebook|referral, only when evident) are optional. " +
            "Set is_ambiguous_item_grouping=true only when 2+ distinct items for ONE customer are named in a way that could mean either separate " +
            "orders or one combined order (e.g. \"2 suits, red and blue, for Ayesha\"). " +
            "If the input is a payment receipt, set intent=unclear, is_order_attempt=false and fill receipt (amount, provider, transaction id). " +
            "If the message is unclear, set is_order_attempt=false and return a short Roman Urdu clarification_question with 2-3 short " +
            "clarification_options the seller can pick by number.";

        var requestBody = new JsonObject
        {
            ["model"] = _options.Model,
            ["temperature"] = 0.1,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userContent }
            },
            ["response_format"] = BuildResponseFormat()
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/chat/completions")
            {
                Content = JsonContent.Create(requestBody)
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken);
            var content = payload?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
            if (content is null) throw new InvalidOperationException("Empty OpenAI response");

            return ParseAnalysis(content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenAI order analysis failed — falling back to unclear.");
            return new AiMessageAnalysis
            {
                Intent = "unclear",
                IsOrderAttempt = false,
                ClarificationQuestion = "⚠️ Kuch masla ho gaya, dobara try karein ya tafseel se likhein.",
                ClarificationOptions = { "Naya order add karna chahte hain", "Kisi order ka status update karna chahte hain" }
            };
        }
    }

    public async Task<string?> GenerateInsightAsync(string factsSummary, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey)) return null;

        try
        {
            var requestBody = new JsonObject
            {
                ["model"] = _options.Model,
                ["temperature"] = 0.4,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = "One short Roman Urdu sentence of business insight/suggestion for a small seller, based on the facts given. No preamble." },
                    new JsonObject { ["role"] = "user", ["content"] = factsSummary }
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/chat/completions")
            {
                Content = JsonContent.Create(requestBody)
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken);
            return payload?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()?.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenAI insight generation failed — omitting insight line.");
            return null;
        }
    }

    internal static AiMessageAnalysis ParseAnalysis(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var options = new List<string>();
        if (root.TryGetProperty("clarification_options", out var opts) && opts.ValueKind == JsonValueKind.Array)
            options.AddRange(opts.EnumerateArray().Select(o => o.GetString()).OfType<string>());

        var orders = new List<AiOrderDraft>();
        if (root.TryGetProperty("orders", out var ordersEl) && ordersEl.ValueKind == JsonValueKind.Array)
            orders.AddRange(ordersEl.EnumerateArray().Where(o => o.ValueKind == JsonValueKind.Object).Select(ParseOrder));

        AiCustomerFeedback? feedback = null;
        if (root.TryGetProperty("feedback", out var fb) && fb.ValueKind == JsonValueKind.Object
            && GetNullableString(fb, "customer_name") is { } fbName && GetNullableString(fb, "text") is { } fbText)
            feedback = new AiCustomerFeedback { CustomerName = fbName, Text = fbText, Sentiment = GetNullableString(fb, "sentiment") };

        AiPaymentReceipt? receipt = null;
        if (root.TryGetProperty("receipt", out var rc) && rc.ValueKind == JsonValueKind.Object)
            receipt = new AiPaymentReceipt
            {
                Amount = rc.TryGetProperty("amount", out var amt) && amt.ValueKind == JsonValueKind.Number ? amt.GetDecimal() : null,
                Provider = GetNullableString(rc, "provider"),
                TransactionId = GetNullableString(rc, "transaction_id")
            };

        return new AiMessageAnalysis
        {
            Intent = GetNullableString(root, "intent") ?? "",
            IsOrderAttempt = root.TryGetProperty("is_order_attempt", out var isOrder) && isOrder.GetBoolean(),
            IsAmbiguousItemGrouping = root.TryGetProperty("is_ambiguous_item_grouping", out var ambEl) && ambEl.GetBoolean(),
            ClarificationQuestion = GetNullableString(root, "clarification_question"),
            ClarificationOptions = options,
            Order = orders.FirstOrDefault(),
            AdditionalOrders = orders.Skip(1).ToList(),
            Feedback = feedback,
            Receipt = receipt
        };
    }

    private static AiOrderDraft ParseOrder(JsonElement orderEl)
    {
        var order = new AiOrderDraft
        {
            CustomerName = GetNullableString(orderEl, "customer_name"),
            Phone = GetNullableString(orderEl, "phone"),
            Address = GetNullableString(orderEl, "address"),
            PaymentMethod = GetNullableString(orderEl, "payment_method"),
            DiscountCode = GetNullableString(orderEl, "discount_code"),
            OrderSource = GetNullableString(orderEl, "order_source")
        };

        if (orderEl.TryGetProperty("missing_required_fields", out var missing) && missing.ValueKind == JsonValueKind.Array)
            order.MissingRequiredFields.AddRange(missing.EnumerateArray().Select(m => m.GetString()).OfType<string>());

        if (orderEl.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                order.Items.Add(new AiOrderItemDraft
                {
                    ProductName = item.GetProperty("product_name").GetString() ?? "",
                    MatchedCatalogProductName = GetNullableString(item, "matched_catalog_product_name"),
                    Quantity = item.TryGetProperty("quantity", out var qty) && qty.ValueKind == JsonValueKind.Number ? qty.GetInt32() : 1
                });
            }
        }

        return order;
    }

    private static string? GetNullableString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static JsonObject NullableString() => new() { ["type"] = new JsonArray { "string", "null" } };

    private static JsonObject StrictObject(JsonObject properties, bool nullable = false) => new()
    {
        ["type"] = nullable ? new JsonArray { "object", "null" } : "object",
        ["properties"] = properties,
        ["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray()),
        ["additionalProperties"] = false
    };

    private static JsonObject BuildResponseFormat()
    {
        var itemSchema = StrictObject(new JsonObject
        {
            ["product_name"] = new JsonObject { ["type"] = "string" },
            ["matched_catalog_product_name"] = NullableString(),
            ["quantity"] = new JsonObject { ["type"] = "integer" }
        });

        var orderSchema = StrictObject(new JsonObject
        {
            ["customer_name"] = NullableString(),
            ["phone"] = NullableString(),
            ["address"] = NullableString(),
            ["payment_method"] = NullableString(),
            ["discount_code"] = NullableString(),
            ["order_source"] = NullableString(),
            ["missing_required_fields"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            ["items"] = new JsonObject { ["type"] = "array", ["items"] = itemSchema }
        });

        var schema = StrictObject(new JsonObject
        {
            ["intent"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray { "new_order", "status_update", "customer_feedback", "off_topic", "unclear" }
            },
            ["is_order_attempt"] = new JsonObject { ["type"] = "boolean" },
            ["is_ambiguous_item_grouping"] = new JsonObject { ["type"] = "boolean" },
            ["clarification_question"] = NullableString(),
            ["clarification_options"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            ["orders"] = new JsonObject { ["type"] = "array", ["items"] = orderSchema },
            ["feedback"] = StrictObject(new JsonObject
            {
                ["customer_name"] = NullableString(),
                ["text"] = NullableString(),
                ["sentiment"] = NullableString()
            }, nullable: true),
            ["receipt"] = StrictObject(new JsonObject
            {
                ["amount"] = new JsonObject { ["type"] = new JsonArray { "number", "null" } },
                ["provider"] = NullableString(),
                ["transaction_id"] = NullableString()
            }, nullable: true)
        });

        return new JsonObject
        {
            ["type"] = "json_schema",
            ["json_schema"] = new JsonObject
            {
                ["name"] = "message_analysis",
                ["strict"] = true,
                ["schema"] = schema
            }
        };
    }
}
