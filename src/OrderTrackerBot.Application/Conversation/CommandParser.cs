using System.Text.RegularExpressions;
using OrderTrackerBot.Application.Time;

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
    Profit,
    Catalog,
    CatalogFilter,
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
    Receipt,
    BrandingHelp,
    Export,
    Shortcuts,
    DeliveryCharge,
    EditOrder,
    OrderDetail,
    StatusPicker,
    Stock,
    Expense,
    ExpenseList,
    MonthlyNet,
    CustomerUpdate,
    OrderPayment,
    OrderDeliveryCharge,
    TaxSettings,
    OrderTaxWithheld,
    RemoveBranding,
    DiscountPerformance,
    NewOrderHelp,
    Guide,
    GuideLater,
    ChangeLanguage,
    BusinessSetup,
    PaymentMethodPrompt,
    ImportCatalogSheet
}

/// <summary>What to export: any of "orders", "customers", "catalog", "discounts" (empty = ask), and an optional orders period (today/yesterday/7d/30d/lastmonth).</summary>
public sealed record ExportRequest(IReadOnlyList<string> Datasets, string? Period);

/// <summary>One change inside "edit order" mode. Kind: done | qty | price | remove | add | phone | address | name | delivery | payment.</summary>
public sealed record OrderEditInstruction(string Kind, int? Item = null, int? Quantity = null, decimal? Amount = null, string? Text = null);

/// <summary>A catalog line: "Lawn Suit - 3500" or "Sugar 5 kg - 500" (unit type + pack size split off the name).</summary>
public sealed record ProductLine(string Name, decimal Price, string UnitType, decimal UnitQty, ProductExtras? Extras = null);

/// <summary>Extra facts written after the price: "Polo Shirt - 500, cost 300, stock 10, color white, fabric: cotton".</summary>
public sealed record ProductExtras(decimal? Cost, int? Stock, string? Category, string? Size, string? Color, string? Sku,
    IReadOnlyDictionary<string, string>? Attributes);

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
    public ExportRequest? Export { get; init; }
    public string? Text3 { get; init; }
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
    // Report periods: today (default) / "yesterday" / "lastmonth" -> ParsedCommand.Text. Roman Urdu "aaj ke orders",
    // "kal ka summary", "pichle mahine ke orders" and the Urdu-script equivalents are accepted alongside English.
    private const string Of = @"(?:ka|ke|ki|کا|کے|کی)";
    private const string OrdersWord = @"(?:orders?|آرڈرز?)";
    private const string SummaryWord = @"(?:summary|خلاصہ)";
    private const string Today = @"(?:aaj|آج)";
    private const string Yesterday = @"(?:kal|کل)";
    private const string LastMonth = @"(?:(?:pichle|pichhle|pichlay|پچھلے)\s+(?:mahine|mahinay|maheene|mahiny|مہینے))";
    // "kal ke orders" / "kal ke kitne orders the?" / "aaj kitne orders aaye": the period word, optional ka/ke, optional "kitne", the word orders, optional question tail.
    private const string OrdersOfPeriod = @"(?:\s+" + Of + @")?(?:\s+(?:kitne|kitnay|کتنے))?\s+" + OrdersWord + @"(?:\s+(?:the|thay|tha|hain|hai|aaye|aae|hue|تھے|تھا|ہیں|ہے|آئے|ہوئے))*\s*[?؟]?";
    private static readonly Regex OrdersToday = new(@"^(?:orders?\s+today|today'?s\s+orders?|" + Today + OrdersOfPeriod + @")$", Opts);
    private static readonly Regex OrdersYesterday = new(@"^(?:orders?\s+yesterday|yesterday'?s\s+orders?|" + Yesterday + OrdersOfPeriod + @")$", Opts);
    private static readonly Regex OrdersLastMonth = new(@"^(?:orders?\s+last\s+month|last\s+month'?s?\s+orders?|" + LastMonth + OrdersOfPeriod + @")$", Opts);
    private static readonly Regex PendingOrders = new(@"^(pending\s+orders?|پینڈنگ\s+آرڈرز?)$", Opts);
    private static readonly Regex TodaysSummary = new(@"^(?:today'?s\s+summary|today\s+summary|" + Today + @"\s+" + Of + @"\s+" + SummaryWord + @")$", Opts);
    private static readonly Regex YesterdaysSummary = new(@"^(?:yesterday'?s\s+summary|summary\s+yesterday|" + Yesterday + @"\s+" + Of + @"\s+" + SummaryWord + @")$", Opts);
    private static readonly Regex LastMonthSummary = new(@"^(?:last\s+month'?s?\s+summary|summary\s+last\s+month|" + LastMonth + @"\s+" + Of + @"\s+" + SummaryWord + @")$", Opts);
    private static readonly Regex Catalog = new(@"^(?:catalog|کیٹلاگ|(?:show|dikhao|dikhana|dekhao|dekhein|view)\s+(?:my\s+|mera\s+)?catalog|catalog\s+(?:show|list|view|dikhao|dikhana|dekhao|dekhein|dekhna))$", Opts);
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
    // WhatsApp "/" commands (registered via deploy/whatsapp-conversational-components.json) arrive as "/orders" etc. and map to the typed command.
    public static readonly IReadOnlyList<(string Name, string Description, string Text)> SlashCommands = new[]
    {
        ("menu", "Main menu kholein", "menu"),
        ("neworder", "Naya order darj karein", "new order"),
        ("orders", "Aaj ke orders", "orders today"),
        ("pending", "Pending orders", "pending orders"),
        ("unpaid", "Unpaid orders", "unpaid orders"),
        ("summary", "Aaj ka hisaab", "today's summary"),
        ("catalog", "Apna catalog", "catalog"),
        ("customers", "Customers ki list", "customer list"),
        ("receipt", "Order ki PDF receipt", "receipt"),
        ("export", "Excel file (orders, customers...)", "export"),
        ("logo", "Receipt par apna logo/banner", "logo"),
        ("help", "Madad", "help"),
    };

    private static string? ExpandSlashCommand(string message)
    {
        var name = message.TrimStart('/').Split(' ', 2)[0].Trim().ToLowerInvariant();
        foreach (var command in SlashCommands) if (command.Name == name) return command.Text;
        return null;
    }

    // Delivery charge: "delivery 200" / "delivery charges: Rs 250" sets the seller's default (or, while confirming an order, just that
    // order's); "free delivery" = 0; "delivery charge" alone shows it. "order 12 delivery 300" changes a saved order.
    private const string DeliveryWord = @"(?:delivery|deliveri|ڈیلیوری)(?:\s+(?:charges?|fee|fees|kharcha))?";
    private static readonly Regex DeliveryAmount = new(@"^(?:set\s+)?" + DeliveryWord + @"\s*[:=-]?\s*(?:rs\.?\s*)?(?<n>\d{1,6})(?:\s*(?:rs|rupees?|روپے))?$", Opts);
    private static readonly Regex DeliveryFree = new(@"^(?:free\s+delivery|delivery\s+free|no\s+delivery(?:\s+charges?)?|delivery\s+(?:charges?\s+)?(?:nahi|none|off)|فری\s+ڈیلیوری)$", Opts);
    private static readonly Regex DeliveryShow = new(@"^(?:my\s+)?" + DeliveryWord + "$", Opts);
    private static readonly Regex OrderDelivery = new(@"^(?:order|آرڈر)?\s*#?(?<id>\d+)\s+" + DeliveryWord + @"\s*[:=-]?\s*(?:rs\.?\s*)?(?<n>\d{1,6}|free|0)$|^(?:order|آرڈر)\s*#?(?<id>\d+)\s+free\s+delivery$", Opts);

    // Tax setup: "ntn 1234567-8" / "strn 17-00-8888-001-37" / "sales tax 18" / "sales tax off"; with no value they show the current one,
    // "tax" shows all three. Text = ntn|strn|rate|show, Text2 = the value ("off" clears; "set" for a rate), Amount = the rate.
    private static readonly Regex TaxId = new(@"^(?:my\s+)?(?<k>ntn|strn)(?:\s*[:=#-]?\s*(?<v>\d[\d\- ]{5,24}\d|off|remove|none|nahi|hata\s*do))?$", Opts);
    private static readonly Regex SalesTaxRate = new(@"^(?:sales\s+tax|gst|سیلز\s+ٹیکس)(?:\s+rate)?\s*[:=-]?\s*(?:(?<n>\d{1,2}(?:\.\d{1,2})?)\s*%?|(?<off>off|none|nahi|remove|hata\s*do))?$", Opts);
    private static readonly Regex TaxShow = new(@"^(?:my\s+)?(?:taxes|tax(?:\s+(?:settings?|setup|info))?)$", Opts);
    // Income tax a courier / payment gateway held back from a payout: "order 12 withheld 120" / "12 wht 120" / "order 12 tax withheld 0".
    private static readonly Regex OrderTaxWithheld = new(@"^(?:order|آرڈر)?\s*#?(?<id>\d+)\s+(?:tax\s+)?(?:withheld|withholding|wht|katauti|kata)\s*:?\s*(?:rs\.?\s*)?(?<a>\d+(?:\.\d+)?)(?:\s*(?:rs|rupees?|روپے))?$", Opts);

    // "Sara ka phone 0300..." / "Sara ka address House 5" / "customer Sara city Lahore" / "customer Sara name Sara Khan".
    // Text = who, Text2 = phone|address|city|name, Text3 = the new value. Questions ("Sara ka phone kya hai") are left alone.
    private const string CustomerField = @"(?<f>phone|number|fone|mobile|address|pata|patta|city|shehar|naam|name|فون|نمبر|پتہ|شہر|نام)";
    private static readonly Regex CustomerUpdateOf = new(@"^(?:customer\s+)?(?<name>[^\d,:]{2,40}?)\s+(?:ka|ki|کا|کی)\s+(?:naya\s+|new\s+)?" + CustomerField + @"\s*(?:ab\s+|hai\s+|:|=|-)?\s*(?<v>.+)$", Opts);
    private static readonly Regex CustomerUpdatePrefix = new(@"^customer\s+(?<name>[^\d,:]{2,40}?)\s+" + CustomerField + @"\s*:?\s*(?<v>.+)$", Opts);
    private static readonly Regex QuestionWords = new(@"^(?:kya|kia|kiya|batao|bata|dikhao|kaun|kahan|\?)|\?$", Opts);

    private static ParsedCommand? TryParseCustomerUpdate(string message)
    {
        var m = CustomerUpdateOf.Match(message);
        if (!m.Success) m = CustomerUpdatePrefix.Match(message);
        if (!m.Success) return null;
        var value = m.Groups["v"].Value.Trim();
        if (QuestionWords.IsMatch(value)) return null;
        var field = m.Groups["f"].Value.ToLowerInvariant() switch
        {
            "phone" or "number" or "fone" or "mobile" or "فون" or "نمبر" => "phone",
            "address" or "pata" or "patta" or "پتہ" => "address",
            "city" or "shehar" or "شہر" => "city",
            _ => "name"
        };
        if (field == "phone")
        {
            var digits = Regex.Replace(value, @"[\s-]", "");
            if (!Regex.IsMatch(digits, @"^\+?\d{7,15}$")) return null;
            value = digits;
        }
        else if (value.Length < 2) return null;
        return new ParsedCommand { Kind = CommandKind.CustomerUpdate, Text = m.Groups["name"].Value.Trim(), Text2 = field, Text3 = value };
    }

    // "stock" lists tracked stock; "stock Kurti 20" sets, "stock Kurti +10" adds, "stock Kurti off" stops tracking. Text = product, Text2 = set|add|off.
    private static readonly Regex StockList = new(@"^(?:stock|stocks|inventory|stock\s+list|اسٹاک)$", Opts);
    private static readonly Regex StockOff = new(@"^(?:stock|اسٹاک)\s*:?\s*(?<name>[^\d].*?)\s+(?:off|band|remove|hatao)$", Opts);
    private static readonly Regex StockSet = new(@"^(?:stock|اسٹاک)\s*:?\s*(?<name>[^\d].*?)\s*[=:]?\s*(?<sign>\+)?\s*(?<n>\d{1,6})$|^(?<name>[^\d].*?)\s+(?:ka\s+|ki\s+)?(?:stock|اسٹاک)\s*[=:]?\s*(?<sign>\+)?\s*(?<n>\d{1,6})$", Opts);

    // "order 12" / "#12" shows one order in full.
    // "status" / "status 13" / "update status 13" / "order 13 status" / "mark 13" (no status said): show the statuses the order can move to as a pick-list.
    private static readonly Regex StatusPicker = new(@"^(?:(?:update|change)\s+)?(?:status|اسٹیٹس)(?:\s+(?:update|change))?(?:\s+#?(?<n>\d+))?$|^(?:order|آرڈر|mark|update)\s*#?(?<n>\d+)\s*(?:status|اسٹیٹس)?(?:\s+(?:update|change))?$", Opts);
    private static readonly Regex OrderDetail = new(@"^(?:order|آرڈر)\s*#?(?<n>\d+)$|^#(?<n>\d+)$", Opts);

    // Part payments: "order 12 advance 500", "12 paid 1000", "advance 500 order 12" add to what the buyer has paid so far.
    private const string PaidWord = @"(?:advance|paid|payment|received|mila|mile|ملے|ایڈوانس)";
    private static readonly Regex OrderPayment = new(@"^(?:order|آرڈر)?\s*#?(?<id>\d+)\s+" + PaidWord + @"\s*:?\s*(?:rs\.?\s*)?(?<a>\d+(?:\.\d+)?)(?:\s*(?:rs|rupees?|روپے))?$" +
        @"|^(?:advance|payment|paid)\s+(?:rs\.?\s*)?(?<a>\d+(?:\.\d+)?)\s+(?:for\s+|in\s+)?(?:order|آرڈر)\s*#?(?<id>\d+)$", Opts);

    // An advance written inside a new order ("Sara, 1 kurti, 0300..., advance 500 jazzcash" / "1000 advance").
    private static readonly Regex AdvanceInTextAfter = new(@"(?<![\p{L}\p{N}])(?:advance|adv|ایڈوانس)(?:\s+(?:paid|diya|di|mila|received|bheja))?\s*[:=-]?\s*(?<rs>rs\.?\s*)?(?<n>\d{1,6})(?!\d)(?<rs2>\s*(?:rs|rupees?|rupay|روپے))?", Opts);
    private static readonly Regex AdvanceInTextBefore = new(@"(?<![\p{L}\p{N}])(?<rs>rs\.?\s*)?(?<n>\d{1,6})(?<rs2>\s*(?:rs|rupees?|rupay|روپے))?\s+(?:advance|ایڈوانس)(?![\p{L}])", Opts);

    /// <summary>Finds an advance amount the seller wrote inside an order (or typed alone while confirming: "advance 500").</summary>
    public static bool TryFindAdvanceInOrderText(string message, out decimal amount)
    {
        amount = 0;
        var text = NormalizeDigits(message);
        foreach (var regex in new[] { AdvanceInTextAfter, AdvanceInTextBefore })
        {
            foreach (Match m in regex.Matches(text))
            {
                var value = decimal.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture);
                var saysRupees = m.Groups["rs"].Success && m.Groups["rs"].Length > 0 || m.Groups["rs2"].Success && m.Groups["rs2"].Length > 0;
                if (value >= 50 || saysRupees)
                {
                    amount = value;
                    return true;
                }
            }
        }
        return false;
    }

    // "edit order 12" / "order 12 edit" / "edit order" (latest) / "آرڈر 12 تبدیل" opens edit mode for a saved order.
    private const string EditVerb = @"(?:edit|change|update|badlo|badlein|badalna|theek\s+karo|tabdeel|tabdeeli)";
    private static readonly Regex EditOrder = new(@"^" + EditVerb + @"\s+order(?:\s*#?(?<n>\d+))?$|^order\s*#?(?<n>\d+)\s+" + EditVerb +
        @"$|^(?:edit|ایڈٹ)\s+آرڈر(?:\s*(?<n>\d+))?$|^آرڈر\s*(?<n>\d+)\s+(?:تبدیل|بدلیں|ایڈٹ|درست)(?:\s+کریں)?$", Opts);

    private static readonly Regex EditDone = new(@"^(?:done|bas|save|ok|okay|theek\s+hai|ho\s+gaya|ٹھیک\s+ہے|ہو\s+گیا|بس)[.!]*$", Opts);
    private static readonly Regex EditQty = new(@"^(?:(?:qty|quantity|tadaad|item)\s*#?(?<i>\d+)\s*(?:=|:|ko|to|->)?\s*|#?(?<i>\d+)\s*(?:=|ko|to|->)\s*)(?<q>\d+)$", Opts);
    private static readonly Regex EditPrice = new(@"^(?:price|rate|qeemat|قیمت)\s*#?(?<i>\d+)\s*(?:=|:|ko|to|->)?\s*(?:rs\.?\s*)?(?<a>\d+(?:\.\d+)?)$", Opts);
    private static readonly Regex EditRemove = new(@"^(?:remove|delete|hatao|hata\s+do|nikalo)\s*(?:item\s*)?#?(?<i>\d+)$|^(?:item\s*)?#?(?<i>\d+)\s+(?:hatao|hata\s+do|remove|nikalo)$", Opts);
    private static readonly Regex EditAddLeadingQty = new(@"^(?:add|aur|jodo|daalo|dalo)\s+(?<q>\d+)\s*x?\s+(?<p>\D.*)$", Opts);
    private static readonly Regex EditAdd = new(@"^(?:add|aur|jodo|daalo|dalo)\s+(?<p>.+?)(?:\s+x?(?<q>\d+))?$", Opts);
    private static readonly Regex EditPhone = new(@"^(?:phone|fone|number|mobile|نمبر|فون)\s*:?\s*(?<t>\+?\d[\d\s-]{6,19})$", Opts);
    private static readonly Regex EditAddress = new(@"^(?:address|pata|patta|پتہ)\s*:?\s*(?<t>.{3,200})$", Opts | RegexOptions.Singleline);
    private static readonly Regex EditName = new(@"^(?:name|naam|نام)\s*:?\s*(?<t>.{2,60})$", Opts);
    private static readonly Regex EditPayment = new(@"^(?:payment|paisay|ادائیگی)\s*:?\s*(?<t>cod|cash|jazz\s*cash|easy\s*paisa|bank|advance|prepaid|online|card)$", Opts);

    /// <summary>Parses one change typed while editing a saved order.</summary>
    public static bool TryParseOrderEdit(string message, out OrderEditInstruction instruction)
    {
        var text = NormalizeDigits(LeadingSymbols.Replace(message.Trim(), "").Trim());
        Match m;
        instruction = new OrderEditInstruction("done");
        if (EditDone.IsMatch(text)) return true;
        if (TryParseDeliveryAmount(text, out var delivery)) { instruction = new("delivery", Amount: delivery); return true; }
        if ((m = EditPrice.Match(text)).Success) { instruction = new("price", int.Parse(m.Groups["i"].Value), Amount: decimal.Parse(m.Groups["a"].Value, System.Globalization.CultureInfo.InvariantCulture)); return true; }
        if ((m = EditQty.Match(text)).Success) { instruction = new("qty", int.Parse(m.Groups["i"].Value), int.Parse(m.Groups["q"].Value)); return true; }
        if ((m = EditRemove.Match(text)).Success) { instruction = new("remove", int.Parse(m.Groups["i"].Value)); return true; }
        if ((m = EditPhone.Match(text)).Success) { instruction = new("phone", Text: Regex.Replace(m.Groups["t"].Value, @"[\s-]", "")); return true; }
        if ((m = EditPayment.Match(text)).Success) { instruction = new("payment", Text: m.Groups["t"].Value.ToLowerInvariant()); return true; }
        if ((m = EditAddress.Match(text)).Success) { instruction = new("address", Text: m.Groups["t"].Value.Trim()); return true; }
        if ((m = EditName.Match(text)).Success) { instruction = new("name", Text: m.Groups["t"].Value.Trim()); return true; }
        if ((m = EditAddLeadingQty.Match(text)).Success || (m = EditAdd.Match(text)).Success)
        {
            instruction = new("add", Quantity: m.Groups["q"].Success ? int.Parse(m.Groups["q"].Value) : 1, Text: m.Groups["p"].Value.Trim());
            return true;
        }
        return false;
    }

    // A delivery charge written inside an order ("Sara, 1 kurti, 0300..., delivery 300" / "+250 delivery" / "free delivery").
    // Small bare numbers are left alone — "delivery 15 tareekh ko" is a date, not Rs.15 — unless "Rs"/"rupay" says it's money.
    private static readonly Regex DeliveryInTextAfter = new(@"(?<![\p{L}\p{N}])(?:delivery|deliveri|shipping|ڈیلیوری)(?:\s+(?:charges?|fee|fees|kharcha))?\s*[:=-]?\s*(?<rs>rs\.?\s*)?(?<n>\d{1,5})(?!\d)(?<rs2>\s*(?:rs|rupees?|rupay|روپے))?", Opts);
    private static readonly Regex DeliveryInTextBefore = new(@"(?<![\p{L}\p{N}])\+?\s*(?<rs>rs\.?\s*)?(?<n>\d{1,5})(?<rs2>\s*(?:rs|rupees?|rupay|روپے))?\s+(?:delivery|ڈیلیوری)(?:\s+(?:charges?|fee|kharcha))?(?![\p{L}])", Opts);
    private static readonly Regex DeliveryFreeInText = new(@"(?<![\p{L}])(?:free\s+delivery|delivery\s+free|فری\s+ڈیلیوری)(?![\p{L}])", Opts);

    /// <summary>Finds a delivery charge the seller wrote inside an order message; false when none is stated.</summary>
    public static bool TryFindDeliveryInOrderText(string message, out decimal amount)
    {
        amount = 0;
        var text = NormalizeDigits(message);
        if (DeliveryFreeInText.IsMatch(text)) return true;
        foreach (var regex in new[] { DeliveryInTextAfter, DeliveryInTextBefore })
        {
            foreach (Match m in regex.Matches(text))
            {
                var value = decimal.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture);
                var saysRupees = m.Groups["rs"].Success && m.Groups["rs"].Length > 0 || m.Groups["rs2"].Success && m.Groups["rs2"].Length > 0;
                if (value >= 50 || saysRupees)
                {
                    amount = value;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>"delivery 250" / "free delivery" -> the amount; used for the seller default and inside an order confirmation.</summary>
    public static bool TryParseDeliveryAmount(string message, out decimal amount)
    {
        var text = LeadingSymbols.Replace(message.Trim(), "").Trim();
        amount = 0;
        if (DeliveryFree.IsMatch(text)) return true;
        var m = DeliveryAmount.Match(text);
        if (!m.Success) return false;
        amount = decimal.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    // "shortcut off" / "shortcut on": the quick-action buttons that follow replies.
    private static readonly Regex Shortcuts = new(@"^(?:shortcuts?|quick\s+actions?)\s+(?<v>on|off|chalu|band)$", Opts);

    // "export" -> asks what; "export orders customers", "export all", "export orders 30 days", "customers export", "excel". One .xlsx comes back.
    private static readonly Regex ExportLead = new(@"^(?:data\s+)?(?:export|download|ایکسپورٹ|ڈاؤنلوڈ)(?:\s+(?<what>.+))?$|^(?:excel|xlsx)$", Opts);
    private static readonly Regex ExportTrail = new(@"^(?<what>.+?)\s+(?:export|excel|xlsx|download)$", Opts);
    private const string NotLetter = @"(?<![\p{L}\p{N}])";
    private const string NotLetterAfter = @"(?![\p{L}\p{N}])";
    private static readonly (string Name, Regex Pattern)[] ExportDatasetWords =
    {
        ("orders", new(NotLetter + @"(?:orders?|آرڈرز?)" + NotLetterAfter, Opts)),
        ("customers", new(NotLetter + @"(?:customers?|گاہک|کسٹمرز?)" + NotLetterAfter, Opts)),
        ("catalog", new(NotLetter + @"(?:catalog(?:ue)?|products?|کیٹلاگ)" + NotLetterAfter, Opts)),
        ("discounts", new(NotLetter + @"(?:discounts?|loyalty|coupons?|ڈسکاؤنٹس?)" + NotLetterAfter, Opts)),
    };
    private static readonly Regex ExportAll = new(NotLetter + @"(?:all|everything|sab(?:\s+kuch)?|poora|full|backup|سب)" + NotLetterAfter, Opts);
    private static readonly (string Period, Regex Pattern)[] ExportPeriodWords =
    {
        ("lastmonth", new(NotLetter + @"(?:last\s+month|pichle\s+mahine?|pichla\s+mah(?:ina)?|پچھلے\s+مہینے)" + NotLetterAfter, Opts)),
        ("30d", new(NotLetter + @"(?:30\s*(?:days?|din)|month|mahina|mahine|مہینہ)" + NotLetterAfter, Opts)),
        ("7d", new(NotLetter + @"(?:7\s*(?:days?|din)|week|hafta|ہفتہ)" + NotLetterAfter, Opts)),
        ("yesterday", new(NotLetter + @"(?:yesterday|kal|کل)" + NotLetterAfter, Opts)),
        ("today", new(NotLetter + @"(?:today|aaj|آج)" + NotLetterAfter, Opts)),
    };

    // "profit" (last 30 days), "profit today|yesterday|week|month|last month", "munafa", "منافع". Text = period key ("today", "yesterday", "lastmonth", "7d", "30d").
    // Anything else after the word (e.g. "profit margin on kurti") is not a profit command.
    private static readonly Regex ProfitLead = new(@"^(?:profit(?:\s+report)?|munafa|nafa|منافع|نفع)(?:\s+(?<p>.+))?$", Opts);

    private static ParsedCommand? TryParseProfit(string message)
    {
        var m = ProfitLead.Match(message);
        if (!m.Success) return null;
        if (!m.Groups["p"].Success) return new ParsedCommand { Kind = CommandKind.Profit, Text = "30d" };
        // A whole period phrase first ("last week", "this quarter", "1 May se 15 May"), then the older keyword scan.
        var period = ReportPeriods.TryParseKey(m.Groups["p"].Value, DateTime.UtcNow, out var phraseKey)
            ? phraseKey
            : ExportPeriodWords.FirstOrDefault(p => p.Pattern.IsMatch(m.Groups["p"].Value)).Period;
        return period is null ? null : new ParsedCommand { Kind = CommandKind.Profit, Text = period };
    }

    // Expenses: "expense 500 packaging", "kharcha Rs 1,200 rent", "expense packaging 500". Text = note (may be empty), Amount = value.
    private const string ExpenseWord = @"(?:expenses?|kharcha|kharch|kharcay|خرچہ|خرچ)";
    private const string ExpenseAmount = @"(?:rs\.?\s*)?(?<a>\d[\d,]*(?:\.\d{1,2})?)";
    private static readonly Regex ExpenseAmountFirst = new(@"^" + ExpenseWord + @"\s*:?\s*" + ExpenseAmount + @"(?:\s+(?<note>.+))?$", Opts);
    private static readonly Regex ExpenseNoteFirst = new(@"^" + ExpenseWord + @"\s*:?\s*(?<note>[^\d].*?)\s+" + ExpenseAmount + @"$", Opts);
    // "expenses", "expenses today|last month|this month" -> list. Text = "today" | "month" | "lastmonth".
    private static readonly Regex ExpenseListLead = new(@"^(?:expenses?|kharche|kharcha|اخراجات)(?:\s+(?<p>today|aaj|آج|this\s+month|month|is\s+mahine|last\s+month|pichle\s+mahine|pichla\s+mah(?:ina)?))?$", Opts);
    // "monthly net", "net", "is mahine ka net", "last month net".
    private static readonly Regex MonthlyNetLead = new(@"^(?:(?:monthly|month|mahana|mahaana)\s+net|net|is\s+mahine\s+ka\s+net|ماہانہ\s+نیٹ)(?:\s+(?<last>last\s+month|pichle\s+mahine|pichla\s+mah(?:ina)?))?$|^(?<last>last\s+month|pichle\s+mahine\s+ka)\s+net$", Opts);

    private static ParsedCommand? TryParseExpense(string message)
    {
        var m = ExpenseAmountFirst.Match(message);
        if (!m.Success) m = ExpenseNoteFirst.Match(message);
        if (m.Success && decimal.TryParse(m.Groups["a"].Value.Replace(",", ""), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var amount))
            return new ParsedCommand { Kind = CommandKind.Expense, Amount = amount, Text = m.Groups["note"].Success ? m.Groups["note"].Value.Trim() : "" };

        if ((m = ExpenseListLead.Match(message)).Success)
        {
            var p = m.Groups["p"].Success ? m.Groups["p"].Value.ToLowerInvariant() : "";
            var period = p.Contains("last") || p.StartsWith("pichl") ? "lastmonth" : p is "today" or "aaj" or "آج" ? "today" : "month";
            return new ParsedCommand { Kind = CommandKind.ExpenseList, Text = period };
        }
        if ((m = MonthlyNetLead.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.MonthlyNet, Text = m.Groups["last"].Success ? "lastmonth" : "month" };
        return null;
    }

    // "For how long": any report followed or preceded by a period phrase, in English / Roman Urdu / Urdu script —
    // "orders last quarter", "pichle hafte ka profit", "kharcha 1 May se 15 May", "aaj 2pm se 6pm ke orders", "کل کے آرڈرز".
    // The period itself is understood by ReportPeriods; ParsedCommand.Text carries its key. A phrase that is not entirely a period
    // (a customer's name, an order number) is left alone, so "Ayesha ka order" and "orders 12" are never taken for reports.
    private static readonly (Regex Words, CommandKind Kind)[] PeriodReportWords =
    {
        (new(@"^(?:orders?|order\s+list|آرڈرز?)$", Opts), CommandKind.OrdersToday),
        (new(@"^(?:summary|reconciliation|hisab|hisaab|sales?|bikri|خلاصہ|حساب|فروخت|سیلز)$", Opts), CommandKind.TodaysSummary),
        (new(@"^(?:profit|munafa|nafa|منافع|نفع)$", Opts), CommandKind.Profit),
        (new(@"^(?:expenses?|kharcha|kharche|kharch|اخراجات|خرچہ|خرچ)$", Opts), CommandKind.ExpenseList),
        (new(@"^net$", Opts), CommandKind.MonthlyNet),
        (new(@"^(?:customers|گاہک|کسٹمرز)$", Opts), CommandKind.CustomerList),
        (new(@"^(?:discounts?|discount\s+(?:report|performance)|coupons?|ڈسکاؤنٹس?)$", Opts), CommandKind.DiscountPerformance),
        (new(@"^(?:trending(?:\s+products?)?|top\s+products?|best\s*sellers?|best\s+selling(?:\s+products?)?)$", Opts), CommandKind.TrendingProducts),
        (new(@"^(?:slow\s+movers?|slow\s+products?)$", Opts), CommandKind.SlowMovers),
    };
    private static readonly Regex PeriodReportTail = new(
        @"(?:\s+(?:the|thay|tha|thi|hain|hai|aaye|aae|hue|hui|batao|bataein|bataiye|btao|dikhao|dikhana|dikhain|dekhao|dekhein|show|please|plz|بتاؤ|بتائیں|دکھاؤ|دکھائیں|تھے|تھا|ہیں|ہے|آئے|ہوئے))+$", Opts);
    private static readonly HashSet<string> OfTokens = new(StringComparer.OrdinalIgnoreCase) { "ka", "ke", "ki", "kay", "کا", "کے", "کی" };
    private static readonly HashSet<string> HowManyTokens = new(StringComparer.OrdinalIgnoreCase) { "kitne", "kitni", "kitna", "کتنے", "کتنی", "کتنا" };

    private static CommandKind? PeriodReportKind(string words) =>
        PeriodReportWords.Where(w => w.Words.IsMatch(words)).Select(w => (CommandKind?)w.Kind).FirstOrDefault();

    private static ParsedCommand? TryParsePeriodReport(string message)
    {
        var text = PeriodReportTail.Replace(message.Trim(), "").Trim();
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2 || tokens.Length > 10) return null;
        var now = DateTime.UtcNow;

        for (var i = 1; i < tokens.Length; i++)
        {
            // "<report> <period>": orders last quarter
            if (PeriodReportKind(string.Join(' ', tokens[..i])) is { } leading
                && ReportPeriods.TryParseKey(string.Join(' ', tokens[i..]), now, out var leadingKey))
                return new ParsedCommand { Kind = leading, Text = leadingKey };

            // "<period> ka/ke/ki [kitne] <report>": pichle hafte ka profit
            if (!OfTokens.Contains(tokens[i])) continue;
            var next = i + 1;
            if (next < tokens.Length && HowManyTokens.Contains(tokens[next])) next++;
            if (next >= tokens.Length) continue;
            if (PeriodReportKind(string.Join(' ', tokens[next..])) is { } trailing
                && ReportPeriods.TryParseKey(string.Join(' ', tokens[..i]), now, out var trailingKey))
                return new ParsedCommand { Kind = trailing, Text = trailingKey };
        }

        return null;
    }

    public static ExportRequest ParseExportRequest(string? what)
    {
        var text = what ?? "";
        var datasets = ExportAll.IsMatch(text)
            ? ExportDatasetWords.Select(d => d.Name).ToList()
            : ExportDatasetWords.Where(d => d.Pattern.IsMatch(text)).Select(d => d.Name).ToList();
        // A whole period phrase ("last quarter", "this year", "1 May se 15 May", "aaj 2pm se 6pm") once the dataset words are taken out;
        // otherwise the older keyword scan ("30 days", "last month", "today", "kal").
        var rest = text;
        foreach (var d in ExportDatasetWords) rest = d.Pattern.Replace(rest, " ");
        rest = ExportAll.Replace(rest, " ");
        rest = Regex.Replace(rest, @"[,&]|(?<![\p{L}\p{N}])(?:and|aur|اور)(?![\p{L}\p{N}])", " ");
        rest = Regex.Replace(rest, @"\s+", " ").Trim();
        var period = rest.Length > 0 && ReportPeriods.TryParseKey(rest, DateTime.UtcNow, out var phraseKey)
            ? phraseKey
            : ExportPeriodWords.FirstOrDefault(p => p.Pattern.IsMatch(text)).Period;
        return new ExportRequest(datasets, period);
    }

    // "logo" / "banner" explain how to set one; "remove logo" clears it. Text is "logo" or "banner".
    private const string BrandingWord = @"(?<k>logo|banner|لوگو|بینر)";
    private static readonly Regex BrandingHelp = new(@"^(?:(?:receipt|set|my)\s+)?" + BrandingWord + "$", Opts);
    private static readonly Regex RemoveBranding = new(@"^(?:remove|delete|clear)\s+(?:receipt\s+)?" + BrandingWord + @"$|^" + BrandingWord + @"\s+(?:hatao|hata\s+do|remove)$", Opts);

    private static string BrandingKind(string word) => word.ToLowerInvariant() is "logo" or "لوگو" ? "logo" : "banner";

    private static readonly Regex BrandingWordAnywhere = new(@"(?<![\p{L}\p{N}])" + BrandingWord + @"(?![\p{L}\p{N}])", Opts);
    // A free-text wish to brand receipts, e.g. "receipt mein apna logo lagana hai", "bill par image kaise lagaon". No digits, so product
    // lines ("Logo T-shirt - 1500") never match: either an image-ish word plus a receipt word, or logo/banner plus a "want to" verb.
    private static readonly Regex BrandingImageWish = new(@"(?<![\p{L}\p{N}])(?:logo|banner|image|photo|picture|tasveer|tasweer|لوگو|بینر|تصویر)(?![\p{L}\p{N}])", Opts);
    private static readonly Regex ReceiptWish = new(@"(?<![\p{L}\p{N}])(?:receipt|invoice|bill|rasid|raseed|رسید)(?![\p{L}\p{N}])", Opts);
    private static readonly Regex WantVerb = new(@"lagan|lagao|lagaon|lagani|lagay|add|set|upload|use|rakh|chahiye|chahta|chahti|change|badal|لگا|چاہی|بدل", Opts);

    /// <summary>
    /// A caption on a picture marks it as receipt branding: exactly "logo"/"banner"/"لوگو"/"بینر" (optionally "receipt logo"),
    /// or a short sentence naming one ("ye mera logo hai", "receipt ke liye banner").
    /// </summary>
    public static bool TryParseBrandingCaption(string? caption, out string kind)
    {
        kind = "";
        var text = (caption ?? "").Trim();
        if (text.Length == 0 || text.Length > 60) return false;
        var m = BrandingHelp.Match(text);
        if (!m.Success) m = BrandingWordAnywhere.Match(text);
        if (!m.Success) return false;
        kind = BrandingKind(m.Groups["k"].Value);
        return true;
    }

    private static bool TryParseBrandingWish(string message, out string? kind)
    {
        kind = null;
        if (message.Length > 90 || message.Any(char.IsDigit) || !BrandingImageWish.IsMatch(message)) return false;
        if (!ReceiptWish.IsMatch(message) && !(BrandingWordAnywhere.IsMatch(message) && WantVerb.IsMatch(message))) return false;
        if (BrandingWordAnywhere.Match(message) is { Success: true } m) kind = BrandingKind(m.Groups["k"].Value);
        return true;
    }

    // "receipt" (latest order) / "receipt 12" / "receipt Ayesha" / "Ayesha ki receipt" / "رسید 12". Number or Text is the order reference.
    private const string ReceiptWord = @"(?:receipt|invoice|rasid|raseed|bill|رسید)";
    private static readonly Regex Receipt = new(@"^(?:(?:order|pdf)\s+)?" + ReceiptWord + @"(?:\s*:?\s*#?(?<n>\d+)|\s*:?\s+(?<name>.+))?$|^(?<name>.+?)\s+(?:ki|ka|ke)\s+" + ReceiptWord + "$", Opts);
    private static readonly Regex DiscountPerformance = new(@"^discount\s+(?:performance|report)$", Opts);
    private static readonly Regex WeeklySummary = new(@"^(weekly\s+summary|week\s+ka\s+summary|ہفتہ\s+وار\s+خلاصہ|ہفتے\s+کا\s+خلاصہ)$", Opts);
    private static readonly Regex UpdateBusinessInfo = new(@"^(update\s+)?business\s+(info|details)$", Opts);
    private static readonly Regex Subscribe = new(@"^(subscribe|subscription|upgrade|plans?)$", Opts);
    private static readonly Regex BroadcastNatural = new(@"\b(?:sab|saare|sare|all)\s+customers?\s+ko\s+(?:batao|bata\s+do|bhejo|bhej\s+do|message\s+karo)\s*:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex AddProduct = new(@"^add\s+product:\s*(.+?)\s*-\s*(\d+(?:\.\d+)?)$", Opts);
    private static readonly Regex EditProduct = new(@"^edit\s+product:\s*(.+?)\s*-\s*(\d+(?:\.\d+)?)$", Opts);
    private static readonly Regex MarkAllPendingShipped = new(@"^mark\s+all\s+pending\s+as\s+shipped$", Opts);
    // "mark 3 shipped" / "mark 3 bhej diya" / "آرڈر 3 شپ ہو گیا": the verb phrase is mapped by StatusFromPhrase.
    private static readonly Regex MarkStatus = new(@"^(?:mark|order|آرڈر)\s+(\d+)\s+(.+)$", Opts);
    private const string DoneWords = @"(?:\s+(?:ho\s*(?:gaya|gya|gaye|gai|gayi)|kar\s+(?:diya|dia|do|dein|den)|ہو\s+گیا(?:\s+ہے)?|کر\s+(?:دیا|دو|دیں)))?";
    private static readonly Regex ShippedPhrase = new(@"^(?:shipped?" + DoneWords + @"|bhej\s*(?:diya|dia|di|do)|شپ" + DoneWords + @"|بھیج\s+(?:دیا|دو))$", Opts);
    private static readonly Regex DeliveredPhrase = new(@"^(?:deliver(?:ed)?" + DoneWords + @"|(?:pohanch|pahunch|pohnch)\s+gaya|ڈیلیور" + DoneWords + @"|پہنچ\s+گیا"
        // "complete" is the seller's word for the final state: delivered.
        + @"|(?:complete(?:d)?|mukammal|mukamal)" + DoneWords + @"|(?:مکمل|کمپلیٹ(?:ڈ)?)" + DoneWords + @")$", Opts);
    private static readonly Regex PaidPhrase = new(@"^(?:paid" + DoneWords + @"|payment\s+(?:aa|mil)\s+(?:gayi|gai)|پیڈ" + DoneWords + @"|ادائیگی\s+ہو\s+گئی)$", Opts);
    private static readonly Regex PendingPhrase = new(@"^(?:pending|پینڈنگ)$", Opts);
    private static readonly Regex ReturnedPhrase = new(@"^(?:return(?:ed)?" + DoneWords + @"|(?:wapas|wapis|waapas)(?:\s+(?:aa|a|aya|agaya|aa\s*gaya|aa\s*gya|ho\s*gaya|ho\s*gya|kar\s+diya))?|واپس(?:\s+(?:آ\s+گیا|آیا|ہو\s+گیا))?|ریٹرن)$", Opts);

    private static string? StatusFromPhrase(string phrase)
    {
        phrase = phrase.Trim();
        if (ShippedPhrase.IsMatch(phrase)) return "shipped";
        if (DeliveredPhrase.IsMatch(phrase)) return "delivered";
        if (PaidPhrase.IsMatch(phrase)) return "paid";
        if (PendingPhrase.IsMatch(phrase)) return "pending";
        if (ReturnedPhrase.IsMatch(phrase)) return "returned";
        return null;
    }
    private static readonly Regex CancelOrder = new(@"^cancel\s+order\s+(\d+)$", Opts);
    private static readonly Regex Undo = new(@"^undo$", Opts);
    private static readonly Regex PaymentLink = new(@"^payment\s+link(?:\s+(\d+))?$", Opts);
    private static readonly Regex AddPaymentMethod = new(@"^add\s+payment:\s*(jazzcash|easypaisa|bank|safepay)\s*(?:,\s*(.+))?$", Opts);
    private static readonly Regex UnpaidOrders = new(@"^unpaid\s+orders?$", Opts);
    private static readonly Regex CodPending = new(@"^cod\s+pending$", Opts);
    private static readonly Regex AddTracking = new(@"^add\s+tracking:\s*(.+?)\s*,\s*(.+)$", Opts);
    private static readonly Regex TrackingLookup = new(@"^(.+?)\s+(?:ka|کا)\s+(?:tracking|ٹریکنگ)$", Opts);
    private static readonly Regex CustomerOrderLookup = new(@"^(.+?)\s+" + Of + @"\s+(?:order|آرڈر)$", Opts);
    private static readonly Regex FuzzyStatusUpdate = new(@"^(.+?)\s+" + Of + @"\s+(?:order|آرڈر)\s+.*?(deliver|ship|pending|bhej|return|wapas|wapis|waapas|ڈیلیور|شپ|پینڈنگ|واپس|ریٹرن)", Opts);
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

    // "Polo Shirt - 500, cost 300, stock 10": the first number is the sale price, the rest are comma/semicolon/pipe separated details.
    private static readonly Regex ProductWithDetails = new(@"^([^\d,;:|\n]{2,50}?)\s*[-–=]\s*(?:rs\.?\s*)?(\d{1,7}(?:\.\d+)?)\s*[,;|]\s*(.+)$", Opts);
    private static readonly Regex KnownDetail = new(
        @"^(?<key>cost price|purchase price|kharid price|buying price|cost|purchase|kharid|khareed|stock|quantity|qty|tadad|maal|colour|color|rang|size|category|qism|kism|sku)\b\s*[:=]?\s*(?<val>.+)$", Opts);
    private static readonly Regex AttributeDetail = new(@"^(?<key>[^\d:=]{2,30}?)\s*:\s*(?<val>.+)$", Opts);

    // The words sellers use for who supplied/made/sorted a product, folded onto one stored name so "vendor: X" and "supplier: X" group together.
    private static readonly Dictionary<string, string> DetailAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["category"] = "category", ["categories"] = "category", ["qism"] = "category", ["kism"] = "category",
        ["vendor"] = "vendor", ["vendors"] = "vendor", ["supplier"] = "vendor", ["suppliers"] = "vendor", ["wholesaler"] = "vendor", ["wholesalers"] = "vendor",
        ["dealer"] = "vendor", ["dealers"] = "vendor",
        ["manufacturer"] = "manufacturer", ["manufacturers"] = "manufacturer", ["maker"] = "manufacturer", ["makers"] = "manufacturer", ["mfg"] = "manufacturer",
        ["company"] = "manufacturer", ["companies"] = "manufacturer", ["factory"] = "manufacturer",
        ["department"] = "department", ["dept"] = "department", ["section"] = "department",
        ["brand"] = "brand",
    };

    /// <summary>"Supplier" -> "vendor", "Dept" -> "department"; any other name is returned as written (trimmed).</summary>
    public static string CanonicalDetailName(string name)
    {
        var trimmed = name.Trim();
        return DetailAliases.TryGetValue(trimmed, out var canonical) ? canonical : trimmed;
    }

    // "products vendor Ali Traders", "catalog category Shirts", "products by department": list the catalog filtered on one detail.
    // Without a value it lists the values in use. Text = canonical field, Text2 = value (null = list values).
    private static readonly Regex CatalogFilter = new(
        @"^(?:products?|catalog|items?)\s+(?:by\s+|ka\s+)?(?<f>categor(?:y|ies)|qism|kism|vendors?|suppliers?|wholesalers?|dealers?|manufacturers?|makers?|mfg|company|companies|factory|department|dept|section|brand)s?\b\s*[:=]?\s*(?<v>.*)$", Opts);

    private static bool TryParseProductDetails(string rest, out ProductExtras? extras)
    {
        extras = null;
        decimal? cost = null; int? stock = null;
        string? category = null, size = null, color = null, sku = null;
        Dictionary<string, string>? attributes = null;
        var any = false;

        foreach (var part in rest.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (KnownDetail.Match(part) is { Success: true } known)
            {
                var key = known.Groups["key"].Value.ToLowerInvariant();
                var value = known.Groups["val"].Value.Trim();
                var number = Regex.Match(value.Replace(",", ""), @"^(?:rs\.?\s*)?(\d{1,7})(?:\.\d+)?$", RegexOptions.IgnoreCase);
                switch (key)
                {
                    case "cost" or "cost price" or "purchase" or "purchase price" or "kharid" or "khareed" or "kharid price" or "buying price":
                        if (!number.Success) return false;
                        cost = decimal.Parse(Regex.Match(value.Replace(",", ""), @"\d+(?:\.\d+)?").Value, System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case "stock" or "qty" or "quantity" or "tadad" or "maal":
                        if (!number.Success) return false;
                        stock = int.Parse(number.Groups[1].Value);
                        break;
                    case "color" or "colour" or "rang": color = value; break;
                    case "size": size = value; break;
                    case "category" or "qism" or "kism": category = value; break;
                    default: sku = value; break;
                }
            }
            else if (AttributeDetail.Match(part) is { Success: true } attribute)
            {
                // Any other detail must be written "name: value" so ordinary words are never mistaken for an attribute.
                var name = CanonicalDetailName(attribute.Groups["key"].Value);
                if (name == "category") category = attribute.Groups["val"].Value.Trim();
                else (attributes ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))[name] = attribute.Groups["val"].Value.Trim();
            }
            else return false;
            any = true;
        }

        if (!any) return false;
        extras = new ProductExtras(cost, stock, category, size, color, sku, attributes);
        return true;
    }

    public static bool TryParseProductLine(string line, out ProductLine? product)
    {
        product = null;
        var text = line.Trim();

        var detailed = ProductWithDetails.Match(text);
        if (detailed.Success && TryParseProductDetails(detailed.Groups[3].Value, out var extras))
        {
            product = new ProductLine(detailed.Groups[1].Value.Trim(), decimal.Parse(detailed.Groups[2].Value), "piece", 1, extras);
            return true;
        }
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
    private static readonly Regex NewOrderHelp = new(@"^(?:new|naya|nya|add|create|make)\s+orders?$|^نیا\s+آرڈر$|^(?:naya\s+)?orders?\s+(?:add|darj|likhna|karna|dalna)(?:\s+(?:karna|karni|hai|karein|krna))*$", Opts);
    private static readonly Regex HowTo =new(@"^(?:add|new|create|make)\s+(discount|product|payment|loyalty|tracking)s?$", Opts);
    // Spoken/Roman Urdu "[ek HBL 50 ka] discount naya bana dein" / "ڈسکاؤنٹ نیا بنا دیں": same as "create discount" with no details.
    private static readonly Regex NaturalCreateDiscount = new(
        @"^(?:[^\n,:]{0,40}?\s)?(?:(?:naya|nya|new|نیا)\s+)?(?:discount|ڈسکاؤنٹ)\s+(?:(?:naya|nya|new|نیا)\s+)?(?:bana\w*(?:\s+(?:do|dein|den|dijiye|karo))?|add\s+kar\w*|بنا\w*(?:\s*(?:دیں|دو))?)[.!۔\s]*$", Opts);

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

    // Handler contract: Text2 starts with "deliver" / "ship" / "pending".
    private static string FuzzyStatusKeyword(string word)
    {
        word = word.ToLowerInvariant();
        if (word.ToLowerInvariant() is "return" or "wapas" or "wapis" or "waapas" or "واپس" or "ریٹرن") return "return";
        return word.StartsWith("deliver") || word == "ڈیلیور" ? "deliver"
            : word.StartsWith("ship") || word.StartsWith("bhej") || word == "شپ" ? "ship"
            : "pending";
    }

    // Eastern Arabic-Indic (٠-٩) and Extended (۰-۹) digits that Urdu keyboards produce -> ASCII, so "آرڈر ۳ شپ ہو گیا" parses.
    private static string NormalizeDigits(string text)
    {
        if (!text.Any(c => c is >= '\u0660' and <= '\u0669' or >= '\u06F0' and <= '\u06F9')) return text;
        return string.Create(text.Length, text, (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
            {
                var c = src[i];
                span[i] = c is >= '\u0660' and <= '\u0669' ? (char)('0' + (c - '\u0660'))
                    : c is >= '\u06F0' and <= '\u06F9' ? (char)('0' + (c - '\u06F0'))
                    : c;
            }
        });
    }

    // Words a speaker (or a speech-to-text transcript) puts in front of a command: "acha, orders today", "bhai suno order 3 shipped".
    // Not "zara": it is also a common first name ("Zara ka order").
    private static readonly Regex LeadingFiller = new(
        @"^(?:(?:assalam(?:u|o)?\s*(?:o\s*)?alaikum|asalam\s*o\s*alaikum|salam|hello(?:\s+bot)?|hey|hi|acha|accha|achha|bhai(?:jan)?|jee|ji|umm+|um+|uh+|hmm+|suno|yaar|please|plz|dekho|okay\s+so|ok\s+so|haan\s+to|bot|اچھا|بھائی|سنو|جی|سلام|ہیلو)(?![\p{L}\p{N}])[\s,.:!-]*)+", Opts);
    private static readonly Regex TrailingPunctuation = new(@"[\s.!?,;:…،۔؟]+$", RegexOptions.Compiled);

    // A spoken negation, question or plan ("deliver nahi hua", "kab ship hoga", "kya deliver ho gaya", "abhi tak pending hai"): it reports or asks
    // about a status, it does not set one.
    private static readonly Regex NotAStatusChange = new(
        @"(?<![\p{L}\p{N}])(?:nahi|nahin|nai|nhi|nah|mat|na\s+hua|na\s+hui|kab|kya|kyun|kyu|kaise|kahan|kaha|kidhar|kitne|when|why|not|abhi\s+tak|hoga|hogi|hongay|honge|chahiye|should|was|has|did|will)(?![\p{L}\p{N}])|\?|؟|نہیں|نہ|کب|کیا|کیوں|کہاں|گا(?![\p{L}])|گی(?![\p{L}])", Opts);

    /// <summary>
    /// Parses a typed or transcribed message into a command. Leading fillers ("acha", "bhai", "please") and trailing sentence punctuation
    /// (speech-to-text ends sentences with "." or "?") are ignored; a question mark also stops a status question from being read as an update.
    /// </summary>
    public static ParsedCommand? TryParse(string rawMessage)
    {
        var text = rawMessage.Trim();
        var filler = LeadingFiller.Match(text);
        if (filler.Success && filler.Length < text.Length) text = text[filler.Length..].Trim();

        var parsed = ParseCore(text, wasQuestion: false);
        if (parsed is not null) return parsed;

        var bare = TrailingPunctuation.Replace(text, "");
        if (bare.Length == 0 || bare.Length == text.Length) return null;
        return ParseCore(bare, wasQuestion: text[bare.Length..].Contains('?') || text[bare.Length..].Contains('؟'));
    }

    private static ParsedCommand? ParseCore(string rawMessage, bool wasQuestion)
    {
        var message = NormalizeDigits(rawMessage.Trim());
        // A tapped button keeps its emoji ("📋 Menu", "⚙️ Business Setup") — parse the words.
        var stripped = LeadingSymbols.Replace(message, "").Trim();
        if (stripped.Length > 0) message = stripped;
        if (message.StartsWith('/') && ExpandSlashCommand(message) is { } expanded) message = expanded;
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
        if (OrdersYesterday.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.OrdersToday, Text = "yesterday" };
        if (OrdersLastMonth.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.OrdersToday, Text = "lastmonth" };
        if (PendingOrders.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.PendingOrders };
        if (TodaysSummary.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.TodaysSummary };
        if (YesterdaysSummary.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.TodaysSummary, Text = "yesterday" };
        if (LastMonthSummary.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.TodaysSummary, Text = "lastmonth" };
        if ((m = CatalogFilter.Match(message)).Success)
        {
            var value = m.Groups["v"].Value.Trim();
            return new ParsedCommand { Kind = CommandKind.CatalogFilter, Text = CanonicalDetailName(m.Groups["f"].Value), Text2 = value.Length == 0 ? null : value };
        }
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
        if (TryParseExpense(message) is { } expense) return expense;
        if (StockList.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.Stock };
        if ((m = StockOff.Match(message)).Success) return new ParsedCommand { Kind = CommandKind.Stock, Text = m.Groups["name"].Value.Trim(), Text2 = "off" };
        if ((m = StockSet.Match(message)).Success)
            return new ParsedCommand
            {
                Kind = CommandKind.Stock, Text = m.Groups["name"].Value.Trim(), Text2 = m.Groups["sign"].Success ? "add" : "set",
                Amount = decimal.Parse(m.Groups["n"].Value)
            };
        if ((m = OrderDetail.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.OrderDetail, Number = int.Parse(m.Groups["n"].Value) };
        if ((m = OrderPayment.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.OrderPayment, Number = int.Parse(m.Groups["id"].Value),
                Amount = decimal.Parse(m.Groups["a"].Value, System.Globalization.CultureInfo.InvariantCulture) };
        if ((m = EditOrder.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.EditOrder, Number = m.Groups["n"].Success ? int.Parse(m.Groups["n"].Value) : null };
        if ((m = OrderDelivery.Match(message)).Success)
            return new ParsedCommand
            {
                Kind = CommandKind.OrderDeliveryCharge, Number = int.Parse(m.Groups["id"].Value),
                Amount = m.Groups["n"].Success && m.Groups["n"].Value != "free" ? decimal.Parse(m.Groups["n"].Value) : 0
            };
        if ((m = OrderTaxWithheld.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.OrderTaxWithheld, Number = int.Parse(m.Groups["id"].Value),
                Amount = decimal.Parse(m.Groups["a"].Value, System.Globalization.CultureInfo.InvariantCulture) };
        if ((m = TaxId.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.TaxSettings, Text = m.Groups["k"].Value.ToLowerInvariant(), Text2 = m.Groups["v"].Success ? m.Groups["v"].Value.Trim() : null };
        if ((m = SalesTaxRate.Match(message)).Success)
            return new ParsedCommand
            {
                Kind = CommandKind.TaxSettings, Text = "rate", Text2 = m.Groups["n"].Success ? "set" : m.Groups["off"].Success ? "off" : null,
                Amount = m.Groups["n"].Success ? decimal.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture) : null
            };
        if (TaxShow.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.TaxSettings, Text = "show" };
        if (TryParseDeliveryAmount(message, out var deliveryAmount))
            return new ParsedCommand { Kind = CommandKind.DeliveryCharge, Amount = deliveryAmount };
        if (DeliveryShow.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.DeliveryCharge };
        if ((m = Shortcuts.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.Shortcuts, Text = m.Groups["v"].Value.ToLowerInvariant() is "on" or "chalu" ? "on" : "off" };
        if (TryParseProfit(message) is { } profit) return profit;
        if ((m = ExportLead.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.Export, Export = ParseExportRequest(m.Groups["what"].Success ? m.Groups["what"].Value : null) };
        if ((m = ExportTrail.Match(message)).Success && ParseExportRequest(m.Groups["what"].Value) is { Datasets.Count: > 0 } trailing)
            return new ParsedCommand { Kind = CommandKind.Export, Export = trailing };
        if (TryParsePeriodReport(message) is { } periodReport) return periodReport;
        if ((m = RemoveBranding.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.RemoveBranding, Text = BrandingKind(m.Groups["k"].Value) };
        if ((m = BrandingHelp.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.BrandingHelp, Text = BrandingKind(m.Groups["k"].Value) };
        if (TryParseBrandingWish(message, out var wishKind))
            return new ParsedCommand { Kind = CommandKind.BrandingHelp, Text = wishKind };
        if ((m = Receipt.Match(message)).Success)
            return new ParsedCommand
            {
                Kind = CommandKind.Receipt,
                Number = m.Groups["n"].Success ? int.Parse(m.Groups["n"].Value) : null,
                Text = m.Groups["name"].Success ? m.Groups["name"].Value.Trim() : null
            };
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

        if (!wasQuestion && TryParseCustomerUpdate(message) is { } customerUpdate) return customerUpdate;
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

        if ((m = StatusPicker.Match(message)).Success && !OrderDetail.IsMatch(message))
            return new ParsedCommand { Kind = CommandKind.StatusPicker, Number = m.Groups["n"].Success ? int.Parse(m.Groups["n"].Value) : null };

        if ((m = MarkStatus.Match(message)).Success && StatusFromPhrase(m.Groups[2].Value) is { } markStatus)
            return new ParsedCommand { Kind = CommandKind.MarkStatus, Number = int.Parse(m.Groups[1].Value), Text = markStatus };

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

        if (!wasQuestion && !NotAStatusChange.IsMatch(message) && (m = FuzzyStatusUpdate.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.FuzzyStatusUpdate, Text = m.Groups[1].Value.Trim(), Text2 = FuzzyStatusKeyword(m.Groups[2].Value) };

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

        if (NaturalCreateDiscount.IsMatch(message))
            return new ParsedCommand { Kind = CommandKind.HowTo, Text = "discount" };

        var lines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 1 && TryParseProductLine(lines[0], out ProductLine? line))
            return new ParsedCommand { Kind = CommandKind.AddProduct, Text = line!.Name, Amount = line.Price, Product = line };
        if (lines.Length > 1 && lines.All(l => TryParseProductLine(l, out ProductLine? _)))
            return new ParsedCommand { Kind = CommandKind.AddProductsBulk, Text = message };
        if (lines.Length == 1 && TryParseInlineProducts(lines[0]) is { } inlineProducts)
            return new ParsedCommand { Kind = CommandKind.AddProductsBulk, Text = inlineProducts };

        return FuzzyCommand(message);
    }

    // "Shirt 200 and pants 800" / "Shirt - 300, pant - 500": several products on one line, with or without dashes.
    private static readonly Regex InlineProductSplit = new(@"\s+(?:and|aur|&)\s+|\s*[,;+&]\s*", Opts);
    private static readonly Regex SpacedPriceLine = new(@"^([^\d,:\n]{3,50}?)\s+(?:rs\.?\s*|pkr\s*)?(\d{2,7})$", Opts);
    private static readonly Regex PhoneLike = new(@"(?<!\d)(?:\+?92[-\s]?|0)3\d{2}[-\s]?\d{7}(?!\d)", Opts);

    /// <summary>Returns the products as newline-separated "name - price" lines, or null unless EVERY part is a product (a phone number means it is an order).</summary>
    private static string? TryParseInlineProducts(string text)
    {
        if (PhoneLike.IsMatch(text)) return null;
        var parts = InlineProductSplit.Split(text).Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
        if (parts.Length < 2) return null;

        var normalized = new List<string>();
        foreach (var part in parts)
        {
            if (TryParseProductLine(part, out ProductLine? _)) normalized.Add(part);
            else if (SpacedPriceLine.Match(part) is { Success: true } m) normalized.Add($"{m.Groups[1].Value.Trim()} - {m.Groups[2].Value}");
            else return null;
        }
        return string.Join("\n", normalized);
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

    // "What do I do / say now?" — answered with a tip for the current step in ANY state (the plain words help / guide / menu keep their old meaning).
    private static readonly Regex GuidanceRequest = new(
        @"^(?:(?:ab\s+)?kya\s+kar(?:un|oon|u)(?:\s+ab)?|kaise\s+kar(?:un|oon|u)|kya\s+(?:bolun|bolu|likhun|likhu)|samajh\s+nahi\s+aa\s+raha|(?:mujhe\s+)?(?:madad|help)\s+chahiye|help\s+me|need\s+help|guidelines?|tips?|what\s+now|what\s+can\s+i\s+(?:do|say|type)|\?+|کیا\s+کروں|مجھے\s+مدد\s+چاہیے)[.!?]*$", Opts);

    public static bool IsGuidanceRequest(string message) => GuidanceRequest.IsMatch(message.Trim());

    public static bool IsAffirmative(string message) => ConfirmYes.IsMatch(message.Trim());
    public static bool IsNegative(string message) => ConfirmNo.IsMatch(message.Trim());
}
