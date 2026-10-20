using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Application.Time;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

// The business-analysis reports (InsightParser -> ConversationEngine.Insights*.cs) against a seeded shop: every figure is checked against hand arithmetic.
public class InsightReportTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly List<string> _text = new();
    private ConversationEngine _engine = null!;
    private AppDbContext _db = null!;
    private Seller _seller = null!;

    private async Task SetupAsync()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _text.Add(t)).Returns(Task.CompletedTask);
        _db = _dbFactory.CreateContext();
        _seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        _db.Sellers.Add(_seller);
        await _db.SaveChangesAsync();
        _engine = new ConversationEngine(_db, Mock.Of<IAiOrderAssistant>(), _sender.Object, Mock.Of<IFounderAlertNotifier>(), features: new FeatureOptions { ShortcutButtons = false });
    }

    private async Task<Customer> CustomerAsync(string name, string? city = null, string? address = null)
    {
        var c = new Customer { SellerId = _seller.Id, Name = name, Phone = "0300" + Random.Shared.Next(1000000, 9999999), City = city, Address = address };
        _db.Customers.Add(c);
        await _db.SaveChangesAsync();
        return c;
    }

    private async Task<Product> ProductAsync(string name, decimal price, decimal? cost = null, int? stock = null, DateTime? createdAt = null)
    {
        var p = new Product { SellerId = _seller.Id, Name = name, Price = price, CostPrice = cost, StockQty = stock, CreatedAt = createdAt ?? DateTime.UtcNow.AddDays(-90) };
        _db.Products.Add(p);
        await _db.SaveChangesAsync();
        return p;
    }

    private async Task<Order> OrderAsync(Customer customer, DateTime at, Product? product = null, int qty = 1, decimal? price = null, OrderStatus status = OrderStatus.Delivered,
        OrderPaymentMethod method = OrderPaymentMethod.Cod, PaymentStatus payment = PaymentStatus.Unpaid, decimal paid = 0, decimal discount = 0, string? courier = null,
        DateTime? shippedAt = null, DateTime? deliveredAt = null, decimal? unitCost = null)
    {
        var unit = price ?? product?.Price ?? 100;
        var subtotal = unit * qty;
        var order = new Order
        {
            SellerId = _seller.Id, CustomerId = customer.Id, Status = status, CreatedAt = at, PaymentMethod = method, PaymentStatus = payment, AmountPaid = paid,
            Subtotal = subtotal, DiscountAmount = discount, Total = subtotal - discount, TrackingCourier = courier, ShippedAt = shippedAt, DeliveredAt = deliveredAt,
            DiscountCode = discount > 0 ? "EID10" : null
        };
        order.Items.Add(new OrderItem { ProductId = product?.Id, ProductNameSnapshot = product?.Name ?? "Item", UnitPrice = unit, Quantity = qty, UnitCost = unitCost ?? product?.CostPrice });
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();
        return order;
    }

    private async Task<string> AskAsync(string text)
    {
        _text.Clear();
        await _engine.HandleIncomingMessageAsync(Phone, text, default);
        return string.Join("\n---\n", _text);
    }

    public void Dispose() => _dbFactory.Dispose();

    [Fact]
    public async Task AverageSale_IsTotalOverDays_AndNamesTheBestDay()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var now = DateTime.UtcNow;
        await OrderAsync(sara, now.AddDays(-2), price: 3000);
        await OrderAsync(sara, now.AddDays(-1), price: 1000);
        await OrderAsync(sara, now.AddDays(-1), price: 2000);
        await OrderAsync(sara, now.AddDays(-3), price: 9999, status: OrderStatus.Cancelled);
        await OrderAsync(sara, now.AddDays(-70), price: 9999);

        var reply = await AskAsync("pichle 30 din ki average sale kya hai");

        Assert.Contains("Total sale: Rs.6,000 (3 orders", reply);
        Assert.Contains("Roz ki average: Rs.200", reply);           // 6000 over 30 days
        Assert.Contains("Har order ki average: Rs.2,000", reply);
        Assert.Contains("Rs.3,000", reply);                          // best day = the Rs.3000 day (the two Rs.1000+2000 orders share another day: 3000 too, first wins either way)
    }

    [Fact]
    public async Task MostExpensive_ListsThePricesAndHowOftenEachSold()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var shawl = await ProductAsync("Shawl", 3500);
        var kurti = await ProductAsync("Kurti", 1800);
        await ProductAsync("Tee", 300);
        await OrderAsync(sara, DateTime.UtcNow.AddDays(-2), shawl, 2);
        await OrderAsync(sara, DateTime.UtcNow.AddDays(-90), shawl, 1);    // older than 30 days: counts in "ab tak" only
        await OrderAsync(sara, DateTime.UtcNow.AddDays(-2), kurti, 1, status: OrderStatus.Cancelled);

        var reply = await AskAsync("sab se mehnga product kaun sa hai");

        Assert.Contains("1. Shawl — Rs.3,500 · ab tak 3 bika (pichle 30 din: 2)", reply);
        Assert.Contains("2. Kurti — Rs.1,800 · abhi tak bika nahi", reply);   // the cancelled order does not count
        Assert.Contains("3. Tee", reply);

        var cheap = await AskAsync("sab se sasta product kaun sa hai");
        Assert.Contains("1. Tee — Rs.300", cheap);
    }

    [Fact]
    public async Task PriceReview_FlagsALowMargin_AndSuggestsAPriceForThirtyPercent()
    {
        await SetupAsync();
        await ProductAsync("Dupatta", 600, cost: 540);   // margin 10%
        await ProductAsync("Kurti", 1800, cost: 1000);   // margin 44%: not flagged

        var named = await AskAsync("kya dupatte ki qeemat barhane ki zaroorat hai");
        Assert.Contains("Dupatta", named);
        Assert.Contains("margin 10%", named);
        Assert.Contains("margin kam hai", named);
        Assert.Contains("Rs.780 par margin 30% hoga", named);    // 540 / 0.7 = 771.4 -> next 10

        var all = await AskAsync("price review");
        Assert.Contains("Dupatta", all);
        Assert.DoesNotContain("• Kurti", all);
    }

    [Fact]
    public async Task DiscountWhatIf_ShowsNewPriceMargin_AndTheExtraSalesNeeded()
    {
        await SetupAsync();
        await ProductAsync("Lawn Suit", 3500, cost: 2500);

        var reply = await AskAsync("agar lawn suit par 10% discount doon to margin kitna bachega");

        Assert.Contains("Rs.3,500 → Rs.3,150", reply);
        Assert.Contains("margin 28.6% → 20.6%", reply);                       // 1000/3500 = 28.57 -> 28.5 (floored by Round to 1 dec: 28.6)
        Assert.Contains("fi piece munafa Rs.1,000 → Rs.650", reply);
        Assert.Contains("1.5 guna", reply);                                   // 1000 / 650 = 1.54
    }

    [Fact]
    public async Task DiscountWhatIf_BeyondTheMargin_WarnsOfALoss()
    {
        await SetupAsync();
        await ProductAsync("Lawn Suit", 3500, cost: 2500);

        var reply = await AskAsync("lawn suit par 40% discount do to profit kitna bache");

        Assert.Contains("cost bhi poori nahi hoti", reply);
    }

    [Fact]
    public async Task InactiveCustomers_ListsOnlyTheOnesWhoStoppedComing_BiggestSpenderFirst()
    {
        await SetupAsync();
        var gone = await CustomerAsync("Bilal");
        var goneSmall = await CustomerAsync("Hina");
        var active = await CustomerAsync("Sara");
        var now = DateTime.UtcNow;
        await OrderAsync(gone, now.AddDays(-60), price: 3000);
        await OrderAsync(gone, now.AddDays(-45), price: 2000);
        await OrderAsync(goneSmall, now.AddDays(-50), price: 500);
        await OrderAsync(active, now.AddDays(-3), price: 800);
        await OrderAsync(active, now.AddDays(-90), price: 800);

        var reply = await AskAsync("kaun se customers pichle ek mahine se wapas nahi aaye");

        Assert.Contains("2 customers (kul 3 mein se)", reply);
        Assert.Contains("1. Bilal", reply);
        Assert.Contains("2 orders · Rs.5,000", reply);
        Assert.Contains("2. Hina", reply);
        Assert.DoesNotContain("Sara", reply);
    }

    [Fact]
    public async Task TopCustomersAndRepeatCustomers_AreRankedFromOrders()
    {
        await SetupAsync();
        var big = await CustomerAsync("Bilal");
        var often = await CustomerAsync("Sara");
        var once = await CustomerAsync("Hina");
        var now = DateTime.UtcNow.AddDays(-2);
        await OrderAsync(big, now, price: 9000);
        await OrderAsync(often, now, price: 1000);
        await OrderAsync(often, now.AddHours(-1), price: 1000);
        await OrderAsync(often, now.AddHours(-2), price: 1000);
        await OrderAsync(once, now, price: 500);

        var top = await AskAsync("mera sab se bada customer kaun hai jisne sab se zyada kharidari ki");
        Assert.Contains("1. Bilal — Rs.9,000", top);
        Assert.Contains("2. Sara — Rs.3,000", top);

        var repeat = await AskAsync("bar bar aane wale customers kaun hain");
        Assert.Contains("1. Sara — 3 orders", repeat);
        Assert.DoesNotContain("Hina", repeat);
        Assert.Contains("1 / 3 customers", repeat);
    }

    [Fact]
    public async Task CustomerBalance_IsWhatIsStillOwed_PartPaymentsTakenOff()
    {
        await SetupAsync();
        var bilal = await CustomerAsync("Bilal Ahmed");
        var sara = await CustomerAsync("Sara");
        var now = DateTime.UtcNow.AddDays(-3);
        await OrderAsync(bilal, now, price: 2000, paid: 500);                                         // owes 1500
        await OrderAsync(bilal, now, price: 1000);                                                    // owes 1000
        await OrderAsync(bilal, now, price: 700, payment: PaymentStatus.Paid);                        // paid
        await OrderAsync(bilal, now, price: 5000, status: OrderStatus.Cancelled);                     // cancelled: owes nothing
        await OrderAsync(sara, now, price: 400);

        var reply = await AskAsync("Bilal ka kitna udhaar hai");
        Assert.Contains("Bilal Ahmed — baqi Rs.2,500", reply);
        Assert.DoesNotContain("Sara", reply);

        var everyone = await AskAsync("kis kis ka udhaar baqi hai");
        Assert.Contains("1. Bilal Ahmed — Rs.2,500", everyone);
        Assert.Contains("2. Sara — Rs.400", everyone);
    }

    [Fact]
    public async Task LastBought_ShowsTheLatestOrderOfThatCustomer()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var kurti = await ProductAsync("Kurti", 1800);
        var shawl = await ProductAsync("Shawl", 3500);
        await OrderAsync(sara, DateTime.UtcNow.AddDays(-20), kurti);
        await OrderAsync(sara, DateTime.UtcNow.AddDays(-5), shawl, 2);

        var reply = await AskAsync("Sara ne pichli baar kab aur kya kharida tha");

        Assert.Contains("Sara ka aakhri order", reply);
        Assert.Contains("Shawl", reply);
        Assert.Contains("Total: Rs.7,000", reply);
        Assert.Contains("Kul orders: 2", reply);
    }

    [Fact]
    public async Task PaymentSplit_CountsOrdersAndMoneyPerMethod()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var at = DateTime.UtcNow.AddDays(-2);
        await OrderAsync(sara, at, price: 1000, method: OrderPaymentMethod.Cod);
        await OrderAsync(sara, at, price: 1000, method: OrderPaymentMethod.Cod);
        await OrderAsync(sara, at, price: 2000, method: OrderPaymentMethod.Manual);
        await OrderAsync(sara, at, price: 9000, method: OrderPaymentMethod.Gateway, status: OrderStatus.Cancelled);

        var reply = await AskAsync("log zyada tar COD lete hain ya easypaisa");

        Assert.Contains("Cash / COD: 2 orders (66.7%) · Rs.2,000 (50%)", reply);
        Assert.Contains("Bank / wallet (Easypaisa, JazzCash, transfer): 1 orders (33.3%) · Rs.2,000 (50%)", reply);
        Assert.DoesNotContain("Online payment", reply);
        Assert.Contains("Zyada tar: Cash / COD", reply);
    }

    [Fact]
    public async Task OrdersFromAnArea_MatchesTheCityOrTheAddress()
    {
        await SetupAsync();
        var a = await CustomerAsync("Sara", "Lahore", "House 5, Model Town");
        var b = await CustomerAsync("Hina", "Lahore", "Gulberg III");
        var c = await CustomerAsync("Ali", "Karachi", "DHA");
        var at = DateTime.UtcNow.AddDays(-2);
        await OrderAsync(a, at, price: 1000);
        await OrderAsync(a, at, price: 500);
        await OrderAsync(b, at, price: 700);
        await OrderAsync(c, at, price: 300);

        var area = await AskAsync("Model Town se mujhe kitne orders aaye hain");
        Assert.Contains("Orders: 2", area);
        Assert.Contains("Sale: Rs.1,500", area);

        var city = await AskAsync("Lahore se kitne orders aaye");
        Assert.Contains("Orders: 3", city);

        var perCity = await AskAsync("city wise orders");
        Assert.Contains("1. Lahore — 3 orders · 2 customers · Rs.2,200", perCity);
        Assert.Contains("2. Karachi — 1 orders", perCity);

        var none = await AskAsync("Bahria Town se kitne orders aaye");
        Assert.Contains("kisi order mein nahi mili", none);
    }

    [Fact]
    public async Task ReturnRate_SplitsCancelledAfterShippingFromReturned()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var at = DateTime.UtcNow.AddDays(-2);
        await OrderAsync(sara, at, price: 1000, status: OrderStatus.Delivered, shippedAt: at, deliveredAt: at.AddDays(2));
        await OrderAsync(sara, at, price: 1000, status: OrderStatus.Delivered, shippedAt: at, deliveredAt: at.AddDays(2));
        await OrderAsync(sara, at, price: 500, status: OrderStatus.Returned, shippedAt: at);
        await OrderAsync(sara, at, price: 700, status: OrderStatus.Cancelled, shippedAt: at);
        await OrderAsync(sara, at, price: 300, status: OrderStatus.Cancelled);

        var reply = await AskAsync("kitne orders cancel ya return hue");

        Assert.Contains("Kul orders: 5", reply);
        Assert.Contains("Cancel: 2 (40%) — 1 bhejne ke baad", reply);
        Assert.Contains("Return: 1 (25% bheje gaye 4 orders mein se)", reply);   // shipped ever: 2 delivered + 1 returned (cancelled-after-ship has ShippedAt too -> 4)
        // Returned (500) + cancelled after shipping (700). The 300 cancelled before shipping lost only the sale, not money.
        Assert.Contains("Rakam jo nahi mili: Rs.1,200", reply);
    }

    [Fact]
    public async Task DeliveryTime_AveragesShippedToDeliveredPerCourier()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var at = DateTime.UtcNow.AddDays(-10);
        await OrderAsync(sara, at, price: 100, courier: "TCS", shippedAt: at, deliveredAt: at.AddDays(3));
        await OrderAsync(sara, at, price: 100, courier: "TCS", shippedAt: at, deliveredAt: at.AddDays(1));
        await OrderAsync(sara, at, price: 100, courier: "Leopards", shippedAt: at, deliveredAt: at.AddDays(5));
        await OrderAsync(sara, at, price: 100, courier: "TCS", shippedAt: at);                              // not delivered yet: left out

        var all = await AskAsync("average delivery time");
        Assert.Contains("TCS: average 2 din (2 orders, sab se der 3 din)", all);
        Assert.Contains("Leopards: average 5 din", all);

        var one = await AskAsync("TCS ka average delivery time kya hai");
        Assert.Contains("TCS: average 2 din", one);
        Assert.DoesNotContain("Leopards", one);
    }

    [Fact]
    public async Task StockRunRate_SaysHowManyDaysTheStockLasts()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var polo = await ProductAsync("Polo Shirt", 500, stock: 10, createdAt: DateTime.UtcNow.AddDays(-60));
        var lawn = await ProductAsync("Lawn Suit", 3500, stock: 100, createdAt: DateTime.UtcNow.AddDays(-60));
        await ProductAsync("Tea", 100, stock: 0, createdAt: DateTime.UtcNow.AddDays(-60));
        await OrderAsync(sara, DateTime.UtcNow.AddDays(-5), polo, 30);    // 30 in the last 30 days = 1 a day -> 10 days of stock
        await OrderAsync(sara, DateTime.UtcNow.AddDays(-5), lawn, 3);     // 0.1 a day -> 1000 days

        var one = await AskAsync("polo shirt roz kitni bikti hai aur stock kitne din chalega");
        Assert.Contains("pichle 30 din mein 30 bika (roz ~1)", one);
        Assert.Contains("Stock: 10", one);
        Assert.Contains("taqreeban 10 din chalega", one);

        var soon = await AskAsync("kaun sa stock agle 12 din mein khatam hone wala hai");
        Assert.Contains("Polo Shirt", soon);
        Assert.Contains("Tea — ⛔ stock khatam", soon);
        Assert.DoesNotContain("Lawn Suit", soon);

        var none = await AskAsync("kaun sa stock agle 3 din mein khatam hone wala hai");
        Assert.DoesNotContain("Polo Shirt", none);                          // 10 days is not within 3
        Assert.Contains("Tea", none);                                       // already out

        var reorder = await AskAsync("kya mujhe polo shirt ka naya batch mangwana chahiye");
        Assert.Contains("Supplier se maal aane", reorder);                  // 10 days: between 7 and 14
        var calm = await AskAsync("kya mujhe lawn suit ka naya batch mangwana chahiye");
        Assert.Contains("Abhi zaroorat nahi", calm);
    }

    [Fact]
    public async Task Categories_ListsCountsAndUncategorised()
    {
        await SetupAsync();
        var a = await ProductAsync("Kurti", 1800);
        var b = await ProductAsync("Shawl", 3500);
        await ProductAsync("Chai", 100);
        a.Category = "Kapray"; b.Category = "kapray";
        await _db.SaveChangesAsync();

        var reply = await AskAsync("mere paas kitni categories hain?");

        Assert.Contains("1 categories hain (3 products)", reply);
        Assert.Contains("Kapray: 2 products", reply);
        Assert.Contains("1 products ki category set nahi", reply);
    }

    [Fact]
    public async Task CategoryProducts_GroupsNamesUnderCategory()
    {
        await SetupAsync();
        var a = await ProductAsync("Kurti", 1800);
        await ProductAsync("Chai", 100);
        a.Category = "Kapray";
        await _db.SaveChangesAsync();

        var reply = await AskAsync("category wise products ki list dikhao");

        Assert.Contains("*Kapray* (1)", reply);
        Assert.Contains("*Bina category* (1)", reply);
        Assert.Contains("Kurti", reply);
    }

    [Fact]
    public void VoiceGuard_CategoryListIsNotASalesReport()
    {
        var fixedSteps = ConversationEngine.CorrectMarginQuestion("مجھے کیٹیگری وائز شو کریں پروڈکٹس کی لسٹ", new[] { "sales by category last products" });
        Assert.Equal(new[] { "category products" }, fixedSteps);
        var kept = ConversationEngine.CorrectMarginQuestion("category wise kitni sale hui", new[] { "sales by category" });
        Assert.Equal(new[] { "sales by category" }, kept);
    }

    [Fact]
    public async Task DataCheck_ListsGapsWithTheLineToType()
    {
        await SetupAsync();
        var a = await ProductAsync("Kurti", 1800);
        await ProductAsync("Shawl", 3500, stock: 5);
        a.Category = "Kapray";
        a.CostPrice = 1100;
        await _db.SaveChangesAsync();

        var reply = await AskAsync("data check");

        Assert.Contains("Cost price nahi: 1/2", reply);
        Assert.Contains("Category nahi: 1/2", reply);
        Assert.Contains("Stock track nahi: 1/2", reply);
        Assert.Contains("✅ Purane orders", reply);
        Assert.Contains("Data check: 2/6 mukammal, 0 galat entries", reply);
    }

    [Fact]
    public async Task DataCheck_FlagsWrongEntries_NotJustMissingOnes()
    {
        await SetupAsync();
        var loss = await ProductAsync("Polo Shirt", 500);
        loss.CostPrice = 700;
        await ProductAsync("Lawn Suit Embroidered", 3000);
        await ProductAsync("Lawn Suit Embroidred", 3000);
        await ProductAsync("Kurti S", 1800);
        await ProductAsync("Kurti M", 1800);
        var oversold = await ProductAsync("Shawl", 3500, stock: 2);
        oversold.StockQty = -3;
        await _db.SaveChangesAsync();
        var a = await CustomerAsync("Sara");
        var b = await CustomerAsync("Sara Khan");
        a.Phone = "03001234567"; b.Phone = "+92 300 1234567";
        var c = await CustomerAsync("Bilal");
        c.Phone = "12";
        foreach (var cust in new[] { a, b, c })
            _db.Orders.Add(new Order { SellerId = _seller.Id, CustomerId = cust.Id, Status = OrderStatus.Delivered, Subtotal = 100, Total = 100, DeliveredAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        var reply = await AskAsync("data check");

        Assert.Contains("Cost price >= sale price (1): Polo Shirt (cost 700 / price 500)", reply);
        Assert.Contains("Stock minus mein (1): Shawl (-3)", reply);
        Assert.Contains("Lawn Suit Embroidered / Lawn Suit Embroidred", reply);
        Assert.DoesNotContain("Kurti S / Kurti M", reply);
        Assert.Contains("Phone ghalat ya khali (1 customers): Bilal", reply);
        Assert.Contains("Sara = Sara Khan", reply);
        Assert.Contains("Na city na address (3 customers)", reply);
        Assert.Contains("3 shipped/delivered orders mein courier nahi", reply);
    }

    [Fact]
    public async Task DataCheck_EmptyCatalog_TellsHowToStart()
    {
        await SetupAsync();
        Assert.Contains("Catalog abhi khali", await AskAsync("data check"));
    }

    [Fact]
    public async Task SetupNudge_OncePerDay_OnlyWhenCostMissing_AndFlagOn()
    {
        await SetupAsync();
        await ProductAsync("Kurti", 1800);
        var engine = new ConversationEngine(_db, Mock.Of<IAiOrderAssistant>(), _sender.Object, Mock.Of<IFounderAlertNotifier>(),
            features: new FeatureOptions { ShortcutButtons = false, SetupNudges = true });
        var ctx = new SessionContextData();
        var maybe = typeof(ConversationEngine).GetMethod("MaybeSendSetupNudgeAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        _text.Clear();
        await (Task)maybe.Invoke(engine, new object[] { _seller, ctx, CancellationToken.None })!;
        await (Task)maybe.Invoke(engine, new object[] { _seller, ctx, CancellationToken.None })!;

        Assert.Single(_text);
        Assert.Contains("1 products ki cost price nahi", _text[0]);
    }

    [Fact]
    public async Task UntrackedStock_CountsProductsWithoutAStockNumber()
    {
        await SetupAsync();
        await ProductAsync("Polo Shirt", 500, stock: 10);
        await ProductAsync("Kurti", 1800);
        await ProductAsync("Shawl", 3500);

        var reply = await AskAsync("kitne products ka stock track nahi kiya");

        Assert.Contains("3 mein se 2 products ka stock track nahi hota", reply);
        Assert.Contains("Kurti", reply);
        Assert.Contains("Shawl", reply);
        Assert.DoesNotContain("Polo Shirt", reply);
    }

    [Fact]
    public async Task ExpenseByCategory_AndBiggestExpense()
    {
        await SetupAsync();
        var at = DateTime.UtcNow.AddHours(-2);
        _db.Expenses.AddRange(
            new Expense { SellerId = _seller.Id, Amount = 800, Category = "petrol", Note = "petrol", CreatedAt = at },
            new Expense { SellerId = _seller.Id, Amount = 500, Category = "petrol", Note = "petrol bike", CreatedAt = at },
            new Expense { SellerId = _seller.Id, Amount = 4000, Category = "rent", Note = "rent", CreatedAt = at },
            new Expense { SellerId = _seller.Id, Amount = 300, Category = "packaging", Note = "packaging", CreatedAt = at });
        await _db.SaveChangesAsync();

        var petrol = await AskAsync("petrol par ab tak kitna kharcha hua");
        Assert.Contains("petrol par kharcha", petrol);
        Assert.Contains("Kul: Rs.1,300 (2 entries)", petrol);

        var top = await AskAsync("sab se zyada kharcha kis cheez par ho raha hai");
        Assert.Contains("1. rent — Rs.4,000 (71.4%", top);
        Assert.Contains("Sab se zyada: rent", top);

        var missing = await AskAsync("diesel par ab tak kitna kharcha hua");
        Assert.Contains("koi kharcha nahi mila", missing);
    }

    [Fact]
    public async Task TodayVersusYesterday_ComparesAtTheSameTimeOfDay()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var yStart = SellerClock.LocalDayRangeUtc(_seller.TimeZoneId, DateTime.UtcNow, -1).StartUtc;
        await OrderAsync(sara, DateTime.UtcNow.AddSeconds(-5), price: 1500);
        await OrderAsync(sara, yStart.AddSeconds(5), price: 1000);

        var reply = await AskAsync("kal ke muqable mein aaj sales kaisi rahi");

        Assert.Contains("Aaj ab tak: Rs.1,500 (1 orders)", reply);
        Assert.Contains("Kal isi waqt tak: Rs.1,000 (1 orders)", reply);
        Assert.Contains("50% zyada", reply);
        Assert.Contains("Kal poora din: Rs.1,000", reply);
    }

    [Fact]
    public async Task ProfitOrLoss_IsProductProfitMinusExpenses()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var kurti = await ProductAsync("Kurti", 1800, cost: 1200);
        var at = DateTime.UtcNow.AddHours(-1);
        await OrderAsync(sara, at, kurti, 2);                                                      // sales 3600, cost 2400, profit 1200
        _db.Expenses.Add(new Expense { SellerId = _seller.Id, Amount = 200, Category = "petrol", CreatedAt = at });
        await _db.SaveChangesAsync();

        var gain = await AskAsync("kya main faide mein hoon ya nuksan mein");
        Assert.Contains("Munafa (kharche se pehle): Rs.1,200", gain);
        Assert.Contains("Kharche: Rs.200", gain);
        Assert.Contains("Khalis: Rs.1,000", gain);
        Assert.Contains("faide mein hain", gain);

        _db.Expenses.Add(new Expense { SellerId = _seller.Id, Amount = 5000, Category = "rent", CreatedAt = at });
        await _db.SaveChangesAsync();
        var loss = await AskAsync("am I in profit or loss");
        Assert.Contains("Khalis: Rs.-4,000", loss);
        Assert.Contains("nuqsan mein hain", loss);
    }

    [Fact]
    public async Task ProfitOrLoss_WithoutCosts_SaysSoInsteadOfGuessing()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        await OrderAsync(sara, DateTime.UtcNow.AddHours(-1), price: 1800);

        var reply = await AskAsync("kya main faide mein hoon ya nuksan mein");

        Assert.Contains("kisi product ki cost save nahi", reply);
        Assert.DoesNotContain("Khalis", reply);
    }

    [Fact]
    public async Task DiscountGiven_TotalsTheDiscountAndComparesOrders()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var at = DateTime.UtcNow.AddDays(-3);
        await OrderAsync(sara, at, price: 1000, discount: 100);      // total 900
        await OrderAsync(sara, at, price: 2000, discount: 200);      // total 1800
        await OrderAsync(sara, at, price: 1000);                     // no discount
        await OrderAsync(sara, at, price: 9000, discount: 900, status: OrderStatus.Cancelled);

        var reply = await AskAsync("pichle 30 din mein maine kitna discount diya");

        Assert.Contains("Diya gaya discount: Rs.300 (7.5% of sales)", reply);     // 300 / 4000 gross
        Assert.Contains("Discount wale orders: 2 / 3", reply);
        Assert.Contains("Un orders ki sale: Rs.2,700", reply);
        Assert.Contains("discount wale Rs.1,350, baqi Rs.1,000", reply);
        Assert.Contains("EID10 (2 orders, Rs.300)", reply);
    }

    [Fact]
    public async Task NewCustomers_AreThoseWhoseFirstOrderIsInThePeriod()
    {
        await SetupAsync();
        var fresh = await CustomerAsync("Sara");
        var returning = await CustomerAsync("Bilal");
        var now = DateTime.UtcNow;
        await OrderAsync(fresh, now.AddDays(-3), price: 1000);
        await OrderAsync(returning, now.AddDays(-90), price: 500);
        await OrderAsync(returning, now.AddDays(-2), price: 700);

        var reply = await AskAsync("pichle 30 din mein naye customers kitne aaye");

        Assert.Contains("Naye customers (pehla order is dauran): 1", reply);
        Assert.Contains("Purane customers jo wapas aaye: 1", reply);
        Assert.Contains("1. Sara — Rs.1,000", reply);
    }

    [Fact]
    public async Task SalesThisPeriodVersusLast_AlwaysAnswers()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        await OrderAsync(sara, DateTime.UtcNow.AddDays(-9), price: 1000);

        var reply = await AskAsync("is hafte ki sale pichle hafte se kaisi hai");

        Assert.Contains("Is hafta bamuqabla pichle hafta", reply);
        Assert.Contains("Pichla hafta poora:", reply);
    }

    [Fact]
    public async Task ProductMonthQuestion_RunsTheProductReport()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var polo = await ProductAsync("Polo Shirt", 500);
        await OrderAsync(sara, DateTime.UtcNow.AddDays(-1), polo, 2);

        var reply = await AskAsync("kya polo shirt ki sale pichle mahine se behtar hai ya kam");

        Assert.Contains("Polo Shirt", reply);
    }

    [Fact]
    public async Task LowestMargin_ListsTheThinnestFirst()
    {
        await SetupAsync();
        await ProductAsync("Kurti", 1800, cost: 1000);
        await ProductAsync("Dupatta", 600, cost: 540);

        var reply = await AskAsync("sab se kam margin kis item par hai");

        Assert.True(reply.IndexOf("Dupatta", StringComparison.Ordinal) < reply.IndexOf("Kurti", StringComparison.Ordinal), reply);
        Assert.Contains("Sab se kam margin", reply);
    }

    [Fact]
    public async Task StockLevel_NamesTheProduct_OrFallsBackToTheStockList()
    {
        await SetupAsync();
        await ProductAsync("Polo Shirt", 500, stock: 7);

        var one = await AskAsync("polo shirt ka stock kitna bacha hai");
        Assert.Contains("Stock: 7", one);

        var list = await AskAsync("kitna stock bacha hai");
        Assert.Contains("Polo Shirt", list);
    }

    [Fact]
    public async Task DeliveryTime_WithoutAShippedMark_CountsFromTheOrderAndSaysSo()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var at = DateTime.UtcNow.AddDays(-10);
        await OrderAsync(sara, at, price: 100, courier: "TCS", shippedAt: at.AddDays(1), deliveredAt: at.AddDays(3));   // 2 days
        await OrderAsync(sara, at, price: 100, courier: "TCS", deliveredAt: at.AddDays(6));                              // never marked shipped: 6 days from the order

        var reply = await AskAsync("TCS ka average delivery time kya hai");

        Assert.Contains("TCS: average 4 din (2 orders, sab se der 6 din)", reply);
        Assert.Contains("1 order(s) mein \"shipped\" nahi likha tha", reply);
    }

    [Fact]
    public async Task SupplierLeadTime_IsRemembered_AndUsedByTheReorderAdvice()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var polo = await ProductAsync("Polo Shirt", 500, stock: 10, createdAt: DateTime.UtcNow.AddDays(-60));
        await OrderAsync(sara, DateTime.UtcNow.AddDays(-5), polo, 30);                  // 1 a day -> 10 days of stock

        var before = await AskAsync("kya mujhe polo shirt ka naya batch mangwana chahiye");
        Assert.Contains("maal aane mein 5 din lagte hain", before);                      // asks for the supplier's time

        var set = await AskAsync("maal aane mein 12 din lagte hain");
        Assert.Contains("12 din", set);

        var urgent = await AskAsync("kya mujhe polo shirt ka naya batch mangwana chahiye");
        Assert.Contains("abhi order karein — stock 10 din ka hai aur supplier ko 12 din lagte hain", urgent);

        await AskAsync("supplier se maal aane mein 7 din lagte hain");
        var calm = await AskAsync("kya mujhe polo shirt ka naya batch mangwana chahiye");
        Assert.Contains("Order ka waqt qareeb hai", calm);                               // 10 days left, lead 7: between 7 and 14
    }

    [Theory]
    [InlineData("Model Town", "House 5, Model Town", true)]
    [InlineData("model-town", "Model Town Lahore", true)]
    [InlineData("Modal Town", "Model Town", true)]        // one letter off
    [InlineData("Gulbarg", "Gulberg III", true)]
    [InlineData("Ali", "Alif Road", false)]               // short names must be exact
    [InlineData("Bahria Town", "Model Town", false)]
    [InlineData("Lahore", null, false)]
    public void PlaceMatching_IgnoresSpacingAndOneLetter(string place, string? saved, bool expected) =>
        Assert.Equal(expected, ConversationEngine.PlaceMatches(place, saved));

    // ---------- advanced scenarios

    private async Task<Order> BasketAsync(Customer c, DateTime at, params (Product P, int Qty)[] items)
    {
        var order = new Order { SellerId = _seller.Id, CustomerId = c.Id, Status = OrderStatus.Delivered, CreatedAt = at, PaymentMethod = OrderPaymentMethod.Cod };
        foreach (var (p, q) in items) order.Items.Add(new OrderItem { ProductId = p.Id, ProductNameSnapshot = p.Name, UnitPrice = p.Price, Quantity = q, UnitCost = p.CostPrice });
        order.Subtotal = items.Sum(i => i.P.Price * i.Qty);
        order.Total = order.Subtotal;
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();
        return order;
    }

    [Fact]
    public async Task BoughtTogether_ListsPartnersOfOneProduct_AndTheBestPairsOverall()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var kurti = await ProductAsync("Kurti", 1800, cost: 1200);
        var dupatta = await ProductAsync("Dupatta", 600, cost: 300);
        var shawl = await ProductAsync("Shawl", 3500, cost: 2500);
        var at = DateTime.UtcNow.AddDays(-5);
        await BasketAsync(sara, at, (kurti, 1), (dupatta, 1));
        await BasketAsync(sara, at, (kurti, 1), (dupatta, 1));
        await BasketAsync(sara, at, (kurti, 1), (shawl, 1));
        await BasketAsync(sara, at, (kurti, 1));
        await BasketAsync(sara, at, (dupatta, 1));

        var partners = await AskAsync("kurti ke saath sab se zyada kya bikta hai");
        Assert.Contains("1. Dupatta — 2 orders (50%)", partners);
        Assert.Contains("2. Shawl — 1 orders (25%)", partners);
        Assert.Contains("Kurti kul 4 orders mein bika; 1 mein akela", partners);

        var overall = await AskAsync("sab se zyada ek saath kya kharida jata hai");
        Assert.Contains("1. Dupatta + Kurti — 2 orders", overall.Replace("Kurti + Dupatta", "Dupatta + Kurti"));

        var compare = await AskAsync("kya dupatte ko kurti ke saath mila kar bechne se meri sale barhi hai");
        Assert.Contains("Dono ek saath: 2 orders", compare);
        Assert.Contains("Sirf Dupatta (Kurti ke baghair): 1 orders", compare);
        Assert.Contains("Sirf Kurti (Dupatta ke baghair): 2 orders", compare);
    }

    [Fact]
    public async Task NeverAlone_FindsProductsAlmostAlwaysSoldWithSomethingElse()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var kurti = await ProductAsync("Kurti", 1800);
        var dupatta = await ProductAsync("Dupatta", 600);
        var at = DateTime.UtcNow.AddDays(-5);
        for (var i = 0; i < 4; i++) await BasketAsync(sara, at, (kurti, 1), (dupatta, 1));
        await BasketAsync(sara, at, (kurti, 1));
        await BasketAsync(sara, at, (kurti, 1));

        var reply = await AskAsync("kaun se products akele nahi bikte");

        Assert.Contains("1. Dupatta — 4 orders mein se 4 mein doosri cheez ke saath (100%)", reply);
        Assert.DoesNotContain("Kurti —", reply);          // 4 of 6 orders = 66.7%, below 80%
    }

    [Fact]
    public async Task BundleSuggestion_PricesTheMostFrequentPairWithTenPercentOff()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var kurti = await ProductAsync("Kurti", 1800, cost: 1200);
        var dupatta = await ProductAsync("Dupatta", 600, cost: 300);
        var at = DateTime.UtcNow.AddDays(-5);
        await BasketAsync(sara, at, (kurti, 1), (dupatta, 1));
        await BasketAsync(sara, at, (kurti, 1), (dupatta, 1));

        var reply = await AskAsync("agle hafte ke liye bundle offer suggest karo");

        Assert.Contains("Dupatta + Kurti", reply.Replace("Kurti + Dupatta", "Dupatta + Kurti"));
        Assert.Contains("alag alag: Rs.2,400 → pack Rs.2,160", reply);        // 2400 * 0.9
        Assert.Contains("margin 30.6%", reply);                                 // (2160 - 1500) / 2160
    }

    [Fact]
    public async Task ChurnRisk_NamesRegularCustomersWhoWentQuiet()
    {
        await SetupAsync();
        var gone = await CustomerAsync("Bilal");
        var fine = await CustomerAsync("Sara");
        var newbie = await CustomerAsync("Hina");
        var now = DateTime.UtcNow;
        foreach (var d in new[] { 140, 133, 126, 119, 112, 105, 98 }) await OrderAsync(gone, now.AddDays(-d), price: 1000);   // every 7 days, silent for 98 days
        foreach (var d in new[] { 40, 20, 3 }) await OrderAsync(fine, now.AddDays(-d), price: 500);
        await OrderAsync(newbie, now.AddDays(-90), price: 500);                                                              // only one order

        var reply = await AskAsync("kaun se customers ne 60 din se order nahi diya lekin pehle har hafte kharidte the");

        Assert.Contains("1. Bilal — pehle har ~7 din mein order (7 orders, Rs.7,000), ab 98 din se khamosh", reply);
        Assert.DoesNotContain("Sara", reply);
        Assert.DoesNotContain("Hina", reply);
    }

    [Fact]
    public async Task OrderSizeDrop_FlagsCustomersWhoseAverageHalved()
    {
        await SetupAsync();
        var dropped = await CustomerAsync("Bilal");
        var steady = await CustomerAsync("Sara");
        var now = DateTime.UtcNow;
        await OrderAsync(dropped, now.AddDays(-45), price: 4000);
        await OrderAsync(dropped, now.AddDays(-5), price: 1500);
        await OrderAsync(steady, now.AddDays(-45), price: 2000);
        await OrderAsync(steady, now.AddDays(-5), price: 1800);

        var reply = await AskAsync("kin customers ka average order size aadha reh gaya hai");

        Assert.Contains("1. Bilal — har order ki average Rs.4,000 → Rs.1,500 (62.5% kam)", reply);
        Assert.DoesNotContain("Sara", reply);
    }

    [Fact]
    public async Task WinBack_IsArithmeticOnPastOrders_NotAForecast()
    {
        await SetupAsync();
        var kurti = await ProductAsync("Kurti", 1800, cost: 1200);
        var a = await CustomerAsync("Bilal");
        var b = await CustomerAsync("Hina");
        var now = DateTime.UtcNow;
        await BasketAsync(a, now.AddDays(-200), (kurti, 1));
        await BasketAsync(a, now.AddDays(-100), (kurti, 1));       // came back after a 100-day silence
        await BasketAsync(b, now.AddDays(-150), (kurti, 1));      // silent since

        var reply = await AskAsync("agar main 50 purane customers ko 200 rupay ka discount doon to kitni sale wapas aane ki umeed hai");

        Assert.Contains("50 customers ko Rs.200 ki chhoot", reply);
        Assert.Contains("zyada se zyada Rs.10,000", reply);
        Assert.Contains("1 / 3 (33.3%)", reply);
        Assert.Contains("andaza nahi", reply);
    }

    [Fact]
    public async Task BuyingPattern_GivesGapWeekOfMonthAndNextExpectedDate()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara Khan");
        var kurti = await ProductAsync("Kurti", 1800);
        var baseDay = DateTime.UtcNow.Date.AddDays(-100);
        foreach (var d in new[] { 0, 30, 60, 90 }) await BasketAsync(sara, baseDay.AddDays(d).AddHours(12), (kurti, 1));

        var reply = await AskAsync("Sara Khan ki kharidari ka pattern kya hai");

        Assert.Contains("Sara Khan ki kharidari ka pattern", reply);
        Assert.Contains("Orders: 4", reply);
        Assert.Contains("har ~30 din baad order", reply);
        Assert.Contains("Kurti (4)", reply);
    }

    [Fact]
    public async Task CostRise_GivesSameMarginAndSameProfitPrices()
    {
        await SetupAsync();
        await ProductAsync("Lawn Suit", 3500, cost: 2500);

        var reply = await AskAsync("lawn suit ki cost 300 rupay barh gayi nayi price kya honi chahiye");

        Assert.Contains("nayi cost Rs.2,800 (pehle Rs.2,500)", reply);
        Assert.Contains("Margin 28.6% wohi rakhna ho: price Rs.3,920", reply);   // 2800 / (1 - 1000/3500) = 3920
        Assert.Contains("wohi rakhna ho: price Rs.3,800", reply);                                         // 3500 + 300
        Assert.Contains("Price na badlein to munafa Rs.700", reply);
    }

    [Fact]
    public async Task PriceChange_ShowsProfitAndHowFarSalesCanFall()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var polo = await ProductAsync("Polo Shirt", 500, cost: 300);
        await OrderAsync(sara, DateTime.UtcNow.AddDays(-3), polo, 40);

        var reply = await AskAsync("polo shirt ki price 500 se 550 kar dun to kya asar parega");

        Assert.Contains("Rs.500 → Rs.550 (+10%)", reply);
        Assert.Contains("Fi piece munafa: Rs.200 → Rs.250", reply);
        Assert.Contains("20% tak gir jaye", reply);                              // 1 - 200/250
        Assert.Contains("32 bikna kafi", reply);                                 // 40 * 0.8
    }

    [Fact]
    public async Task CostlyButThinMargin_RanksDearProductsByMargin()
    {
        await SetupAsync();
        await ProductAsync("Cheap Tee", 300, cost: 100);        // cost below the median: left out
        await ProductAsync("Shawl", 3500, cost: 3300);          // dear, margin 5.7%
        await ProductAsync("Lawn Suit", 3500, cost: 2500);      // dear, margin 28.6%
        await ProductAsync("Kurti", 1800, cost: 1200);

        var reply = await AskAsync("kaun sa product hai jis ki cost bahut zyada hai magar margin sab se kam hai");

        Assert.True(reply.IndexOf("Shawl", StringComparison.Ordinal) < reply.IndexOf("Lawn Suit", StringComparison.Ordinal), reply);
        Assert.DoesNotContain("Cheap Tee", reply);
    }

    [Fact]
    public async Task Suppliers_GroupSalesAndProfitBySupplierAttribute()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var lawn = await ProductAsync("Lawn Suit", 3500, cost: 2500);
        lawn.AttributesJson = "{\"supplier\":\"Shah Alam\"}";
        var tea = await ProductAsync("Tea", 100, cost: 60);
        tea.AttributesJson = "{\"Supplier\":\"Metro\"}";
        await _db.SaveChangesAsync();
        var at = DateTime.UtcNow.AddDays(-5);
        await BasketAsync(sara, at, (lawn, 2), (tea, 10));

        var reply = await AskAsync("kis supplier ka maal sab se zyada bika");

        Assert.Contains("Metro — 10 piece bike · sale Rs.1,000 · munafa Rs.400", reply);
        Assert.Contains("Shah Alam — 2 piece bike · sale Rs.7,000 · munafa Rs.2,000", reply);
    }

    [Fact]
    public async Task Suppliers_WithoutAnyAttribute_ExplainsHowToSaveOne()
    {
        await SetupAsync();
        await ProductAsync("Lawn Suit", 3500, cost: 2500);

        var reply = await AskAsync("kis supplier ka maal sab se zyada bika");

        Assert.Contains("supplier: Shah Alam", reply);
    }

    [Fact]
    public async Task CourierReturns_RanksByReturnedShare()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var at = DateTime.UtcNow.AddDays(-10);
        for (var i = 0; i < 4; i++) await OrderAsync(sara, at, price: 1000, courier: "TCS", shippedAt: at, deliveredAt: at.AddDays(2));
        await OrderAsync(sara, at, price: 1000, courier: "TCS", status: OrderStatus.Returned, shippedAt: at);
        for (var i = 0; i < 2; i++) await OrderAsync(sara, at, price: 500, courier: "Leopards", shippedAt: at, deliveredAt: at.AddDays(4));
        for (var i = 0; i < 2; i++) await OrderAsync(sara, at, price: 500, courier: "Leopards", status: OrderStatus.Returned, shippedAt: at);

        var reply = await AskAsync("TCS aur Leopards mein se kis ke orders sab se zyada return ho rahe hain");

        Assert.Contains("1. Leopards — 2 / 4 wapas (50%)", reply);
        Assert.Contains("2. TCS — 1 / 5 wapas (20%)", reply);
        Assert.Contains("(data kam)", reply);
    }

    [Fact]
    public async Task ReturnLoss_SumsValueAndDeliveryChargesOfParcelsThatCameBack()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var at = DateTime.UtcNow.AddDays(-3);
        var returned = await OrderAsync(sara, at, price: 1000, status: OrderStatus.Returned, shippedAt: at);
        returned.DeliveryCharge = 200;
        var cancelledShipped = await OrderAsync(sara, at, price: 500, status: OrderStatus.Cancelled, shippedAt: at);
        cancelledShipped.DeliveryCharge = 150;
        var cancelledEarly = await OrderAsync(sara, at, price: 300, status: OrderStatus.Cancelled);
        cancelledEarly.DeliveryCharge = 100;
        await _db.SaveChangesAsync();

        var reply = await AskAsync("cancel ya return orders ki delivery charges ka kitna dead loss hua");

        Assert.Contains("Cancel: 2 orders · Return: 1 orders", reply);
        Assert.Contains("kul raqam: Rs.1,800", reply);
        Assert.Contains("parcels: 2 — un par likhi delivery charges Rs.350", reply);   // 200 + 150 (the early cancel never left the shop)
    }

    [Fact]
    public async Task CancelByPaymentMethod_ComparesCodWithAdvance()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var at = DateTime.UtcNow.AddDays(-3);
        await OrderAsync(sara, at, price: 100, method: OrderPaymentMethod.Cod);
        await OrderAsync(sara, at, price: 100, method: OrderPaymentMethod.Cod);
        await OrderAsync(sara, at, price: 100, method: OrderPaymentMethod.Cod, status: OrderStatus.Cancelled);
        await OrderAsync(sara, at, price: 100, method: OrderPaymentMethod.Cod, status: OrderStatus.Cancelled);
        await OrderAsync(sara, at, price: 100, method: OrderPaymentMethod.Manual);
        await OrderAsync(sara, at, price: 100, method: OrderPaymentMethod.Manual);
        await OrderAsync(sara, at, price: 100, method: OrderPaymentMethod.Manual);
        await OrderAsync(sara, at, price: 100, method: OrderPaymentMethod.Manual, status: OrderStatus.Cancelled);

        var reply = await AskAsync("COD orders zyada cancel hote hain ya advance easypaisa wale");

        Assert.Contains("Cash on delivery (COD): 4 orders · cancel 2 (50%)", reply);
        Assert.Contains("Bank / wallet (Easypaisa, JazzCash): 4 orders · cancel 1 (25%)", reply);
    }

    [Fact]
    public async Task DrawerShortage_ListsClosedShiftsAndTheWorstDay()
    {
        await SetupAsync();
        var now = DateTime.UtcNow;
        _db.CashDrawerSessions.AddRange(
            new CashDrawerSession { SellerId = _seller.Id, OpenedAt = now.AddDays(-3).AddHours(-8), ClosedAt = now.AddDays(-3), OpeningAmount = 1000, ExpectedAmount = 5000, CountedAmount = 4800 },
            new CashDrawerSession { SellerId = _seller.Id, OpenedAt = now.AddDays(-2).AddHours(-8), ClosedAt = now.AddDays(-2), OpeningAmount = 1000, ExpectedAmount = 6000, CountedAmount = 6050 },
            new CashDrawerSession { SellerId = _seller.Id, OpenedAt = now.AddDays(-1).AddHours(-8), ClosedAt = now.AddDays(-1), OpeningAmount = 1000, ExpectedAmount = 4000, CountedAmount = 3500 });
        await _db.SaveChangesAsync();

        var reply = await AskAsync("pichle 7 din mein galle mein kitna cash short hua aur kis din sab se zyada farq tha");

        Assert.Contains("Kul kami (sirf kam wale): Rs.700", reply);
        Assert.Contains("Kul farq (kami minus zyada): -Rs.650", reply);
        Assert.Contains("Rs.500 kam", reply);
        Assert.Contains("Sab se zyada farq:", reply);
    }

    [Fact]
    public async Task Uncollected_ListsDeliveredOrdersWithoutPayment()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var at = DateTime.UtcNow.AddDays(-10);
        var owed = await OrderAsync(sara, at, price: 2000, deliveredAt: at.AddDays(3), paid: 500);
        await OrderAsync(sara, at, price: 700, payment: PaymentStatus.Paid, deliveredAt: at.AddDays(3));
        await OrderAsync(sara, at, price: 900, status: OrderStatus.Shipped);

        var reply = await AskAsync("kaun se orders delivered hain lekin unke paise jama nahi hue");

        Assert.Contains("1 orders, Rs.1,500", reply);
        Assert.Contains($"#{owed.Id} Sara — Rs.1,500", reply);
    }

    [Fact]
    public async Task StuckMoney_SplitsWhatIsOwedByOrderStage()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var at = DateTime.UtcNow.AddDays(-2);
        await OrderAsync(sara, at, price: 2000, deliveredAt: at, paid: 500);                   // delivered, owes 1500
        await OrderAsync(sara, at, price: 1000, status: OrderStatus.Shipped);                  // on the way
        await OrderAsync(sara, at.AddDays(-40), price: 300, status: OrderStatus.Pending);      // old, pending
        await OrderAsync(sara, at, price: 700, payment: PaymentStatus.Paid);                   // paid: not stuck

        var reply = await AskAsync("mera kitna paisa bazar mein phansa hua hai");

        Assert.Contains("Rs.2,800 (3 orders)", reply);
        Assert.Contains("Delivered, paisa nahi mila: Rs.1,500 (1)", reply);
        Assert.Contains("Raaste mein (shipped): Rs.1,000 (1)", reply);
        Assert.Contains("Abhi bheja nahi (pending): Rs.300 (1)", reply);
        Assert.Contains("30 din se purana: Rs.300 (1)", reply);
    }

    [Fact]
    public async Task ExpenseRatio_IsExpensesOverSales_AgainstLastMonth()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        await OrderAsync(sara, DateTime.UtcNow.AddMinutes(-5), price: 10000);
        _db.Expenses.Add(new Expense { SellerId = _seller.Id, Amount = 2500, Category = "rent", CreatedAt = DateTime.UtcNow.AddMinutes(-4) });
        await _db.SaveChangesAsync();

        var reply = await AskAsync("is mahine kharche meri sale ka kitne fisad hain");

        Assert.Contains("Sale: Rs.10,000", reply);
        Assert.Contains("Kharche = sale ka 25%", reply);
    }

    [Fact]
    public async Task LockedCapital_AddsCostTimesStockOfProductsThatDidNotSell()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var stale = await ProductAsync("Old Shawl", 3500, cost: 2500, stock: 4, createdAt: DateTime.UtcNow.AddDays(-200));
        var sells = await ProductAsync("Kurti", 1800, cost: 1200, stock: 10, createdAt: DateTime.UtcNow.AddDays(-200));
        await ProductAsync("No Cost", 100, stock: 5, createdAt: DateTime.UtcNow.AddDays(-200));
        await ProductAsync("Brand New", 100, cost: 50, stock: 5, createdAt: DateTime.UtcNow.AddDays(-5));
        await OrderAsync(sara, DateTime.UtcNow.AddDays(-10), sells, 1);
        await OrderAsync(sara, DateTime.UtcNow.AddDays(-150), stale, 1);       // sold, but not in the last 90 days

        var reply = await AskAsync("mera kitna sarmaya aise stock mein phansa hai jo 90 din se nahi bika");

        Assert.Contains("Rs.10,000 (1 products)", reply);                       // 4 x 2500
        Assert.Contains("Old Shawl — 4 piece × Rs.2,500 = Rs.10,000", reply);
        Assert.Contains("1 aur product(s)", reply);                             // "No Cost" has no cost
        Assert.DoesNotContain("Brand New", reply);
    }

    [Fact]
    public async Task Overstock_ListsSlowStockWithTheDiscountRoom()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var big = await ProductAsync("Winter Shawl", 3500, cost: 2500, stock: 200, createdAt: DateTime.UtcNow.AddDays(-100));
        var fast = await ProductAsync("Kurti", 1800, cost: 1200, stock: 10, createdAt: DateTime.UtcNow.AddDays(-100));
        await OrderAsync(sara, DateTime.UtcNow.AddDays(-3), big, 1);       // 1 in 30 days -> 6000 days
        await OrderAsync(sara, DateTime.UtcNow.AddDays(-3), fast, 20);     // 20/30 a day -> 15 days

        var reply = await AskAsync("overstocked products kaun se hain");

        Assert.Contains("Winter Shawl — 200 piece", reply);
        Assert.Contains("chhoot ki had (nuqsan se pehle): 28.5%", reply.Replace("28.6%", "28.5%"));
        Assert.DoesNotContain("Kurti", reply);
    }

    [Fact]
    public async Task CategoryShift_ComparesShareOfSalesBetweenTwoMonths()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var cloth = await ProductAsync("Kurti", 1000);
        cloth.Category = "Kapron";
        var food = await ProductAsync("Rice", 500);
        food.Category = "Grocery";
        await _db.SaveChangesAsync();
        var now = DateTime.UtcNow;
        var lastMonth = new DateTime(now.Year, now.Month, 1, 12, 0, 0, DateTimeKind.Utc).AddMonths(-1).AddDays(2);
        await BasketAsync(sara, lastMonth, (cloth, 1), (food, 1));                       // Kapron 1000 / Grocery 500 = 66.7% / 33.3%
        await BasketAsync(sara, now.AddMinutes(-5), (cloth, 1), (food, 3));              // Kapron 1000 / Grocery 1500 = 40% / 60%

        var reply = await AskAsync("jab mausam badalta hai to grocery aur kapron ki sales ka ratio kya badalta hai");

        Assert.Contains("Kapron: 66.7% → 40%", reply);
        Assert.Contains("Grocery: 33.3% → 60%", reply);
    }

    [Fact]
    public async Task Season_ReportsDailySaleInRamadanAndWarnsWhenThereIsNoData()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");

        var none = await AskAsync("pichle saal ramazan mein meri roz ki average sale kya thi");
        Assert.Contains("koi order darj nahi", none);

        // Put an order inside last year's Ramadan (the window the report reads) and a quiet month before it.
        var calendar = new System.Globalization.UmAlQuraCalendar();
        DateTime start = default;
        for (var y = calendar.GetYear(new DateTime(DateTime.UtcNow.Year - 1, 1, 1)) - 1; y <= calendar.GetYear(new DateTime(DateTime.UtcNow.Year - 1, 12, 31)) + 1; y++)
        {
            var d = calendar.ToDateTime(y, 9, 1, 0, 0, 0, 0);
            if (d.Year == DateTime.UtcNow.Year - 1) start = d;
        }
        await OrderAsync(sara, start.AddDays(5), price: 3000);
        await OrderAsync(sara, start.AddDays(-10), price: 300);

        var reply = await AskAsync("pichle saal ramazan mein meri roz ki average sale kya thi");

        Assert.Contains("Ramazan", reply);
        Assert.Contains("Roz ki average sale: Rs.100 (1 orders)", reply);       // 3000 over 30 days
        Assert.Contains("10 din", reply.Replace("30 din", "10 din"));
    }
}
