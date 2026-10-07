using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderTrackerBot.Application.Abstractions;
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
    private readonly IIssueReporter _issues;

    public OpenAiOrderAssistant(HttpClient httpClient, IOptions<OpenAiOptions> options, ILogger<OpenAiOrderAssistant> logger, IIssueReporter issues)
    {
        _issues = issues;
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
                AiUnavailable = true,
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
            "or chatter unrelated to the business, e.g. \"bohat thak gayi hoon aaj\"), support_query (the seller forwards a question a buyer " +
            "asked them, e.g. \"mera order kab tak aayega? — Bilal ne poocha\" or \"Ayesha pooch rahi hai Karachi bhejte hain?\" — fill support_query " +
            "with the buyer's name if given and the question itself), add_products (the seller is telling you which products THEY sell or stock — " +
            "\"mere paas 3 khaddar chadar aur 2 wool dupatte hain\", \"yeh naye products hain\" — with no customer buying anything: this is NOT an order; " +
            "set is_order_attempt=false and list each product in new_products with its price in rupees when the seller said one, else null; the name " +
            "is the product only, without a unit or quantity word such as thaan/gaz/kg. Also fill cost_price (what it cost the seller to buy, \"kharid/cost\"), " +
            "stock_qty (how many they have) and attributes (other facts, each as name+value, e.g. colour, size, fabric) ONLY when the seller said them, else null / an empty list; " +
            "a number is never copied from the catalog), unclear. " +
            "For new_order put one entry per customer in orders (two different customers in one message = two entries). Quantity is the number " +
            "of catalog units; for weight-sold items like \"15kg kaju\" use the number of kg (15). " +
            "Required order fields are customer_name and phone; if either is missing, still return the order with what you found and list the " +
            "missing ones in missing_required_fields (values: 'CustomerName', 'Phone'). Address, payment_method, discount_code and order_source " +
            "(and delivery_charge: the delivery/shipping charge in rupees only when the order states one, e.g. \"delivery 250\", \"+200 delivery\"; " +
            "\"free delivery\" = 0; never fold it into an item price; null when not mentioned) " +
            "(instagram|whatsapp|tiktok|facebook|referral, only when evident) are optional. " +
            "Set is_ambiguous_item_grouping=true only when 2+ distinct items for ONE customer are named in a way that could mean either separate " +
            "orders or one combined order (e.g. \"2 suits, red and blue, for Ayesha\"). " +
            "If the input is a payment receipt, set intent=unclear, is_order_attempt=false and fill receipt (amount, provider, transaction id). " +
            "If the message is unclear, set is_order_attempt=false and return a short Roman Urdu clarification_question with 2-3 short " +
            "clarification_options the seller can pick by number.";

        if (context.PendingPriceProducts.Count > 0)
            systemPrompt +=
                "\n\nPENDING PRICES: the bot just asked the seller for the price of these products: " + string.Join("; ", context.PendingPriceProducts) + ". " +
                "A message that gives a price for them is intent add_products and must list EXACTLY these product names (copy the spelling above) in new_products, " +
                "never a new product named after a unit or word in the message. One price for all (\"teenon ki 5000\", \"sab 5000\", \"har suit 5000\", \"5000 fi piece\") = " +
                "that price on every pending product; separate prices (\"chadar 800, dupatte 1200\") = each price on its own product; a product with no price said stays price null. " +
                "Only treat the message as something else if it clearly is (an order, a command, a different product).";

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
            await _issues.ReportAsync(IssueCodes.OpenAiAnalysisFailed, null, $"Seller: {context.BusinessName}", ex, cancellationToken);
            return new AiMessageAnalysis
            {
                Intent = "unclear",
                IsOrderAttempt = false,
                AiUnavailable = true,
                ClarificationQuestion = "⚠️ Kuch masla ho gaya, dobara try karein ya tafseel se likhein.",
                ClarificationOptions = { "Naya order add karna chahte hain", "Kisi order ka status update karna chahte hain" }
            };
        }
    }

    public Task<string?> GenerateInsightAsync(string factsSummary, CancellationToken cancellationToken = default) =>
        CompleteTextAsync("One short Roman Urdu sentence of business insight/suggestion for a small seller, based on the facts given. No preamble.",
            factsSummary, 0.4, "insight generation", cancellationToken);

    public async Task<IReadOnlyList<string>?> TranslateAsync(IReadOnlyList<string> texts, string targetLanguage, CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0) return texts;

        var language = targetLanguage == "english" ? "natural, simple English" : "natural Urdu in Urdu (Nastaliq) script, simple everyday wording";
        var json = await CompleteTextAsync(
            "You translate messages of a WhatsApp order-tracking bot for small Pakistani sellers. The input is a JSON object {\"texts\": [...]} " +
            $"whose strings are in Roman Urdu / English. Translate each into {language}. Rules: keep every number, price, order number (#6), phone number, " +
            "code, emoji, line break and bullet exactly as is (use ASCII digits); keep text inside double quotes (commands the seller must type, e.g. " +
            "\"mark 3 shipped\", \"catalog\", \"menu\", \"done\", \"skip\") unchanged; keep proper nouns, business names and product names as written; " +
            "do not add, drop or explain anything. Return ONLY JSON: {\"texts\": [...]} with exactly the same number of strings in the same order.",
            new JsonObject { ["texts"] = new JsonArray(texts.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()) }.ToJsonString(),
            0.1, "message translation", cancellationToken, jsonMode: true);
        if (json is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("texts", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != texts.Count)
                return null;
            var result = array.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null).ToList();
            return result.Any(string.IsNullOrWhiteSpace) ? null : result!;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "OpenAI message translation returned invalid JSON — sending the original text.");
            return null;
        }
    }

    public async Task<AiVoiceInterpretation?> InterpretVoiceAsync(AiVoiceContext context, string transcript, CancellationToken cancellationToken = default)
    {
        var catalog = context.CatalogNames.Count == 0 ? "(empty)" : string.Join("; ", context.CatalogNames);
        var orders = context.RecentOrders.Count == 0 ? "(no orders yet)" : string.Join("\n", context.RecentOrders);
        var customers = context.KnownCustomers.Count == 0 ? "(no customers yet)" : string.Join("\n", context.KnownCustomers);
        var json = await CompleteTextAsync(
            $"You clean up voice notes that the owner of the small Pakistani shop '{context.BusinessName}' sends to their WhatsApp order-tracking bot. " +
            "The input is a speech-to-text transcript; it may be Urdu script, Roman Urdu or English, with misheard words and spoken numbers. " +
            "FIRST understand what the seller means (the transcript's words are often phonetically wrong: \"kaan\" may be cotton, \"soor\" suit, " +
            "\"paroshak\" products); never transliterate sounds word for word — write the intended words with their normal spellings (khaddar, chadar, dupatta, wool, cotton, suit, product). " +
            "Then rewrite it into the exact text the seller would have TYPED to the bot, in Latin script (Roman Urdu / English), for this situation: " +
            $"{context.Situation}\nSeller's catalog: {catalog}\nSeller's latest orders (use these to resolve \"Hassan ka order\", \"last order\", \"suit ki price\"):\n{orders}\n" +
            $"Seller's saved customers (name, phone, order history):\n{customers}\n" +
            (context.RecentExchanges.Count == 0 ? "" : $"The last messages in this chat, oldest first (a short spoken answer usually replies to the last bot message):\n{string.Join("\n", context.RecentExchanges)}\n") + "\n" +
            "Rules: (1) Keep every digit of every number, price and phone; turn spoken number words into digits (char = 4, ek = 1, teen hazaar paanch sau = 3500). " +
            "(2) If a yes/no answer is expected and the transcript clearly agrees (haan, ji, theek hai, kar do) answer exactly \"yes\"; if it clearly refuses " +
            "(nahi, ruko, mat karo) answer exactly \"no\". (3) If a numbered choice is expected, answer only the option number — from a number word " +
            "(pehla/first/ek = 1, dusra/second/do = 2) or from the meaning of the option the seller refers to. (4) When a product mentioned clearly is a catalog " +
            "product, use its exact catalog name. (5) When products are being added to the catalog, write each as \"Name - price\" (e.g. \"Lawn Suit - 3500\"); " +
            "if the seller also gave the cost they paid, the quantity they have, a colour/size or another attribute, append each as a comma-separated part: " +
            "in the form \"<Name> - <sale price>, cost <n>, stock <n>, color <c>, size <s>, <attribute>: <value>\" using only the parts the seller said " +
            "(the number after the dash is the SALE price; \"cost\"/\"kharid\" is what it cost them; never put a number or attribute the seller did not say); " +
            "a seller saying what they stock is adding a product, never placing an order; when they gave no prices, write one clean sentence in Roman Urdu " +
            "built ONLY from the product names and quantities the seller actually said, in the form \"<qty> <name>, <qty> <name> naye products hain\" " +
            "(the shape is a pattern: never output product names or numbers that are not in the transcript or the last chat messages — if the seller only " +
            "corrects a name (\"X nahi, Y naam hai\"), keep that correction as one sentence in their own words). A cloth/fabric unit word (thaan, than, " +
            "gaz, meter) is a unit, not part of the product name: \"char thaan mozgi\", not \"char mozgi thaan\". (6) A dictated customer order is written like " +
            "\"Ayesha, 2 lawn suit, 03001234567, Gulberg Lahore\". Commands keep their typed form (\"orders today\", \"mark 3 shipped\", \"stock Kurti 20\", " +
            "\"delivery 250\", \"catalog\" for any request to see/show the catalog). (7) A product's catalog price is only a default: the seller often sells one order at a different price. To change what ONE " +
            "customer was charged use three steps: \"edit order <order id>\", \"price <item number> = <amount>\", \"done\" (item numbers are the 1) 2) numbers " +
            "in the order list above; \"qty <item number> = <n>\", \"remove <item number>\", \"delivery <amount>\", \"phone <digits>\" and \"address <text>\" are the other edits you can put between edit order and done). " +
            "Never touch the catalog price for this. (8) NAMES: when the seller names a person, match it against the saved customers (spelling, Urdu script and phonetic " +
            "variants all count). One clear match = an existing customer: use the saved spelling, and use their order history (an order command for them needs the right order id; " +
            "\"Hassan ka order\" with several orders = ask which). Two or more plausible matches = return a question listing them (\"Hassan Ali (0300…) ya Hassan Raza (0321…)?\"). " +
            "No match = a NEW customer: for a new order keep the spoken name, but for an edit/status/receipt on an existing order ask which customer or order they mean, " +
            "because that customer has no orders. If the seller asks for help or says they do not know what to do or say (\"kya bolun\", \"samajh nahi aa raha\", " +
            "\"kaise karun\", \"madad chahiye\") return exactly one step: \"kya karun\" (or \"guide\" when they ask for the step-by-step guide). Same for PRODUCTS against the catalog: a clear match uses the catalog spelling; an unknown name that is not a plain " +
            "misspelling is a new product (a new order may keep it — the bot then asks whether to add it); two close matches = ask which. (9) Never invent anything the seller did not say — above all never add a \"done\", \"skip\", \"yes\", \"no\", \"setup\" or \"menu\" step on your own: " +
            "those only when the seller clearly said them (done, khatam, bas, skip, haan, nahi...). If the seller clearly wants a change but a needed value was " +
            "not said (the new price, or which of several matching orders/items), return no steps and a short Roman Urdu question asking exactly that, naming the " +
            "customer and item (\"Hassan ke order #12 mein Lawn Suit ki price kitni rakhni hai?\"). If you are not sure what they want, return one step: the transcript " +
            "written in Latin script. Return ONLY JSON: {\"steps\": [\"...\"], \"question\": null}; steps are run one after another as separate typed messages " +
            "(normally just one; at most 4); question is a string only when asking.",
            new JsonObject { ["transcript"] = transcript }.ToJsonString(),
            0.1, "voice interpretation", cancellationToken, jsonMode: true, model: _options.VoiceModel);
        if (json is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var steps = new List<string>();
            if (doc.RootElement.TryGetProperty("steps", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array)
                steps.AddRange(stepsEl.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString()!.Trim())
                    .Where(s => s.Length > 0).Take(4));
            var question = GetNullableString(doc.RootElement, "question")?.Trim();
            if (string.IsNullOrWhiteSpace(question)) question = null;
            return steps.Count == 0 && question is null ? null : new AiVoiceInterpretation { Steps = steps, Question = question };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "OpenAI voice interpretation returned invalid JSON — using the transcript as is.");
            return null;
        }
    }

    public Task<string?> DraftSupportReplyAsync(string businessName, string question, string orderFacts, CancellationToken cancellationToken = default) =>
        CompleteTextAsync(
            $"You draft WhatsApp replies for the small Pakistani shop '{businessName}'. The seller will forward your reply to their customer. " +
            "Write 1-2 short, polite sentences in Roman Urdu (match the customer's language if they wrote English/Urdu script). Use ONLY the " +
            "order facts given — never invent dates, prices or tracking numbers; if a fact is missing, say the seller will confirm shortly. " +
            "Reply with the message text only, no quotes or preamble.",
            $"Customer question: {question}\nOrder facts: {orderFacts}", 0.3, "support reply draft", cancellationToken);

    public async Task<AiCommentClassification?> ClassifyCommentAsync(string businessName, string commentText, CancellationToken cancellationToken = default)
    {
        var json = await CompleteTextAsync(
            $"Classify an Instagram comment left on a post by the Pakistani shop '{businessName}'. Return ONLY JSON: " +
            "{\"intent\": \"order_interest\"|\"support_query\"|\"spam\"|\"unclear\", \"suggested_reply\": string|null}. " +
            "order_interest = wants to buy / asks to order / shares a phone number to order. support_query = a question (delivery area, sizes, " +
            "availability, shipping time) that is not yet an order. spam = promotion, links, bots, irrelevant. For support_query and order_interest " +
            "write suggested_reply: one short friendly public reply in the commenter's language (Roman Urdu by default); never invent prices — for " +
            "price/order questions invite them to DM. For spam/unclear use null.",
            commentText, 0.1, "comment classification", cancellationToken, jsonMode: true);
        if (json is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var intent = GetNullableString(doc.RootElement, "intent");
            if (intent is not ("order_interest" or "support_query" or "spam" or "unclear")) return null;
            return new AiCommentClassification { Intent = intent, SuggestedReply = GetNullableString(doc.RootElement, "suggested_reply") };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "OpenAI comment classification returned invalid JSON.");
            return null;
        }
    }

    /// <summary>A plain single-turn completion; null when AI is unavailable or fails (every caller has a non-AI fallback).</summary>
    private async Task<string?> CompleteTextAsync(string systemPrompt, string userText, double temperature, string purpose,
        CancellationToken cancellationToken, bool jsonMode = false, string? model = null)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey)) return null;

        try
        {
            var requestBody = new JsonObject
            {
                ["model"] = string.IsNullOrWhiteSpace(model) ? _options.Model : model,
                ["temperature"] = temperature,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                    new JsonObject { ["role"] = "user", ["content"] = userText }
                }
            };
            if (jsonMode) requestBody["response_format"] = new JsonObject { ["type"] = "json_object" };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/chat/completions")
            {
                Content = JsonContent.Create(requestBody)
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken);
            var text = payload?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenAI {Purpose} failed — using the non-AI fallback.", purpose);
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

        AiSupportQuery? supportQuery = null;
        if (root.TryGetProperty("support_query", out var sq) && sq.ValueKind == JsonValueKind.Object
            && GetNullableString(sq, "question") is { Length: > 0 } question)
            supportQuery = new AiSupportQuery { CustomerName = GetNullableString(sq, "customer_name"), Question = question };

        var newProducts = new List<AiNewProduct>();
        if (root.TryGetProperty("new_products", out var np) && np.ValueKind == JsonValueKind.Array)
            foreach (var p in np.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Object))
                if (GetNullableString(p, "name")?.Trim() is { Length: > 0 } productName)
                    newProducts.Add(new AiNewProduct
                    {
                        Name = productName,
                        Price = p.TryGetProperty("price", out var price) && price.ValueKind == JsonValueKind.Number
                            && price.TryGetDecimal(out var amountValue) && amountValue > 0 ? amountValue : null,
                        Cost = p.TryGetProperty("cost_price", out var costPrice) && costPrice.ValueKind == JsonValueKind.Number
                            && costPrice.TryGetDecimal(out var costValue) && costValue > 0 ? costValue : null,
                        Stock = p.TryGetProperty("stock_qty", out var stockQty) && stockQty.ValueKind == JsonValueKind.Number
                            && stockQty.TryGetInt32(out var stockValue) && stockValue >= 0 ? stockValue : null,
                        Attributes = p.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Array
                            ? attrs.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object)
                                .Select(a => (Name: GetNullableString(a, "name")?.Trim(), Value: GetNullableString(a, "value")?.Trim()))
                                .Where(a => !string.IsNullOrEmpty(a.Name) && !string.IsNullOrEmpty(a.Value))
                                .GroupBy(a => a.Name!, StringComparer.OrdinalIgnoreCase)
                                .ToDictionary(g => g.Key, g => g.First().Value!)
                            : new Dictionary<string, string>()
                    });

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
            SupportQuery = supportQuery,
            NewProducts = newProducts,
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
            OrderSource = GetNullableString(orderEl, "order_source"),
            DeliveryCharge = orderEl.TryGetProperty("delivery_charge", out var delivery) && delivery.ValueKind == JsonValueKind.Number
                && delivery.TryGetDecimal(out var amount) && amount >= 0 ? amount : null
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
            ["delivery_charge"] = new JsonObject { ["type"] = new JsonArray { "number", "null" } },
            ["missing_required_fields"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            ["items"] = new JsonObject { ["type"] = "array", ["items"] = itemSchema }
        });

        var schema = StrictObject(new JsonObject
        {
            ["intent"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray { "new_order", "status_update", "customer_feedback", "support_query", "add_products", "off_topic", "unclear" }
            },
            ["new_products"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = StrictObject(new JsonObject
                {
                    ["name"] = new JsonObject { ["type"] = "string" },
                    ["price"] = new JsonObject { ["type"] = new JsonArray { "number", "null" } },
                    ["cost_price"] = new JsonObject { ["type"] = new JsonArray { "number", "null" } },
                    ["stock_qty"] = new JsonObject { ["type"] = new JsonArray { "integer", "null" } },
                    ["attributes"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = StrictObject(new JsonObject { ["name"] = new JsonObject { ["type"] = "string" }, ["value"] = new JsonObject { ["type"] = "string" } })
                    }
                })
            },
            ["support_query"] = StrictObject(new JsonObject
            {
                ["customer_name"] = NullableString(),
                ["question"] = NullableString()
            }, nullable: true),
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
