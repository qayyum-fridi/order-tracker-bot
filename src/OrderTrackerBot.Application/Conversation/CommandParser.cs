using System.Text.RegularExpressions;

namespace OrderTrackerBot.Application.Conversation;

public enum CommandKind
{
    Start,
    Greeting,
    Help,
    Menu,
    OrdersToday,
    PendingOrders,
    TodaysSummary,
    Catalog,
    AddProduct,
    AddProductsBulk,
    ShareCatalog,
    MenuCategory,
    CustomerList,
    CustomerDetail,
    CustomerSearch,
    DetailedForm,
    HowTo,
    EditProduct,
    MarkStatus,
    MarkAllPendingShipped,
    CancelOrder,
    Undo,
    PaymentLink,
    AddPaymentMethod,
    UnpaidOrders,
    CodPending,
    AddTracking,
    TrackingLookup,
    CustomerOrderLookup,
    FuzzyStatusUpdate,
    CreateDiscount,
    DiscountList,
    CreateLoyalty,
    LoyalCustomers,
    TrendingProducts,
    SlowMovers,
    Feedback,
    CustomerFeedbackList,
    ResetAccount,
    Broadcast,
    DeleteProduct,
    PriceTiers,
    DeleteCustomer,
    RestoreCustomer,
    MoreCustomers,
    CampaignStatus,
    WeeklySummary,
    UpdateBusinessInfo,
    Subscribe,
    SupportQueries,
    ResolveSupportQuery,
    ReplySupportQuery,
    ForwardedQuery,
    ConnectInstagram,
    DisconnectInstagram,
    CommentLeads,
    LeadAction,
    ProductReport,
    DiscountPerformance,
    NewOrderHelp,
    Guide,
    GuideLater,
    ChangeLanguage,
    BusinessSetup,
    PaymentMethodPrompt,
    ImportCatalogSheet
}

/// <summary>A catalog line: "Lawn Suit - 3500" or "Sugar 5 kg - 500" (unit type + pack size split off the name).</summary>
public sealed record ProductLine(string Name, decimal Price, string UnitType, decimal UnitQty);

/// <summary>
/// A deterministic command: fixed/near-fixed syntax that is answered by a plain DB
/// lookup or update, never an LLM call (the "no typing dots" screens in the UX spec).
/// Free-form text that matches nothing here falls through to the AI order-extraction path.
/// </summary>
public sealed class ParsedCommand
{
    public required CommandKind Kind { get; init; }
    public string? Text { get; init; }
    public string? Text2 { get; init; }
    public int? Number { get; init; }
    public decimal? Amount { get; init; }
    public ProductLine? Product { get; init; }
}

public static class CommandParser
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private static readonly Regex Start = new(@"^start$", Opts);
    private static readonly Regex Greeting = new(@"^(hi|hello|hey|salam|assalam[u]?\s*alaikum|asalam[u]?\s*alaikum)$", Opts);
    private static readonly Regex Help = new(@"^(help|مدد)$", Opts);
    private static readonly Regex Guide = new(@"^(guide|gaid|guide\s+dekhein|guide\s+dekhna|poora\s+guide)$", Opts);
    private static readonly Regex GuideLater = new(@"^baad\s+mein$", Opts);
    private static readonly Regex ChangeLanguage = new(@"^(change\s+language|language(\s+(badlein|badlo|change))?|zabaan\s+badlein|language\s+badal(na|ein)?|زبان\s+بدلیں)$", Opts);
    private static readonly Regex BusinessSetup = new(@"^(business\s+setup|setup|settings)$", Opts);
    private static readonly Regex PaymentMethodPrompt = new(@"^payment\s+method$", Opts);
    private static readonly Regex LeadingSymbols = new(@"^[\p{So}\p{Cs}\uFE0F\u200D\s]+", RegexOptions.Compiled);
    private static readonly Regex Menu = new(@"^(menu|مینو)$", Opts);
    private static readonly Regex OrdersToday = new(@"^(orders?\s+today|آج\s+کے\s+آرڈرز)$", Opts);
    private static readonly Regex PendingOrders = new(@"^(pending\s+orders?|پینڈنگ\s+آرڈرز)$", Opts);
    private static readonly Regex TodaysSummary = new(@"^(today'?s\s+summary|آج\s+کا\s+خلاصہ)$", Opts);
    private static readonly Regex Catalog = new(@"^(catalog|کیٹلاگ)$", Opts);
    private static readonly Regex ShareCatalog = new(@"^share\s+catalog$", Opts);
    private static readonly Regex MenuCategory = new(@"^menu\s+(orders|reports|catalog|payments|discounts|customers|settings)$", Opts);
    private static readonly Regex CustomerList = new(@"^(customers?\s+list|my\s+customers)$", Opts);
    private static readonly Regex CustomerSearch = new(@"^search\s+customers?:\s*(.+)$", Opts);
    private static readonly Regex CustomerDetail = new(@"^customer\s+(?!list$|feedback$)(.+)$", Opts);
    private static readonly Regex DeleteCustomer = new(@"^(?:delete|remove)\s+customer:?\s*(.+)$", Opts);
    private static readonly Regex RestoreCustomer = new(@"^restore\s+customer:?\s*(.+)$", Opts);
    private static readonly Regex MoreCustomers = new(@"^(more|aur|next)$", Opts);
    private static readonly Regex DeleteProduct = new(@"^(?:delete|remove)\s+product:?\s*(.+)$", Opts);
    private static readonly Regex PriceTiers = new(@"^(.+?)\s*[-–:]\s*(?:price\s+tiers?|bulk\s+pric(?:e|ing)|wholesale)\s*:\s*(.+)$", Opts);
    private static readonly Regex CampaignStatus = new(@"^campaign\s+status$", Opts);
    private static readonly Regex ProductReport = new(@"^(.+?)\s+(?:ka|ki)\s+report$|^report:?\s+(.+)$", Opts);
    private static readonly Regex DiscountPerformance = new(@"^discount\s+(?:performance|report)$", Opts);
    private static readonly Regex WeeklySummary = new(@"^(weekly\s+summary|week\s+ka\s+summary)$", Opts);
    private static readonly Regex UpdateBusinessInfo = new(@"^(update\s+)?business\s+(info|details)$", Opts);
    private static readonly Regex Subscribe = new(@"^(subscribe|subscription|upgrade|plans?)$", Opts);
    private static readonly Regex BroadcastNatural = new(@"\b(?:sab|saare|sare|all)\s+customers?\s+ko\s+(?:batao|bata\s+do|bhejo|bhej\s+do|message\s+karo)\s*:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex AddProduct = new(@"^add\s+product:\s*(.+?)\s*-\s*(\d+(?:\.\d+)?)$", Opts);
    private static readonly Regex EditProduct = new(@"^edit\s+product:\s*(.+?)\s*-\s*(\d+(?:\.\d+)?)$", Opts);
    private static readonly Regex MarkAllPendingShipped = new(@"^mark\s+all\s+pending\s+as\s+shipped$", Opts);
    private static readonly Regex MarkStatus = new(@"^mark\s+(\d+)\s+(shipped|delivered|paid|pending)$", Opts);
    private static readonly Regex CancelOrder = new(@"^cancel\s+order\s+(\d+)$", Opts);
    private static readonly Regex Undo = new(@"^undo$", Opts);
    private static readonly Regex PaymentLink = new(@"^payment\s+link(?:\s+(\d+))?$", Opts);
    private static readonly Regex AddPaymentMethod = new(@"^add\s+payment:\s*(jazzcash|easypaisa|bank|safepay)\s*(?:,\s*(.+))?$", Opts);
    private static readonly Regex UnpaidOrders = new(@"^unpaid\s+orders?$", Opts);
    private static readonly Regex CodPending = new(@"^cod\s+pending$", Opts);
    private static readonly Regex AddTracking = new(@"^add\s+tracking:\s*(.+?)\s*,\s*(.+)$", Opts);
    private static readonly Regex TrackingLookup = new(@"^(.+?)\s+ka\s+tracking$", Opts);
    private static readonly Regex CustomerOrderLookup = new(@"^(.+?)\s+ka\s+order$", Opts);
    private static readonly Regex FuzzyStatusUpdate = new(@"^(.+?)\s+ka\s+order\s+.*\b(deliver|ship|pending)\w*\b", Opts);
    private static readonly Regex CreateDiscount = new(@"^create\s+discount:\s*(.+)$", Opts);
    private static readonly Regex DiscountList = new(@"^discount\s+list$", Opts);
    private static readonly Regex CreateLoyalty = new(@"^create\s+loyalty:\s*(\d+)\s+orders?\s*=\s*(\d+(?:\.\d+)?)\s*(?:%|percent|pc)?\s*off$", Opts);
    private static readonly Regex LoyalCustomers = new(@"^loyal\s+customers?$", Opts);
    private static readonly Regex TrendingProducts = new(@"^trending\s+products?$", Opts);
    private static readonly Regex SlowMovers = new(@"^slow\s+movers?$", Opts);
    private static readonly Regex CustomerFeedbackList = new(@"^customer\s+feedback$", Opts);
    private static readonly Regex ResetAccount = new(@"^(reset|delete)\s+account$|^account\s+(reset|delete)$", Opts);
    private static readonly Regex Feedback = new(@"^feedback:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex Broadcast = new(@"^broadcast:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    // "Lawn Suit - 3500" / "Lawn suite-3500" / "Kurti = 1800" with no command prefix. The name has no digits,
    // commas or colons, so real orders ("Sara, 1 kurti, 0300...") never match.
    private static readonly Regex BareProductLine = new(@"^([^\d,:\n]{2,50}?)\s*[-–=]\s*(\d{1,7}(?:\.\d+)?)$", Opts);
    // Weight/pack products: "Sugar 5 kg - 500", "Rice 10kg - 1200", "Eggs 1 dozen - 400".
    private static readonly Regex UnitProductLine = new(@"^([^\d,:\n]{2,50}?)\s+(\d+(?:\.\d+)?)\s*([a-z]+)\s*[-–=]\s*(\d{1,7}(?:\.\d+)?)$", Opts);
    private static readonly Regex NameWithUnit = new(@"^(.+?)\s+(\d+(?:\.\d+)?)\s*([a-z]+)$", Opts);

    private static readonly Dictionary<string, string> UnitAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["kg"] = "kg", ["kgs"] = "kg", ["kilo"] = "kg", ["g"] = "gram", ["gm"] = "gram", ["gms"] = "gram", ["gram"] = "gram", ["grams"] = "gram",
        ["dozen"] = "dozen", ["darjan"] = "dozen", ["l"] = "liter", ["ltr"] = "liter", ["liter"] = "liter", ["litre"] = "liter", ["liters"] = "liter",
        ["m"] = "meter", ["meter"] = "meter", ["metre"] = "meter", ["meters"] = "meter", ["yard"] = "yard", ["yards"] = "yard", ["gaz"] = "yard",
        ["pack"] = "pack", ["packs"] = "pack", ["packet"] = "pack", ["pc"] = "piece", ["pcs"] = "piece", ["piece"] = "piece", ["pieces"] = "piece"
    };

    public static string? NormalizeUnit(string raw) => UnitAliases.TryGetValue(raw.Trim(), out var unit) ? unit : null;

    public static bool TryParseProductLine(string line, out string name, out decimal price)
    {
        var ok = TryParseProductLine(line, out ProductLine? product);
        name = product?.Name ?? "";
        price = product?.Price ?? 0;
        return ok;
    }

    public static bool TryParseProductLine(string line, out ProductLine? product)
    {
        product = null;
        var text = line.Trim();
        var m = UnitProductLine.Match(text);
        if (m.Success && NormalizeUnit(m.Groups[3].Value) is { } unit)
        {
            product = new ProductLine(m.Groups[1].Value.Trim(), decimal.Parse(m.Groups[4].Value), unit, decimal.Parse(m.Groups[2].Value));
            return true;
        }

        m = BareProductLine.Match(text);
        if (!m.Success || m.Groups[1].Value.Trim().Length == 0) return false;
        product = new ProductLine(m.Groups[1].Value.Trim(), decimal.Parse(m.Groups[2].Value), "piece", 1);
        return true;
    }

    /// <summary>"Sugar 5 kg" -> (Sugar, kg, 5); a name without a recognised unit is a single piece.</summary>
    public static ProductLine SplitUnit(string name, decimal price)
    {
        var m = NameWithUnit.Match(name.Trim());
        return m.Success && NormalizeUnit(m.Groups[3].Value) is { } unit
            ? new ProductLine(m.Groups[1].Value.Trim(), price, unit, decimal.Parse(m.Groups[2].Value))
            : new ProductLine(name.Trim(), price, "piece", 1);
    }

    // "add discount" / "new product" with no details: show the exact format instead of guessing via the AI.
    private static readonly Regex DetailedForm = new(@"^(?:add\s+)?(product|customer|order)\s*\(\s*detailed\s*\)$|^new\s+(order)\s*\(\s*detailed\s*\)$", Opts);
    private static readonly Regex NewOrderHelp = new(@"^(?:new|naya|nya|add|create|make)\s+orders?$|^(?:naya\s+)?orders?\s+(?:add|darj|likhna|karna|dalna)(?:\s+(?:karna|karni|hai|karein|krna))*$", Opts);
    private static readonly Regex HowTo =new(@"^(?:add|new|create|make)\s+(discount|product|payment|loyalty|tracking)s?$", Opts);

    private static readonly Regex SafepayId = new(@"^safepay\s+id:\s*(.+)$", Opts);

    // Large-catalog onboarding path (Setup Effort spec): seller fills a Google Sheet template
    // and sends the share link back instead of typing 50+ products one by one.
    private static readonly Regex CatalogSheetLink = new(@"https://docs\.google\.com/spreadsheets/d/[A-Za-z0-9_-]+[^\s]*", Opts);

    // Screens 5d-5..5d-10: Instagram comment leads and buyer support queries.
    private static readonly Regex SupportQueries = new(@"^(?:support\s+quer(?:y|ies)|customer\s+quer(?:y|ies)|open\s+quer(?:y|ies)|queries)$", Opts);
    private static readonly Regex ResolveSupportQuery = new(@"^mark\s+(?:query\s+)?(\d+)\s+(?:as\s+)?(?:resolved|solved|done)$", Opts);
    private static readonly Regex ReplySupportQuery = new(@"^reply\s+(\d+)(?:\s*:\s*(.+))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex ConnectInstagram = new(@"^(?:connect|link)\s+(?:instagram|insta|ig)$|^(?:instagram|insta|ig)\s+(?:connect|link)$", Opts);
    private static readonly Regex DisconnectInstagram = new(@"^(?:disconnect|unlink)\s+(?:instagram|insta|ig)$", Opts);
    private static readonly Regex CommentLeads = new(@"^(?:comment\s+leads?|leads|ig\s+leads?|instagram\s+leads?)$", Opts);
    private static readonly Regex LeadAction = new(@"^lead\s+#?(\d+)\s+(converted|convert|followed\s*up|follow\s*up|dismiss(?:ed)?|spam)(?:\s+(?:order\s+)?#?(\d+))?$", Opts);
    private const string AskedVerb = @"(?:poocha|pucha|puchha|poochha|puocha|pooch\s+raha|pooch\s+rahi|pooch\s+rahe|asked)(?:\s+(?:hai|he|tha|thi))?";
    private const RegexOptions QueryOpts = RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline;
    // "mera order kab tak aayega? — Bilal ne poocha" (any punctuation separator: -, –, —, ?, …)
    private static readonly Regex QueryThenAsker = new(@"^(?<q>.+?)\s*[^\p{L}\p{N}\s]+\s*(?<name>[\p{L}][\p{L} .]{0,39}?)\s+ne\s+" + AskedVerb + @"[.!]*$", QueryOpts);
    // "Bilal ne poocha: mera order kab aayega?"
    private static readonly Regex AskerThenQuery = new(@"^(?<name>[\p{L}][\p{L} .]{0,39}?)\s+ne\s+" + AskedVerb + @"\s*[:\-—–]\s*(?<q>.+)$", QueryOpts);
    // "query: Bilal, mera order kab aayega?"
    private static readonly Regex QueryPrefix = new(@"^(?:customer\s+)?(?:query|sawal)\s*:\s*(?<name>[^,\n]{2,40}?)\s*,\s*(?<q>.+)$", QueryOpts);

    /// <summary>A buyer question the seller forwarded, in one of the fixed shapes above; the AI catches looser phrasings.</summary>
    public static bool TryParseForwardedQuery(string message, out string customerName, out string question)
    {
        foreach (var regex in new[] { QueryThenAsker, AskerThenQuery, QueryPrefix })
        {
            var m = regex.Match(message.Trim());
            if (!m.Success) continue;
            customerName = m.Groups["name"].Value.Trim();
            question = m.Groups["q"].Value.Trim();
            if (customerName.Length > 0 && question.Length > 0) return true;
        }
        customerName = question = "";
        return false;
    }

    public static ParsedCommand? TryParse(string rawMessage)
    {
        var message = rawMessage.Trim();
        // A tapped button keeps its emoji ("📋 Menu", "⚙️ Business Setup") — parse the words.
        var stripped = LeadingSymbols.Replace(message, "").Trim();
        if (stripped.Length > 0) message = stripped;
        Match m;

        if ((m = CatalogSheetLink.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.ImportCatalogSheet, Text = m.Value };
        if (Start.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.Start };
        if (Greeting.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.Greeting };
        if (Help.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.Help };
        if (ChangeLanguage.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.ChangeLanguage };
        if (BusinessSetup.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.BusinessSetup };
        if (PaymentMethodPrompt.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.PaymentMethodPrompt };
        if (Guide.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.Guide, Text = message.Contains("dekh", StringComparison.OrdinalIgnoreCase) || message.StartsWith("poora", StringComparison.OrdinalIgnoreCase) ? "full" : null };
        if (GuideLater.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.GuideLater };
        if (Menu.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.Menu };
        if (OrdersToday.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.OrdersToday };
        if (PendingOrders.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.PendingOrders };
        if (TodaysSummary.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.TodaysSummary };
        if (Catalog.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.Catalog };
        if (ShareCatalog.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.ShareCatalog };
        if ((m = MenuCategory.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.MenuCategory, Text = m.Groups[1].Value.ToLowerInvariant() };
        if (CustomerList.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.CustomerList };
        if ((m = CustomerSearch.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.CustomerSearch, Text = m.Groups[1].Value.Trim() };
        if ((m = DeleteCustomer.Match(message)).Success)
        {
            var arg = m.Groups[1].Value.Trim();
            return new ParsedCommand { Kind = CommandKind.DeleteCustomer, Text = arg, Number = int.TryParse(arg, out var n) ? n : null };
        }
        if ((m = RestoreCustomer.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.RestoreCustomer, Text = m.Groups[1].Value.Trim() };
        if (MoreCustomers.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.MoreCustomers };
        if ((m = DeleteProduct.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.DeleteProduct, Text = m.Groups[1].Value.Trim() };
        if ((m = PriceTiers.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.PriceTiers, Text = m.Groups[1].Value.Trim(), Text2 = m.Groups[2].Value.Trim() };
        if (CampaignStatus.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.CampaignStatus };
        if (DiscountPerformance.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.DiscountPerformance };
        if ((m = ProductReport.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.ProductReport, Text = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim() };
        if (WeeklySummary.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.WeeklySummary };
        if (UpdateBusinessInfo.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.UpdateBusinessInfo };
        if (Subscribe.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.Subscribe };
        if (SupportQueries.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.SupportQueries };
        if ((m = ResolveSupportQuery.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.ResolveSupportQuery, Number = int.Parse(m.Groups[1].Value) };
        if ((m = ReplySupportQuery.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.ReplySupportQuery, Number = int.Parse(m.Groups[1].Value), Text = m.Groups[2].Success ? m.Groups[2].Value.Trim() : null };
        if (ConnectInstagram.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.ConnectInstagram };
        if (DisconnectInstagram.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.DisconnectInstagram };
        if (CommentLeads.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.CommentLeads };
        if ((m = LeadAction.Match(message)).Success)
        {
            var verb = m.Groups[2].Value.ToLowerInvariant();
            var action = verb.StartsWith("conv") ? "converted" : verb.StartsWith("follow") ? "followed_up" : "dismissed";
            return new ParsedCommand
            {
                Kind = CommandKind.LeadAction, Number = int.Parse(m.Groups[1].Value), Text = action,
                Amount = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : null
            };
        }
        if (TryParseForwardedQuery(message, out var asker, out var question))
            return new ParsedCommand { Kind = CommandKind.ForwardedQuery, Text = asker, Text2 = question };

        if ((m = CustomerDetail.Match(message)).Success)
        {
            var arg = m.Groups[1].Value.Trim();
            return new ParsedCommand { Kind = CommandKind.CustomerDetail, Text = arg, Number = int.TryParse(arg, out var n) ? n : null };
        }

        if ((m = AddProduct.Match(message)).Success)
        {
            var product = SplitUnit(m.Groups[1].Value, decimal.Parse(m.Groups[2].Value));
            return new ParsedCommand { Kind = CommandKind.AddProduct, Text = product.Name, Amount = product.Price, Product = product };
        }

        if ((m = EditProduct.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.EditProduct, Text = m.Groups[1].Value.Trim(), Amount = decimal.Parse(m.Groups[2].Value) };

        if (MarkAllPendingShipped.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.MarkAllPendingShipped };

        if ((m = MarkStatus.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.MarkStatus, Number = int.Parse(m.Groups[1].Value), Text = m.Groups[2].Value.ToLowerInvariant() };

        if ((m = CancelOrder.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.CancelOrder, Number = int.Parse(m.Groups[1].Value) };

        if (Undo.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.Undo };

        if ((m = PaymentLink.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.PaymentLink, Number = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : null };

        if ((m = AddPaymentMethod.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.AddPaymentMethod, Text = m.Groups[1].Value.ToLowerInvariant(), Text2 = m.Groups[2].Success ? m.Groups[2].Value.Trim() : null };

        if (UnpaidOrders.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.UnpaidOrders };
        if (CodPending.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.CodPending };

        if ((m = AddTracking.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.AddTracking, Text = m.Groups[1].Value.Trim(), Text2 = m.Groups[2].Value.Trim() };

        if ((m = TrackingLookup.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.TrackingLookup, Text = m.Groups[1].Value.Trim() };

        if ((m = FuzzyStatusUpdate.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.FuzzyStatusUpdate, Text = m.Groups[1].Value.Trim(), Text2 = m.Groups[2].Value.ToLowerInvariant() };

        if ((m = CustomerOrderLookup.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.CustomerOrderLookup, Text = m.Groups[1].Value.Trim() };

        if ((m = CreateDiscount.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.CreateDiscount, Text = m.Groups[1].Value.Trim() };

        if (DiscountList.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.DiscountList };

        if ((m = CreateLoyalty.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.CreateLoyalty, Number = int.Parse(m.Groups[1].Value), Amount = decimal.Parse(m.Groups[2].Value) };

        if (LoyalCustomers.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.LoyalCustomers };
        if (TrendingProducts.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.TrendingProducts };
        if (SlowMovers.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.SlowMovers };

        if (ResetAccount.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.ResetAccount };

        if (CustomerFeedbackList.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.CustomerFeedbackList };

        if ((m = Feedback.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.Feedback, Text = m.Groups[1].Value.Trim() };

        if ((m = Broadcast.Match(message)).Success || (m = BroadcastNatural.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.Broadcast, Text = m.Groups[1].Value.Trim() };

        if ((m = SafepayId.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.AddPaymentMethod, Text = "safepay", Text2 = m.Groups[1].Value.Trim() };

        if ((m = DetailedForm.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.DetailedForm, Text = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToLowerInvariant() };

        if (NewOrderHelp.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.NewOrderHelp };

        if ((m = HowTo.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.HowTo, Text = m.Groups[1].Value.ToLowerInvariant() };

        var lines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 1 && TryParseProductLine(lines[0], out ProductLine? line))
            return new ParsedCommand { Kind = CommandKind.AddProduct, Text = line!.Name, Amount = line.Price, Product = line };
        if (lines.Length > 1 && lines.All(l => TryParseProductLine(l, out ProductLine? _)))
            return new ParsedCommand { Kind = CommandKind.AddProductsBulk, Text = message };

        return FuzzyCommand(message);
    }

    // Typo tolerance ("odrers todya" -> orders today). Only fixed phrases; undo is excluded since a typo must never revert work.
    private static readonly (string Phrase, CommandKind Kind)[] FuzzyPhrases =
    {
        ("orders today", CommandKind.OrdersToday), ("pending orders", CommandKind.PendingOrders),
        ("today's summary", CommandKind.TodaysSummary), ("catalog", CommandKind.Catalog),
        ("unpaid orders", CommandKind.UnpaidOrders), ("cod pending", CommandKind.CodPending),
        ("loyal customers", CommandKind.LoyalCustomers), ("trending products", CommandKind.TrendingProducts),
        ("slow movers", CommandKind.SlowMovers), ("discount list", CommandKind.DiscountList),
        ("share catalog", CommandKind.ShareCatalog), ("customer list", CommandKind.CustomerList), ("menu", CommandKind.Menu), ("help", CommandKind.Help),
        ("campaign status", CommandKind.CampaignStatus), ("weekly summary", CommandKind.WeeklySummary),
        ("reset account", CommandKind.ResetAccount), ("support queries", CommandKind.SupportQueries),
        ("comment leads", CommandKind.CommentLeads), ("discount performance", CommandKind.DiscountPerformance), ("new order", CommandKind.NewOrderHelp), ("connect instagram", CommandKind.ConnectInstagram)
    };

    private static ParsedCommand? FuzzyCommand(string message)
    {
        var text = message.Trim().ToLowerInvariant();
        if (text.Length < 4 || text.Any(char.IsDigit) || text.Contains(',')) return null;

        var best = FuzzyPhrases
            .Select(p => (p.Kind, Distance: EditDistance(text, p.Phrase), p.Phrase))
            .OrderBy(p => p.Distance)
            .First();
        var allowed = best.Phrase.Length >= 8 ? 2 : 1;
        return best.Distance is > 0 && best.Distance <= allowed ? new ParsedCommand { Kind = best.Kind } : null;
    }

    // Optimal-string-alignment distance: an adjacent swap ("odrers") costs 1, not 2.
    private static int EditDistance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        for (var j = 1; j <= b.Length; j++)
        {
            var cost = a[i - 1] == b[j - 1] ? 0 : 1;
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
        }
        return d[a.Length, b.Length];
    }

    private static readonly Regex ConfirmYes = new(@"^(yes|y|ha|haan|han|ji|ji haan|ok|okay|👍\S*|✅|ہاں|جی)[.!]*$", Opts);
    private static readonly Regex ConfirmNo = new(@"^(no|n|nahi|نہیں)$", Opts);

    public static bool IsAffirmative(string message) => ConfirmYes.IsMatch(message.Trim());
    public static bool IsNegative(string message) => ConfirmNo.IsMatch(message.Trim());
}
