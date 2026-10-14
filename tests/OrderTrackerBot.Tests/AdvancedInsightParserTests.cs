using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

// The 25 advanced scenarios (Urdu script as a seller would say them) plus Roman Urdu / English versions: each must reach its report.
public class AdvancedInsightParserTests
{
    private static ParsedCommand Parse(string text) => CommandParser.TryParse(text) ?? throw new Xunit.Sdk.XunitException($"not a command: {text}");

    private static void Topic(string text, string topic)
    {
        var parsed = Parse(text);
        Assert.Equal(CommandKind.Insight, parsed.Kind);
        Assert.Equal(topic, parsed.Text);
    }

    [Theory]
    [InlineData("جب کوئی کسٹمر کُرتی خریدتا ہے، تو وہ اس کے ساتھ زیادہ تر کیا لیتا ہے؟ مجھے بیسٹ جوڑے (Combinations) بتاؤ۔")]
    [InlineData("kurti ke saath sab se zyada kya bikta hai")]
    [InlineData("what do customers buy together with kurti")]
    [InlineData("کیا ڈوپٹے کو کُرتی کے ساتھ ملا کر بیچنے سے میری نیٹ سیل بڑھی ہے یا گاہک الگ الگ خریدنا پسند کر رہے ہیں؟")]
    public void BoughtTogether(string text) => Topic(text, "pairs");

    [Theory]
    [InlineData("ان 3 پروڈکٹس کی لسٹ دو جو اکیلے نہیں بکتے، بلکہ ہمیشہ کسی دوسرے مال کے ساتھ ہی بکتے ہیں۔")]
    [InlineData("kaun se products akele nahi bikte")]
    [InlineData("which products never sell alone")]
    public void NeverSoldAlone(string text) => Topic(text, "nosolo");

    [Theory]
    [InlineData("اگلے ہفتے کے لیے مجھے کوئی دو ایسی چیزیں سجیسٹ کرو جن کا میں ایک پیکٹ (Bundle Offer) بنا کر اچھے منافع کے ساتھ بیچ سکوں۔")]
    [InlineData("agle hafte ke liye bundle offer suggest karo")]
    [InlineData("suggest me a bundle offer")]
    public void BundleSuggestion(string text) => Topic(text, "bundle");

    [Theory]
    [InlineData("میرے وہ ٹاپ 5 گاہک کون سے ہیں جنہوں نے پچھلے 60 دن سے کوئی آرڈر نہیں دیا لیکن وہ پہلے ہر ہفتے خریدتے تھے؟", 60)]
    [InlineData("kaun se customers ne 45 din se order nahi diya lekin pehle har hafte kharidte the", 45)]
    [InlineData("customers who used to buy every week but have not ordered", 60)]
    public void ChurnRisk(string text, int days)
    {
        var parsed = Parse(text);
        Assert.Equal("churnrisk", parsed.Text);
        Assert.Equal(days, (int?)parsed.Amount);
    }

    [Theory]
    [InlineData("کون سے گاہک ایسے ہیں جن کا آرڈر سائز (پیسے خرچ کرنے کی اوسط) پچھلے مہینے کے مقابلے میں آدھی رہ گئی ہے؟")]
    [InlineData("kin customers ka average order size aadha reh gaya hai")]
    public void OrderSizeDrop(string text) => Topic(text, "aovdrop");

    [Theory]
    [InlineData("اگر میں اپنے پرانے پچاس کسٹمرز کو 200 روپے کا ڈسکاؤنٹ دوں، تو مجھے کتنی سیل واپس ملنے کی امید ہے؟", 200)]
    [InlineData("agar main 50 purane customers ko 300 rupay ka discount doon to kitni sale wapas aane ki umeed hai", 300)]
    public void WinBackOffer(string text, int rupees)
    {
        var parsed = Parse(text);
        Assert.Equal("winback", parsed.Text);
        Assert.Equal(rupees, (int?)parsed.Amount);
    }

    [Theory]
    [InlineData("سارہ خان کی خریداری کا پیٹرن کیا ہے؟ وہ مہینے کے کس ہفتے میں سب سے زیادہ مال خریدتی ہیں؟")]
    [InlineData("Sara Khan ki kharidari ka pattern kya hai")]
    [InlineData("what is Sara's buying pattern")]
    public void BuyingPattern(string text) => Topic(text, "pattern");

    [Theory]
    [InlineData("شاہ عالم مارکیٹ سے جو نیا لان سوٹ کا بیچ آیا ہے، اس کی لاگت (Cost) پچھلے بیچ سے 300 روپے زیادہ ہے۔ اگر میں منافع سیم رکھوں تو نئی سیل پرائس کیا ہونی چاہیے؟", 300)]
    [InlineData("lawn suit ki cost 300 rupay barh gayi nayi price kya honi chahiye", 300)]
    public void CostRise(string text, int rise)
    {
        var parsed = Parse(text);
        Assert.Equal("costup", parsed.Text);
        Assert.Equal(rise, (int?)parsed.Amount);
    }

    [Theory]
    [InlineData("اگر میں پولو شرٹ کی قیمت 500 سے بڑھا کر 550 کر دوں، تو میرے منافع پر کیا اثر پڑے گا اور کیا گاہک خریدنا کم کر دیں گے؟", 550)]
    [InlineData("polo shirt ki price 500 se 550 kar dun to kya asar parega", 550)]
    public void PriceChange(string text, int price)
    {
        var parsed = Parse(text);
        Assert.Equal("priceeffect", parsed.Text);
        Assert.Equal(price, (int?)parsed.Amount);
    }

    [Theory]
    [InlineData("پچھلے 3 مہینوں میں کونسے سپلائر کا مال سب سے زیادہ بکا اور اس پر کتنا منافع ملا؟")]
    [InlineData("kis supplier ka maal sab se zyada bika")]
    [InlineData("which supplier's stock sold the most")]
    public void Suppliers(string text) => Topic(text, "suppliers");

    [Theory]
    [InlineData("میرا کونسا پروڈکٹ ایسا ہے جس کی لاگت (Cost Price) بہت زیادہ ہے مگر دکان پر وہ مارجن سب سے کم دے رہا ہے؟")]
    [InlineData("kaun sa product hai jis ki cost bahut zyada hai magar margin sab se kam hai")]
    public void CostlyButThinMargin(string text) => Topic(text, "costlylow");

    [Theory]
    [InlineData("ٹی سی ایس (TCS) اور لیپرڈز (Leopards) میں سے کس کمپنی کے آرڈرز سب سے زیادہ ریٹرن (RTO) ہو کر واپس آ رہے ہیں؟")]
    [InlineData("TCS aur Leopards mein se kis ke orders sab se zyada return ho rahe hain")]
    public void CourierReturns(string text) => Topic(text, "courierreturns");

    [Theory]
    [InlineData("جو آرڈرز کینسل یا ریٹرن ہوئے، ان کی وجہ سے دکان کو ڈلیوری چارجز کی مد میں کتنا خالص نقصان (Dead Loss) ہوا؟")]
    [InlineData("cancel ya return orders ki delivery charges ka kitna dead loss hua")]
    public void ReturnLoss(string text) => Topic(text, "returnloss");

    [Theory]
    [InlineData("لاہور کے باہر بھیجے جانے والے آرڈرز پر اوسطاً کتنا ڈلیوری ٹائم لگ رہا ہے اور اس کا گاہک کے پیمنٹ کرنے پر کیا اثر ہے؟")]
    [InlineData("Lahore ke bahar orders ka delivery time kitna lag raha hai")]
    public void CityDelivery(string text) => Topic(text, "citydelivery");

    [Theory]
    [InlineData("کیا کیش آن ڈلیوری (COD) والے آرڈرز زیادہ کینسل ہوتے ہیں یا ایڈوانس ایزی پیسہ والے؟ مجھے فیصد (Percentage) بتاؤ۔")]
    [InlineData("COD orders zyada cancel hote hain ya advance easypaisa wale")]
    public void CancelByPaymentMethod(string text) => Topic(text, "cancelbymethod");

    [Theory]
    [InlineData("پچھلے ایک ہفتے میں دراز بند کرتے وقت کل کتنا نقد کیش شارٹ (Shortage) ہوا اور کس دن سب سے زیادہ فرق تھا؟")]
    [InlineData("pichle hafte galle mein kitna cash short hua aur kis din sab se zyada farq tha")]
    public void DrawerShortage(string text) => Topic(text, "drawershort");

    [Theory]
    [InlineData("کیا کوئی ایسا کسٹمر ہے جس کا آرڈر سسٹم میں 'ڈیلیورڈ' مارک ہے لیکن دراز میں اس کے پیسے جمع نہیں ہوئے؟")]
    [InlineData("kaun se orders delivered hain lekin unke paise jama nahi hue")]
    public void DeliveredButNotPaid(string text) => Topic(text, "uncollected");

    [Theory]
    [InlineData("میرا کتنا پیسہ اس وقت بازار میں پھنسا ہوا ہے (COD Pending + Unpaid Orders) جو ابھی تک میرے ہاتھ میں نہیں آیا؟")]
    [InlineData("mera kitna paisa bazar mein phansa hua hai")]
    [InlineData("how much money is stuck in the market")]
    public void MoneyStuck(string text) => Topic(text, "stuckmoney");

    [Theory]
    [InlineData("اس مہینے دکان کے اپنے خرچے (Expenses) میری نیٹ سیل کا کتنے فیصد ہیں؟ کیا خرچے حد سے بڑھ رہے ہیں؟")]
    [InlineData("is mahine kharche meri sale ka kitne fisad hain")]
    public void ExpenseShare(string text) => Topic(text, "expenseratio");

    [Theory]
    [InlineData("میرا کتنا سرمایہ (Capital/Cash) اس وقت ایسے اسٹاک میں پھنسا ہوا ہے جو پچھلے 90 دن سے ایک بار بھی نہیں بکا؟", 90)]
    [InlineData("mera kitna sarmaya aise stock mein phansa hai jo 120 din se nahi bika", 120)]
    public void LockedCapital(string text, int days)
    {
        var parsed = Parse(text);
        Assert.Equal("lockedcapital", parsed.Text);
        Assert.Equal(days, (int?)parsed.Amount);
    }

    [Fact]
    public void StockRunningOutByTheNextEid()
    {
        var parsed = Parse("اگر موجودہ رفتار برقرار رہی، تو کونسے 3 پروڈکٹس ایسے ہیں جو اگلی عید یا سیزن تک بالکل ختم ہو جائیں گے؟");
        Assert.Equal("stockending", parsed.Text);
        Assert.True(parsed.Amount > 0);
    }

    [Theory]
    [InlineData("ان پروڈکٹس کی لسٹ دو جن کا اسٹاک بہت زیادہ (Overstocked) پڑا ہوا ہے اور انہیں نکالنے کے لیے کتنا ڈسکاؤنٹ دینا پڑے گا؟")]
    [InlineData("overstocked products kaun se hain")]
    public void Overstock(string text) => Topic(text, "overstock");

    [Theory]
    [InlineData("پچھلے سال رمضان اور عید کے دنوں میں میری روزانہ کی اوسط سیل کیا تھی؟ اس بار مجھے کتنا اسٹاک پہلے سے رکھنا چاہیے؟")]
    [InlineData("pichle saal ramazan mein meri roz ki average sale kya thi")]
    public void Seasons(string text) => Topic(text, "seasonal");

    [Theory]
    [InlineData("جب موسم بدلتا ہے (مثلاً ستمبر سے اکتوبر)، تو گروسری اور کپڑوں کی سیلز کے تناسب (Ratio) میں کیا تبدیلی آتی ہے؟")]
    [InlineData("september se october mein grocery aur kapron ki sales ka ratio kya badla")]
    public void CategoryMix(string text) => Topic(text, "categoryshift");

    // The simple questions from before must keep their meaning.
    [Theory]
    [InlineData("kaun se customers pichle ek mahine se wapas nahi aaye", "inactive")]
    [InlineData("sab se mehnga product kaun sa hai", "pricey")]
    [InlineData("polo shirt kitne din chalega", "stockdays")]
    [InlineData("kya mujhe lawn suit ka naya batch mangwana chahiye", "reorder")]
    [InlineData("kitne orders cancel ya return hue", "returns")]
    public void EarlierTopicsAreUnchanged(string text, string topic) => Topic(text, topic);

    [Fact]
    public void NextEid_IsInTheFuture() => Assert.InRange(InsightParserEid(), 1, 360);

    private static int InsightParserEid() => (int)Parse("agli eid tak kaun sa stock khatam ho jayega").Amount!;
}
