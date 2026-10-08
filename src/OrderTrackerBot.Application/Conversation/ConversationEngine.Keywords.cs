using System.Text.RegularExpressions;

namespace OrderTrackerBot.Application.Conversation;

public partial class ConversationEngine
{
    private const RegexOptions KeywordOpts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>
    /// Feature keywords (Roman Urdu / English / Urdu script) that mean the seller is talking about something the bot can do.
    /// Every option is a typed command, so picking one runs it. Most specific areas first: the first hit wins, and generic words
    /// (product, order) come last so "ye naya discount product ke liye hai" is a discount question, not a catalog one.
    /// </summary>
    public static readonly (string Question, Regex Keywords, string[] Options)[] CommandKeywordAreas =
    {
        ("🎟️ Discount ke baare mein — aap kya karna chahte hain?",
            new(@"(?<![\p{L}\p{N}])(?:discounts?|coupons?|promo(?:\s*code)?|offers?|ڈسکاؤنٹ\w*|کوپن|چھوٹ|رعایت)(?![\p{L}\p{N}])", KeywordOpts),
            new[] { "create discount", "discount list", "discount performance" }),
        ("💸 Kharche ke baare mein — aap kya dekhna chahte hain?",
            new(@"(?<![\p{L}\p{N}])(?:expenses?|kharcha|kharch|kharcay|خرچہ|خرچ)(?![\p{L}\p{N}])", KeywordOpts),
            new[] { "expenses", "monthly net" }),
        ("🧾 Receipt ke baare mein — aap kya karna chahte hain?",
            new(@"(?<![\p{L}\p{N}])(?:receipts?|rasid|raseed|رسید)(?![\p{L}\p{N}])", KeywordOpts),
            new[] { "receipt" }),
        ("📤 Export ke baare mein — aap kya chahte hain?",
            new(@"(?<![\p{L}\p{N}])(?:export|excel|xlsx|ایکسپورٹ)(?![\p{L}\p{N}])", KeywordOpts),
            new[] { "export" }),
        ("💳 Payment ke baare mein — aap kya karna chahte hain?",
            new(@"(?<![\p{L}\p{N}])(?:payments?|jazzcash|easypaisa|safepay|ادائیگی)(?![\p{L}\p{N}])", KeywordOpts),
            new[] { "add payment" }),
        ("🌟 Customers / loyalty ke baare mein — aap kya karna chahte hain?",
            new(@"(?<![\p{L}\p{N}])(?:loyal\w*|customers?|گاہک|کسٹمرز?)(?![\p{L}\p{N}])", KeywordOpts),
            new[] { "loyal customers", "create loyalty" }),
        ("📈 Sales / profit ke baare mein — aap kya dekhna chahte hain?",
            new(@"(?<![\p{L}\p{N}])(?:profit|munafa|nafa|sales?|summary|منافع|نفع)(?![\p{L}\p{N}])", KeywordOpts),
            new[] { "profit", "monthly net", "orders today" }),
        ("📦 Stock ke baare mein — aap kya karna chahte hain?",
            new(@"(?<![\p{L}\p{N}])(?:stock|inventory|maal|اسٹاک)(?![\p{L}\p{N}])", KeywordOpts),
            new[] { "stock", "catalog" }),
        ("🛍️ Products ke baare mein — aap kya karna chahte hain?",
            new(@"(?<![\p{L}\p{N}])(?:products?|catalog(?:ue)?|پروڈکٹس?|کیٹلاگ)(?![\p{L}\p{N}])", KeywordOpts),
            new[] { "add product", "catalog" }),
        ("📋 Orders ke baare mein — aap kya karna chahte hain?",
            new(@"(?<![\p{L}\p{N}])(?:orders?|آرڈرز?)(?![\p{L}\p{N}])", KeywordOpts),
            new[] { "new order", "orders today" }),
    };

    /// <summary>
    /// The seller said something we did not understand but it names a feature (a voice note is rewritten to text first, so this covers both):
    /// ask which command they mean instead of "I only do orders" or a generic "didn't understand".
    /// </summary>
    public static bool TryKeywordClarification(string? text, out string question, out List<string> options)
    {
        question = "";
        options = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return false;

        foreach (var area in CommandKeywordAreas)
        {
            if (!area.Keywords.IsMatch(text)) continue;
            question = area.Question;
            options = area.Options.ToList();
            return true;
        }

        return false;
    }
}
