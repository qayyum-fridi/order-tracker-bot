using System.Globalization;
using System.Text.RegularExpressions;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// The harder business questions (bundles, customers about to leave, pricing what-ifs, courier losses, cash and stock risk, seasons). Same idea as <see cref="InsightParser"/>:
/// a topic is recognised from a few distinctive words in any order and the engine answers from the database (ConversationEngine.Insights3.cs / Insights4.cs).
/// It is tried before the simpler topics because its sentences are longer and more specific ("customers who stopped coming AND used to buy every week").
/// </summary>
internal static class AdvancedInsightParser
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private const string B = @"(?<![\p{L}\p{N}])";
    private const string E = @"(?![\p{L}\p{N}])";

    private static Regex W(string alternatives) => new(B + "(?:" + alternatives + ")" + E, Opts);

    private static readonly Regex Together = W(@"saath|sath|together|combination\w*|combo\w*|jodi|jode|jore|ساتھ|جوڑ\w*");
    private static readonly Regex BuyVerb = W(@"kharid\w*|khareed\w*|leta|lete|lena|lety|bik\w*|buy\w*|bought|sold|sell\w*|لیتا|لیتے|خرید\w*|بک\w*");
    private static readonly Regex Alone = W(@"akele|akela|akeli|alone|single|اکیلے|اکیلا");
    private static readonly Regex Not = W(@"nahi|nahin|na|not|never|نہیں");
    private static readonly Regex Bundle = W(@"bundle\w*|packet|pack|packs|offer|پیکٹ|بنڈل");
    private static readonly Regex Suggest = W(@"suggest\w*|sujha\w*|banao|bana|banaun|banau|bata\w*|do|dena|بتا\w*|بنا\w*");
    private static readonly Regex MixSell = W(@"mila\s*kar|milakar|mix|ملا\s*کر");
    private static readonly Regex Away = W(@"nahi\s+(?:diya|kiya|aa\w*|khareed\w*|kharid\w*)|nahin\s+(?:diya|kiya|aa\w*)|ghayab|gayab|inactive|churn\w*|wapas\s+nahi\w*|غائب|نہیں\s+(?:دیا|کیا|آئے)|haven'?t\s+(?:ordered|come|bought)|have\s+not\s+(?:ordered|come|bought)|not\s+ordered");
    private static readonly Regex Regular = W(@"har\s+hafte|har\s+mahine|har\s+maah|weekly|regular\w*|aksar|hamesha|pehle\s+(?:bahut|zyada|har|roz)|used\s+to|ہر\s+ہفتے|ہر\s+مہینے|ہر\s+ماہ|اکثر|ہمیشہ|پہلے");
    private static readonly Regex Customer = W(@"customers?|gahak|gaahak|clients?|buyers?|کسٹمرز?|گاہک\w*|خریدار\w*");
    private static readonly Regex OrderSize = W(@"order\s+size|average\s+order|aosat\s+order|spend\w*|kharcha|paise\s+kharch\w*|آرڈر\s+سائز|خرچ\w*");
    private static readonly Regex Halved = W(@"aadhi|aadha|adhi|half|ghat\w*|drop\w*|decreas\w*|آدھی|آدھا|گھٹ\w*");
    private static readonly Regex Discount = W(@"discount|chhoot|chut|رعایت|ڈسکاؤنٹ|چھوٹ");
    private static readonly Regex Expect = W(@"umeed|ummeed|expect\w*|wapas|return\w*|kitni\s+sale|امید|واپس");
    private static readonly Regex If = W(@"agar|if|اگر");
    private static readonly Regex Pattern = W(@"pattern|adat|aadat|cycle|kab\s+kab|kis\s+hafte|which\s+week|which\s+time|پیٹرن|عادت|کس\s+ہفتے");
    private static readonly Regex Cost = W(@"cost|lagat|لاگت");
    private static readonly Regex Raise = W(@"barh\w*|badh\w*|zyada|increase\w*|hike|up|بڑھ\w*|زیادہ");
    private static readonly Regex Price = W(@"qeemat|qimat|price|rate|daam|sale\s+price|قیمت|ریٹ|دام|پرائس");
    private static readonly Regex Lowest = W(@"kam|sab\s+se\s+kam|lowest|least|minimum|کم");
    private static readonly Regex Margin = W(@"margin|munafa|nafa|مارجن|منافع");
    private static readonly Regex Courier = W(@"courier|tcs|leopards?|bluex|blue\s*ex|trax|postex|m\s*&\s*p|call\s*courier|ٹی\s*سی\s*ایس|لیپرڈز|کورئیر|کوریئر");
    private static readonly Regex Return = W(@"return\w*|rto|wapas|wapis|vapas|ریٹرن|واپس");
    private static readonly Regex Cancel = W(@"cancel\w*|کینسل");
    private static readonly Regex DeliveryCharge = W(@"delivery\s+charg\w*|delivery\s+ka\s+kharch\w*|dead\s*loss|ڈلیوری\s+چارج\w*");
    private static readonly Regex DeliveryTime = W(@"delivery\s+(?:time|waqt)|delivery\s+mein\s+kitn\w+|ڈلیوری\s+(?:ٹائم|وقت)");
    private static readonly Regex Outside = W(@"bahar|outside|dusre\s+shehar|other\s+city|باہر|دوسرے\s+شہر");
    private static readonly Regex Cod = W(@"cod|cash\s+on\s+delivery|کیش\s+آن\s+ڈلیوری");
    private static readonly Regex Advance = W(@"advance|easy\s*paisa|easypaisa|jazz\s*cash|jazzcash|bank|ایڈوانس|ایزی\s*پیسہ|جاز\s*کیش");
    private static readonly Regex Drawer = W(@"galla|galle|gallay|drawer|دراز|گلے|گلا");
    private static readonly Regex Short = W(@"short\w*|farq|difference|kami|kam\s+paise|کمی|فرق|شارٹ\w*");
    private static readonly Regex Delivered = W(@"delivered|deliver\s+ho\s+chuk\w*|ڈیلیورڈ");
    private static readonly Regex Money = W(@"paisa|paise|money|raqam|rupay|پیسہ|پیسے|رقم");
    private static readonly Regex Stuck = W(@"phans\w*|stuck|atka\w*|bazar\s+mein|market\s+mein|بازار|پھنس\w*");
    private static readonly Regex Expense = W(@"kharcha|kharche|kharch|expenses?|اخراجات|خرچ\w*");
    private static readonly Regex Percent = W(@"percent|fisad|ratio|share|فیصد");
    private static readonly Regex Sale = W(@"sales?|bikri|revenue|net\s+sale|فروخت|سیلز?|بکری");
    private static readonly Regex Capital = W(@"sarmaya|capital|paisa|paise|raqam|investment|سرمایہ|پیسہ|پیسے");
    private static readonly Regex StockWord = W(@"stock|maal|mal|products?|اسٹاک|مال|پروڈکٹ\w*");
    private static readonly Regex Locked = W(@"phans\w*|locked|stuck|pada|bandh|پھنس\w*|بند");
    private static readonly Regex Overstock = W(@"overstock\w*|zaroorat\s+se\s+zyada|bahut\s+zyada\s+stock|dher\s+sara\s+stock|ڈھیر");
    private static readonly Regex Season = W(@"ramazan|ramadan|ramzan|eid|عید|رمضان");
    private static readonly Regex Finish = W(@"khatam|finish\w*|run\s*out|ختم");
    private static readonly Regex Supplier = W(@"supplier|suppliers|vendor|wholesaler|سپلائر");
    private static readonly Regex Ratio = W(@"ratio|tanasub|taasub|hissa|share|تناسب|حصہ");
    private static readonly Regex CategoryWord = W(@"category|categories|grocery|garocery|kapron|kapre|kapda|clothes|cloth|گروسری|کپڑ\w*");
    private static readonly Regex Season2 = W(@"mausam|badalta|badalne|badalta|موسم");

    private static readonly Regex Digits = new(@"\d{1,7}(?:,\d{3})*", Opts);
    private static readonly Regex FromTo = new(@"(?<a>\d{2,7})\s*(?:se|to|سے)\s+(?:[\p{L}]+\s+){0,3}(?<b>\d{2,7})|(?<a>\d{2,7})\s*(?:se|to|سے)\s*(?<b>\d{2,7})", Opts);
    private static readonly Regex DaysNumber = new(@"(?<n>\d{1,3})\s*(?:days?|din|dino|دن)", Opts);
    private static readonly Regex MoneyNumber = new(@"(?:rs\.?|pkr)?\s*(?<n>\d{1,6})\s*(?:rupay|rupees?|rupe|rs\b|روپے|روپیہ)|(?:rs\.?|pkr)\s*(?<n2>\d{1,6})", Opts);
    private static readonly Regex CustomersNumber = new(@"(?<n>\d{1,5})\s*(?:purane\s+|puranay\s+|old\s+)?(?:customers?|gahak|کسٹمرز?|گاہک)", Opts);

    public static ParsedCommand? TryParse(string text, string? period)
    {
        ParsedCommand Topic(string topic, decimal? amount = null, string? subject = null) =>
            new() { Kind = CommandKind.Insight, Text = topic, Text2 = period, Text3 = subject ?? text, Amount = amount };

        if (Supplier.IsMatch(text) && (Sale.IsMatch(text) || BuyVerb.IsMatch(text) || Margin.IsMatch(text))) return Topic("suppliers");

        // --- baskets and bundles
        if (Alone.IsMatch(text) && Not.IsMatch(text) && BuyVerb.IsMatch(text)) return Topic("nosolo");
        if (Bundle.IsMatch(text) && (Suggest.IsMatch(text) || Regex.IsMatch(text, @"sujha|suggest|تجویز|بیچ\s*سک", Opts))) return Topic("bundle");
        if (MixSell.IsMatch(text) && (Sale.IsMatch(text) || BuyVerb.IsMatch(text))) return Topic("pairs");
        if (Together.IsMatch(text) && BuyVerb.IsMatch(text)) return Topic("pairs");

        // --- customers about to leave
        if (Customer.IsMatch(text) && OrderSize.IsMatch(text) && Halved.IsMatch(text)) return Topic("aovdrop");
        if (Discount.IsMatch(text) && Customer.IsMatch(text) && If.IsMatch(text) && Expect.IsMatch(text)) return Topic("winback", MoneyAmount(text), CustomersCount(text)?.ToString(CultureInfo.InvariantCulture));
        if (Away.IsMatch(text) && Regular.IsMatch(text)) return Topic("churnrisk", Days(text, 60));
        if (Pattern.IsMatch(text) && (BuyVerb.IsMatch(text) || Regex.IsMatch(text, @"order|khareed|kharid|خرید", Opts))) return Topic("pattern");

        // --- pricing
        if (Cost.IsMatch(text) && Raise.IsMatch(text) && Price.IsMatch(text) && Digits.IsMatch(text)) return Topic("costup", FirstNumber(text));
        if (Price.IsMatch(text) && FromTo.Match(text) is { Success: true } move && decimal.TryParse(move.Groups["b"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var newPrice)
            && decimal.TryParse(move.Groups["a"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var oldPrice) && oldPrice != newPrice)
            return Topic("priceeffect", newPrice, text);
        if (Cost.IsMatch(text) && Margin.IsMatch(text) && Lowest.IsMatch(text) && Regex.IsMatch(text, @"zyada|bahut|mehng\w*|high|ziyada|زیادہ|بہت", Opts)) return Topic("costlylow");

        // --- couriers and returns
        if (Cancel.IsMatch(text) && Cod.IsMatch(text) && Advance.IsMatch(text)) return Topic("cancelbymethod");
        if (Courier.IsMatch(text) && Return.IsMatch(text) && Regex.IsMatch(text, @"kis|konsi|kaun\w*|which|zyada|most|کس|کون\w*|زیادہ", Opts)) return Topic("courierreturns");
        if ((Return.IsMatch(text) || Cancel.IsMatch(text)) && DeliveryCharge.IsMatch(text)) return Topic("returnloss");
        if (DeliveryTime.IsMatch(text) && Outside.IsMatch(text)) return Topic("citydelivery");

        // --- cash
        if (Drawer.IsMatch(text) && Short.IsMatch(text) && period is not "today" && (period is not null || Regex.IsMatch(text, @"kis\s+din|which\s+day|hafte|week|کس\s+دن|ہفتے", Opts))) return Topic("drawershort");
        if (Delivered.IsMatch(text) && Money.IsMatch(text) && Not.IsMatch(text)) return Topic("uncollected");
        if (Stuck.IsMatch(text) && Money.IsMatch(text) && !StockWord.IsMatch(text)) return Topic("stuckmoney");
        if (Expense.IsMatch(text) && Percent.IsMatch(text) && Sale.IsMatch(text)) return Topic("expenseratio");

        // --- stock capital and seasons
        if (Capital.IsMatch(text) && StockWord.IsMatch(text) && Locked.IsMatch(text)) return Topic("lockedcapital", Days(text, 90));
        if (Overstock.IsMatch(text)) return Topic("overstock");
        if (Season.IsMatch(text) && StockWord.IsMatch(text) && Finish.IsMatch(text)) return Topic("stockending", DaysUntilNextEid(DateTime.UtcNow));
        if (Season.IsMatch(text) && (Sale.IsMatch(text) || Regex.IsMatch(text, @"avg|average|ausat|stock|اوسط|اسٹاک", Opts))) return Topic("seasonal");
        if (Ratio.IsMatch(text) && (CategoryWord.IsMatch(text) || Season2.IsMatch(text)) && Sale.IsMatch(text)) return Topic("categoryshift", subject: text);
        return null;
    }

    /// <summary>The first number in the sentence ("lagat 300 rupay zyada" -> 300).</summary>
    private static decimal? FirstNumber(string text) =>
        Digits.Match(text) is { Success: true } m && decimal.TryParse(m.Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static decimal? MoneyAmount(string text)
    {
        var m = MoneyNumber.Match(text);
        if (!m.Success) return null;
        var raw = m.Groups["n"].Success ? m.Groups["n"].Value : m.Groups["n2"].Value;
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static int? CustomersCount(string text) =>
        CustomersNumber.Match(text) is { Success: true } m && int.TryParse(m.Groups["n"].Value, out var n) ? n : null;

    private static decimal Days(string text, int fallback) =>
        DaysNumber.Match(text) is { Success: true } m && int.TryParse(m.Groups["n"].Value, out var n) && n > 0 ? n : fallback;

    /// <summary>Days from today to the next Eid ul-Fitr or Eid ul-Adha (Umm al-Qura calendar; Pakistan's moon sighting can differ by a day).</summary>
    internal static int DaysUntilNextEid(DateTime utcNow)
    {
        var calendar = new UmAlQuraCalendar();
        var year = calendar.GetYear(utcNow);
        var best = DateTime.MaxValue;
        for (var y = year; y <= year + 1; y++)
            foreach (var (month, day) in new[] { (10, 1), (12, 10) })
            {
                var date = calendar.ToDateTime(y, month, day, 0, 0, 0, 0);
                if (date.Date >= utcNow.Date && date < best) best = date;
            }
        return Math.Max(1, (int)Math.Ceiling((best.Date - utcNow.Date).TotalDays));
    }
}
