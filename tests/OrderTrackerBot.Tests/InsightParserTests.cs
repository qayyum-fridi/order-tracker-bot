using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

// The 35 business-analysis questions a seller can ask (Urdu script as a seller would speak them), plus Roman Urdu / English ways of saying the same thing.
// Each phrase must reach the right report; the numbers themselves are computed in InsightReportTests.
public class InsightParserTests
{
    private static ParsedCommand Parse(string text) => CommandParser.TryParse(text) ?? throw new Xunit.Sdk.XunitException($"not a command: {text}");

    private static void Topic(string text, string topic)
    {
        var parsed = Parse(text);
        Assert.Equal(CommandKind.Insight, parsed.Kind);
        Assert.Equal(topic, parsed.Text);
    }

    // --- products and sales (1-7)
    [Theory]
    [InlineData("سب سے زیادہ بکنے والا پروڈکٹ کون سا ہے؟")]
    [InlineData("sab se zyada bikne wala product kaun sa hai")]
    [InlineData("top selling item")]
    public void TopSelling(string text) => Assert.Equal(CommandKind.TrendingProducts, Parse(text).Kind);

    [Theory]
    [InlineData("پچھلے ہفتے میری ٹوٹل نیٹ سیل کتنی ہوئی تھی؟", "lastweek")]
    [InlineData("pichle hafte meri net sale kitni thi", "lastweek")]
    [InlineData("what was my net sales last week", "lastweek")]
    public void NetSaleLastWeek(string text, string period)
    {
        var parsed = Parse(text);
        Assert.Equal(CommandKind.TodaysSummary, parsed.Kind);
        Assert.Equal(period, parsed.Text);
    }

    [Theory]
    [InlineData("کون سا مال بالکل نہیں بک رہا تاکہ میں اسے کلیئر کروں؟")]
    [InlineData("kaun sa maal bilkul nahi bik raha clear karna hai")]
    [InlineData("which products are not selling")]
    public void DeadStock(string text) => Assert.Equal(CommandKind.SlowMovers, Parse(text).Kind);

    [Fact]
    public void ProductMonthComparison() => Topic("کیا پولو شرٹ کی سیل پچھلے مہینے سے بہتر ہے یا کم؟", "productcompare");

    [Theory]
    [InlineData("اس مہینے کپڑوں سے کتنی کمائی ہوئی اور گروسری سے کتنی؟")]
    [InlineData("kapron se kitni sale hui aur grocery se kitni")]
    public void CategorySales(string text) => Assert.Equal(CommandKind.CategorySales, Parse(text).Kind);

    [Theory]
    [InlineData("میرا سب سے مہنگا پروڈکٹ کون سا ہے اور وہ کتنی بار بکا؟")]
    [InlineData("sab se mehnga product kaun sa hai")]
    [InlineData("which is my most expensive product")]
    public void MostExpensive(string text) => Topic(text, "pricey");

    [Theory]
    [InlineData("sab se sasta product kaun sa hai")]
    [InlineData("cheapest item")]
    public void Cheapest(string text) => Topic(text, "cheap");

    [Theory]
    [InlineData("پچھلے 30 دن کی اوسط سیلز (Average Daily Sales) کیا ہے؟")]
    [InlineData("pichle 30 din ki average sale kya hai")]
    [InlineData("roz ki average bikri kitni hai")]
    [InlineData("what is my average daily sales")]
    public void AverageDailySales(string text) => Topic(text, "avg");

    // --- profit and margin (8-13)
    [Theory]
    [InlineData("مجھے کس پروڈکٹ پر سب سے زیادہ پرافٹ مارجن مل رہا ہے؟")]
    [InlineData("kis product par sab se zyada profit margin mil raha hai")]
    public void BestMargin(string text)
    {
        var parsed = Parse(text);
        Assert.Equal(CommandKind.Margin, parsed.Kind);
        Assert.Null(parsed.Text);
    }

    [Theory]
    [InlineData("سب سے کم پرافٹ مارجن کس آئٹم پر ہے؟")]
    [InlineData("sab se kam margin kis item par hai")]
    [InlineData("which product has the lowest margin")]
    public void LowestMargin(string text)
    {
        var parsed = Parse(text);
        Assert.Equal(CommandKind.Margin, parsed.Kind);
        Assert.Equal("low", parsed.Text);
    }

    [Theory]
    [InlineData("آج کی ٹوٹل سیلز میں میرا خالص منافع (Net Profit) کتنا ہے؟")]
    [InlineData("aaj ka khalis munafa kitna hai")]
    [InlineData("what is my net profit today")]
    public void NetProfitToday(string text)
    {
        var parsed = Parse(text);
        Assert.Equal(CommandKind.Profit, parsed.Kind);
        Assert.Equal("today", parsed.Text);
    }

    [Theory]
    [InlineData("اگر میں لان سوٹ پر 10% ڈسکاؤنٹ دوں تو میرا مارجن کتنا بچے گا؟", 10)]
    [InlineData("agar lawn suit par 10% discount doon to margin kitna bachega", 10)]
    [InlineData("if I give 15 percent discount on kurti what margin is left", 15)]
    public void DiscountWhatIf(string text, int percent)
    {
        var parsed = Parse(text);
        Assert.Equal("whatif", parsed.Text);
        Assert.Equal(percent, parsed.Amount);
    }

    [Theory]
    [InlineData("کیا دوپٹے کی قیمت بڑھانے کی ضرورت ہے؟ میرا مارجن کم ہو رہا ہے۔")]
    [InlineData("kya dupatte ki qeemat barhane ki zaroorat hai")]
    [InlineData("should I increase the price of dupatta")]
    [InlineData("price review")]
    public void PriceReview(string text) => Topic(text, "priceup");

    [Theory]
    [InlineData("پچھلے مہینے میں نے کتنا ڈسکاؤنٹ دیا اور اس سے سیلز پر کیا فرق پڑا؟")]
    [InlineData("pichle mahine maine kitna discount diya")]
    [InlineData("how much discount did I give last month")]
    public void DiscountGiven(string text)
    {
        var parsed = Parse(text);
        Assert.Equal("discgiven", parsed.Text);
        Assert.Equal("lastmonth", parsed.Text2);
    }

    // --- customers (14-20)
    [Theory]
    [InlineData("میرا سب سے بڑا کسٹمر کون ہے جس نے سب سے زیادہ خریداری کی؟")]
    [InlineData("mera sab se bada customer kaun hai jisne sab se zyada kharidari ki")]
    [InlineData("who is my top spending customer")]
    public void TopSpender(string text) => Topic(text, "topcustomers");

    [Theory]
    [InlineData("سارا نے پچھلی بار مجھ سے کب اور کیا خریدا تھا؟")]
    [InlineData("Sara ne pichli baar kab aur kya kharida tha")]
    [InlineData("what did Sara buy last time")]
    public void LastPurchase(string text) => Topic(text, "lastbought");

    [Theory]
    [InlineData("کون سے کسٹمرز پچھلے ایک مہینے سے واپس نہیں آئے؟", 30)]
    [InlineData("kaun se customers pichle ek mahine se wapas nahi aaye", 30)]
    [InlineData("customers who haven't ordered in 14 days", 14)]
    [InlineData("kaun se customers 2 mahine se nahi aaye", 60)]
    public void InactiveCustomers(string text, int days)
    {
        var parsed = Parse(text);
        Assert.Equal("inactive", parsed.Text);
        Assert.Equal(days, parsed.Amount);
    }

    [Theory]
    [InlineData("بلال کا کل کتنا ادھار (Pending Balance) رہتا ہے؟")]
    [InlineData("Bilal ka kitna udhaar hai")]
    [InlineData("how much balance does Bilal owe")]
    public void CustomerBalance(string text) => Topic(text, "balance");

    [Theory]
    [InlineData("کیا لوگ زیادہ تر کیش آن ڈلیوری (COD) لیتے ہیں یا ایزی پیسہ؟")]
    [InlineData("log zyada tar COD lete hain ya easypaisa")]
    [InlineData("payment method split")]
    public void PaymentSplit(string text) => Topic(text, "paysplit");

    [Theory]
    [InlineData("سب سے زیادہ ریپیٹ کسٹمرز (بار بار آنے والے گاہک) کون سے ہیں؟")]
    [InlineData("bar bar aane wale customers kaun hain")]
    [InlineData("who are my repeat customers")]
    public void RepeatCustomers(string text) => Topic(text, "repeat");

    [Theory]
    [InlineData("ماڈل ٹاؤن سے مجھے کتنے آرڈرز آئے ہیں؟", "ماڈل ٹاؤن")]
    [InlineData("Model Town se mujhe kitne orders aaye hain", "Model Town")]
    [InlineData("how many orders from Gulberg", "Gulberg")]
    public void OrdersFromAnArea(string text, string place)
    {
        var parsed = Parse(text);
        Assert.Equal("area", parsed.Text);
        Assert.Equal(place, parsed.Text3, ignoreCase: true);
    }

    [Theory]
    [InlineData("city wise orders")]
    [InlineData("shehar ke hisab se orders")]
    public void OrdersPerCity(string text)
    {
        var parsed = Parse(text);
        Assert.Equal("area", parsed.Text);
        Assert.Equal("", parsed.Text3);
    }

    // --- stock (21-25)
    [Theory]
    [InlineData("کون سا اسٹاک اگلے 3 دن میں ختم ہونے والا ہے؟", 3)]
    [InlineData("kaun sa stock agle 3 din mein khatam hone wala hai", 3)]
    [InlineData("which stock will run out soon", null)]
    public void StockEnding(string text, int? days)
    {
        var parsed = Parse(text);
        Assert.Equal("stockending", parsed.Text);
        Assert.Equal(days, (int?)parsed.Amount);
    }

    [Theory]
    [InlineData("پولو شرٹ روزانہ کتنی بکتی ہے اور موجودہ اسٹاک کتنے دن چلے گا؟")]
    [InlineData("polo shirt roz kitni bikti hai aur stock kitne din chalega")]
    [InlineData("how many days will the stock last")]
    public void StockDays(string text) => Topic(text, "stockdays");

    [Theory]
    [InlineData("کیا مجھے لان سوٹ کا نیا بیچ آرڈر کر لینا چاہیے؟")]
    [InlineData("kya mujhe lawn suit ka naya batch mangwana chahiye")]
    [InlineData("should I restock lawn suit")]
    public void Reorder(string text) => Topic(text, "reorder");

    [Fact]
    public void Seasonal_WinterLastYear_IsATrendingReportOverARange()
    {
        var parsed = Parse("پچھلے سال سردیوں میں سب سے زیادہ کیا بکا تھا؟");
        Assert.Equal(CommandKind.TrendingProducts, parsed.Kind);
        Assert.StartsWith("range:", parsed.Text);
    }

    [Theory]
    [InlineData("مجھے کیٹیگری وائز شو کریں پروڈکٹس کی لسٹ")]
    [InlineData("category wise products ki list dikhao")]
    [InlineData("category products")]
    public void ProductsByCategory(string text) => Topic(text, "catproducts");

    [Theory]
    [InlineData("میرے پاس کتنی کیٹیگریز ہیں؟")]
    [InlineData("mere paas kitni categories hain?")]
    [InlineData("how many categories do I have")]
    public void CatalogCategories(string text) => Topic(text, "categories");

    [Theory]
    [InlineData("کتنے پروڈکٹس ایسے ہیں جن کا اسٹاک میں نے ٹریک نہیں کیا ہوا؟")]
    [InlineData("kitne products ka stock track nahi kiya")]
    [InlineData("how many products are not tracked in stock")]
    public void UntrackedStock(string text) => Topic(text, "untracked");

    // --- expenses (26-30)
    [Theory]
    [InlineData("اس مہینے میرے ٹوٹل بزنس کے خرچے (Total Expenses) کتنے ہیں؟")]
    [InlineData("is mahine mere total kharche kitne hain")]
    public void TotalExpenses(string text) => Assert.Equal(CommandKind.ExpenseList, Parse(text).Kind);

    [Theory]
    [InlineData("پٹرول پر اب تک کتنے پیسے لگ چکے ہیں؟", "پٹرول", "all")]
    [InlineData("petrol par ab tak kitna kharcha hua", "petrol", "all")]
    [InlineData("how much spent on packaging", "packaging", null)]
    public void ExpenseOnACategory(string text, string category, string? period)
    {
        var parsed = Parse(text);
        Assert.Equal("expcat", parsed.Text);
        Assert.Equal(category, parsed.Text3);
        Assert.Equal(period, parsed.Text2);
    }

    [Theory]
    [InlineData("سب سے زیادہ خرچہ کس چیز پر ہو رہا ہے؟")]
    [InlineData("sab se zyada kharcha kis cheez par ho raha hai")]
    [InlineData("what is my biggest expense")]
    public void BiggestExpense(string text) => Topic(text, "exptop");

    [Theory]
    [InlineData("کتنے آرڈرز ڈلیوری کے دوران کینسل یا ریٹرن ہوئے؟")]
    [InlineData("kitne orders cancel ya return hue")]
    [InlineData("what is my return rate")]
    public void ReturnRate(string text) => Topic(text, "returns");

    [Theory]
    [InlineData("ٹی سی ایس (TCS) کے ذریعے بھیجے گئے آرڈرز کا اوسط ڈلیوری وقت کیا ہے؟")]
    [InlineData("TCS ka average delivery time kya hai")]
    [InlineData("average delivery time")]
    public void DeliveryTime(string text) => Topic(text, "deliverytime");

    // --- summaries (31-35)
    [Theory]
    [InlineData("مجھے پچھلے پورے ہفتے کا ایک کاروباری خلاصہ (Weekly Summary) دو۔")]
    [InlineData("mujhe pichle poore hafte ka karobari khulasa do")]
    public void WeeklySummary(string text)
    {
        var parsed = Parse(text);
        Assert.Equal(CommandKind.TodaysSummary, parsed.Kind);
        Assert.Equal("lastweek", parsed.Text);
    }

    [Theory]
    [InlineData("آج گلے (Drawer) میں کتنا کیش شارٹ یا زیادہ ہے؟")]
    [InlineData("aaj galle mein kitna cash short ya zyada hai")]
    public void DrawerDifference(string text) => Assert.Equal(CommandKind.CashDrawer, Parse(text).Kind);

    [Theory]
    [InlineData("کیا میں اس وقت فائدے میں جا رہا ہوں یا نقصان میں؟")]
    [InlineData("kya main faide mein hoon ya nuksan mein")]
    [InlineData("am I in profit or loss")]
    public void ProfitOrLoss(string text) => Topic(text, "pnl");

    [Theory]
    [InlineData("کل کے مقابلے میں آج سیلز کیسی رہیں؟")]
    [InlineData("kal ke muqable mein aaj sales kaisi rahi")]
    [InlineData("today sales compared to yesterday")]
    public void TodayVsYesterday(string text) => Topic(text, "vsyesterday");

    [Theory]
    [InlineData("اس مہینے کا اب تک کا ٹوٹل ریونیو (Revenue) دکھاؤ۔")]
    [InlineData("is mahine ka total revenue dikhao")]
    [InlineData("show me this month's revenue")]
    public void MonthRevenue(string text)
    {
        var parsed = Parse(text);
        Assert.Equal(CommandKind.TodaysSummary, parsed.Kind);
        Assert.Equal("thismonth", parsed.Text);
    }

    // --- more ways a seller says the same things (beyond the 35)
    [Theory]
    [InlineData("kis product par sab se zyada munafa hota hai", CommandKind.Margin, null)]
    [InlineData("kaun sa product loss mein bik raha hai", CommandKind.Margin, "low")]
    [InlineData("mera profit margin kitna hai", CommandKind.Margin, null)]
    [InlineData("kitna discount de sakta hoon", CommandKind.Margin, null)]
    [InlineData("sab se kam bikne wala product kaun sa hai", CommandKind.SlowMovers, "30d")]
    [InlineData("kaun sa product sab se zyada bika", CommandKind.TrendingProducts, "30d")]
    [InlineData("kitne return aaye", CommandKind.Insight, "returns")]
    [InlineData("kitne orders easypaisa se aaye", CommandKind.Insight, "paysplit")]
    [InlineData("kaun sa courier sab se tez hai", CommandKind.Insight, "deliverytime")]
    [InlineData("courier ne kitne din liye", CommandKind.Insight, "deliverytime")]
    [InlineData("stock khatam hone wala hai", CommandKind.Insight, "stockending")]
    [InlineData("polo shirt ka stock kitna bacha hai", CommandKind.Insight, "stocklevel")]
    [InlineData("kharcha category wise dikhao", CommandKind.Insight, "exptop")]
    [InlineData("naye customers kitne aaye", CommandKind.Insight, "newcustomers")]
    [InlineData("how many new customers this month", CommandKind.Insight, "newcustomers")]
    [InlineData("kisne sab se zyada kharida", CommandKind.Insight, "topcustomers")]
    [InlineData("top customers", CommandKind.Insight, "topcustomers")]
    [InlineData("udhaar list", CommandKind.Insight, "balance")]
    [InlineData("kis kis ka udhaar baqi hai", CommandKind.Insight, "balance")]
    [InlineData("Sara ka last order kab tha", CommandKind.Insight, "lastbought")]
    [InlineData("kal se zyada sale hui aaj", CommandKind.Insight, "vsyesterday")]
    [InlineData("is hafte ki sale pichle hafte se kaisi hai", CommandKind.Insight, "vsprev")]
    [InlineData("is mahine ki sale pichle mahine se kam hai ya zyada", CommandKind.Insight, "vsprev")]
    [InlineData("sab se zyada sale kis din hui", CommandKind.Insight, "avg")]
    [InlineData("average order value", CommandKind.Insight, "avg")]
    [InlineData("maal aane mein 5 din lagte hain", CommandKind.Insight, "leadtime")]
    [InlineData("supplier takes 10 days to deliver", CommandKind.Insight, "leadtime")]
    [InlineData("polo shirt kitne din chalega", CommandKind.Insight, "stockdays")]
    [InlineData("munafa kitna hua is mahine", CommandKind.Profit, null)]
    public void MoreWaysToAsk(string text, CommandKind kind, string? topicOrText)
    {
        var parsed = Parse(text);
        Assert.Equal(kind, parsed.Kind);
        if (topicOrText is not null) Assert.Equal(topicOrText, parsed.Text);
    }

    // --- ordinary commands and orders must never be taken for a question
    [Theory]
    [InlineData("orders today")]
    [InlineData("mark 3 shipped")]
    [InlineData("Ayesha ka order")]
    [InlineData("stock Kurti 20")]
    [InlineData("expense 500 petrol")]
    [InlineData("cod pending")]
    [InlineData("Kurti - 1800, cost 1200")]
    [InlineData("create discount: EID10 10%")]
    [InlineData("loyal customers")]
    [InlineData("margin")]
    [InlineData("profit")]
    public void OrdinaryCommands_KeepTheirMeaning(string text) => Assert.NotEqual(CommandKind.Insight, Parse(text).Kind);

    [Theory]
    [InlineData("Ayesha 03001234567 Lahore Kurti 2 delivery 200")]
    [InlineData("Sara, Model Town, 2 Lawn Suit 3500")]
    [InlineData("hello")]
    [InlineData("thanks")]
    public void OrdersAndChat_AreNotQuestions(string text) => Assert.NotEqual(CommandKind.Insight, CommandParser.TryParse(text)?.Kind);
}
