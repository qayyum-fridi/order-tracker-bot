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
    Broadcast
}

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
}

public static class CommandParser
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private static readonly Regex Start = new(@"^start$", Opts);
    private static readonly Regex Greeting = new(@"^(hi|hello|hey|salam|assalam[u]?\s*alaikum|asalam[u]?\s*alaikum)$", Opts);
    private static readonly Regex Help = new(@"^(help|مدد)$", Opts);
    private static readonly Regex Menu = new(@"^(menu|مینو)$", Opts);
    private static readonly Regex OrdersToday = new(@"^(orders?\s+today|آج\s+کے\s+آرڈرز)$", Opts);
    private static readonly Regex PendingOrders = new(@"^(pending\s+orders?|پینڈنگ\s+آرڈرز)$", Opts);
    private static readonly Regex TodaysSummary = new(@"^(today'?s\s+summary|آج\s+کا\s+خلاصہ)$", Opts);
    private static readonly Regex Catalog = new(@"^(catalog|کیٹلاگ)$", Opts);
    private static readonly Regex ShareCatalog = new(@"^share\s+catalog$", Opts);
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
    private static readonly Regex CreateLoyalty = new(@"^create\s+loyalty:\s*(\d+)\s+orders?\s*=\s*(\d+(?:\.\d+)?)\s*%?\s*off$", Opts);
    private static readonly Regex LoyalCustomers = new(@"^loyal\s+customers?$", Opts);
    private static readonly Regex TrendingProducts = new(@"^trending\s+products?$", Opts);
    private static readonly Regex SlowMovers = new(@"^slow\s+movers?$", Opts);
    private static readonly Regex CustomerFeedbackList = new(@"^customer\s+feedback$", Opts);
    private static readonly Regex ResetAccount = new(@"^(reset|delete)\s+account$", Opts);
    private static readonly Regex Feedback = new(@"^feedback:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex Broadcast = new(@"^broadcast:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    // "Lawn Suit - 3500" / "Lawn suite-3500" / "Kurti = 1800" with no command prefix. The name has no digits,
    // commas or colons, so real orders ("Sara, 1 kurti, 0300...") never match.
    private static readonly Regex BareProductLine = new(@"^([^\d,:\n]{2,50}?)\s*[-–=]\s*(\d{1,7}(?:\.\d+)?)$", Opts);

    public static bool TryParseProductLine(string line, out string name, out decimal price)
    {
        var m = BareProductLine.Match(line.Trim());
        name = m.Success ? m.Groups[1].Value.Trim() : "";
        price = m.Success ? decimal.Parse(m.Groups[2].Value) : 0;
        return m.Success && name.Length > 0;
    }

    // "add discount" / "new product" with no details: show the exact format instead of guessing via the AI.
    private static readonly Regex HowTo = new(@"^(?:add|new|create|make)\s+(discount|product|payment|loyalty|tracking)s?$", Opts);

    private static readonly Regex SafepayId = new(@"^safepay\s+id:\s*(.+)$", Opts);

    public static ParsedCommand? TryParse(string rawMessage)
    {
        var message = rawMessage.Trim();
        Match m;

        if (Start.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.Start };
        if (Greeting.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.Greeting };
        if (Help.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.Help };
        if (Menu.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.Menu };
        if (OrdersToday.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.OrdersToday };
        if (PendingOrders.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.PendingOrders };
        if (TodaysSummary.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.TodaysSummary };
        if (Catalog.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.Catalog };
        if (ShareCatalog.IsMatch(message)) return new ParsedCommand { Kind = CommandKind.ShareCatalog };

        if ((m = AddProduct.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.AddProduct, Text = m.Groups[1].Value.Trim(), Amount = decimal.Parse(m.Groups[2].Value) };

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

        if ((m = Broadcast.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.Broadcast, Text = m.Groups[1].Value.Trim() };

        if ((m = SafepayId.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.AddPaymentMethod, Text = "safepay", Text2 = m.Groups[1].Value.Trim() };

        if ((m = HowTo.Match(message)).Success)
            return new ParsedCommand { Kind = CommandKind.HowTo, Text = m.Groups[1].Value.ToLowerInvariant() };

        var lines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 1 && TryParseProductLine(lines[0], out var name, out var price))
            return new ParsedCommand { Kind = CommandKind.AddProduct, Text = name, Amount = price };
        if (lines.Length > 1 && lines.All(l => TryParseProductLine(l, out _, out _)))
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
        ("share catalog", CommandKind.ShareCatalog), ("menu", CommandKind.Menu), ("help", CommandKind.Help)
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
