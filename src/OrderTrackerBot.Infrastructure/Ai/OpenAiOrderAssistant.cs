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
/// comes back as parseable structured data instead of prose.
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

    public async Task<AiMessageAnalysis> AnalyzeMessageAsync(AiAnalysisContext context, string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogWarning("OpenAI API key not configured — treating message as unclear.");
            return new AiMessageAnalysis
            {
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
            "The seller (never the end customer) sends you freeform messages in Roman Urdu, Urdu script, and/or English, often mixed. " +
            "Your job is ONLY to decide whether the message is an attempt to place a new order or update an order's status, and if so extract " +
            "structured fields. Never invent data that is not in the message. Match product names loosely (typos, informal names, missing words) " +
            "against this seller's catalog:\n" + catalogText + "\n\n" +
            "Required order fields are customer_name and phone; if either is missing, still return the order with what you found and list the " +
            "missing ones in missing_required_fields (values: 'CustomerName', 'Phone'). Address and payment_method are optional. " +
            "Set is_ambiguous_item_grouping=true only when 2+ distinct items are named in a way that could mean either separate orders or one " +
            "combined order (e.g. \"2 suits, red and blue, for Ayesha\"). " +
            "If the message is not an order attempt at all (a greeting, gibberish, an unrelated question, or clearly a status query/command you " +
            "don't recognize), set is_order_attempt=false and return a short Roman Urdu clarification_question with 2-3 short clarification_options " +
            "the seller can pick by number.";

        var requestBody = new JsonObject
        {
            ["model"] = _options.Model,
            ["temperature"] = 0.1,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = message }
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

    private static AiMessageAnalysis ParseAnalysis(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var analysis = new AiMessageAnalysis
        {
            IsOrderAttempt = root.GetProperty("is_order_attempt").GetBoolean(),
            IsAmbiguousItemGrouping = root.TryGetProperty("is_ambiguous_item_grouping", out var ambEl) && ambEl.GetBoolean(),
            ClarificationQuestion = root.TryGetProperty("clarification_question", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString() : null
        };

        if (root.TryGetProperty("clarification_options", out var opts) && opts.ValueKind == JsonValueKind.Array)
            analysis.ClarificationOptions.AddRange(opts.EnumerateArray().Select(o => o.GetString()!).Where(s => s is not null));

        if (root.TryGetProperty("order", out var orderEl) && orderEl.ValueKind == JsonValueKind.Object)
        {
            var order = new AiOrderDraft
            {
                CustomerName = GetNullableString(orderEl, "customer_name"),
                Phone = GetNullableString(orderEl, "phone"),
                Address = GetNullableString(orderEl, "address"),
                PaymentMethod = GetNullableString(orderEl, "payment_method"),
                DiscountCode = GetNullableString(orderEl, "discount_code")
            };

            if (orderEl.TryGetProperty("missing_required_fields", out var missing) && missing.ValueKind == JsonValueKind.Array)
                order.MissingRequiredFields.AddRange(missing.EnumerateArray().Select(m => m.GetString()!).Where(s => s is not null));

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

            return new AiMessageAnalysis
            {
                IsOrderAttempt = analysis.IsOrderAttempt,
                IsAmbiguousItemGrouping = analysis.IsAmbiguousItemGrouping,
                ClarificationQuestion = analysis.ClarificationQuestion,
                ClarificationOptions = analysis.ClarificationOptions,
                Order = order
            };
        }

        return analysis;
    }

    private static string? GetNullableString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static JsonObject BuildResponseFormat()
    {
        var itemSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["product_name"] = new JsonObject { ["type"] = "string" },
                ["matched_catalog_product_name"] = new JsonObject { ["type"] = new JsonArray { "string", "null" } },
                ["quantity"] = new JsonObject { ["type"] = "integer" }
            },
            ["required"] = new JsonArray { "product_name", "matched_catalog_product_name", "quantity" },
            ["additionalProperties"] = false
        };

        var orderSchema = new JsonObject
        {
            ["type"] = new JsonArray { "object", "null" },
            ["properties"] = new JsonObject
            {
                ["customer_name"] = new JsonObject { ["type"] = new JsonArray { "string", "null" } },
                ["phone"] = new JsonObject { ["type"] = new JsonArray { "string", "null" } },
                ["address"] = new JsonObject { ["type"] = new JsonArray { "string", "null" } },
                ["payment_method"] = new JsonObject { ["type"] = new JsonArray { "string", "null" } },
                ["discount_code"] = new JsonObject { ["type"] = new JsonArray { "string", "null" } },
                ["missing_required_fields"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                ["items"] = new JsonObject { ["type"] = "array", ["items"] = itemSchema }
            },
            ["required"] = new JsonArray { "customer_name", "phone", "address", "payment_method", "discount_code", "missing_required_fields", "items" },
            ["additionalProperties"] = false
        };

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["is_order_attempt"] = new JsonObject { ["type"] = "boolean" },
                ["is_ambiguous_item_grouping"] = new JsonObject { ["type"] = "boolean" },
                ["clarification_question"] = new JsonObject { ["type"] = new JsonArray { "string", "null" } },
                ["clarification_options"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                ["order"] = orderSchema
            },
            ["required"] = new JsonArray { "is_order_attempt", "is_ambiguous_item_grouping", "clarification_question", "clarification_options", "order" },
            ["additionalProperties"] = false
        };

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
