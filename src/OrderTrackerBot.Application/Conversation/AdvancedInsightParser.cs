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
    private static readonly Regex Cancel = W(@"cancel\w*|کینسل\w*");
    private static readonly Regex DeliveryCharge = W(@"delivery\s+charg\w*|delivery\s+ka\s+kharch\w*|dead\s*loss|ڈلیوری\s+چارج\w*");
    private static readonly Regex DeliveryTime = W(@"delivery\s+(?:time|waqt)|delivery\s+mein\s+kitn\w+|ڈلیوری\s+(?:ٹائم|وقت)");
    private static readonly Regex Outside = W(@"bahar|outside|dusre\s+shehar|other\s+city|باہر|دوسرے\s+شہر");
    private static readonly Regex Cod = W(@"cod|cash\s+on\s+delivery|کیش\s+آن\s+ڈلیوری");
    private static readonly Regex Advance = W(@"advance|easy\s*paisa|easypaisa|jazz\s*cash|jazzcash|bank|ایڈوانس|ایزی\s*پیسہ|جاز\s*کیش");
    private static readonly Regex Drawer = W(@"galla|galle|gallay|drawer|دراز|گلے|گلا");
    private static readonly Regex Short = W(@"short\w*|farq|difference|kami|kam\s+paise|کمی|فرق|شارٹ\w*|mismatch|bezabta\w*|irregular\w*|بے\s*ضابطگی");
    private static readonly Regex Delivered = W(@"delivered|deliver\s+ho\s+chuk\w*|ڈیلیورڈ|ڈیلیور\s+(?:تو\s+)?ہو\s+چکے");
    private static readonly Regex Money = W(@"paisa|paise|money|raqam|rupay|cash|settlement|پیسہ|پیسے|رقم|کیش");
    private static readonly Regex Stuck = W(@"phans\w*|stuck|atka\w*|bazar\s+mein|market\s+mein|بازار|پھنس\w*|مارکیٹ\s+میں");
    private static readonly Regex Expense = W(@"kharcha|kharche|kharch|expenses?|اخراجات|خرچ\w*");
    private static readonly Regex Percent = W(@"percent|fisad|ratio|share|فیصد");
    private static readonly Regex Sale = W(@"sales?|bikri|revenue|net\s+sale|فروخت|سیلز?|بکری");
    private static readonly Regex Capital = W(@"sarmaya|capital|paisa|paise|raqam|investment|سرمایہ|پیسہ|پیسے");
    private static readonly Regex StockWord = W(@"stock|maal|mal|products?|اسٹاک|مال|پروڈکٹ\w*");
    private static readonly Regex Locked = W(@"phans\w*|locked|stuck|pada|bandh|پھنس\w*|بند");
    private static readonly Regex Overstock = W(@"overstock\w*|zaroorat\s+se\s+zyada|bahut\s+zyada\s+stock|dher\s+sara\s+stock|ڈھیر");
    private static readonly Regex Season = W(@"ramazan|ramadan|ramzan|eid|عید|رمضان");
    private static readonly Regex Finish = W(@"khatam|finish\w*|run\s*out|ختم");
    private static readonly Regex Supplier = W(@"supplier|suppliers|vendor|wholesaler|سپلائر|سپلائرز");
    private static readonly Regex Ratio = W(@"ratio|tanasub|taasub|hissa|share|تناسب|حصہ");
    private static readonly Regex CategoryWord = W(@"category|categories|grocery|garocery|kapron|kapre|kapda|clothes|cloth|گروسری|کپڑ\w*|کیٹیگری");
    private static readonly Regex Season2 = W(@"mausam|badalta|badalne|badalta|موسم");

    // ---- hyper-complex scenarios (part 3)
    private static readonly Regex NetProfit = W(@"net\s+profit|khalis\s+munafa|contribution|cumulative|خالص\s+منافع|نیٹ\s+منافع|منافع|profit");
    private static readonly Regex Earn = W(@"kama\s+kar|kamaya|earned|کما\s+کر|کمایا");
    private static readonly Regex FirstTime = W(@"pehli\s+baar|first\s+time|pehla\s+order|پہلی\s+بار|پہلا\s+آرڈر");
    private static readonly Regex SecondOrder = W(@"doosra|dusra|second|دوسرا|دوبارہ|dobara");
    private static readonly Regex Vip = W(@"vip|wafadar|loyal|وفادار");
    private static readonly Regex Share = W(@"hissa|share|حصہ");
    private static readonly Regex Velocity = W(@"velocity|raftar|رفتار");
    private static readonly Regex Falls = W(@"gir\w*|drop\w*|ghat\w*|گر\w*|گھٹ\w*");
    private static readonly Regex FastMoving = W(@"fast[- ]?moving|aate\s+hi|jaldi\s+bik\w*|turant\s+bik\w*|آتے\s+ہی");
    private static readonly Regex Cleared = W(@"saaf\s+ho|clear\s+ho|days\s+on\s+hand|صاف\s+ہو");
    private static readonly Regex AskingOutOfStock = W(@"puch\w*|ask\w*|demand|maang|پوچھ\w*|مانگ");
    private static readonly Regex OutOfStock = W(@"out\s+of\s+stock|stock\s+khatam|ختم\s+ہو\s+چکا|اسٹاک\s+ختم");
    private static readonly Regex Dropped = W(@"gira|gir\s+gaya|compress\w*|گرا|گر\s+گیا|نیچے");
    private static readonly Regex BreakEven = W(@"break[- ]?even|بریک\s*ایون");
    private static readonly Regex CostFell = W(@"kam\s+hui|kam\s+ho\s+gayi|ghati|کم\s+ہوئی|گھٹی|decreased?");
    private static readonly Regex OldRate = W(@"purana|purani|old|پرانا|پرانی");
    private static readonly Regex Failed = W(@"fail\w*|nakam|ناکام|فیل");
    private static readonly Regex CityWord = W(@"shehar|sheher|city|area|ilaqa|شہر|علاقہ");
    private static readonly Regex Damaged = W(@"kharab|gum|lost|damage\w*|خراب|گم");
    private static readonly Regex StockOrCatalog = W(@"stock|catalog|maal|suit|سوٹ|کیٹلاگ|اسٹاک|مال");
    private static readonly Regex Sensitive = W(@"drop|kam\s+ho|sensitiv\w*|asar|ڈراپ|اثر");
    private static readonly Regex CashSales = W(@"cash\s+sales?|cash\s+flow|نقد\s+سیلز|کیش\s+فلو");
    private static readonly Regex CodeWord = W(@"code|coupon|کوڈ");
    private static readonly Regex Cannibal = W(@"cannibal\w*|veisay|waisay|ویسے|سستے\s+میں|سستے\s+ریٹ");
    private static readonly Regex Promotion = W(@"promotion|promo|پروموشن");
    private static readonly Regex NewCustomersWord = W(@"naye\s+(?:gahak|customers?)|new\s+customers?|نئے\s+(?:گاہک|کسٹمرز?)");
    private static readonly Regex FullPrice = W(@"pura\s+rate|full\s+price|پورا\s+ریٹ|intezar|انتظار");
    private static readonly Regex ShortWord = W(@"short|shortage|شارٹ");
    private static readonly Regex FixedBills = W(@"kiraya|کرایہ|rent|bijli|بجلی|electric\w*|bill|بل|utilit\w*");
    private static readonly Regex AtLeast = W(@"kam\s+se\s+kam|minimum|at\s+least|کم\s+از\s+کم");
    private static readonly Regex Lifecycle = W(@"life\s*cycle|لائف\s*سائیکل|aakhri\s+marhal\w*|آخری\s+مرحل\w*|musalsal\s+gir\w*|مستقل\s+گر\w*");
    private static readonly Regex ShiftWord = W(@"shift|taraf|ruchan|trend|طرف|ٹرینڈ|رجحان");
    private static readonly Regex Audit = W(@"business\s+audit|audit|آڈٹ|جائزہ\s+لو");
    private static readonly Regex Earlier = W(@"pehle|used\s+to|پہلے");

    private static readonly Regex Percent2 = new(@"\d{1,3}\s*(?:%|percent|fisad|فیصد)", Opts);
    // ---- hyper-strategic scenarios (part 4)
    private static readonly Regex Salary = W(@"salary|tankhwa|تنخواہ|aakhri\s+\d+\s+din|آخری\s+\d+\s+دن|25\s*(?:se|سے)\s*30");
    private static readonly Regex WalkIn = W(@"walk[- ]?in|واک[- ]?اِن|واک[- ]?ان|dukaan\s+par|counter");
    private static readonly Regex Lifetime = W(@"lifetime|contribution|ausat\s+kharid\w*|اوسط\s+خریداری");
    private static readonly Regex HoldingCost = W(@"holding\s+cost|ہولڈنگ\s+کاسٹ");
    private static readonly Regex Turnover = W(@"turnover|velocity|raftar|گردش|رفتار");
    private static readonly Regex Liquidity = W(@"liquidity|cash\s+azad|release|آزاد");
    private static readonly Regex DeadStock = W(@"dead\s*stock|ڈیڈ\s*اسٹاک|nahi\s+bika|نہیں\s+بکا");
    private static readonly Regex Defects = W(@"nuqs|nuqsan|kharabi|defect\w*|damage\w*|نقص|خرابی");
    private static readonly Regex InTransit = W(@"transit|rastay?\s+mein|راستے\s+میں|phans\w*|پھنس\w*");
    private static readonly Regex Refusal = W(@"refus\w*|inkar|انکار");
    private static readonly Regex PaidWord = W(@"paid|ادا");
    private static readonly Regex Basket = W(@"basket|ٹوکری|سیلز\s+میں");
    private static readonly Regex Settlement = W(@"settlement|cycle|سائیکل");
    private static readonly Regex Wastage = W(@"wastage|wastege|خراب\s+مال|ضائع");
    private static readonly Regex FifthDate = W(@"5\s*(?:tarikh|تاریخ)|5th|paanch\s+tarikh");
    private static readonly Regex Receivable = W(@"wasool\s+hona\s+baqi|وصول\s+ہونا\s+باقی|receivable\w*");
    private static readonly Regex Premium = W(@"mehng\w+|premium|مہنگ\w+");
    private static readonly Regex Cheap = W(@"sast\w+|cheap|سست\w+");
    private static readonly Regex OrderSizeWord = W(@"order\s+size|آرڈر\s+سائز");
    private static readonly Regex OrdersWord = W(@"orders?|آرڈرز?|آرڈر");
    private static readonly Regex RateWord = W(@"rate|ratio|percent\w*|percentage|fisad|share|شرح|فیصد|ریٹ");
    private static readonly Regex Digits = new(@"\d{1,7}(?:,\d{3})*", Opts);
    private static readonly Regex FromTo = new(@"(?<a>\d{2,7})\s*(?:روپے|rupay|rupees?|rs\.?)?\s*(?:se|to|سے)\s+(?:[\p{L}]+\s+){0,3}(?<b>\d{2,7})|(?<a>\d{2,7})\s*(?:روپے|rupay|rupees?|rs\.?)?\s*(?:se|to|سے)\s*(?<b>\d{2,7})", Opts);
    private static readonly Regex DaysNumber = new(@"(?<n>\d{1,3})\s*(?:days?|din|dino|دن|دنوں)", Opts);
    private static readonly Regex MoneyNumber = new(@"(?:rs\.?|pkr)?\s*(?<n>\d{1,6})\s*(?:rupay|rupees?|rupe|rs\b|روپے|روپیہ)|(?:rs\.?|pkr)\s*(?<n2>\d{1,6})", Opts);
    private static readonly Regex CustomersNumber = new(@"(?<n>\d{1,5})\s*(?:purane\s+|puranay\s+|old\s+)?(?:customers?|gahak|کسٹمرز?|گاہک)", Opts);

    public static ParsedCommand? TryParse(string text, string? period)
    {
        ParsedCommand Topic(string topic, decimal? amount = null, string? subject = null, string? periodKey = null) =>
            new() { Kind = CommandKind.Insight, Text = topic, Text2 = periodKey ?? period, Text3 = subject ?? text, Amount = amount };


        // --- hyper-strategic scenarios (part 4): more specific than everything below
        if (Salary.IsMatch(text) && (Regex.IsMatch(text, @"order|آرڈر", Opts) || WalkIn.IsMatch(text))) return Topic("salarycycle", periodKey: "");
        if (Discount.IsMatch(text) && FirstTime.IsMatch(text) && Customer.IsMatch(text) && Lifetime.IsMatch(text)) return Topic("promocohort");
        if (Regex.IsMatch(text, @"kis\s+din|konse\s+din|which\s+day|کس\s+(?:مخصوص\s+)?دن", Opts) && Regex.IsMatch(text, @"waqt|time|baje|وقت|بجے", Opts) && OrdersWord.IsMatch(text) && Regex.IsMatch(text, @"sab\s+se\s+zyada|peak|most|سب\s+سے\s+زیادہ", Opts)) return Topic("peaktime", periodKey: "");
        if (Customer.IsMatch(text) && Advance.IsMatch(text) && Cod.IsMatch(text) && Premium.IsMatch(text) && Cheap.IsMatch(text)) return Topic("paypattern");
        if (HoldingCost.IsMatch(text)) return Topic("holdingcost", Days(text, 60));
        if (Turnover.IsMatch(text) && Margin.IsMatch(text) && Regex.IsMatch(text, @"kam|low|کم", Opts) && Regex.IsMatch(text, @"profit|munafa|منافع", Opts) && Regex.IsMatch(text, @"sab\s+se\s+zyada|most|سب\s+سے\s+زیادہ", Opts)) return Topic("turnoverstar");
        if (Liquidity.IsMatch(text) && DeadStock.IsMatch(text)) return Topic("liquidate", PercentOf(text) ?? 10);
        if (Supplier.IsMatch(text) && Defects.IsMatch(text)) return Topic("supplierdefects", Days(text, 180));
        if (Supplier.IsMatch(text) && Percent2.IsMatch(text) && Raise.IsMatch(text) && Regex.IsMatch(text, @"ho?l\s*sale|rate|ریٹ|ہول\s*سیل", Opts)) return Topic("suppliershock", PercentOf(text));
        if (Supplier.IsMatch(text) && Margin.IsMatch(text) && Regex.IsMatch(text, @"lead\s*time|waqt|وقت", Opts)) return Topic("suppliercompare");
        if (Courier.IsMatch(text) && Return.IsMatch(text) && InTransit.IsMatch(text)) return Topic("rtotransit");
        if (DeliveryTime.IsMatch(text) && Refusal.IsMatch(text)) return Topic("citycompare");
        if (Return.IsMatch(text) && Percent.IsMatch(text) && Courier.IsMatch(text) && Regex.IsMatch(text, @"profit|munafa|منافع", Opts) && Regex.IsMatch(text, @"kiraya|کرایہ|fare|fee", Opts)) return Topic("rtoprofitshare", Days(text, 90));
        if (PaidWord.IsMatch(text) && (Cancel.IsMatch(text) || Return.IsMatch(text)) && (Drawer.IsMatch(text) || Regex.IsMatch(text, @"naqd|cash|نقد", Opts)) && Regex.IsMatch(text, @"kam\s+nahi|کم\s+نہیں|audit", Opts)) return Topic("paidcancelled", Days(text, 90));
        if (Discount.IsMatch(text) && CategoryWord.IsMatch(text) && Basket.IsMatch(text) && Regex.IsMatch(text, @"izafa|اضافہ|increase", Opts)) return Topic("discountbasket");
        if (Discount.IsMatch(text) && Return.IsMatch(text) && RateWord.IsMatch(text) && Customer.IsMatch(text)) return Topic("discountreturn");
        if (FixedBills.IsMatch(text) && Supplier.IsMatch(text) && (FifthDate.IsMatch(text) || Regex.IsMatch(text, @"roz|rozana|daily|روزانہ", Opts))) return Topic("cashneed");
        if (Advance.IsMatch(text) && Cod.IsMatch(text) && Settlement.IsMatch(text)) return Topic("settlement");
        if (Advance.IsMatch(text) && OrderSizeWord.IsMatch(text) && Customer.IsMatch(text)) return Topic("advancesize");
        if (Cancel.IsMatch(text) && Return.IsMatch(text) && Wastage.IsMatch(text)) return Topic("leakage", Days(text, 30));
        if ((Stuck.IsMatch(text) || Receivable.IsMatch(text)) && (Money.IsMatch(text) || Capital.IsMatch(text)) && !StockWord.IsMatch(text)) return Topic("stuckmoney");

        // --- hyper-complex scenarios: more specific than the ones below, so tried first
        if (Audit.IsMatch(text) && (Regex.IsMatch(text, @"business|dukaan|دکان|کاروبار", Opts) || text.Length < 30)) return Topic("audit");
        if ((Customer.IsMatch(text) && Regex.IsMatch(text, @"at\s+risk|khatre", Opts)) || Regex.IsMatch(text, @"churn\s+risk", Opts)) return Topic("churnrisk", 60);
        if (Customer.IsMatch(text) && NetProfit.IsMatch(text) && (Discount.IsMatch(text) || Return.IsMatch(text)) && Lowest.IsMatch(text)) return Topic("custprofit");
        if (Customer.IsMatch(text) && NetProfit.IsMatch(text) && Earn.IsMatch(text)) return Topic("custprofit", Days(text, 365));
        if (Customer.IsMatch(text) && FirstTime.IsMatch(text) && SecondOrder.IsMatch(text)) return Topic("retention");
        if (Vip.IsMatch(text) && Customer.IsMatch(text) && Sale.IsMatch(text) && Share.IsMatch(text)) return Topic("vipshare");
        if (Velocity.IsMatch(text) && Falls.IsMatch(text) && (StockWord.IsMatch(text) || Regex.IsMatch(text, @"product|پروڈکٹ", Opts))) return Topic("velocitydrop", Days(text, 15));
        if (FastMoving.IsMatch(text) && StockWord.IsMatch(text)) return Topic("fastmoving");
        if (Cleared.IsMatch(text) && StockWord.IsMatch(text)) return Topic("daysonhand");
        if (StockWord.IsMatch(text) && AskingOutOfStock.IsMatch(text) && (OutOfStock.IsMatch(text) || Finish.IsMatch(text))) return Topic("outofstockhot");
        if (Margin.IsMatch(text) && Dropped.IsMatch(text) && Cost.IsMatch(text)) return Topic("margindrop");
        if (Cost.IsMatch(text) && BreakEven.IsMatch(text) && Percent2.IsMatch(text)) return Topic("costshock", PercentOf(text));
        if (Cost.IsMatch(text) && CostFell.IsMatch(text) && (OldRate.IsMatch(text) || Price.IsMatch(text))) return Topic("costdrop");
        if (Failed.IsMatch(text) && Regex.IsMatch(text, @"delivery|deliver|ڈلیوری|پارسل|parcel", Opts) && CityWord.IsMatch(text)) return Topic("failcity");
        if ((Return.IsMatch(text)) && Damaged.IsMatch(text) && StockOrCatalog.IsMatch(text)) return Topic("rtostock");
        if (DeliveryCharge.IsMatch(text) && FromTo.IsMatch(text) && Sensitive.IsMatch(text) && FromTo.Match(text) is { Success: true } chg
            && decimal.TryParse(chg.Groups["b"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var newCharge)) return Topic("chargeeffect", newCharge);
        if (Expense.IsMatch(text) && CashSales.IsMatch(text)) return Topic("cashflow");
        if (Drawer.IsMatch(text) && Regex.IsMatch(text, @"band|close|بند", Opts) && Supplier.IsMatch(text) && Regex.IsMatch(text, @"bank|بینک|jama|جمع", Opts)) return Topic("cashplan", MoneyAmount(text) ?? (FirstNumber(text) is >= 100 and var amt ? amt : null));
        if (CodeWord.IsMatch(text) && (Discount.IsMatch(text) || Regex.IsMatch(text, @"\b[A-Z]{2,}\d{1,3}\b", RegexOptions.None)) && (Cannibal.IsMatch(text) || Regex.IsMatch(text, @"munafa|منافع|profit|barha|بڑھا", Opts)) && (Sale.IsMatch(text) || Cannibal.IsMatch(text))) return Topic("promoroi");
        if ((Promotion.IsMatch(text) || Discount.IsMatch(text)) && NewCustomersWord.IsMatch(text) && (Regex.IsMatch(text, @"kis|konsi|kaun\w*|کس|کون\w*", Opts))) return Topic("promonew");
        if (Discount.IsMatch(text) && Customer.IsMatch(text) && FullPrice.IsMatch(text)) return Topic("promoloyalty");
        if (Season.IsMatch(text) && StockWord.IsMatch(text) && Percent.IsMatch(text) && ShortWord.IsMatch(text)) return Topic("eidstock");
        if (FixedBills.IsMatch(text) && Sale.IsMatch(text) && AtLeast.IsMatch(text) && Regex.IsMatch(text, @"roz|rozana|daily|روزانہ|har\s+din", Opts)) return Topic("breakevendaily");
        if (Lifecycle.IsMatch(text)) return Topic("declining");
        if (Regex.IsMatch(text, @"mehng\w+|مہنگ\w+", Opts) && Regex.IsMatch(text, @"sast\w+|سست\w+", Opts) && ShiftWord.IsMatch(text)) return Topic("pricemix");

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
        if (Courier.IsMatch(text) && Regex.IsMatch(text, @"fee|fees|bill|ko\s+kitna\s+diya|kitna\s+diya|فیس|بل", Opts) && !Return.IsMatch(text) && !Cancel.IsMatch(text)) return Topic("courierfees");

        // --- cash
        if (Drawer.IsMatch(text) && Short.IsMatch(text) && period is not "today" && (period is not null || DaysNumber.IsMatch(text) || Regex.IsMatch(text, @"kis\s+din|which\s+day|hafte|week|کس\s+دن|ہفتے", Opts)))
            return new ParsedCommand { Kind = CommandKind.Insight, Text = "drawershort", Text2 = period ?? (DaysNumber.Match(text) is { Success: true } dn ? dn.Groups["n"].Value + "d" : null), Text3 = text };
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

    private static decimal? PercentOf(string text) =>
        Regex.Match(text, @"(?<n>\d{1,3}(?:\.\d+)?)\s*(?:%|percent|fisad|فیصد)", Opts) is { Success: true } m
        && decimal.TryParse(m.Groups["n"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;

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
