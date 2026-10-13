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
        Assert.Contains("Rakam jo nahi mili: Rs.1,500", reply);
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
}
