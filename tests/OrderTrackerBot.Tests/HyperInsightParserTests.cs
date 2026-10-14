using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

// The 25 hyper-complex scenarios (Urdu script as a seller would say them) plus Roman Urdu / English versions.
public class HyperInsightParserTests
{
    private static ParsedCommand Parse(string text) => CommandParser.TryParse(text) ?? throw new Xunit.Sdk.XunitException($"not a command: {text}");

    private static ParsedCommand Topic(string text, string topic)
    {
        var parsed = Parse(text);
        Assert.Equal(CommandKind.Insight, parsed.Kind);
        Assert.Equal(topic, parsed.Text);
        return parsed;
    }

    [Theory]
    [InlineData("میرا وہ کون سا کسٹمر ہے جو آرڈرز تو بہت بڑے دیتا ہے، لیکن ڈسکاؤنٹ مانگنے یا ریٹرن (RTO) کی وجہ سے اس سے میرا نیٹ منافع (Net Profit Contribution) سب سے کم ہے؟")]
    [InlineData("kaun sa customer bade orders deta hai magar discount aur return ki wajah se mera net profit contribution sab se kam hai")]
    [InlineData("which customer has the lowest net profit contribution because of discounts and returns")]
    public void CustomerContribution(string text) => Topic(text, "custprofit");

    [Theory]
    [InlineData("کسٹمر بلال خان نے پچھلے ایک سال میں مجھے ٹوٹل کتنا خالص منافع (Cumulative Net Profit) کما کر دیا ہے؟")]
    [InlineData("customer Bilal Khan ne pichle ek saal mein mujhe kitna khalis munafa kama kar diya")]
    public void OneCustomersProfit(string text) => Topic(text, "custprofit");

    [Theory]
    [InlineData("پچھلے 6 مہینوں میں جن گاہکوں نے پہلی بار خریداری کی تھی، ان میں سے کتنے فیصد گاہکوں نے دوسرا آرڈر دیا؟ (New Customer Retention Rate)")]
    [InlineData("pichle 6 mahine mein jin customers ne pehli baar kharidari ki unmein se kitne fisad ne dobara order diya")]
    public void NewCustomerRetention(string text) => Topic(text, "retention");

    [Theory]
    [InlineData("اگر میں اپنے ٹاپ 10% سب سے زیادہ وفادار گاہکوں (VIP Customers) کی لسٹ نکالوں، تو میری کل دکان کی سیلز میں ان کا حصہ کتنے فیصد بنتا ہے؟")]
    [InlineData("mere top 10% vip customers ka meri kul sale mein hissa kitna hai")]
    public void VipShare(string text) => Topic(text, "vipshare");

    [Theory]
    [InlineData("ان پروڈکٹس کی لسٹ دو جن کی سیلز کی رفتار (Sales Velocity) پچھلے 15 دنوں میں اچانک 50% گر گئی ہے، تاکہ میں ان کی قیمت کم کرنے کا فیصلہ کروں۔", 15)]
    [InlineData("un products ki list do jin ki sales velocity pichle 15 din mein achanak 50% gir gayi hai", 15)]
    public void VelocityDrop(string text, int days)
    {
        var parsed = Topic(text, "velocitydrop");
        Assert.Equal(days, (int?)parsed.Amount);
    }

    [Theory]
    [InlineData("میرا کون سا مال ایسا ہے جو دکان پر آتے ہی 3 دن کے اندر پورا بک جاتا ہے (Fast-Moving items) اور مجھے اس کا کتنا اسٹاک ہول سیلر سے ایڈوانس بک کروانا چاہیے؟")]
    [InlineData("mera kaun sa maal fast moving hai aur mujhe uska kitna stock advance book karwana chahiye")]
    public void FastMoving(string text) => Topic(text, "fastmoving");

    [Theory]
    [InlineData("اس وقت دکان میں جتنا بھی اسٹاک پڑا ہے، اگر وہ موجودہ رفتار سے بکتا رہے، تو دکان کا سارا مال کتنے دنوں میں مکمل صاف ہو جائے گا؟ (Inventory Days on Hand)")]
    [InlineData("agar maujooda raftar se bikta raha to mera sara stock kitne dino mein saaf ho jayega")]
    public void DaysOnHand(string text) => Topic(text, "daysonhand");

    [Theory]
    [InlineData("کون سے ایسے پروڈکٹس ہیں جن کا اسٹاک تو ختم (Out of Stock) ہو چکا ہے، لیکن گاہک ابھی تک واٹس ایپ پر ان کا پوچھ رہے ہیں؟ (Missed Demand Analysis)")]
    [InlineData("kin products ka stock out of stock hai lekin gahak abhi tak unka puch rahe hain")]
    public void MissedDemand(string text) => Topic(text, "outofstockhot");

    [Theory]
    [InlineData("پچھلے 30 دن میں میرے کیٹلاگ کے کونسے 5 پروڈکٹس ایسے ہیں جن کا منافع کا فیصد (Profit Margin %) لاگت بڑھنے کی وجہ سے سب سے زیادہ نیچے گرا ہے؟")]
    [InlineData("pichle 30 din mein kaun se 5 products ka profit margin lagat barhne ki wajah se sab se zyada gira hai")]
    public void MarginDrop(string text) => Topic(text, "margindrop");

    [Theory]
    [InlineData("اگر ہول سیل مارکیٹ میں کپڑوں کی لاگت (Cost Price) میں 15% اضافہ ہو جائے، اور میں اپنی قیمتیں نہ بڑھاؤں، تو میرا دکان کا بریک ایون (Break-even یعنی نہ نفع نہ نقصان) پوائنٹ کہاں پر جائے گا؟", 15)]
    [InlineData("agar kapron ki cost 15% barh jaye aur main qeemat na barhaun to mera break even kahan jayega", 15)]
    public void CostShock(string text, int percent)
    {
        var parsed = Topic(text, "costshock");
        Assert.Equal(percent, (int?)parsed.Amount);
    }

    [Theory]
    [InlineData("کیا کوئی ایسا پروڈکٹ ہے جس کی ہول سیل لاگت (Cost) تو کم ہوئی ہے لیکن میں نے دکان پر اس کا ریٹ پرانا ہی رکھا ہوا ہے، جس سے میرا مارجن خود بخود بڑھ گیا ہے؟")]
    [InlineData("kya koi product hai jis ki cost kam hui hai lekin maine rate purana hi rakha hua hai")]
    public void CostFellRateUnchanged(string text) => Topic(text, "costdrop");

    [Theory]
    [InlineData("ٹی سی ایس (TCS) کے پاس میرے ایسے کتنے آرڈرز پینڈنگ پڑے ہیں جو ڈیلیور تو ہو چکے ہیں لیکن ان کا کیش (COD Cash Settlement) ابھی تک میرے بینک اکاؤنٹ یا دراز میں جمع نہیں ہوا؟")]
    [InlineData("TCS ke paas mere kitne orders delivered hain lekin unka cash jama nahi hua")]
    public void CourierHoldingCash(string text) => Topic(text, "uncollected");

    [Theory]
    [InlineData("کون سا شہر یا علاقہ ایسا ہے جہاں بھیجے گئے پارسل سب سے زیادہ فیل (Failed Deliveries) ہوتے ہیں، تاکہ میں وہاں کے گاہکوں سے ایڈوانس ایزی پیسہ مانگوں؟")]
    [InlineData("kaun se shehar mein bheje gaye parcel sab se zyada fail hote hain")]
    public void FailedDeliveriesByCity(string text) => Topic(text, "failcity");

    [Theory]
    [InlineData("پچھلے ایک مہینے میں ریٹرن آنے والے سوٹوں (RTO) کو دوبارہ کیٹلاگ میں شامل کرنے پر کتنا اسٹاک واپس چڑھا اور کتنا مال راستے میں خراب یا گم ہوا؟")]
    [InlineData("pichle mahine return aane wale suit catalog mein wapas kitna stock chadha aur kitna maal kharab ya gum hua")]
    public void ReturnedStock(string text) => Topic(text, "rtostock");

    [Theory]
    [InlineData("اگر میں ڈلیوری چارجز 200 روپے سے بڑھا کر 250 روپے کر دوں، تو کیا دور کے شہروں والے گاہکوں کے آرڈرز ڈراپ ہو جائیں گے؟ (Delivery Price Sensitivity)", 250)]
    [InlineData("agar main delivery charges 200 se 250 kar dun to kya orders drop ho jayenge", 250)]
    public void DeliveryChargeSensitivity(string text, int charge)
    {
        var parsed = Topic(text, "chargeeffect");
        Assert.Equal(charge, (int?)parsed.Amount);
    }

    [Theory]
    [InlineData("پچھلے 30 دنوں کا ڈیٹا دیکھ کر بتاؤ کہ دراز (Drawer) میں سب سے زیادہ نقد کیش کی بے ضابطگی (Cash Mismatch) کس دن اور کس شفٹ میں ہوئی؟")]
    [InlineData("pichle 30 din ka data dekh kar batao drawer mein sab se zyada cash mismatch kis din hui")]
    public void DrawerMismatch(string text)
    {
        var parsed = Topic(text, "drawershort");
        Assert.Equal("30d", parsed.Text2);
    }

    [Theory]
    [InlineData("کیا دکان کے روزمرہ کے خرچے (Expenses) میری نقد سیلز (Cash Sales) سے زیادہ ہو رہے ہیں؟ میرا نیٹ کیش فلو (Net Cash Flow) پوزیٹو ہے یا نیگیٹو؟")]
    [InlineData("kya mere kharche meri cash sales se zyada ho rahe hain net cash flow positive hai ya negative")]
    public void NetCashFlow(string text) => Topic(text, "cashflow");

    [Theory]
    [InlineData("آج رات دراز بند کرنے کے بعد، مجھے کل صبح سپلائر کو پیمنٹ کرنے کے لیے کتنا کیش دراز میں چھوڑنا چاہیے اور کتنا بینک میں جمع کروانا چاہیے؟")]
    [InlineData("aaj raat drawer band karne ke baad kal supplier ko payment ke liye kitna cash chhodna chahiye aur kitna bank mein jama karwana chahiye")]
    public void DrawerCloseAndDeposit(string text) => Topic(text, "cashplan");

    [Theory]
    [InlineData("جو کوڈ 'EID10' میں نے چلایا تھا، اس سے جو نیو سیلز آئیں، کیا ان سے دکان کا ٹوٹل منافع بڑھا یا گاہکوں نے وہی چیزیں سستے میں خرید لیں جو وہ ویسے بھی پورے ریٹ پر خریدنے والے تھے؟")]
    [InlineData("jo code EID10 maine chalaya tha us se sale ka munafa barha ya gahakon ne wohi cheezein waisay bhi khareedni thin")]
    public void PromoReturn(string text) => Topic(text, "promoroi");

    [Theory]
    [InlineData("کس ڈسکاؤنٹ پروموشن (مثال کے طور پر: Flat 50 off یا 10% Percent) نے دکان پر سب سے زیادہ نئے گاہک کھینچے؟")]
    [InlineData("kis discount promotion ne sab se zyada naye gahak khenche")]
    public void PromoNewCustomers(string text) => Topic(text, "promonew");

    [Theory]
    [InlineData("کیا ڈسکاؤنٹ والے پروڈکٹس خریدنے والے گاہک دوبارہ پورا ریٹ دے کر خریداری کرتے ہیں یا وہ صرف سیل (Sale) کا انتظار کرتے ہیں؟")]
    [InlineData("kya discount wale gahak dobara pura rate de kar kharidte hain ya sirf sale ka intezar karte hain")]
    public void DiscountBuyersComeBack(string text) => Topic(text, "promoloyalty");

    [Theory]
    [InlineData("اگلے مہینے عید آ رہی ہے۔ پچھلے سال کا ڈیٹا دیکھ کر بتاؤ کہ مجھے گروسری کا اسٹاک کتنا فیصد بڑھانا چاہیے اور کون سے 3 آئٹمز سب سے پہلے شارٹ ہوں گے؟")]
    [InlineData("agle mahine eid hai pichle saal ka data dekh kar batao grocery ka stock kitna fisad barhana chahiye aur kaun se 3 items pehle short honge")]
    public void EidStockPlan(string text) => Topic(text, "eidstock");

    [Theory]
    [InlineData("پچھلے 3 مہینوں کے ٹرینڈ کے مطابق، اگلا مہینہ شروع ہوتے ہی مجھے دکان کا کرایہ اور بجلی کا بل نکالنے کے لیے روزانہ کم از کم کتنے روپے کی سیل کرنی پڑے گی؟")]
    [InlineData("pichle 3 mahine ke trend se kiraya aur bijli ka bill nikalne ke liye roz kam se kam kitni sale karni paregi")]
    public void DailySaleToCoverFixedCosts(string text) => Topic(text, "breakevendaily");

    [Theory]
    [InlineData("کون سا پروڈکٹ اپنے لائف سائیکل کے آخری مرحلے پر ہے (یعنی اس کی سیلز مستقل گر رہی ہیں) اور مجھے اس کا نیا آرڈر ہول سیلر کو دینے سے رک جانا چاہیے؟")]
    [InlineData("kaun sa product apne life cycle ke aakhri marhale par hai aur kya mujhe naya order rok dena chahiye")]
    public void ProductLifecycle(string text) => Topic(text, "declining");

    [Theory]
    [InlineData("کیا کسٹمرز اب مہنگے لان سوٹ کی طرف جا رہے ہیں یا سستی کرتیوں کی سیل بڑھ رہی ہے؟ دکان کا کسٹمر ٹرینڈ کس طرف شفٹ ہو رہا ہے؟")]
    [InlineData("kya customers ab mehnge lawn suit ki taraf ja rahe hain ya sasti kurtion ki sale barh rahi hai trend kis taraf shift ho raha hai")]
    public void PriceMixShift(string text) => Topic(text, "pricemix");

    [Theory]
    [InlineData("business audit")]
    [InlineData("mere business ka audit karo")]
    [InlineData("دکان کا بزنس آڈٹ")]
    public void BusinessAudit(string text) => Topic(text, "audit");

    // Earlier topics keep their meaning.
    [Theory]
    [InlineData("kaun se customers pichle ek mahine se wapas nahi aaye", "inactive")]
    [InlineData("kitne orders cancel ya return hue", "returns")]
    [InlineData("kaun sa stock agle 3 din mein khatam hone wala hai", "stockending")]
    [InlineData("mera kitna paisa bazar mein phansa hua hai", "stuckmoney")]
    [InlineData("kaun se products akele nahi bikte", "nosolo")]
    public void EarlierTopicsAreUnchanged(string text, string topic) => Topic(text, topic);
}
