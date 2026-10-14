using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

// The strategic scenarios against a seeded shop; every figure is hand arithmetic.
public class StrategicInsightReportTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly List<string> _text = new();
    private ConversationEngine _engine = null!;
    private AppDbContext _db = null!;
    private Seller _seller = null!;

    private async Task SetupAsync(string? city = null)
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _text.Add(t)).Returns(Task.CompletedTask);
        _db = _dbFactory.CreateContext();
        _seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha", City = city, OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        _db.Sellers.Add(_seller);
        await _db.SaveChangesAsync();
        _engine = new ConversationEngine(_db, Mock.Of<IAiOrderAssistant>(), _sender.Object, Mock.Of<IFounderAlertNotifier>(), features: new FeatureOptions { ShortcutButtons = false });
    }

    private async Task<Customer> CustomerAsync(string name, string? city = null)
    {
        var c = new Customer { SellerId = _seller.Id, Name = name, Phone = "0300" + Random.Shared.Next(1000000, 9999999), City = city };
        _db.Customers.Add(c);
        await _db.SaveChangesAsync();
        return c;
    }

    private async Task<Product> ProductAsync(string name, decimal price, decimal? cost = null, int? stock = null, int createdDaysAgo = 300, string? category = null, string? attributes = null)
    {
        var p = new Product { SellerId = _seller.Id, Name = name, Price = price, CostPrice = cost, StockQty = stock, Category = category, AttributesJson = attributes, CreatedAt = DateTime.UtcNow.AddDays(-createdDaysAgo) };
        _db.Products.Add(p);
        await _db.SaveChangesAsync();
        return p;
    }

    private async Task<Order> SaleAsync(Customer c, DateTime at, Product? p = null, int qty = 1, decimal? price = null, decimal discount = 0, string? code = null, OrderStatus status = OrderStatus.Delivered,
        OrderPaymentMethod method = OrderPaymentMethod.Cod, PaymentStatus payment = PaymentStatus.Unpaid, decimal delivery = 0, DateTime? shippedAt = null, DateTime? deliveredAt = null,
        DateTime? paidAt = null, DateTime? returnedAt = null, string? courier = null, string? source = null, decimal? courierFee = null, int? payoutId = null)
    {
        var unit = price ?? p?.Price ?? 100;
        var subtotal = unit * qty;
        var order = new Order
        {
            SellerId = _seller.Id, CustomerId = c.Id, Status = status, CreatedAt = at, UpdatedAt = at, PaymentMethod = method, PaymentStatus = payment, Subtotal = subtotal, DiscountAmount = discount, DiscountCode = code,
            DeliveryCharge = delivery, Total = subtotal - discount + delivery, ShippedAt = shippedAt, DeliveredAt = deliveredAt, PaidAt = paidAt, ReturnedAt = returnedAt, TrackingCourier = courier,
            OrderSource = source, CourierFee = courierFee, PayoutId = payoutId
        };
        order.Items.Add(new OrderItem { ProductId = p?.Id, ProductNameSnapshot = p?.Name ?? "Item", UnitPrice = unit, Quantity = qty, UnitCost = p?.CostPrice });
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

    // The latest date (UTC, 07:00 = 12:00 in Karachi) whose local day of the month is `day` / that falls on `weekday`.
    private static DateTime LastDayOfMonth(int day)
    {
        var d = DateTime.UtcNow.Date.AddDays(-2);
        while (d.Day != day) d = d.AddDays(-1);
        return d.AddHours(7);
    }

    private static DateTime LastWeekday(DayOfWeek weekday, int utcHour)
    {
        var d = DateTime.UtcNow.Date.AddDays(-1);
        while (d.DayOfWeek != weekday) d = d.AddDays(-1);
        return d.AddHours(utcHour);
    }

    public void Dispose() => _dbFactory.Dispose();

    [Fact]
    public async Task SalaryCycle_SplitsLateMonthFromTheRest_AndCounterFromOnline()
    {
        await SetupAsync();
        var c = await CustomerAsync("Sara");
        var late = LastDayOfMonth(26);
        var early = LastDayOfMonth(10);
        await SaleAsync(c, late, price: 1000, source: "offline"); await SaleAsync(c, late.AddMinutes(5), price: 1000, source: "offline");
        await SaleAsync(c, late.AddMinutes(9), price: 3000, method: OrderPaymentMethod.Manual);
        await SaleAsync(c, early, price: 500, source: "offline");
        await SaleAsync(c, early.AddMinutes(5), price: 1500); await SaleAsync(c, early.AddMinutes(9), price: 1500);

        var reply = await AskAsync("mahine ke aakhri 5 din walk-in aur online gahakon ke order size aur cash payment mein kya farq aata hai");

        Assert.Contains("25 se 31 tareekh:\n• Dukaan par (walk-in): 2 orders · har order ki average Rs.1,000 · cash 100%\n• Online / WhatsApp: 1 orders · har order ki average Rs.3,000 · cash 0%", reply);
        Assert.Contains("Baqi din:\n• Dukaan par (walk-in): 1 orders · har order ki average Rs.500 · cash 100%\n• Online / WhatsApp: 2 orders · har order ki average Rs.1,500 · cash 100%", reply);
    }

    [Fact]
    public async Task PromoCohort_ComparesWhatCustomersWhoStartedOnADiscountSpentWithFullPriceStarters()
    {
        await SetupAsync();
        var now = DateTime.UtcNow;
        var p1 = await CustomerAsync("P1"); var p2 = await CustomerAsync("P2"); var f1 = await CustomerAsync("F1");
        await SaleAsync(p1, now.AddDays(-100), price: 1000, code: "EID10"); await SaleAsync(p1, now.AddDays(-50), price: 1000);
        await SaleAsync(p2, now.AddDays(-90), price: 1000, code: "EID10");
        await SaleAsync(f1, now.AddDays(-100), price: 1500); await SaleAsync(f1, now.AddDays(-60), price: 1500); await SaleAsync(f1, now.AddDays(-20), price: 1500);

        var reply = await AskAsync("jin customers ne discount promotion par pehli baar kharidari ki unki lifetime contribution aam rate walon se kitni kam ya zyada hai");

        Assert.Contains("Discount se aaye (2): ab tak har customer ne Rs.1,500, 1.5 orders", reply);
        Assert.Contains("Pure rate par aaye (1): ab tak har customer ne Rs.4,500, 3 orders", reply);
        Assert.Contains("discount wale kam (66.7%)", reply);
    }

    [Fact]
    public async Task PeakTime_FindsTheBusiestDayAndPartOfDay()
    {
        await SetupAsync();
        var c = await CustomerAsync("Sara");
        var sunday = LastWeekday(DayOfWeek.Sunday, 13);          // 18:00 Karachi
        for (var i = 0; i < 4; i++) await SaleAsync(c, sunday.AddMinutes(i * 7));
        await SaleAsync(c, LastWeekday(DayOfWeek.Monday, 5));    // 10:00 Karachi

        var reply = await AskAsync("hafte ke kis din aur kis waqt sab se zyada orders book hote hain");

        Assert.Contains("Sab se tez din: Sunday (Itwar) (4)", reply);
        Assert.Contains("Sab se tez waqt: shaam (4–8) (4)", reply);
    }

    [Fact]
    public async Task PaymentHabit_FlagsAdvanceForDearAndCodForCheap()
    {
        await SetupAsync();
        var a = await CustomerAsync("Bilal"); var c = await CustomerAsync("Hina");
        var at = DateTime.UtcNow.AddDays(-5);
        for (var i = 0; i < 3; i++) { await SaleAsync(a, at, price: 3000, method: OrderPaymentMethod.Manual); await SaleAsync(a, at, price: 300); }
        for (var i = 0; i < 3; i++) { await SaleAsync(c, at, price: 3000); await SaleAsync(c, at, price: 300); }

        var reply = await AskAsync("kya koi customers hain jo mehnga maal khareedte waqt advance easypaisa karte hain lekin sasta maal COD par mangte hain");

        Assert.Contains("1. Bilal — bade 3 orders mein 100% advance/wallet; chhote 3 mein 100% COD", reply);
        Assert.DoesNotContain("Hina", reply);
    }

    [Fact]
    public async Task HoldingCost_SharesRentAndBillsByStockValueOverTheIdleDays()
    {
        await SetupAsync();
        var c = await CustomerAsync("Sara");
        var now = DateTime.UtcNow;
        var idle = await ProductAsync("Lawn Suit", 3500, cost: 1000, stock: 10, createdDaysAgo: 200);
        var moving = await ProductAsync("Kurti", 1800, cost: 1000, stock: 10, createdDaysAgo: 200);
        await SaleAsync(c, now.AddDays(-5), moving);
        _db.Expenses.AddRange(new Expense { SellerId = _seller.Id, Amount = 30000, Category = "kiraya", CreatedAt = now.AddDays(-40) }, new Expense { SellerId = _seller.Id, Amount = 6000, Category = "bijli", CreatedAt = now.AddDays(-40) });
        await _db.SaveChangesAsync();

        var reply = await AskAsync("agar lawn suit 60 din se zyada stock mein pada rahe to uski holding cost mera margin kitna kam karti hai");

        Assert.Contains("1. Lawn Suit — 10 piece, holding cost Rs.12,000 (Rs.1,200 fi piece = qeemat ka 34.3%); margin 71.4% → 37.1%", reply);
        Assert.DoesNotContain("Kurti —", reply);
    }

    [Fact]
    public async Task TurnoverStar_PicksTheLowMarginProductThatEarnsTheMost()
    {
        await SetupAsync();
        var c = await CustomerAsync("Sara");
        var a = await ProductAsync("Alpha", 1000, cost: 500); var b = await ProductAsync("Bravo", 1000, cost: 800); var d = await ProductAsync("Charlie", 1000, cost: 600);
        var at = DateTime.UtcNow.AddDays(-5);
        await SaleAsync(c, at, a, 10); await SaleAsync(c, at, b, 100); await SaleAsync(c, at, d, 20);

        var reply = await AskAsync("kaun sa product jis ka margin kam hai lekin turnover velocity itni tez hai ke mahine mein sab se zyada munafa deta hai");

        Assert.Contains("1. Bravo — margin 20% (beech wala 40%), roz ~3.3 bikta hai, 30 din ka munafa Rs.20,000 (munafe mein number 1 / 3)", reply);
        Assert.DoesNotContain("Alpha —", reply);
    }

    [Fact]
    public async Task Liquidate_ReleasesCostMinusTheMarkdown()
    {
        await SetupAsync();
        await ProductAsync("Old Shawl", 3500, cost: 2500, stock: 4);
        await ProductAsync("Old Tee", 300, cost: 100, stock: 10);

        var reply = await AskAsync("agar main dead stock jo 90 din se nahi bika cost se 10% sasta bech dun to kitna cash liquidity release hoga");

        Assert.Contains("cost par maal Rs.11,000", reply);
        Assert.Contains("Fauri cash milega: Rs.9,900", reply);
        Assert.Contains("Nuqsan (cost ke muqable): Rs.1,100", reply);
    }

    [Fact]
    public async Task SupplierCompare_RanksByMargin_AndSaysLeadTimeIsNotPerSupplier()
    {
        await SetupAsync();
        var c = await CustomerAsync("Sara");
        var lawn = await ProductAsync("Lawn Suit", 3500, cost: 2500, attributes: "{\"supplier\":\"Shah Alam\"}");
        var kurti = await ProductAsync("Kurti", 1800, cost: 1200, attributes: "{\"supplier\":\"Khan\"}");
        var at = DateTime.UtcNow.AddDays(-10);
        await SaleAsync(c, at, lawn, 2); await SaleAsync(c, at, kurti, 2);
        await AskAsync("maal aane mein 5 din lagte hain");

        var reply = await AskAsync("supplier Khan aur Shah Alam ke maal par margin aur lead time kya hai");

        Assert.Contains("1. Khan — 2 piece bike · margin 33.3%", reply);
        Assert.Contains("2. Shah Alam — 2 piece bike · margin 28.6%", reply);
        Assert.Contains("sab suppliers ke liye 5 din", reply);
    }

    [Fact]
    public async Task SupplierDefects_RanksReturnedAndWrittenOffShare()
    {
        await SetupAsync();
        var c = await CustomerAsync("Sara");
        var lawn = await ProductAsync("Lawn Suit", 3500, cost: 2500, attributes: "{\"supplier\":\"Shah Alam\"}");
        var kurti = await ProductAsync("Kurti", 1800, cost: 1200, attributes: "{\"supplier\":\"Khan\"}");
        var at = DateTime.UtcNow.AddDays(-20);
        await SaleAsync(c, at, lawn, 8); await SaleAsync(c, at, lawn, 2, status: OrderStatus.Returned);
        await SaleAsync(c, at, kurti, 10);
        _db.Losses.Add(new Loss { SellerId = _seller.Id, ProductId = kurti.Id, ProductName = "Kurti", Quantity = 1, UnitCost = 1200, CreatedAt = at });
        await _db.SaveChangesAsync();

        var reply = await AskAsync("pichle 6 mahine mein kis supplier ke maal mein sab se zyada kharabi nikli jis ki wajah se customers ne return kiya");

        Assert.Contains("1. Shah Alam — 20% (2 wapas + 0 kharab/gum, 10 piece mein se)", reply);
        Assert.Contains("2. Khan — 10% (0 wapas + 1 kharab/gum, 10 piece mein se)", reply);
    }

    [Fact]
    public async Task SupplierRateRise_GivesPriceForSameMarginAndForSameProfit()
    {
        await SetupAsync();
        await ProductAsync("Lawn Suit", 3500, cost: 2500, attributes: "{\"supplier\":\"Shah Alam\"}");

        var reply = await AskAsync("agar supplier Shah Alam agli khep par wholesale rate 20% barha de to margin barqarar rakhne ke liye mujhe kam se kam kitna rate barhana paray ga");

        Assert.Contains("Shah Alam", reply);
        Assert.Contains("price Rs.4,200 (+20%)", reply);
        Assert.Contains("Rs.4,000 (+14.3%)", reply);
    }

    [Fact]
    public async Task ReturnTransit_AveragesShippedToReturnedPerCourier_AndFlagsStuckParcels()
    {
        await SetupAsync();
        var c = await CustomerAsync("Sara");
        var now = DateTime.UtcNow;
        await SaleAsync(c, now.AddDays(-20), status: OrderStatus.Returned, courier: "TCS", shippedAt: now.AddDays(-18), returnedAt: now.AddDays(-12));        // 6 days
        await SaleAsync(c, now.AddDays(-20), status: OrderStatus.Returned, courier: "TCS", shippedAt: now.AddDays(-18), returnedAt: now.AddDays(-10));        // 8 days
        await SaleAsync(c, now.AddDays(-20), status: OrderStatus.Shipped, courier: "Leopards", shippedAt: now.AddDays(-15));

        var reply = await AskAsync("Leopards aur TCS mein se kis courier ke paas mere return parcel RTO sab se zyada din raaste mein phanse rehte hain");

        Assert.Contains("1. TCS — return aane mein average 7 din (sab se zyada 8; 2 parcels)", reply);
        Assert.Contains("2. Leopards — koi returned parcel nahi · 1 parcel 10 din se \"shipped\" hi hain", reply);
    }

    [Fact]
    public async Task CityCompare_ShowsDeliveryDaysAndReturnedShareForTheCitiesNamed()
    {
        await SetupAsync();
        var k = await CustomerAsync("Ali", "Karachi"); var i = await CustomerAsync("Sara", "Islamabad");
        var at = DateTime.UtcNow.AddDays(-20);
        await SaleAsync(k, at, shippedAt: at, deliveredAt: at.AddDays(2)); await SaleAsync(k, at, shippedAt: at, deliveredAt: at.AddDays(4)); await SaleAsync(k, at, status: OrderStatus.Returned, shippedAt: at);
        await SaleAsync(i, at, shippedAt: at, deliveredAt: at.AddDays(6)); await SaleAsync(i, at, shippedAt: at, deliveredAt: at.AddDays(6));

        var reply = await AskAsync("Karachi aur Islamabad ke orders par delivery time kitna lag raha hai aur kya customers parcel lene se inkar refusal kar rahe hain");

        Assert.Contains("Karachi: 3 parcels · delivery average 3 din · wapas 1 (33.3%)", reply);
        Assert.Contains("Islamabad: 2 parcels · delivery average 6 din · wapas 0 (0%)", reply);
    }

    [Fact]
    public async Task ReturnShareOfProfit_UsesTheWrittenCourierFee()
    {
        await SetupAsync();
        var c = await CustomerAsync("Sara");
        var kurti = await ProductAsync("Kurti", 1800, cost: 1200);
        var at = DateTime.UtcNow.AddDays(-10);
        await SaleAsync(c, at, kurti, 10);                                                                          // profit 6000
        await SaleAsync(c, at, status: OrderStatus.Returned, shippedAt: at, courierFee: 250);
        await SaleAsync(c, at, status: OrderStatus.Returned, shippedAt: at, courierFee: 150);

        var reply = await AskAsync("pichle 90 din mein jitne parcel return hue unka delivery aur wapsi ka kiraya mila kar courier ko kul munafa ka kitna fisad chala gaya");

        Assert.Contains("Courier par gaya: Rs.400 (likhi hui fee)", reply);
        Assert.Contains("courier ka hissa 6.7%", reply);
    }

    [Fact]
    public async Task PaidThenCancelled_ChecksForAMatchingCashOut()
    {
        await SetupAsync();
        var c = await CustomerAsync("Sara");
        var now = DateTime.UtcNow;
        var refunded = await SaleAsync(c, now.AddDays(-5), price: 2000, status: OrderStatus.Cancelled, payment: PaymentStatus.Paid, paidAt: now.AddDays(-5));
        var missing = await SaleAsync(c, now.AddDays(-5), price: 1500, status: OrderStatus.Returned, payment: PaymentStatus.Paid, paidAt: now.AddDays(-5));
        refunded.UpdatedAt = now.AddDays(-2); missing.UpdatedAt = now.AddDays(-2);
        var drawer = new CashDrawerSession { SellerId = _seller.Id, OpenedAt = now.AddDays(-3), OpeningAmount = 0 };
        _db.CashDrawerSessions.Add(drawer);
        await _db.SaveChangesAsync();
        _db.CashMovements.Add(new CashMovement { SellerId = _seller.Id, SessionId = drawer.Id, Kind = CashMovementKind.Out, Amount = 2000, CreatedAt = DateTime.UtcNow.AddMinutes(1) });
        await _db.SaveChangesAsync();

        var reply = await AskAsync("kya koi order pehle Paid mark hua phir Cancelled ya Returned ho gaya magar drawer se naqd raqam kam nahi hui audit");

        Assert.Contains($"#{refunded.Id} Sara — CANCELLED, mila hua Rs.2,000 → drawer mein cash out mila (Rs.2,000)", reply);
        Assert.Contains($"#{missing.Id} Sara — RETURNED, mila hua Rs.1,500 → ⚠️ koi cash out nahi mila", reply);
    }

    [Fact]
    public async Task DiscountBasket_ComparesAverageOrderWithAndWithoutDiscountPerCategory()
    {
        await SetupAsync();
        var c = await CustomerAsync("Sara");
        var kurti = await ProductAsync("Kurti", 2000, category: "Kapron");
        var at = DateTime.UtcNow.AddDays(-10);
        for (var i = 0; i < 3; i++) await SaleAsync(c, at, kurti, 1, price: 2000, discount: 0, code: "EID10");           // total 2000, discount flag via amount below
        _db.Orders.Where(o => o.DiscountCode == "EID10").ToList().ForEach(o => o.DiscountAmount = 200);                  // revenue 2000 - 200
        await _db.SaveChangesAsync();
        for (var i = 0; i < 3; i++) await SaleAsync(c, at, kurti, 1, price: 1000);

        var reply = await AskAsync("kis category kurti ya dupatta par discount dene se basket size mein sab se zyada izafa hua");

        Assert.Contains("1. Kapron — discount wale order ki average Rs.2,000 (3) bamuqabla Rs.1,000 (3) → +100%", reply);
    }

    [Fact]
    public async Task DiscountReturnRate_ComparesReturnedShareOfDiscountedAndFullPriceParcels()
    {
        await SetupAsync();
        var c = await CustomerAsync("Sara");
        var at = DateTime.UtcNow.AddDays(-10);
        for (var i = 0; i < 10; i++) await SaleAsync(c, at, price: 1000, discount: 100, code: "EID10", shippedAt: at, status: i < 2 ? OrderStatus.Returned : OrderStatus.Delivered);
        for (var i = 0; i < 10; i++) await SaleAsync(c, at, price: 1000, shippedAt: at, status: i < 1 ? OrderStatus.Returned : OrderStatus.Delivered);

        var reply = await AskAsync("kya discount par khareedne wale customers ka return rate pura rate dene walon se zyada hai");

        Assert.Contains("Discount wale orders: 10 bheje, 2 wapas → 20%", reply);
        Assert.Contains("Bina discount: 10 bheje, 1 wapas → 10%", reply);
        Assert.Contains("discount wale orders zyada wapas aaye", reply);
    }

    [Fact]
    public async Task CashNeeded_AddsRentBillsAndSupplierPayments()
    {
        await SetupAsync();
        var now = DateTime.UtcNow;
        _db.Expenses.AddRange(
            new Expense { SellerId = _seller.Id, Amount = 30000, Category = "kiraya", CreatedAt = now.AddDays(-40) },
            new Expense { SellerId = _seller.Id, Amount = 6000, Category = "bijli", CreatedAt = now.AddDays(-40) },
            new Expense { SellerId = _seller.Id, Amount = 60000, Category = "supplier", CreatedAt = now.AddDays(-40) });
        await _db.SaveChangesAsync();

        var reply = await AskAsync("agle mahine ki 5 tarikh tak kiraya bijli ka bill aur suppliers ki payment nikalne ke liye roz kam se kam kitne rupay ka maal bechna lazmi hai");

        Assert.Contains("Kiraya / bijli / bill (mahine ki average): Rs.12,000", reply);
        Assert.Contains("Supplier ki payment (mahine ki average): Rs.20,000", reply);
        Assert.Contains("Kul zaroorat: Rs.32,000", reply);
        Assert.Contains("⚠️ roz ~", reply);
    }

    [Fact]
    public async Task Settlement_AveragesDaysToPaymentByMethod()
    {
        await SetupAsync();
        var c = await CustomerAsync("Sara");
        var now = DateTime.UtcNow;
        _db.Payouts.Add(new Payout { SellerId = _seller.Id, Source = "TCS", Amount = 1000 });
        await _db.SaveChangesAsync();
        var payoutId = _db.Payouts.Single().Id;
        await SaleAsync(c, now.AddDays(-10), method: OrderPaymentMethod.Manual, payment: PaymentStatus.Paid, paidAt: now.AddDays(-9));
        await SaleAsync(c, now.AddDays(-10), deliveredAt: now.AddDays(-8), payment: PaymentStatus.Paid, paidAt: now.AddDays(-6));
        await SaleAsync(c, now.AddDays(-12), deliveredAt: now.AddDays(-10), payment: PaymentStatus.Paid, paidAt: now.AddDays(-3), payoutId: payoutId);

        var reply = await AskAsync("easypaisa jazzcash ki advance payments aur COD ke paison ke bank mein aane ke waqt cash settlement cycle mein kitna farq hai");

        Assert.Contains("(order se paid mark tak): order se average 1 din", reply);
        Assert.Contains("COD cash (delivery se paid mark tak): order se average 2 din", reply);
        Assert.Contains("courier payout ke zariye (delivery se payout tak): order se average 7 din", reply);
    }

    [Fact]
    public async Task AdvanceOrderSize_ComparesAverageTotals()
    {
        await SetupAsync();
        var c = await CustomerAsync("Sara");
        var at = DateTime.UtcNow.AddDays(-10);
        await SaleAsync(c, at, price: 3000, method: OrderPaymentMethod.Manual); await SaleAsync(c, at, price: 3000, method: OrderPaymentMethod.Manual);
        await SaleAsync(c, at, price: 1000); await SaleAsync(c, at, price: 1000);

        var reply = await AskAsync("kya advance payment karne wale customers ka order size aam customers se bara hota hai");

        Assert.Contains("Advance / wallet / bank: 2 orders · har order ki average Rs.3,000", reply);
        Assert.Contains("COD / cash: 2 orders · har order ki average Rs.1,000", reply);
        Assert.Contains("advance wale orders bade hain (200%)", reply);
    }

    [Fact]
    public async Task Leakage_AddsCourierCostOfReturnedParcelsAndWrittenOffGoods()
    {
        await SetupAsync();
        var c = await CustomerAsync("Sara");
        var now = DateTime.UtcNow.AddDays(-5);
        await SaleAsync(c, now, price: 500, status: OrderStatus.Cancelled);                                                              // before shipping: no cash loss
        await SaleAsync(c, now, price: 2000, delivery: 200, status: OrderStatus.Returned, shippedAt: now, courierFee: 260);              // fee written
        await SaleAsync(c, now, price: 700, delivery: 150, status: OrderStatus.Cancelled, shippedAt: now);                               // cancelled after shipping: delivery charge stands in
        _db.Losses.Add(new Loss { SellerId = _seller.Id, ProductName = "Kurti", Quantity = 2, UnitCost = 500, CreatedAt = now });
        await _db.SaveChangesAsync();

        var reply = await AskAsync("pichle 30 din mein cancellation return aur wastage ki wajah se dukaan ko kul kitna nuqsan hua");

        Assert.Contains("Bhejne se pehle cancel: 1 orders", reply);
        Assert.Contains("courier par gaya Rs.410", reply);
        Assert.Contains("Kharab / gum maal: 2 piece, cost Rs.1,000", reply);
        Assert.Contains("Pakka naqd nuqsan: Rs.1,410", reply);
    }

    [Fact]
    public async Task PendingCapital_IsTheMoneyStillOwed()
    {
        await SetupAsync();
        var c = await CustomerAsync("Sara");
        await SaleAsync(c, DateTime.UtcNow.AddDays(-3), price: 2000, deliveredAt: DateTime.UtcNow.AddDays(-2));

        var reply = await AskAsync("میرا کتنا سرمایہ اس وقت مارکیٹ میں پینڈنگ پڑا ہے جو گاہکوں یا کورئیر کمپنیوں سے ابھی وصول ہونا باقی ہے؟");

        Assert.Contains("Bazar mein phansa paisa: Rs.2,000", reply);
    }
}
