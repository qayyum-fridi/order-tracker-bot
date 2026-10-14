using System.Globalization;
using System.Text.RegularExpressions;
using OrderTrackerBot.Application.Time;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// Business-analysis questions a seller asks in their own words ("sab se mehnga product kaun sa hai", "kaun se customers wapas nahi aaye", "polo shirt kitne din chalega").
/// Each question is a topic recognised from a few distinctive words (English / Roman Urdu / Urdu script) rather than one fixed sentence, so word order and filler do not matter.
/// The parser only picks the topic and the period; every number is computed by <c>ConversationEngine.Insights.cs</c> from the database (no AI computes a figure).
/// A topic needs two or more of its words together, and it runs after every exact command, so ordinary commands are never taken for a question.
/// </summary>
internal static class InsightParser
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private const string B = @"(?<![\p{L}\p{N}])";
    private const string E = @"(?![\p{L}\p{N}])";

    private static Regex W(string alternatives) => new(B + "(?:" + alternatives + ")" + E, Opts);

    private static readonly Regex Avg = W(@"average|avg|ausat|ausath|aosat|اوسط|ایوریج");
    private static readonly Regex Sale = W(@"sales?|bikri|bikree|farokht|revenue|kamai|kamayi|income|فروخت|سیلز?|بکری|کمائی|آمدنی");
    private static readonly Regex Daily = W(@"daily|per\s+day|roz|rozana|roza|har\s+din|روزانہ|روز|ہر\s+دن|فی\s+دن");
    private static readonly Regex Pricey = W(@"mehng\w*|mahng\w*|most\s+expensive|costliest|priciest|expensive|highest\s+pric\w*|مہنگ\w*");
    private static readonly Regex Cheap = W(@"sast\w*|cheapest|lowest\s+pric\w*|least\s+expensive|سست\w*");
    private static readonly Regex CategoryWord = W(@"categor(?:y|ies|ys)|katigar\w*|kategor\w*|کیٹیگر\w*|کیٹگر\w*|زمر\w*");
    private static readonly Regex Product = W(@"products?|items?|maal|mal|cheez\w*|saman|سامان|پروڈکٹس?|آئٹمز?|مال|چیز\w*");
    private static readonly Regex Which = W(@"kaun\s*sa|kaun\s*si|kon\s*sa|kon\s*si|konsa|konsi|kaunsa|kaunsi|which|کون\s*سا|کون\s*سی|کونسا|کونسی");
    private static readonly Regex How = W(@"kitn\w+|how\s+(?:many|much)|کتن\w+");
    private static readonly Regex PriceWord = W(@"qeemat|qimat|price|prices|rate|daam|dam|قیمت|ریٹ|دام");
    private static readonly Regex Raise = W(@"barh\w*|badh\w*|barha\w*|increase|raise|hike|zyada\s+kar\w*|بڑھ\w*|بڑھا\w*|اضافہ");
    private static readonly Regex Need = W(@"zaroorat|zarurat|chahiye|chahie|chahiay|should|need|kya|karni|karna|review|jaiza|ضرورت|چاہیے|کیا|جائزہ");
    private static readonly Regex Discount = W(@"discount|chhoot|chut|off|rebate|رعایت|ڈسکاؤنٹ|چھوٹ");
    private static readonly Regex Margin = W(@"margin|munafa|nafa|profit|bach\w*|مارجن|منافع|نفع|بچ\w*");
    private static readonly Regex MarginExplicit = W(@"margin|margins|مارجن");
    private static readonly Regex CategoryAsk = new(@"(?:\bse|سے)\s+(?:kitn\w+|کتن\w+)\s+(?:sale|sales|bikri|kamai|kamayi|income|سیل|سیلز|کمائی|بکری)", Opts);
    private static readonly Regex NewWord = W(@"naye|naya|nayi|nai|new|نئے|نیا|نئی");
    private static readonly Regex CompareLoose = W(@"muqable\w*|mukable\w*|muqabla|compar\w*|vs|versus|behtar|better|worse|kaisi|kaisa|zyada|kam|less|more|barh\w*|ghat\w*|مقابل\w*|بہتر|کیسی|کیسا|زیادہ|کم");
    private static readonly Regex ThisUnit = W(@"(?:this|is|iss|isi|current)\s+(?<u>week|month|hafte|hafta|haftay|mahine|mahina)|اس\s+(?<u>ہفتے|مہینے)");
    private static readonly Regex LastUnit = W(@"(?:last|previous|pichle|pichlay|pichla|guzishta)\s+(?<u>week|month|hafte|hafta|haftay|mahine|mahina)|(?:پچھلے|گزشتہ)\s+(?<u>ہفتے|مہینے)");
    private static readonly Regex CanGive = W(@"de\s+sak\w*|dy\s+sak\w*|can\s+give|can\s+i\s+give|dena\s+chahiye|dene\s+ki|دے\s+سک\w*");
    private static readonly Regex LowStock = W(@"low\s+stock|stock\s+kam|kam\s+stock|stock\s+low|کم\s+اسٹاک|اسٹاک\s+کم");
    private static readonly Regex Left = W(@"bacha|bachi|bache|bachay|left|remaining|available|hai|baqi|بچا|بچی|بچے|باقی");
    private static readonly Regex Fastest = W(@"tez|fast|fastest|jaldi|best|behtar|quick\w*|تیز|جلدی");
    private static readonly Regex Breakdown = W(@"category\s*wise|breakdown|taqseem|tafseel|details?|تقسیم|تفصیل");
    private static readonly Regex LeadSubject = W(@"supplier|suppliers|maal|mal|stock|lead\s*time|wholesaler|سپلائر|مال|اسٹاک");
    private static readonly Regex LeadVerb = W(@"aane|aata|ata|ati|aati|lagta|lagte|lagti|lead\s*time|arrive\w*|takes?|deliver\w*|آنے|لگتے|لگتا|لگتی");
    private static readonly Regex AskWord = W(@"what|kya|how\s+(?:many|much)|kitn\w+|کتن\w+|کیا");
    private static readonly Regex MonthWord = W(@"month|mahine|mahina|mah|مہینے|مہینہ|ماہ");
    private static readonly Regex Pct = new(@"(?<n>\d{1,3}(?:\.\d+)?)\s*(?:%|percent|pc|fisad|فیصد)", Opts);
    private static readonly Regex Given = W(@"diya|diye|dia|given|gave|give|dene|dete|دیا|دیے");

    private static readonly Regex Customer = W(@"customers?|gahak|gaahak|clients?|buyers?|کسٹمرز?|گاہک\w*|خریدار\w*");
    private static readonly Regex Away = W(@"nahi\s+aa\w*|nahin\s+aa\w*|wapas\s+nahi\w*|wapis\s+nahi\w*|vapas\s+nahi\w*|inactive|dormant|churn\w*|gayab|ghayab|lost|nahi\s+khareed\w*|nahi\s+kharid\w*|order\s+nahi\s+(?:kiya|diya)|not\s+(?:come|come\s+back|returned|ordered|back)|haven'?t\s+(?:come|returned|ordered)|did\s+not\s+(?:come|return|order)|واپس\s+نہیں|نہیں\s+آئے|نہیں\s+آیا|غائب");
    private static readonly Regex BigWord = W(@"sab\s+se\s+(?:bada|bara|zyada|acha|achha|achcha)|top|best|biggest|highest|largest|سب\s+سے\s+(?:بڑا|زیادہ)|بڑا");
    private static readonly Regex BuyWord = W(@"khareed\w*|kharid\w*|spend\w*|spent|purchas\w*|shopping|business|total|خرید\w*|خریداری");
    private static readonly Regex Repeat = W(@"repeat\w*|baar\s+baar|bar\s+bar|dobara|returning|regular|بار\s+بار|ریپیٹ|دوبارہ");
    private static readonly Regex Udhaar = W(@"udhaar|udhar|baqaya|baqiya|bakaya|baaqi|balance|outstanding|dues|ادھار|بقایا|باقی");
    private static readonly Regex LastTime = W(@"pichli\s+baar|pichhli\s+baar|pichle\s+baar|last\s+time|aakhri\s+baar|akhri\s+baar|last\s+order|پچھلی\s+بار|آخری\s+بار");
    private static readonly Regex BuyVerb = W(@"kharid\w*|khareed\w*|bought|buy|purchased|liya|li\s+thi|order\s+kiya|لیا|خرید\w*");
    private static readonly Regex OrdersWord = W(@"orders?|آرڈرز?|آرڈر");

    private static readonly Regex CodWord = W(@"cod|cash\s+on\s+delivery|کیش\s+آن\s+ڈلیوری|کیش");
    private static readonly Regex OtherPay = W(@"easy\s*paisa|easypaisa|jazz\s*cash|jazzcash|bank|card|wallet|transfer|ایزی\s*پیسہ|جاز\s*کیش|بینک|کارڈ");
    private static readonly Regex PayMethodPhrase = W(@"payment\s+(?:method|split|breakdown|mode)s?|ادائیگی\s+کا\s+طریقہ|payment\s+kaise|paisay\s+kaise");
    private static readonly Regex MostWord = W(@"zyada|zyadatar|aksar|most|mostly|majority|زیادہ|اکثر");

    private static readonly Regex StockWord = W(@"stock|stok|maal|mal|اسٹاک|مال");
    private static readonly Regex Finish = W(@"khatam|khtm|finish\w*|run\s*out|runout|out\s+of\s+stock|end\s+ho\w*|ending|ختم");
    private static readonly Regex DaysLast = W(@"kitne\s+din|kitn[ae]\s+dino|how\s+many\s+days|days?\s+(?:chal|last)|chal\s*eg\w*|chale\s*g\w*|din\s+chal\w*|کتنے\s+دن|چلے\s*گا|چلے\s*گی");
    private static readonly Regex LastsWord = W(@"chal\s*eg\w*|chale\s*g\w*|chalta|chalti|last\s*s?|چلے\s*گ\w*");
    private static readonly Regex SoldWord = W(@"bik\w*|sell\w*|sold|بک\w*");
    private static readonly Regex Reorder = W(@"restock|re-?order|naya\s+batch|new\s+batch|batch|mangwa\w*|mangw\w*|supplier|wholesale|منگوا\w*|نیا\s+بیچ|بیچ");
    private static readonly Regex Track = W(@"track\w*|ٹریک");
    private static readonly Regex Not = W(@"nahi|nahin|na|not|never|without|bina|بغیر|نہیں");

    private static readonly Regex ExpWord = W(@"kharcha|kharche|kharch|kharchon|expenses?|spend\w*|spent|lag\w*|خرچ\w*|اخراجات|لگ\w*");
    private static readonly Regex ExpMost = W(@"sab\s+se\s+zyada|sab\s+se\s+bada|sab\s+se\s+bara|most|highest|biggest|largest|سب\s+سے\s+(?:زیادہ|بڑا)");
    private static readonly Regex WhichThing = W(@"kis|konsi|kaunsi|kaun\s*si|kaun|what|which|where|kahan|کس|کون\w*|کہاں");
    private static readonly Regex ExpOnPattern = new(
        @"(?:(?<c>[\p{L}]{3,20})\s+(?:pe|par|per|پر|پہ)\s+(?:(?:ab|abhi)\s+tak\s+|اب\s+تک\s+)?(?:kitn\w+|کتن\w+|how\s+much)|(?:spent|spend|expenses?|kharcha|kharch)\s+(?:on|for)\s+(?<c>[\p{L}]{3,20})|how\s+much\s+(?:on|for)\s+(?<c>[\p{L}]{3,20}))", Opts);
    private static readonly Regex AllTime = W(@"ab\s+tak|abhi\s+tak|so\s+far|till\s+now|until\s+now|اب\s+تک|اب\s+تلک");

    private static readonly Regex Return = W(@"return\w*|wapas|wapis|vapas|ریٹرن|واپس");
    private static readonly Regex Cancel = W(@"cancel\w*|کینسل|منسوخ");
    private static readonly Regex RateWord = W(@"rate|ratio|percent\w*|percentage|fisad|share|shara|شرح|فیصد");
    private static readonly Regex DeliveryTime = W(@"delivery\s+(?:time|waqt|ka\s+waqt|ka\s+time)|delivery\s+mein\s+kitn\w+|ڈلیوری\s+(?:ٹائم|وقت)|کتنے\s+دن\s+(?:میں|لگ\w*)|kitne\s+din\s+(?:mein|me|lagte|lagta|lagay|lagte\s+hain)");
    private static readonly Regex CourierWord = W(@"courier|tcs|leopards?|bluex|blue\s*ex|trax|postex|m\s*&\s*p|call\s*courier|ٹی\s*سی\s*ایس|کوریئر");
    private static readonly Regex TimeAvg = W(@"average|avg|ausat|waqt|time|din\s+lagte|ٹائم|وقت|اوسط");

    private static readonly Regex TodayWord = W(@"aaj|today|آج");
    private static readonly Regex YesterdayWord = W(@"kal|yesterday|کل");
    private static readonly Regex Compare = W(@"muqable\w*|mukable\w*|muqabla|comparison|compare\w*|vs|versus|behtar|better|worse|kaisi|kaisa|مقابل\w*|بہتر|کیسی|کیسا");
    private static readonly Regex ProfitWord = W(@"faid\w*|fayd\w*|munaf\w*|nafe|nafa|profit|gain|فائد\w*|منافع|نفع");
    private static readonly Regex LossWord = W(@"nuksan|nuqsan|nuqsaan|loss|نقصان");

    private static readonly Regex LowWord = W(@"kam|sab\s+se\s+kam|lowest|least|worst|minimum|کم");
    private static readonly Regex DeadStock = W(@"nahi\s+bik\w*|bilkul\s+nahi|dead\s+stock|not\s+selling|clear\s+kar\w*|phans\w*|نہیں\s+بک\w*|پھنس\w*");
    private static readonly Regex TopSelling = W(@"sab\s+se\s+zyada\s+bik\w*|sab\s+se\s+zyada\s+bika|top\s+sell\w*|best\s+sell\w*|most\s+sold|bestsellers?|سب\s+سے\s+زیادہ\s+بک\w*");
    private static readonly Regex Drawer = W(@"galla|gallay|galle|galley|gale|drawer|گلے|گلا|گلہ");
    private static readonly Regex ShortOver = W(@"short|shortage|zyada|kam|difference|farq|discrepancy|زیادہ|کم|فرق");
    private static readonly Regex SummaryWord = W(@"khulasa|khulasah|khalasa|summary|overview|خلاصہ");
    private static readonly Regex NetWord = W(@"net|khalis|khaalis|خالص");
    private static readonly Regex RevenueWord = W(@"revenue|net\s+sales?|total\s+sales?|ریونیو|ٹوٹل\s+سیل");
    private static readonly Regex Winter = W(@"winters?|sardi|sardiyon|sardiyan|sardiyo|سردی\w*|سردیوں");
    private static readonly Regex Summer = W(@"summers?|garmi|garmiyon|garmiyan|گرمی\w*|گرمیوں");
    private static readonly Regex LastYear = W(@"pichle\s+sal|pichle\s+saal|pichlay\s+saal|last\s+year|guzishta\s+saal|پچھلے\s+سال|گزشتہ\s+سال");
    private static readonly Regex CityWise = new(@"(?:city|area|shehar|sheher|town|علاقے|شہر)\s*[- ]?\s*wise|(?:city|area|shehar|sheher)\s+ke\s+hisab|علاقے\s+کے\s+حساب", Opts);

    private static readonly Regex AreaSe = new(
        @"(?<place>[\p{L}][\p{L}\s]{1,40}?)\s+(?:se|سے)\s+(?:mujhe\s+|muje\s+|mere\s+|meray\s+|مجھے\s+|میرے\s+)?(?:kitn\w+\s+|کتن\w+\s+|how\s+many\s+)?(?:orders?|آرڈرز?)", Opts);
    private static readonly Regex AreaFrom = new(
        @"(?:orders?|آرڈرز?)\s+(?:from|in|of)\s+(?<place>[\p{L}][\p{L}\s]{1,40}?)$|(?:from|in)\s+(?<place>[\p{L}][\p{L}\s]{1,40}?)\s+(?:how\s+many\s+)?orders?", Opts);
    private static readonly Regex AreaKe = new(
        @"(?<place>[\p{L}][\p{L}\s]{1,40}?)\s+(?:ke|mein|main|میں|کے)\s+(?:kitn\w+|کتن\w+|how\s+many)\s+(?:orders?|آرڈرز?)", Opts);

    private static readonly HashSet<string> PlaceFiller = new(StringComparer.OrdinalIgnoreCase)
    {
        "mujhe", "muje", "mere", "meray", "mera", "meri", "kya", "aur", "kitne", "kitni", "kitna", "how", "many", "much", "total", "ab", "tak", "abhi", "ye", "yeh", "wo", "woh",
        "city", "area", "shehar", "sheher", "in", "from", "of", "the", "my", "me", "all", "sab", "sirf", "bataiye", "batao", "bataein", "dikhao", "show", "please", "plz",
        "مجھے", "میرے", "میری", "میرا", "کیا", "اور", "کتنے", "کتنی", "کل", "اب", "تک", "یہ", "وہ", "سب", "صرف", "شہر", "علاقہ"
    };

    private static readonly Regex DaysNumber = new(@"(?<n>\d{1,3})\s*(?:days?|din|dino|دن)", Opts);
    private static readonly Regex MonthsNumber = new(@"(?:(?<n>\d{1,2})|(?<w>ek|aik|do|teen|ایک|دو|تین))\s*(?:months?|mahin\w+|mah|مہین\w+)", Opts);

    private static readonly char[] Trim = { ' ', '?', '؟', '.', '!', '،', ',', '۔', ';', ':' };

    public static ParsedCommand? TryParse(string message)
    {
        if (message.Contains('\n') || message.Length < 8 || message.Length > 240) return null;
        var text = Regex.Replace(Regex.Replace(message.Trim().Trim(Trim), @"(?<=\p{L})['’]s\b", ""), @"\s+", " ");
        if (text.Length < 8) return null;

        var (period, rest) = ExtractPeriod(text);
        ParsedCommand Topic(string topic, decimal? amount = null, string? periodKey = null, string? subject = null) =>
            new() { Kind = CommandKind.Insight, Text = topic, Text2 = periodKey ?? period, Text3 = subject ?? text, Amount = amount };

        if (AdvancedInsightParser.TryParse(text, period) is { } advanced) return advanced;

        // --- discount what-if / price review (margin questions about ONE product, so before the plain ones)
        if (Discount.IsMatch(text) && Margin.IsMatch(text) && Pct.Match(text) is { Success: true } pct
            && decimal.TryParse(pct.Groups["n"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var percent))
            return Topic("whatif", percent);
        if (PriceWord.IsMatch(text) && Raise.IsMatch(text) && Need.IsMatch(text) && !Discount.IsMatch(text)) return Topic("priceup");
        if (Regex.IsMatch(text, @"price\s+review|qeemat\s+ka\s+jaiza|قیمت\s+کا\s+جائزہ", Opts)) return Topic("priceup");
        if (MarginExplicit.IsMatch(text) && (Product.IsMatch(text) || Which.IsMatch(text)) && !Discount.IsMatch(text))
            return new ParsedCommand { Kind = CommandKind.Margin, Text = LowWord.IsMatch(text) ? "low" : null };

        if (Discount.IsMatch(text) && CanGive.IsMatch(text) && !Pct.IsMatch(text)) return new ParsedCommand { Kind = CommandKind.Margin };
        if (MarginExplicit.IsMatch(text) && AskWord.IsMatch(text) && !Discount.IsMatch(text)) return new ParsedCommand { Kind = CommandKind.Margin, Text = LowWord.IsMatch(text) ? "low" : null };
        if (LossWord.IsMatch(text) && Product.IsMatch(text) && (Which.IsMatch(text) || SoldWord.IsMatch(text)) && !ProfitWord.IsMatch(text)) return new ParsedCommand { Kind = CommandKind.Margin, Text = "low" };
        if (ProfitWord.IsMatch(text) && BigWord.IsMatch(text) && (Product.IsMatch(text) || Which.IsMatch(text)) && !LossWord.IsMatch(text) && !Customer.IsMatch(text)) return new ParsedCommand { Kind = CommandKind.Margin };

        // --- products and sales
        if (Pricey.IsMatch(text) && (Product.IsMatch(text) || Which.IsMatch(text)) && !Discount.IsMatch(text)) return Topic("pricey");
        if (Cheap.IsMatch(text) && (Product.IsMatch(text) || Which.IsMatch(text)) && !Discount.IsMatch(text)) return Topic("cheap");
        if (Avg.IsMatch(text) && !DeliveryTime.IsMatch(text) && !CourierWord.IsMatch(text) && (Sale.IsMatch(text) || Daily.IsMatch(text) || OrdersWord.IsMatch(text) || period is not null)) return Topic("avg");
        if (Sale.IsMatch(text) && BigWord.IsMatch(text) && Regex.IsMatch(text, @"(?<![\p{L}\p{N}])(?:din|day|دن)(?![\p{L}\p{N}])", Opts) && (Which.IsMatch(text) || WhichThing.IsMatch(text))) return Topic("avg");
        if (Discount.IsMatch(text) && Given.IsMatch(text) && (How.IsMatch(text) || text.Contains("total", StringComparison.OrdinalIgnoreCase))) return Topic("discgiven");

        // --- customers
        if (Customer.IsMatch(text) && Away.IsMatch(text)) return Topic("inactive", InactiveDays(text));
        if (Customer.IsMatch(text) && Repeat.IsMatch(text)) return Topic("repeat");
        if (Customer.IsMatch(text) && NewWord.IsMatch(text) && (How.IsMatch(text) || AskWord.IsMatch(text))) return Topic("newcustomers");
        if (Customer.IsMatch(text) && BigWord.IsMatch(text) && !Away.IsMatch(text)) return Topic("topcustomers");
        if (Regex.IsMatch(text, @"(?<![\p{L}\p{N}])(?:kisne|kis\s+ne|who|کس\s+نے)(?![\p{L}\p{N}])", Opts) && BigWord.IsMatch(text) && BuyWord.IsMatch(text)) return Topic("topcustomers");
        if (Udhaar.IsMatch(text) && (How.IsMatch(text) || Which.IsMatch(text) || Customer.IsMatch(text) || Regex.IsMatch(text, @"\b(?:hai|rehta|rehti|raha|list|sab|all|kis\s+kis|kis\s+ka)\b|رہتا|ہے|فہرست", Opts)) && !OrdersWord.IsMatch(text)) return Topic("balance");
        if (LastTime.IsMatch(text) && (BuyVerb.IsMatch(text) || OrdersWord.IsMatch(text))) return Topic("lastbought");

        // --- payments, returns, delivery
        if (PayMethodPhrase.IsMatch(text) || (CodWord.IsMatch(text) && OtherPay.IsMatch(text)) || (OtherPay.IsMatch(text) && OrdersWord.IsMatch(text) && How.IsMatch(text)) || (CodWord.IsMatch(text) && MostWord.IsMatch(text) && (OrdersWord.IsMatch(text) || Customer.IsMatch(text) || Which.IsMatch(text))))
            return Topic("paysplit");
        if ((Return.IsMatch(text) || Cancel.IsMatch(text)) && (How.IsMatch(text) || RateWord.IsMatch(text)) && (OrdersWord.IsMatch(text) || RateWord.IsMatch(text) || Return.IsMatch(text))) return Topic("returns");
        if (DeliveryTime.IsMatch(text) || (CourierWord.IsMatch(text) && TimeAvg.IsMatch(text) && Regex.IsMatch(text, @"delivery|ڈلیوری|din|دن|waqt|وقت", Opts))
            || (CourierWord.IsMatch(text) && (DaysLast.IsMatch(text) || Fastest.IsMatch(text)))) return Topic("deliverytime");

        // --- catalog categories ("mere paas kitni categories hain")
        if (CategoryWord.IsMatch(text) && Product.IsMatch(text) && !Sale.IsMatch(text) && !Avg.IsMatch(text)) return Topic("catproducts");
        if (CategoryWord.IsMatch(text) && !Sale.IsMatch(text) && (How.IsMatch(text) || Which.IsMatch(text) || AskWord.IsMatch(text))) return Topic("categories");
        // --- stock
        if (LeadSubject.IsMatch(text) && LeadVerb.IsMatch(text) && !How.IsMatch(text) && !Which.IsMatch(text) && DaysNumber.Match(text) is { Success: true } lead
            && int.TryParse(lead.Groups["n"].Value, out var leadDays) && leadDays > 0)
            return Topic("leadtime", leadDays);
        if (Track.IsMatch(text) && Not.IsMatch(text) && (Product.IsMatch(text) || StockWord.IsMatch(text))) return Topic("untracked");
        if ((StockWord.IsMatch(text) && Finish.IsMatch(text)) || LowStock.IsMatch(text)) return Topic("stockending", NumberOfDays(text));
        if (Reorder.IsMatch(text) && (StockWord.IsMatch(text) || Product.IsMatch(text) || Need.IsMatch(text))) return Topic("reorder");
        if ((StockWord.IsMatch(text) && DaysLast.IsMatch(text)) || (DaysLast.IsMatch(text) && LastsWord.IsMatch(text) && How.IsMatch(text)) || (Daily.IsMatch(text) && SoldWord.IsMatch(text) && How.IsMatch(text))) return Topic("stockdays");

        if (StockWord.IsMatch(text) && How.IsMatch(text) && Left.IsMatch(text)) return Topic("stocklevel");

        // --- expenses
        if (ExpWord.IsMatch(text) && ExpOnPattern.Match(text) is { Success: true } on)
            return Topic("expcat", periodKey: period ?? (AllTime.IsMatch(text) ? "all" : null), subject: on.Groups["c"].Value.Trim());
        if (ExpWord.IsMatch(text) && ((ExpMost.IsMatch(text) && WhichThing.IsMatch(text)) || Breakdown.IsMatch(text))) return Topic("exptop");

        // --- today / profit
        if (TodayWord.IsMatch(text) && YesterdayWord.IsMatch(text) && (Sale.IsMatch(text) || OrdersWord.IsMatch(text)) && CompareLoose.IsMatch(text)) return Topic("vsyesterday", periodKey: "");
        if (ThisUnit.Match(text) is { Success: true } thisUnit && LastUnit.Match(text) is { Success: true } lastUnit && (Sale.IsMatch(text) || OrdersWord.IsMatch(text))
            && UnitOf(thisUnit.Groups["u"].Value) == UnitOf(lastUnit.Groups["u"].Value))
            return Topic("vsprev", periodKey: UnitOf(thisUnit.Groups["u"].Value));
        if (ProfitWord.IsMatch(text) && LossWord.IsMatch(text)) return Topic("pnl");
        if (CategoryAsk.IsMatch(text)) return new ParsedCommand { Kind = CommandKind.CategorySales, Text = period };
        if (Sale.IsMatch(text) && MonthWord.IsMatch(text) && (Compare.IsMatch(text) || LowWord.IsMatch(text))) return Topic("productcompare");
        if (ExpWord.IsMatch(text) && How.IsMatch(text) && (period is not null || Regex.IsMatch(text, @"total|ٹوٹل|kul|کل", Opts)) && !Regex.IsMatch(text, @"sab\s+se|سب\s+سے", Opts))
            return new ParsedCommand { Kind = CommandKind.ExpenseList, Text = period ?? "month" };

        // --- the same questions answered by commands that already exist
        if ((Winter.IsMatch(text) || Summer.IsMatch(text)) && (SoldWord.IsMatch(text) || BigWord.IsMatch(text) || MostWord.IsMatch(text) || text.Contains("top", StringComparison.OrdinalIgnoreCase)))
            return new ParsedCommand { Kind = CommandKind.TrendingProducts, Text = SeasonRange(Winter.IsMatch(text), LastYear.IsMatch(text), DateTime.UtcNow) };
        if (SoldWord.IsMatch(text) && LowWord.IsMatch(text) && (Product.IsMatch(text) || Which.IsMatch(text)) && !MarginExplicit.IsMatch(text) && !StockWord.IsMatch(text)) return new ParsedCommand { Kind = CommandKind.SlowMovers, Text = period ?? "30d" };
        if (SoldWord.IsMatch(text) && BigWord.IsMatch(text) && (Product.IsMatch(text) || Which.IsMatch(text)) && !Customer.IsMatch(text)) return new ParsedCommand { Kind = CommandKind.TrendingProducts, Text = period ?? "30d" };
        if (DeadStock.IsMatch(text) && (Product.IsMatch(text) || StockWord.IsMatch(text) || Which.IsMatch(text))) return new ParsedCommand { Kind = CommandKind.SlowMovers, Text = period ?? "30d" };
        if (TopSelling.IsMatch(text)) return new ParsedCommand { Kind = CommandKind.TrendingProducts, Text = period ?? "30d" };
        if (Drawer.IsMatch(text) && ShortOver.IsMatch(text) && (How.IsMatch(text) || Regex.IsMatch(text, @"cash|کیش|paisa|paise|پیسے", Opts))) return new ParsedCommand { Kind = CommandKind.CashDrawer, Text = "status" };
        if (ProfitWord.IsMatch(text) && NetWord.IsMatch(text) && (TodayWord.IsMatch(text) || period is not null)) return new ParsedCommand { Kind = CommandKind.Profit, Text = period ?? "today" };
        if (ProfitWord.IsMatch(text) && AskWord.IsMatch(text) && period is not null && !LossWord.IsMatch(text)) return new ParsedCommand { Kind = CommandKind.Profit, Text = period };
        if (SummaryWord.IsMatch(text) && period is not null && text.Split(' ').Length >= 4 && Regex.IsMatch(text, @"karobar\w*|business|poore|poora|weekly|پورے|کاروبار\w*", Opts))
            return new ParsedCommand { Kind = CommandKind.TodaysSummary, Text = period };
        if ((RevenueWord.IsMatch(text) || (Sale.IsMatch(text) && NetWord.IsMatch(text))) && period is not null && (AskWord.IsMatch(text) || Regex.IsMatch(text, @"dikha\w*|bata\w*|show|دکھا\w*|بتا\w*", Opts)))
            return new ParsedCommand { Kind = CommandKind.TodaysSummary, Text = period };
        if (Sale.IsMatch(text) && AskWord.IsMatch(text) && period is not null && !Daily.IsMatch(text) && !Avg.IsMatch(text) && !Regex.IsMatch(text, @"category|categories|product|پروڈکٹ", Opts))
            return new ParsedCommand { Kind = CommandKind.TodaysSummary, Text = period };

        // --- orders from an area / city wise
        if (OrdersWord.IsMatch(text) || Sale.IsMatch(text))
        {
            if (CityWise.IsMatch(text)) return Topic("area", subject: "");
            var place = PlaceOf(rest);
            if (place is not null) return Topic("area", subject: place);
        }
        return null;
    }

    /// <summary>The longest run of words that is wholly a period (ReportPeriods), and the text with that run taken out.</summary>
    private static (string? Key, string Remaining) ExtractPeriod(string text)
    {
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var now = DateTime.UtcNow;
        for (var len = Math.Min(7, tokens.Length); len >= 1; len--)
            for (var start = 0; start + len <= tokens.Length; start++)
            {
                var candidate = string.Join(' ', tokens[start..(start + len)]);
                if (len == 1 && candidate.Length < 3) continue;
                if (!ReportPeriods.TryParseKey(candidate, now, out var key)) continue;
                if (ReportPeriods.Resolve(key, null, now) is { IsFuture: true }) continue; // "aane wale customers" is not "tomorrow"
                var rest = string.Join(' ', tokens[..start].Concat(tokens[(start + len)..]));
                return (key, rest);
            }
        return (null, text);
    }

    private static string? PlaceOf(string rest)
    {
        foreach (var rx in new[] { AreaSe, AreaFrom, AreaKe })
        {
            var m = rx.Match(rest);
            if (!m.Success) continue;
            var words = m.Groups["place"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            while (words.Count > 0 && PlaceFiller.Contains(words[0])) words.RemoveAt(0);
            while (words.Count > 0 && PlaceFiller.Contains(words[^1])) words.RemoveAt(words.Count - 1);
            var place = string.Join(' ', words);
            if (place.Length >= 3 && words.Count <= 4) return place;
        }
        return null;
    }

    /// <summary>"pichle ek mahine se" -> 30, "2 mahine" -> 60, "14 din" -> 14, "hafte" -> 7; 30 when nothing is said.</summary>
    private static decimal InactiveDays(string text)
    {
        if (DaysNumber.Match(text) is { Success: true } d && int.TryParse(d.Groups["n"].Value, out var days) && days > 0) return days;
        if (MonthsNumber.Match(text) is { Success: true } m)
        {
            var n = m.Groups["n"].Success ? int.Parse(m.Groups["n"].Value) : m.Groups["w"].Value.ToLowerInvariant() switch { "do" or "دو" => 2, "teen" or "تین" => 3, _ => 1 };
            return Math.Clamp(n, 1, 24) * 30;
        }
        return Regex.IsMatch(text, @"hafte|haftay|week|ہفتے", Opts) ? 7 : 30;
    }

    private static string UnitOf(string word) => word.ToLowerInvariant() switch
    {
        "week" or "hafte" or "hafta" or "haftay" or "ہفتے" => "week",
        _ => "month"
    };

    private static decimal? NumberOfDays(string text) =>
        DaysNumber.Match(text) is { Success: true } d && int.TryParse(d.Groups["n"].Value, out var days) && days is > 0 and <= 365 ? days : null;

    /// <summary>A whole-day range key for the winter (Nov-Feb) or summer (Apr-Aug) of last year, or of the latest finished season.</summary>
    internal static string SeasonRange(bool winter, bool lastYear, DateTime utcNow)
    {
        var year = utcNow.Year;
        int startYear;
        if (winter)
        {
            // "pichle saal ki sardiyan" = the winter that began last calendar year; otherwise the latest winter that has ended.
            startYear = lastYear ? year - 1 : utcNow.Month >= 3 ? year - 1 : year - 2;
            return $"range:{startYear}-11-01..{startYear + 1}-02-{(DateTime.IsLeapYear(startYear + 1) ? 29 : 28)}";
        }
        startYear = lastYear ? year - 1 : utcNow.Month >= 9 ? year : year - 1;
        return $"range:{startYear}-04-01..{startYear}-08-31";
    }
}
