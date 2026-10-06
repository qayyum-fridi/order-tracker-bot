using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

public class CommandParserTests
{
    [Theory]
    [InlineData("orders today", CommandKind.OrdersToday)]
    [InlineData("Orders Today", CommandKind.OrdersToday)]
    [InlineData("pending orders", CommandKind.PendingOrders)]
    [InlineData("catalog", CommandKind.Catalog)]
    [InlineData("undo", CommandKind.Undo)]
    [InlineData("help", CommandKind.Help)]
    [InlineData("menu", CommandKind.Menu)]
    [InlineData("discount list", CommandKind.DiscountList)]
    [InlineData("loyal customers", CommandKind.LoyalCustomers)]
    [InlineData("unpaid orders", CommandKind.UnpaidOrders)]
    [InlineData("cod pending", CommandKind.CodPending)]
    [InlineData("trending products", CommandKind.TrendingProducts)]
    [InlineData("slow movers", CommandKind.SlowMovers)]
    public void ParsesSimpleCommands(string message, CommandKind expected)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed!.Kind);
    }

    [Fact]
    public void ParsesMarkStatus_WithOrderNumberAndKeyword()
    {
        var parsed = CommandParser.TryParse("mark 3 shipped");
        Assert.NotNull(parsed);
        Assert.Equal(CommandKind.MarkStatus, parsed!.Kind);
        Assert.Equal(3, parsed.Number);
        Assert.Equal("shipped", parsed.Text);
    }

    [Fact]
    public void ParsesAddProduct_WithNameAndPrice()
    {
        var parsed = CommandParser.TryParse("add product: Lawn Suit - 3500");
        Assert.NotNull(parsed);
        Assert.Equal(CommandKind.AddProduct, parsed!.Kind);
        Assert.Equal("Lawn Suit", parsed.Text);
        Assert.Equal(3500m, parsed.Amount);
    }

    [Fact]
    public void ParsesCancelOrder_WithNumber()
    {
        var parsed = CommandParser.TryParse("cancel order 5");
        Assert.NotNull(parsed);
        Assert.Equal(CommandKind.CancelOrder, parsed!.Kind);
        Assert.Equal(5, parsed.Number);
    }

    [Fact]
    public void ParsesCustomerOrderLookup_RomanUrduPattern()
    {
        var parsed = CommandParser.TryParse("ayesha ka order");
        Assert.NotNull(parsed);
        Assert.Equal(CommandKind.CustomerOrderLookup, parsed!.Kind);
        Assert.Equal("ayesha", parsed.Text);
    }

    [Fact]
    public void ParsesFuzzyStatusUpdate_BeforeGenericLookup()
    {
        var parsed = CommandParser.TryParse("ayesha ka order deliver ho gaya");
        Assert.NotNull(parsed);
        Assert.Equal(CommandKind.FuzzyStatusUpdate, parsed!.Kind);
        Assert.Equal("ayesha", parsed.Text);
        Assert.Equal("deliver", parsed.Text2);
    }

    [Fact]
    public void ParsesCreateDiscount_RawTextForFurtherParsing()
    {
        var parsed = CommandParser.TryParse("create discount: EID10, 10 percent, expires 15 days");
        Assert.NotNull(parsed);
        Assert.Equal(CommandKind.CreateDiscount, parsed!.Kind);
        Assert.Equal("EID10, 10 percent, expires 15 days", parsed.Text);
    }

    [Fact]
    public void FreeformOrderText_DoesNotMatchAnyDeterministicCommand()
    {
        var parsed = CommandParser.TryParse("Sara, 1 kurti, 03009876543, Gulberg Lahore");
        Assert.Null(parsed);
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("YES", true)]
    [InlineData("haan", true)]
    [InlineData("no", false)]
    public void IsAffirmative_RecognisesCommonConfirmations(string message, bool expected)
    {
        Assert.Equal(expected, CommandParser.IsAffirmative(message));
    }

    [Theory]
    [InlineData("aaj ke orders", CommandKind.OrdersToday, null)]
    [InlineData("آج کے آرڈرز", CommandKind.OrdersToday, null)]
    [InlineData("kal ke orders", CommandKind.OrdersToday, "yesterday")]
    [InlineData("orders yesterday", CommandKind.OrdersToday, "yesterday")]
    [InlineData("کل کے آرڈرز", CommandKind.OrdersToday, "yesterday")]
    [InlineData("pichle mahine ke orders", CommandKind.OrdersToday, "lastmonth")]
    [InlineData("orders last month", CommandKind.OrdersToday, "lastmonth")]
    [InlineData("kal ka summary", CommandKind.TodaysSummary, "yesterday")]
    [InlineData("آج کا خلاصہ", CommandKind.TodaysSummary, null)]
    [InlineData("کل کا خلاصہ", CommandKind.TodaysSummary, "yesterday")]
    [InlineData("last month summary", CommandKind.TodaysSummary, "lastmonth")]
    [InlineData("پچھلے مہینے کا خلاصہ", CommandKind.TodaysSummary, "lastmonth")]
    [InlineData("پینڈنگ آرڈر", CommandKind.PendingOrders, null)]
    [InlineData("ہفتہ وار خلاصہ", CommandKind.WeeklySummary, null)]
    public void ParsesReportPeriods_RomanUrduAndUrduScript(string message, CommandKind kind, string? period)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.NotNull(parsed);
        Assert.Equal(kind, parsed!.Kind);
        Assert.Equal(period, parsed.Text);
    }

    [Theory]
    [InlineData("mark 3 bhej diya", 3, "shipped")]
    [InlineData("mark 3 deliver ho gaya", 3, "delivered")]
    [InlineData("آرڈر 3 شپ ہو گیا", 3, "shipped")]
    [InlineData("آرڈر ۳ ڈیلیور ہو گیا", 3, "delivered")]
    [InlineData("آرڈر 12 پیڈ", 12, "paid")]
    [InlineData("order 4 shipped", 4, "shipped")]
    [InlineData("mark 3 shipped", 3, "shipped")]
    public void ParsesMarkStatus_Synonyms(string message, int number, string status)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.NotNull(parsed);
        Assert.Equal(CommandKind.MarkStatus, parsed!.Kind);
        Assert.Equal(number, parsed.Number);
        Assert.Equal(status, parsed.Text);
    }

    [Fact]
    public void MarkStatus_WithUnknownVerb_FallsThrough()
    {
        Assert.NotEqual(CommandKind.MarkStatus, CommandParser.TryParse("order 3 kurti 2500")?.Kind);
    }

    [Theory]
    [InlineData("Ayesha کا آرڈر", CommandKind.CustomerOrderLookup, "Ayesha")]
    [InlineData("عائشہ کی آرڈر", CommandKind.CustomerOrderLookup, "عائشہ")]
    [InlineData("Ayesha کا ٹریکنگ", CommandKind.TrackingLookup, "Ayesha")]
    public void ParsesUrduScriptCustomerLookups(string message, CommandKind kind, string name)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.NotNull(parsed);
        Assert.Equal(kind, parsed!.Kind);
        Assert.Equal(name, parsed.Text);
    }

    [Theory]
    [InlineData("Ayesha کا آرڈر ڈیلیور ہو گیا", "deliver")]
    [InlineData("Ayesha کا آرڈر شپ ہو گیا", "ship")]
    [InlineData("ayesha ka order bhej diya", "ship")]
    public void ParsesFuzzyStatusUpdate_UrduWords(string message, string keyword)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.NotNull(parsed);
        Assert.Equal(CommandKind.FuzzyStatusUpdate, parsed!.Kind);
        Assert.Equal("Ayesha", parsed.Text, ignoreCase: true);
        Assert.Equal(keyword, parsed.Text2);
    }

    [Theory]
    [InlineData("receipt", null, null)]
    [InlineData("Receipt 12", 12, null)]
    [InlineData("receipt #12", 12, null)]
    [InlineData("rasid 3", 3, null)]
    [InlineData("invoice 7", 7, null)]
    [InlineData("رسید 5", 5, null)]
    [InlineData("receipt Ayesha", null, "Ayesha")]
    [InlineData("Ayesha ki receipt", null, "Ayesha")]
    [InlineData("📄 order receipt 4", 4, null)]
    public void ParsesReceiptCommand(string message, int? number, string? name)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.NotNull(parsed);
        Assert.Equal(CommandKind.Receipt, parsed!.Kind);
        Assert.Equal(number, parsed.Number);
        Assert.Equal(name, parsed.Text);
    }

    [Theory]
    [InlineData("logo", CommandKind.BrandingHelp, "logo")]
    [InlineData("Banner", CommandKind.BrandingHelp, "banner")]
    [InlineData("receipt logo", CommandKind.BrandingHelp, "logo")]
    [InlineData("لوگو", CommandKind.BrandingHelp, "logo")]
    [InlineData("remove logo", CommandKind.RemoveBranding, "logo")]
    [InlineData("banner hatao", CommandKind.RemoveBranding, "banner")]
    [InlineData("delete receipt banner", CommandKind.RemoveBranding, "banner")]
    public void ParsesBrandingCommands(string message, CommandKind kind, string text)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.NotNull(parsed);
        Assert.Equal(kind, parsed!.Kind);
        Assert.Equal(text, parsed.Text);
    }

    [Theory]
    [InlineData("logo", true, "logo")]
    [InlineData("Receipt Banner", true, "banner")]
    [InlineData("بینر", true, "banner")]
    [InlineData("Ayesha ka order", false, "")]
    [InlineData(null, false, "")]
    public void ParsesBrandingCaption(string? caption, bool expected, string kind)
    {
        Assert.Equal(expected, CommandParser.TryParseBrandingCaption(caption, out var parsedKind));
        Assert.Equal(kind, parsedKind);
    }

    [Fact]
    public void ReceiptWithCustomerNamedLogo_StillBrandingHelp()
    {
        // "receipt logo" is the branding help, not a receipt for a customer called "logo".
        Assert.Equal(CommandKind.BrandingHelp, CommandParser.TryParse("receipt logo")!.Kind);
    }

    [Theory]
    [InlineData("receipt par apna logo lagana hai", "logo")]
    [InlineData("bill mein banner kaise lagaon", "banner")]
    [InlineData("receipt par image lagani hai", null)]
    [InlineData("invoice mein apni tasveer chahiye", null)]
    [InlineData("mujhe logo add karna hai", "logo")]
    [InlineData("رسید پر لوگو لگانا ہے", "logo")]
    public void ParsesBrandingWishInFreeText(string message, string? kind)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.NotNull(parsed);
        Assert.Equal(CommandKind.BrandingHelp, parsed!.Kind);
        Assert.Equal(kind, parsed.Text);
    }

    [Theory]
    [InlineData("Logo T-shirt - 1500")]
    [InlineData("Ayesha ka order logo wala 2 suit")]
    [InlineData("Banner Stand - 2500")]
    public void BrandingWish_DoesNotHijackProductsOrOrders(string message)
    {
        Assert.NotEqual(CommandKind.BrandingHelp, CommandParser.TryParse(message)?.Kind);
    }

    [Theory]
    [InlineData("ye mera logo hai", true, "logo")]
    [InlineData("receipt ke liye banner", true, "banner")]
    [InlineData("order screenshot Ayesha 2 suit 03001234567 aaj ke liye", false, "")]
    public void BrandingCaption_AcceptsShortSentences(string caption, bool expected, string kind)
    {
        Assert.Equal(expected, CommandParser.TryParseBrandingCaption(caption, out var parsed));
        Assert.Equal(kind, parsed);
    }

    [Theory]
    [InlineData("export", "", null)]
    [InlineData("Export", "", null)]
    [InlineData("excel", "", null)]
    [InlineData("export orders", "orders", null)]
    [InlineData("export orders customers", "orders,customers", null)]
    [InlineData("export orders, catalog and discounts", "orders,catalog,discounts", null)]
    [InlineData("export all", "orders,customers,catalog,discounts", null)]
    [InlineData("export sab kuch", "orders,customers,catalog,discounts", null)]
    [InlineData("export orders 30 days", "orders", "30d")]
    [InlineData("export orders last month", "orders", "lastmonth")]
    [InlineData("export orders pichle mahine", "orders", "lastmonth")]
    [InlineData("export orders today", "orders", "today")]
    [InlineData("export orders kal", "orders", "yesterday")]
    [InlineData("download customers", "customers", null)]
    [InlineData("customers export", "customers", null)]
    [InlineData("catalog excel", "catalog", null)]
    [InlineData("ایکسپورٹ کیٹلاگ", "catalog", null)]
    [InlineData("📊 export products", "catalog", null)]
    public void ParsesExport(string message, string datasets, string? period)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.NotNull(parsed);
        Assert.Equal(CommandKind.Export, parsed!.Kind);
        Assert.Equal(datasets, string.Join(",", parsed.Export!.Datasets));
        Assert.Equal(period, parsed.Export.Period);
    }

    [Theory]
    [InlineData("Ayesha excel")]
    [InlineData("customer export now please")]
    [InlineData("Excel Sheet Set - 1500")]
    public void Export_DoesNotHijackOtherMessages(string message)
    {
        Assert.NotEqual(CommandKind.Export, CommandParser.TryParse(message)?.Kind);
    }

    [Theory]
    [InlineData("📋 Menu", CommandKind.Menu)]
    [InlineData("➕ Naya order", CommandKind.NewOrderHelp)]
    [InlineData("➕ New order", CommandKind.NewOrderHelp)]
    [InlineData("📦 Orders today", CommandKind.OrdersToday)]
    [InlineData("📋 مینو", CommandKind.Menu)]
    [InlineData("➕ نیا آرڈر", CommandKind.NewOrderHelp)]
    [InlineData("📦 آج کے آرڈرز", CommandKind.OrdersToday)]
    public void ShortcutButtonLabels_AreRealCommands(string label, CommandKind kind)
    {
        Assert.Equal(kind, CommandParser.TryParse(label)?.Kind);
    }

    [Fact]
    public void SlashCommands_AllResolveToRealCommands_WithWhatsAppSafeNames()
    {
        Assert.InRange(CommandParser.SlashCommands.Count, 1, 30);
        foreach (var (name, description, text) in CommandParser.SlashCommands)
        {
            Assert.Matches("^[a-z0-9]{1,20}$", name);
            Assert.InRange(description.Length, 1, 100);
            Assert.NotNull(CommandParser.TryParse("/" + name));
            Assert.Equal(CommandParser.TryParse(text)!.Kind, CommandParser.TryParse("/" + name)!.Kind);
        }
        Assert.Equal(CommandKind.OrdersToday, CommandParser.TryParse("/orders")!.Kind);
        Assert.Equal(CommandKind.NewOrderHelp, CommandParser.TryParse("/NewOrder")!.Kind);
        Assert.Null(CommandParser.TryParse("/doesnotexist"));
    }

    [Theory]
    [InlineData("shortcut off", "off")]
    [InlineData("shortcuts on", "on")]
    [InlineData("shortcut band", "off")]
    [InlineData("quick actions chalu", "on")]
    public void ParsesShortcutToggle(string message, string value)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.Equal(CommandKind.Shortcuts, parsed!.Kind);
        Assert.Equal(value, parsed.Text);
    }

    [Theory]
    [InlineData("mark 3 returned")]
    [InlineData("mark 3 return ho gaya")]
    [InlineData("mark 3 wapas aa gaya")]
    [InlineData("آرڈر 3 واپس آ گیا")]
    [InlineData("order 3 wapas")]
    public void ParsesMarkReturned(string message)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.Equal(CommandKind.MarkStatus, parsed!.Kind);
        Assert.Equal(3, parsed.Number);
        Assert.Equal("returned", parsed.Text);
    }

    [Theory]
    [InlineData("Ayesha ka order wapas aa gaya", "return")]
    [InlineData("Ayesha کا آرڈر واپس آ گیا", "return")]
    [InlineData("Ayesha ka order deliver ho gaya", "deliver")]
    public void FuzzyStatusUpdate_KnowsReturns(string message, string keyword)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.Equal(CommandKind.FuzzyStatusUpdate, parsed!.Kind);
        Assert.Equal(keyword, parsed.Text2);
    }

    [Theory]
    [InlineData("delivery 200", 200)]
    [InlineData("Delivery charges: Rs 250", 250)]
    [InlineData("delivery fee 150", 150)]
    [InlineData("free delivery", 0)]
    [InlineData("delivery free", 0)]
    [InlineData("ڈیلیوری 180", 180)]
    public void ParsesDeliveryChargeDefault(string message, int amount)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.Equal(CommandKind.DeliveryCharge, parsed!.Kind);
        Assert.Equal(amount, parsed.Amount);
    }

    [Fact]
    public void DeliveryChargeAlone_ShowsCurrentSetting()
    {
        var parsed = CommandParser.TryParse("delivery charge");
        Assert.Equal(CommandKind.DeliveryCharge, parsed!.Kind);
        Assert.Null(parsed.Amount);
    }

    [Theory]
    [InlineData("order 12 delivery 300", 12, 300)]
    [InlineData("12 delivery 0", 12, 0)]
    [InlineData("order 12 delivery free", 12, 0)]
    [InlineData("order #12 free delivery", 12, 0)]
    public void ParsesSavedOrderDeliveryCharge(string message, int id, int amount)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.Equal(CommandKind.OrderDeliveryCharge, parsed!.Kind);
        Assert.Equal(id, parsed.Number);
        Assert.Equal(amount, parsed.Amount);
    }

    [Theory]
    [InlineData("mark 3 delivered", "delivered")]
    [InlineData("mark 3 deliver ho gaya", "delivered")]
    public void Delivery_DoesNotBreakDeliveredStatus(string message, string status)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.Equal(CommandKind.MarkStatus, parsed!.Kind);
        Assert.Equal(status, parsed.Text);
    }

    [Theory]
    [InlineData("Sara, 1 kurti, 03001234567, delivery 300", 300)]
    [InlineData("Sara 2 suit 0300-1234567 Gulberg, delivery charges 250", 250)]
    [InlineData("Ayesha 1 lawn suit +200 delivery", 200)]
    [InlineData("Bilal, 1 kurti, 03001234567, free delivery", 0)]
    [InlineData("Bilal 1 kurti delivery Rs 40", 40)]
    [InlineData("Sana, 1 suit, 03001234567, ڈیلیوری 180", 180)]
    [InlineData("Sana 1 suit delivery: ۲۵۰", 250)]
    public void FindsDeliveryChargeInsideOrderText(string message, int expected)
    {
        Assert.True(CommandParser.TryFindDeliveryInOrderText(message, out var amount));
        Assert.Equal(expected, amount);
    }

    [Theory]
    [InlineData("Sara, 1 kurti, 03001234567")]
    [InlineData("Sara 1 kurti, delivery 15 tareekh ko, 03001234567")]
    [InlineData("Sara 1 kurti, delivery date 20 oct")]
    [InlineData("Sara 1 kurti 03001234567 delivery jaldi chahiye")]
    [InlineData("Sara, 2 suit deliver kar dena, 03001234567")]
    public void IgnoresDatesPhonesAndPlainMentionsOfDelivery(string message)
    {
        Assert.False(CommandParser.TryFindDeliveryInOrderText(message, out _));
    }

    [Theory]
    [InlineData("edit order 12", 12)]
    [InlineData("Edit Order #12", 12)]
    [InlineData("order 12 edit", 12)]
    [InlineData("order 12 badlo", 12)]
    [InlineData("change order 7", 7)]
    [InlineData("edit order", null)]
    [InlineData("آرڈر 12 تبدیل", 12)]
    [InlineData("✏️ edit order 3", 3)]
    public void ParsesEditOrder(string message, int? number)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.Equal(CommandKind.EditOrder, parsed!.Kind);
        Assert.Equal(number, parsed.Number);
    }

    [Theory]
    [InlineData("1 = 3", "qty", 1, 3, null, null)]
    [InlineData("qty 2 = 5", "qty", 2, 5, null, null)]
    [InlineData("item 1 ko 4", "qty", 1, 4, null, null)]
    [InlineData("price 1 = 1500", "price", 1, null, 1500, null)]
    [InlineData("rate 2 1200", "price", 2, null, 1200, null)]
    [InlineData("remove 2", "remove", 2, null, null, null)]
    [InlineData("2 hatao", "remove", 2, null, null, null)]
    [InlineData("add Kurti 2", "add", null, 2, null, "Kurti")]
    [InlineData("add 2 Kurti", "add", null, 2, null, "Kurti")]
    [InlineData("add Sugar 5 kg", "add", null, 1, null, "Sugar 5 kg")]
    [InlineData("phone 0300-111 2222", "phone", null, null, null, "03001112222")]
    [InlineData("address House 5, Gulberg", "address", null, null, null, "House 5, Gulberg")]
    [InlineData("name Sara Khan", "name", null, null, null, "Sara Khan")]
    [InlineData("delivery 250", "delivery", null, null, 250, null)]
    [InlineData("free delivery", "delivery", null, null, 0, null)]
    [InlineData("payment jazzcash", "payment", null, null, null, "jazzcash")]
    [InlineData("done", "done", null, null, null, null)]
    [InlineData("bas", "done", null, null, null, null)]
    public void ParsesOrderEditInstructions(string message, string kind, int? item, int? qty, int? amount, string? text)
    {
        Assert.True(CommandParser.TryParseOrderEdit(message, out var change));
        Assert.Equal(kind, change.Kind);
        Assert.Equal(item, change.Item);
        Assert.Equal(qty, change.Quantity);
        Assert.Equal(amount, change.Amount is null ? null : (int?)change.Amount);
        Assert.Equal(text, change.Text);
    }

    [Fact]
    public void EditOrder_DoesNotStealOtherOrderCommands()
    {
        Assert.Equal(CommandKind.MarkStatus, CommandParser.TryParse("mark 12 shipped")!.Kind);
        Assert.Equal(CommandKind.OrderDeliveryCharge, CommandParser.TryParse("order 12 delivery 300")!.Kind);
        Assert.Equal(CommandKind.CancelOrder, CommandParser.TryParse("cancel order 12")!.Kind);
        Assert.False(CommandParser.TryParseOrderEdit("orders today", out _));
    }

    [Theory]
    [InlineData("order 12", 12)]
    [InlineData("Order #5", 5)]
    [InlineData("#7", 7)]
    [InlineData("آرڈر 12", 12)]
    public void ParsesOrderDetail(string message, int number)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.Equal(CommandKind.OrderDetail, parsed!.Kind);
        Assert.Equal(number, parsed.Number);
    }

    [Theory]
    [InlineData("order 12 advance 500", 12, 500)]
    [InlineData("12 paid 1000", 12, 1000)]
    [InlineData("order #12 mila 300 rs", 12, 300)]
    [InlineData("advance 500 order 12", 12, 500)]
    [InlineData("payment Rs 750 for order 3", 3, 750)]
    public void ParsesOrderPayment(string message, int id, int amount)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.Equal(CommandKind.OrderPayment, parsed!.Kind);
        Assert.Equal(id, parsed.Number);
        Assert.Equal(amount, parsed.Amount);
    }

    [Fact]
    public void OrderPaymentAndDetail_DoNotStealOtherCommands()
    {
        Assert.Equal("paid", CommandParser.TryParse("mark 12 paid")!.Text);
        Assert.Equal(CommandKind.MarkStatus, CommandParser.TryParse("order 12 paid")!.Kind);
        Assert.Equal(CommandKind.OrderDeliveryCharge, CommandParser.TryParse("order 12 delivery 300")!.Kind);
        Assert.Equal(CommandKind.EditOrder, CommandParser.TryParse("order 12 edit")!.Kind);
        Assert.Equal(CommandKind.OrdersToday, CommandParser.TryParse("orders today")!.Kind);
    }

    [Theory]
    [InlineData("Sara, 1 kurti, 03001234567, advance 500 jazzcash", 500)]
    [InlineData("Sara 2 suit 0300-1234567, 1000 advance diya", 1000)]
    [InlineData("Sara 1 kurti advance paid Rs 300", 300)]
    [InlineData("Sana 1 suit ایڈوانس 400", 400)]
    public void FindsAdvanceInsideOrderText(string message, int expected)
    {
        Assert.True(CommandParser.TryFindAdvanceInOrderText(message, out var amount));
        Assert.Equal(expected, amount);
    }

    [Theory]
    [InlineData("Sara, 1 kurti, 03001234567, advance jazzcash se karegi")]
    [InlineData("Sara 1 kurti advance 03001234567")]
    [InlineData("Sara 1 kurti, 0300-1234567")]
    public void NoAdvance_WhenNoAmountIsStated(string message)
    {
        Assert.False(CommandParser.TryFindAdvanceInOrderText(message, out _));
    }

    [Theory]
    [InlineData("stock", null, null, null)]
    [InlineData("inventory", null, null, null)]
    [InlineData("stock Kurti 20", "Kurti", "set", 20)]
    [InlineData("stock: Lawn Suit = 5", "Lawn Suit", "set", 5)]
    [InlineData("Kurti stock 20", "Kurti", "set", 20)]
    [InlineData("Kurti ka stock 15", "Kurti", "set", 15)]
    [InlineData("stock Kurti +10", "Kurti", "add", 10)]
    [InlineData("stock Kurti off", "Kurti", "off", null)]
    public void ParsesStockCommands(string message, string? product, string? mode, int? amount)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.Equal(CommandKind.Stock, parsed!.Kind);
        Assert.Equal(product, parsed.Text);
        Assert.Equal(mode, parsed.Text2);
        Assert.Equal(amount, parsed.Amount is null ? null : (int?)parsed.Amount);
    }

    [Fact]
    public void Stock_DoesNotStealProductLines()
    {
        Assert.Equal(CommandKind.AddProduct, CommandParser.TryParse("Kurti - 1800")!.Kind);
        Assert.Equal(CommandKind.AddProduct, CommandParser.TryParse("Sugar 5 kg - 500")!.Kind);
    }
}
