using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

// The 28 strategic scenarios (Urdu script as a seller would say them) plus Roman Urdu versions; the ones that repeat an earlier report keep that report's topic.
public class StrategicInsightParserTests
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
    [InlineData("مہینے کے آخری 5 دنوں (25 سے 30 تاریخ) میں واک-اِن اور آن لائن گاہکوں کے آرڈر سائز اور نقد ادائیگی کے رویے میں کیا فرق آتا ہے؟")]
    [InlineData("mahine ke aakhri 5 din walk-in aur online gahakon ke order size aur cash payment mein kya farq aata hai")]
    public void SalaryCycle(string text) => Topic(text, "salarycycle");

    [Theory]
    [InlineData("پچھلے سال جن گاہکوں نے ڈسکاؤنٹ پروموشن پر پہلی بار خریداری کی تھی، پچھلے 12 مہینوں میں ان کی اوسط خریداری (Customer Lifetime Contribution) عام ریٹ پر خریدنے والوں سے کتنی کم یا زیادہ رہی؟")]
    [InlineData("jin customers ne discount promotion par pehli baar kharidari ki unki lifetime contribution aam rate walon se kitni kam ya zyada hai")]
    public void PromoCohort(string text) => Topic(text, "promocohort");

    [Theory]
    [InlineData("ہفتے کے کس مخصوص دن اور کس وقت (مثلاً اتوار کی شام یا جمعے کی دوپہر) دکان پر سب سے زیادہ آرڈرز بک ہوتے ہیں، تاکہ میں اس وقت واٹس ایپ پر فوری جواب دینے کے لیے الرٹ رہوں؟")]
    [InlineData("hafte ke kis din aur kis waqt sab se zyada orders book hote hain")]
    public void PeakTime(string text) => Topic(text, "peaktime");

    [Theory]
    [InlineData("کیا کوئی ایسے گاہک ہیں جو مہنگا مال (Premium Items) خریدتے وقت ہمیشہ ایڈوانس ایزی پیسہ کرتے ہیں، لیکن سستا مال خریدتے وقت کیش آن ڈلیوری (COD) مانگتے ہیں؟")]
    [InlineData("kya koi customers hain jo mehnga maal khareedte waqt advance easypaisa karte hain lekin sasta maal COD par mangte hain")]
    public void PaymentHabit(string text) => Topic(text, "paypattern");

    [Theory]
    [InlineData("اگر کوئی لان سوٹ دکان کے اسٹاک میں 60 دن سے زیادہ پڑا رہے، تو دکان کے کرائے اور بجلی کے تناسب سے اس کی ہولڈنگ کاسٹ (Holding Cost) میرے پرافٹ مارجن کو کتنے فیصد کم کر دیتی ہے؟", 60)]
    [InlineData("agar lawn suit 60 din se zyada stock mein pada rahe to uski holding cost mera margin kitna kam karti hai", 60)]
    public void HoldingCost(string text, int days)
    {
        var parsed = Topic(text, "holdingcost");
        Assert.Equal(days, (int?)parsed.Amount);
    }

    [Theory]
    [InlineData("میرا کون سا پروڈکٹ ایسا ہے جس کا منافع کا مارجن تو کم ہے، لیکن اس کی گردش کی رفتار (Capital Turnover Velocity) اتنی تیز ہے کہ وہ مہینے میں سب سے زیادہ مجموعی منافع کما کر دیتا ہے؟")]
    [InlineData("kaun sa product jis ka margin kam hai lekin turnover velocity itni tez hai ke mahine mein sab se zyada munafa deta hai")]
    public void LowMarginHighTurnover(string text) => Topic(text, "turnoverstar");

    [Theory]
    [InlineData("اگر میں اپنے ڈیڈ اسٹاک (جو 90 دن سے نہیں بکا) کو ہول سیل لاگت سے بھی 10% سستا کر کے بیچ دوں، تو دکان میں کتنا کیش فوری آزاد (Liquidity Release) ہوگا جسے میں چلتے ہوئے مال میں لگا سکوں؟", 10)]
    [InlineData("agar main dead stock jo 90 din se nahi bika cost se 10% sasta bech dun to kitna cash liquidity release hoga", 10)]
    public void Liquidate(string text, int percent)
    {
        var parsed = Topic(text, "liquidate");
        Assert.Equal(percent, (int?)parsed.Amount);
    }

    [Theory]
    [InlineData("اعظم کلوتھ مارکیٹ کے سپلائر خان صاحب سے مال منگوانے پر کتنا اوسط وقت (Lead Time) لگتا ہے، اور ان کے مال پر مجھے شاہ عالم مارکیٹ کے سپلائر کے مقابلے میں کتنا خالص مارجن ملتا ہے؟")]
    [InlineData("supplier Khan sahab se maal manga'ne mein lead time kitna lagta hai aur unke maal par margin Shah Alam supplier se kitna zyada hai")]
    public void SupplierCompare(string text) => Topic(text, "suppliercompare");

    [Theory]
    [InlineData("پچھلے 6 مہینوں میں کس سپلائر کے مال میں سب سے زیادہ نقص یا خرابی (Defective/Damaged Rate) نکلی، جس کی وجہ سے گاہکوں نے مال ریٹرن کیا؟")]
    [InlineData("pichle 6 mahine mein kis supplier ke maal mein sab se zyada kharabi nikli jis ki wajah se customers ne return kiya")]
    public void SupplierDefects(string text) => Topic(text, "supplierdefects");

    [Theory]
    [InlineData("اگر سپلائر اگلی کھیپ پر ہول سیل ریٹ 20% بڑھا دیتا ہے، تو مجھے اپنا پرافٹ مارجن برقرار رکھنے کے لیے گاہک کے لیے کم از کم کتنا ریٹ بڑھانا پڑے گا؟", 20)]
    [InlineData("agar supplier agli khep par wholesale rate 20% barha de to margin barqarar rakhne ke liye mujhe kam se kam kitna rate barhana paray ga", 20)]
    public void SupplierRateRise(string text, int percent)
    {
        var parsed = Topic(text, "suppliershock");
        Assert.Equal(percent, (int?)parsed.Amount);
    }

    [Theory]
    [InlineData("لیپرڈز اور ٹی سی ایس میں سے کس کورئیر کمپنی کے پاس میرے ریٹرن پارسل (RTO) سب سے زیادہ دنوں تک راستے میں پھنسے رہتے ہیں، جس کی وجہ سے میرا اسٹاک لاک ہو جاتا ہے؟")]
    [InlineData("Leopards aur TCS mein se kis courier ke paas mere return parcel RTO sab se zyada din raaste mein phanse rehte hain")]
    public void ReturnTransitDays(string text) => Topic(text, "rtotransit");

    [Theory]
    [InlineData("کراچی اور اسلام آباد کے آرڈرز پر اوسطاً کتنا کورئیر ڈلیوری ٹائم لگ رہا ہے، اور کیا زیادہ تاخیر کی وجہ سے وہاں کے گاہک پارسل لینے سے انکار (Refusal Rate) کر رہے ہیں؟")]
    [InlineData("Karachi aur Islamabad ke orders par delivery time kitna lag raha hai aur kya customers parcel lene se inkar refusal kar rahe hain")]
    public void CityDeliveryAndRefusal(string text) => Topic(text, "citycompare");

    [Theory]
    [InlineData("پچھلے 90 دن میں جتنے بھی پارسل ریٹرن ہوئے، ان کا ڈلیوری اور واپسی کا کرایہ ملا کر دکان کے کل منافع میں سے کتنے فیصد رقم کورئیر کمپنیوں کے کھاتے میں چلی گئی؟", 90)]
    [InlineData("pichle 90 din mein jitne parcel return hue unka delivery aur wapsi ka kiraya mila kar courier ko kul munafa ka kitna fisad chala gaya", 90)]
    public void ReturnShareOfProfit(string text, int days)
    {
        var parsed = Topic(text, "rtoprofitshare");
        Assert.Equal(days, (int?)parsed.Amount);
    }

    [Theory]
    [InlineData("کیا کوئی ایسا آرڈر ہے جو سسٹم میں پہلے 'Paid' مارک کیا گیا تھا، لیکن بعد میں اسے 'Cancelled' یا 'Returned' کر دیا گیا، مگر دراز سے نقد رقم کم نہیں ہوئی؟ (Audit Trail Verification)")]
    [InlineData("kya koi order pehle Paid mark hua phir Cancelled ya Returned ho gaya magar drawer se naqd raqam kam nahi hui audit")]
    public void PaidThenCancelled(string text) => Topic(text, "paidcancelled");

    [Theory]
    [InlineData("پچھلے 30 دن کا ڈیٹا دیکھ کر بتاؤ کہ دراز (Drawer) بند کرتے وقت کیش کا فرق (Cash Shortage) کس مخصوص دن یا شفٹ میں سب سے زیادہ پایا گیا؟")]
    public void DrawerShortageAgain(string text) => Topic(text, "drawershort");

    [Theory]
    [InlineData("دکان کے روزمرہ کے چھوٹے خرچے (Petty Cash Expenses) میری کل نیٹ سیلز کا کتنے فیصد تک پہنچ چکے ہیں؟ کیا یہ گزشتہ مہینے کے تناسب سے بڑھ رہے ہیں؟")]
    public void PettyCashShare(string text) => Topic(text, "expenseratio");

    [Theory]
    [InlineData("کیا 'EID50' ڈسکاؤنٹ کوڈ چلانے سے وہ مستقل گاہک بھی سستے ریٹ پر مال لے گئے جو ویسے بھی پورا ریٹ دینے والے تھے؟ (Margin Cannibalization Analysis)")]
    [InlineData("kya EID50 discount code se woh permanent gahak bhi sasta maal le gaye jo waisay bhi pura rate dene wale thay")]
    public void Cannibalisation(string text) => Topic(text, "promoroi");

    [Theory]
    [InlineData("کس مخصوص پروڈکٹ کیٹیگری (مثلاً کُرتی یا دوپٹہ) پر ڈسکاؤنٹ دینے سے دکان کی مجموعی سیلز (Basket Size) میں سب سے زیادہ اضافہ ریکارڈ کیا گیا؟")]
    [InlineData("kis category kurti ya dupatta par discount dene se basket size mein sab se zyada izafa hua")]
    public void DiscountBasketLift(string text) => Topic(text, "discountbasket");

    [Theory]
    [InlineData("کیا ڈسکاؤنٹ پر مال خریدنے والے گاہکوں کا ریٹرن ریٹ (RTO Rate) پورا ریٹ دے کر خریدنے والے عام گاہکوں سے زیادہ ہے؟")]
    [InlineData("kya discount par khareedne wale customers ka return rate pura rate dene walon se zyada hai")]
    public void DiscountReturnRate(string text) => Topic(text, "discountreturn");

    [Theory]
    [InlineData("پچھلے سال رمضان اور عید کے پہلے 15 دنوں میں دکان کا اوسط ڈیلی ریونیو کیا تھا؟ اس بار عید کی تیاری کے لیے مجھے کتنا 'سیفٹی اسٹاک' (Safety Stock) پہلے سے خرید کر رکھنا چاہیے؟")]
    public void SeasonRevenueAndSafetyStock(string text) => Topic(text, "seasonal");

    [Theory]
    [InlineData("جب موسم بدلتا ہے، تو گاہک گروسری کے آئٹمز سے ہٹ کر کپڑوں کی خریداری پر کتنے فیصد زیادہ بجٹ شفٹ کرتے ہیں؟ سیلز کا تناسب بتاؤ")]
    public void WeatherCategoryShift(string text) => Topic(text, "categoryshift");

    [Theory]
    [InlineData("موجودہ سیلز کی رفتار کے مطابق، اگلے مہینے کی 5 تاریخ تک دکان کا کرایہ، بجلی کا بل اور سپلائرز کی پیمنٹ نکالنے کے لیے مجھے روزانہ کم از کم کتنے روپے کا مال بیچنا لازمی ہے؟")]
    [InlineData("agle mahine ki 5 tarikh tak kiraya bijli ka bill aur suppliers ki payment nikalne ke liye roz kam se kam kitne rupay ka maal bechna lazmi hai")]
    public void CashNeededByTheFifth(string text) => Topic(text, "cashneed");

    [Theory]
    [InlineData("میرا کون سا پروڈکٹ ایسا ہے جو اپنے لائف سائیکل کے آخری مرحلے پر پہنچ چکا ہے (یعنی اس کی ڈیمانڈ مستقل گر رہی ہے) اور مجھے اس کا نیا آرڈر دینے سے رک جانا چاہیے؟")]
    public void LifeCycleAgain(string text) => Topic(text, "declining");

    [Theory]
    [InlineData("کیا گاہک اب سستی کرتیوں کے بجائے مہنگے لان سوٹ کی طرف جا رہے ہیں؟ دکان کا کسٹمر بائینگ ٹرینڈ (Customer Buying Trend) کس طرف شفٹ ہو رہا ہے؟")]
    public void BuyingTrendAgain(string text) => Topic(text, "pricemix");

    [Theory]
    [InlineData("ایزی پیسہ اور جیز کیش کے ذریعے آنے والی ایڈوانس پیمنٹس اور کیش آن ڈلیوری (COD) کے پیسوں کے بینک میں آنے کے وقت (Cash Settlement Cycle) میں کتنا فرق ہے؟")]
    [InlineData("easypaisa jazzcash ki advance payments aur COD ke paison ke bank mein aane ke waqt cash settlement cycle mein kitna farq hai")]
    public void SettlementCycle(string text) => Topic(text, "settlement");

    [Theory]
    [InlineData("کیا ایڈوانس پیمنٹ کرنے والے کسٹمرز کا آرڈر سائز عام کسٹمرز سے بڑا ہوتا ہے؟")]
    [InlineData("kya advance payment karne wale customers ka order size aam customers se bara hota hai")]
    public void AdvanceOrderSize(string text) => Topic(text, "advancesize");

    [Theory]
    [InlineData("پچھلے 30 دن میں کینسلیشن، ریٹرن، اور خراب مال (Wastage) کی وجہ سے دکان کو مجموعی طور پر کتنا خالص مالی نقصان ہوا؟", 30)]
    [InlineData("pichle 30 din mein cancellation return aur wastage ki wajah se dukaan ko kul kitna nuqsan hua", 30)]
    public void OperationalLeakage(string text, int days)
    {
        var parsed = Topic(text, "leakage");
        Assert.Equal(days, (int?)parsed.Amount);
    }

    [Theory]
    [InlineData("میرا کتنا سرمایہ اس وقت مارکیٹ میں پینڈنگ پڑا ہے جو گاہکوں یا کورئیر کمپنیوں سے ابھی وصول ہونا باقی ہے؟")]
    public void PendingCapital(string text) => Topic(text, "stuckmoney");

    // Earlier topics keep their meaning.
    [Theory]
    [InlineData("mera kitna paisa bazar mein phansa hua hai", "stuckmoney")]
    [InlineData("mera kitna sarmaya aise stock mein phansa hai jo 90 din se nahi bika", "lockedcapital")]
    [InlineData("kitne orders cancel ya return hue", "returns")]
    [InlineData("kis supplier ka maal sab se zyada bika", "suppliers")]
    [InlineData("pichle 7 din drawer mein kitna cash short hua", "drawershort")]
    [InlineData("kiraya aur bijli ka bill nikalne ke liye roz kam se kam kitni sale karni paregi", "breakevendaily")]
    [InlineData("TCS aur Leopards mein se kis ke orders sab se zyada return ho rahe hain", "courierreturns")]
    public void EarlierTopicsAreUnchanged(string text, string topic) => Topic(text, topic);
}
