using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

// The hyper-complex business scenarios against a seeded shop; every figure is hand arithmetic.
public class HyperInsightReportTests : IDisposable
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
        _seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections", City = city, OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
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

    private async Task<Product> ProductAsync(string name, decimal price, decimal? cost = null, int? stock = null, int createdDaysAgo = 120, string? category = null)
    {
        var p = new Product { SellerId = _seller.Id, Name = name, Price = price, CostPrice = cost, StockQty = stock, Category = category, CreatedAt = DateTime.UtcNow.AddDays(-createdDaysAgo) };
        _db.Products.Add(p);
        await _db.SaveChangesAsync();
        return p;
    }

    private async Task<Order> SaleAsync(Customer c, DateTime at, Product? p = null, int qty = 1, decimal? price = null, decimal discount = 0, string? code = null, OrderStatus status = OrderStatus.Delivered,
        OrderPaymentMethod method = OrderPaymentMethod.Cod, PaymentStatus payment = PaymentStatus.Unpaid, decimal delivery = 0, string? courier = null, DateTime? shippedAt = null,
        DateTime? deliveredAt = null, DateTime? paidAt = null, DateTime? returnedAt = null, decimal? unitCost = null, bool noCost = false)
    {
        var unit = price ?? p?.Price ?? 100;
        var subtotal = unit * qty;
        var order = new Order
        {
            SellerId = _seller.Id, CustomerId = c.Id, Status = status, CreatedAt = at, PaymentMethod = method, PaymentStatus = payment, Subtotal = subtotal, DiscountAmount = discount,
            DiscountCode = code, DeliveryCharge = delivery, Total = subtotal - discount + delivery, TrackingCourier = courier, ShippedAt = shippedAt, DeliveredAt = deliveredAt, PaidAt = paidAt, ReturnedAt = returnedAt
        };
        order.Items.Add(new OrderItem { ProductId = p?.Id, ProductNameSnapshot = p?.Name ?? "Item", UnitPrice = unit, Quantity = qty, UnitCost = noCost ? null : unitCost ?? p?.CostPrice });
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
    public async Task CustomerContribution_RanksBigSpendersByWhatTheyLeaveAfterDiscountAndReturns()
    {
        await SetupAsync();
        var kurti = await ProductAsync("Kurti", 1800, cost: 1200);
        var a = await CustomerAsync("Bilal Khan");
        var b = await CustomerAsync("Sara");
        var c = await CustomerAsync("Hina");
        var now = DateTime.UtcNow;
        for (var i = 1; i <= 3; i++) await SaleAsync(a, now.AddDays(-i * 10), kurti, 2, discount: 600);             // sale 3000, profit 600 each
        await SaleAsync(a, now.AddDays(-5), kurti, 2, status: OrderStatus.Returned, delivery: 200);                   // came back: delivery charge lost
        for (var i = 1; i <= 3; i++) await SaleAsync(b, now.AddDays(-i * 10), kurti, 1);                              // sale 1800, profit 600 each
        await SaleAsync(c, now.AddDays(-3), kurti, 1);

        var ranking = await AskAsync("kaun sa customer bade orders deta hai magar discount aur return ki wajah se mera net profit contribution sab se kam hai");
        Assert.Contains("1. Bilal Khan — sale Rs.9,000 · discount Rs.1,800 · return 1 · khalis fayda Rs.1,600", ranking);
        Assert.Contains("2. Sara — sale Rs.5,400 · discount Rs.0 · return 0 · khalis fayda Rs.1,800", ranking);
        Assert.DoesNotContain("Hina", ranking);

        var one = await AskAsync("customer Bilal Khan ne pichle ek saal mein mujhe kitna khalis munafa kama kar diya");
        Assert.Contains("Orders: 3 · sale Rs.9,000", one);
        Assert.Contains("Munafa (discount ke baad): Rs.1,800", one);
        Assert.Contains("Khalis fayda: Rs.1,600", one);
    }

    [Fact]
    public async Task Retention_CountsHowManyFirstTimersOrderedAgain()
    {
        await SetupAsync();
        var now = DateTime.UtcNow;
        var c1 = await CustomerAsync("C1"); var c2 = await CustomerAsync("C2"); var c3 = await CustomerAsync("C3"); var c4 = await CustomerAsync("C4"); var c5 = await CustomerAsync("C5");
        await SaleAsync(c1, now.AddDays(-100)); await SaleAsync(c1, now.AddDays(-50));
        await SaleAsync(c2, now.AddDays(-90));
        await SaleAsync(c3, now.AddDays(-60)); await SaleAsync(c3, now.AddDays(-10));
        await SaleAsync(c4, now.AddDays(-5));
        await SaleAsync(c5, now.AddDays(-300)); await SaleAsync(c5, now.AddDays(-5));          // first order is outside the 6 months

        var reply = await AskAsync("pichle 6 mahine mein jin customers ne pehli baar kharidari ki unmein se kitne fisad ne dobara order diya");

        Assert.Contains("Pehli baar kharidne wale: 4", reply);
        Assert.Contains("Dusra order dene wale: 2 (50%)", reply);
        Assert.Contains("(3): 2 (66.7%)", reply);
    }

    [Fact]
    public async Task VipShare_TopTenPercentOfCustomers()
    {
        await SetupAsync();
        var now = DateTime.UtcNow.AddDays(-3);
        var big = await CustomerAsync("Whale");
        await SaleAsync(big, now, price: 10000);
        for (var i = 0; i < 9; i++) await SaleAsync(await CustomerAsync("C" + i), now, price: 1000);

        var reply = await AskAsync("mere top 10% vip customers ka meri kul sale mein hissa kitna hai");

        Assert.Contains("Kul customers: 10", reply);
        Assert.Contains("Top 10% (1 customers): Rs.10,000 = sale ka 52.6%", reply);
        Assert.Contains("80% sirf 7 customers (70%)", reply);
    }

    [Fact]
    public async Task VelocityDrop_ListsProductsWhoseSalesFellByHalf()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var shawl = await ProductAsync("Shawl", 3500, cost: 2500, stock: 12);
        var kurti = await ProductAsync("Kurti", 1800);
        var now = DateTime.UtcNow;
        await SaleAsync(sara, now.AddDays(-20), shawl, 8); await SaleAsync(sara, now.AddDays(-3), shawl, 2);
        await SaleAsync(sara, now.AddDays(-20), kurti, 6); await SaleAsync(sara, now.AddDays(-3), kurti, 6);

        var reply = await AskAsync("un products ki list do jin ki sales velocity pichle 15 din mein achanak 50% gir gayi hai");

        Assert.Contains("1. Shawl — 8 → 2 piece (75% kam)", reply);
        Assert.Contains("stock 12", reply);
        Assert.DoesNotContain("Kurti", reply);
    }

    [Fact]
    public async Task FastMoving_SuggestsAQuantityFromTheDailyPace()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var polo = await ProductAsync("Polo Shirt", 500, cost: 300, stock: 4);
        await SaleAsync(sara, DateTime.UtcNow.AddDays(-5), polo, 30);

        var reply = await AskAsync("mera kaun sa maal fast moving hai aur mujhe uska kitna stock advance book karwana chahiye");

        Assert.Contains("1. Polo Shirt — roz ~1 bikta hai · stock 4", reply);
        Assert.Contains("14 din ke liye ~14 piece chahiye → mangwayein ~10", reply);
    }

    [Fact]
    public async Task DaysOnHand_IsAllStockOverTheCombinedPace()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var polo = await ProductAsync("Polo Shirt", 500, cost: 300, stock: 10);
        var lawn = await ProductAsync("Lawn Suit", 3500, cost: 2500, stock: 100);
        await ProductAsync("Tea", 100, cost: 10, stock: 5);
        await SaleAsync(sara, DateTime.UtcNow.AddDays(-5), polo, 30);       // 1 a day
        await SaleAsync(sara, DateTime.UtcNow.AddDays(-5), lawn, 3);        // 0.1 a day

        var reply = await AskAsync("agar maujooda raftar se bikta raha to mera sara stock kitne dino mein saaf ho jayega");

        Assert.Contains("110 piece, roz ~1.1 bikte hain → sara maal ~100 din", reply);
        Assert.Contains("1 products (Rs.50 ki cost)", reply);
    }

    [Fact]
    public async Task OutOfStockThatSold_IsListed_AndTheBotSaysItCannotSeeBuyerChats()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var polo = await ProductAsync("Polo Shirt", 500, stock: 0);
        await ProductAsync("Rice", 100, stock: 0);
        await SaleAsync(sara, DateTime.UtcNow.AddDays(-5), polo, 5);

        var reply = await AskAsync("kin products ka stock out of stock hai lekin gahak abhi tak unka puch rahe hain");

        Assert.Contains("1. Polo Shirt — stock khatam; pichle 30 din 5 bika (~Rs.2,500)", reply);
        Assert.DoesNotContain("Rice", reply);
        Assert.Contains("chat nahi dekhta", reply);
    }

    [Fact]
    public async Task MarginDrop_ComparesAverageMarginOfTwoMonths()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var kurti = await ProductAsync("Kurti", 1800, cost: 1000);
        var now = DateTime.UtcNow;
        await SaleAsync(sara, now.AddDays(-45), kurti, 2, unitCost: 1000);
        await SaleAsync(sara, now.AddDays(-5), kurti, 2, unitCost: 1400);

        var reply = await AskAsync("pichle 30 din mein kaun se 5 products ka profit margin lagat barhne ki wajah se sab se zyada gira hai");

        Assert.Contains("1. Kurti — margin 44.4% → 22.2%; cost Rs.1,000 → Rs.1,400", reply);
    }

    [Fact]
    public async Task CostShock_ShowsTheBreakEvenBeforeAndAfter()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var kurti = await ProductAsync("Kurti", 1800, cost: 1200, category: "Kapron");
        var rice = await ProductAsync("Rice", 500, cost: 400, category: "Grocery");
        var now = DateTime.UtcNow;
        await SaleAsync(sara, now.AddDays(-5), kurti, 10);                       // revenue 18000, cost 12000
        await SaleAsync(sara, now.AddDays(-5), rice, 100);                       // different category: left out when kapron is named
        _db.Expenses.Add(new Expense { SellerId = _seller.Id, Amount = 4000, Category = "rent", CreatedAt = now.AddDays(-3) });
        await _db.SaveChangesAsync();

        var reply = await AskAsync("agar kapron ki cost 15% barh jaye aur main qeemat na barhaun to mera break even kahan jayega");

        Assert.Contains("Kapron", reply);
        Assert.Contains("sale Rs.18,000, cost Rs.12,000, margin 33.3%", reply);
        Assert.Contains("kharche nikalne ke liye mahine ki sale: Rs.12,000", reply);
        Assert.Contains("margin 23.3% → kharche nikalne ke liye sale: Rs.17,143", reply);
        Assert.Contains("Munafa Rs.6,000 → Rs.4,200", reply);
    }

    [Fact]
    public async Task CostDrop_FindsMarginThatRoseWithAnUnchangedPrice()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var kurti = await ProductAsync("Kurti", 1800, cost: 1000);        // cost is now 1000
        var tee = await ProductAsync("Tee", 300, cost: 100);              // unchanged cost
        var now = DateTime.UtcNow;
        await SaleAsync(sara, now.AddDays(-20), kurti, 1, unitCost: 1200);
        await SaleAsync(sara, now.AddDays(-20), tee, 1, unitCost: 100);

        var reply = await AskAsync("kya koi product hai jis ki cost kam hui hai lekin maine rate purana hi rakha hua hai");

        Assert.Contains("1. Kurti — cost Rs.1,200 → Rs.1,000, price wohi Rs.1,800", reply);
        Assert.Contains("margin 33.3% → 44.4%", reply);
        Assert.DoesNotContain("Tee", reply);
    }

    [Fact]
    public async Task CourierHoldingCash_CanBeLimitedToOneCourier()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var at = DateTime.UtcNow.AddDays(-8);
        await SaleAsync(sara, at, price: 2000, courier: "TCS", deliveredAt: at.AddDays(2));
        await SaleAsync(sara, at, price: 700, courier: "Leopards", deliveredAt: at.AddDays(2));

        var reply = await AskAsync("TCS ke paas mere kitne orders delivered hain lekin unka cash jama nahi hua");

        Assert.Contains("TCS ke paas Delivered magar paisa nahi mila — 1 orders, Rs.2,000", reply);
        Assert.DoesNotContain("Rs.700", reply);
    }

    [Fact]
    public async Task FailedDeliveries_RanksCitiesByReturnedShare()
    {
        await SetupAsync();
        var at = DateTime.UtcNow.AddDays(-10);
        var kcust = await CustomerAsync("Ali", "Karachi");
        var lcust = await CustomerAsync("Sara", "Lahore");
        for (var i = 0; i < 2; i++) await SaleAsync(kcust, at, shippedAt: at, status: OrderStatus.Returned);
        for (var i = 0; i < 2; i++) await SaleAsync(kcust, at, shippedAt: at, deliveredAt: at.AddDays(2));
        for (var i = 0; i < 5; i++) await SaleAsync(lcust, at, shippedAt: at, deliveredAt: at.AddDays(2));

        var reply = await AskAsync("kaun se shehar mein bheje gaye parcel sab se zyada fail hote hain");

        Assert.Contains("1. Karachi — 2 / 4 wapas (50%)", reply);
        Assert.DoesNotContain("Lahore", reply);
    }

    [Fact]
    public async Task ReturnedStock_CountsUnitsBackInStockAndTheLossesLogged()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var suit = await ProductAsync("Lawn Suit", 3500, cost: 2500, stock: 10);
        var plain = await ProductAsync("Tee", 300);                               // not tracked
        var now = DateTime.UtcNow;
        await SaleAsync(sara, now.AddDays(-9), suit, 3, status: OrderStatus.Returned, shippedAt: now.AddDays(-8), returnedAt: now.AddDays(-3));
        await SaleAsync(sara, now.AddDays(-9), plain, 2, status: OrderStatus.Returned, shippedAt: now.AddDays(-8), returnedAt: now.AddDays(-3));
        _db.Losses.Add(new Loss { SellerId = _seller.Id, ProductName = "Lawn Suit", Quantity = 1, UnitCost = 2500, Reason = "damaged", CreatedAt = now.AddDays(-2) });
        await _db.SaveChangesAsync();

        var reply = await AskAsync("pichle 30 din mein return aane wale suit catalog mein wapas kitna stock chadha aur kitna maal kharab ya gum hua");

        Assert.Contains("Wapas aaye orders: 2 (5 piece)", reply);
        Assert.Contains("track hone wale products): 3 piece", reply);
        Assert.Contains("Stock track na hone wale: 2 piece", reply);
        Assert.Contains("Kharab / gum likha gaya: 1 piece, cost Rs.2,500", reply);
    }

    [Fact]
    public async Task DeliveryCharge_ShowsHowOrdersAtEachChargeFared_NotAForecast()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var at = DateTime.UtcNow.AddDays(-10);
        for (var i = 0; i < 4; i++) await SaleAsync(sara, at, delivery: 200);
        await SaleAsync(sara, at, delivery: 200, status: OrderStatus.Cancelled);
        for (var i = 0; i < 3; i++) await SaleAsync(sara, at, delivery: 250);

        var reply = await AskAsync("agar main delivery charges 200 se 250 kar dun to kya orders drop ho jayenge");

        Assert.Contains("Delivery Rs.200: 5 orders · cancel 1 (20%)", reply);
        Assert.Contains("Delivery Rs.250: 3 orders · cancel 0 (0%)", reply);
        Assert.Contains("bot nahi jaanta", reply);
    }

    [Fact]
    public async Task CashFlow_ComparesPaidOrdersWithExpenses()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var at = DateTime.UtcNow.AddMinutes(-1);
        await SaleAsync(sara, at, price: 1000, payment: PaymentStatus.Paid, paidAt: at);
        await SaleAsync(sara, at, price: 1000, payment: PaymentStatus.Paid, paidAt: at);
        await SaleAsync(sara, at, price: 3000, method: OrderPaymentMethod.Manual, payment: PaymentStatus.Paid, paidAt: at);
        _db.Expenses.Add(new Expense { SellerId = _seller.Id, Amount = 1500, Category = "rent", CreatedAt = at });
        await _db.SaveChangesAsync();

        var reply = await AskAsync("kya mere kharche meri cash sales se zyada ho rahe hain net cash flow positive hai ya negative");

        Assert.Contains("Kul wasooli (paid orders): Rs.5,000", reply);
        Assert.Contains("cash (COD / dukaan par): Rs.2,000", reply);
        Assert.Contains("Net cash flow (kul wasooli − kharche): Rs.3,500", reply);
        Assert.Contains("Sirf cash wasooli − kharche: Rs.500", reply);
    }

    [Fact]
    public async Task CashForecast_ProjectsPaceOverTheRequestedDays()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var at = DateTime.UtcNow.AddDays(-9);
        await SaleAsync(sara, at, price: 3000, payment: PaymentStatus.Paid, paidAt: at);
        _db.Expenses.Add(new Expense { SellerId = _seller.Id, Amount = 600, Category = "rent", CreatedAt = at });
        await _db.SaveChangesAsync();

        var reply = await AskAsync("agle 20 din mein kitna cash aayega");

        Assert.Contains("Agle 20 din ka cash andaza", reply);
        Assert.Contains("Pichle 10 din", reply);
        Assert.Contains("Wasooli ~Rs.6,000", reply);
        Assert.Contains("Kharcha ~Rs.1,200", reply);
        Assert.Contains("Net cash ~Rs.4,800", reply);
    }

    [Fact]
    public async Task CashPlan_KeepsTheSupplierPaymentPlusDailyExpensesInTheDrawer()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var now = DateTime.UtcNow;
        _db.CashDrawerSessions.Add(new CashDrawerSession { SellerId = _seller.Id, OpenedAt = now.AddHours(-3), OpeningAmount = 1000 });
        _db.Expenses.Add(new Expense { SellerId = _seller.Id, Amount = 1400, Category = "petrol", CreatedAt = now.AddDays(-5) });
        await _db.SaveChangesAsync();
        await SaleAsync(sara, now.AddHours(-2), price: 4000, payment: PaymentStatus.Paid, paidAt: now.AddHours(-1));

        var reply = await AskAsync("aaj raat drawer band karne ke baad kal supplier ko 3000 dene hain kitna cash chhodna aur kitna bank mein jama karwana chahiye");

        Assert.Contains("Ab drawer mein hona chahiye: Rs.5,000", reply);
        Assert.Contains("Drawer mein rakhein ~Rs.3,100, baqi ~Rs.1,900 bank mein jama", reply);
    }

    [Fact]
    public async Task PromoReturn_SplitsNewCustomersFromOnesWhoWouldHaveBoughtAnyway()
    {
        await SetupAsync();
        await ProductAsyncDiscount("EID10", DiscountType.Percent, 10);
        var fresh = await CustomerAsync("Fresh");
        var regular = await CustomerAsync("Regular");
        var now = DateTime.UtcNow;
        await SaleAsync(regular, now.AddDays(-20), price: 2000);                                         // bought 20 days before the code
        await SaleAsync(fresh, now.AddDays(-5), price: 1000, discount: 100, code: "EID10");
        await SaleAsync(regular, now.AddDays(-4), price: 2000, discount: 200, code: "EID10");

        var reply = await AskAsync("jo code EID10 maine chalaya tha us se sale ka munafa barha ya gahakon ne wohi cheezein waisay bhi khareedni thin");

        Assert.Contains("EID10", reply);
        Assert.Contains("Orders: 2 · sale Rs.2,700 · diya gaya discount Rs.300", reply);
        Assert.Contains("Bilkul naye customers: 1", reply);
        Assert.Contains("pichle 60 din mein khareed chuke customers: 1", reply);
        Assert.Contains("Rs.200", reply);
        Assert.Contains("andaza hai, sabit nahi", reply);
    }

    private async Task ProductAsyncDiscount(string code, DiscountType type, decimal value)
    {
        _db.Discounts.Add(new Discount { SellerId = _seller.Id, Code = code, Type = type, Value = value });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task PromoNewCustomers_GroupsFirstOrdersByDiscountType()
    {
        await SetupAsync();
        await ProductAsyncDiscount("PCT10", DiscountType.Percent, 10);
        await ProductAsyncDiscount("FLAT50", DiscountType.Flat, 50);
        var now = DateTime.UtcNow.AddDays(-5);
        await SaleAsync(await CustomerAsync("A"), now, price: 1000, discount: 100, code: "PCT10");
        await SaleAsync(await CustomerAsync("B"), now, price: 1000, discount: 100, code: "PCT10");
        await SaleAsync(await CustomerAsync("C"), now, price: 1000, discount: 50, code: "FLAT50");
        await SaleAsync(await CustomerAsync("D"), now, price: 1000);

        var reply = await AskAsync("kis discount promotion ne sab se zyada naye gahak khenche");

        Assert.Contains("Percent discount: 2 naye customers", reply);
        Assert.Contains("Flat discount: 1 naye customers", reply);
        Assert.Contains("1. PCT10 (Percent discount) — 2", reply);
    }

    [Fact]
    public async Task PromoLoyalty_SeparatesFullPriceBuyersFromOfferOnlyBuyers()
    {
        await SetupAsync();
        await ProductAsyncDiscount("PCT10", DiscountType.Percent, 10);
        var now = DateTime.UtcNow;
        var s1 = await CustomerAsync("S1"); var s2 = await CustomerAsync("S2"); var s3 = await CustomerAsync("S3");
        await SaleAsync(s1, now.AddDays(-50), price: 1000, discount: 100, code: "PCT10"); await SaleAsync(s1, now.AddDays(-20), price: 1000);
        await SaleAsync(s2, now.AddDays(-50), price: 1000, discount: 100, code: "PCT10"); await SaleAsync(s2, now.AddDays(-20), price: 1000, discount: 100, code: "PCT10");
        await SaleAsync(s3, now.AddDays(-50), price: 1000, discount: 100, code: "PCT10");

        var reply = await AskAsync("kya discount wale gahak dobara pura rate de kar kharidte hain ya sirf sale ka intezar karte hain");

        Assert.Contains("Dobara pura rate de kar kharida: 1 (33.3%)", reply);
        Assert.Contains("Sirf discount par wapas aaye: 1 (33.3%)", reply);
        Assert.Contains("Dobara aaye hi nahi: 1 (33.3%)", reply);
    }

    [Fact]
    public async Task EidStock_ComparesLastEidWithTheMonthBefore()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var rice = await ProductAsync("Rice", 500, cost: 400, stock: 40, category: "Grocery", createdDaysAgo: 900);
        var calendar = new System.Globalization.UmAlQuraCalendar();
        DateTime EidStart(int gregorianYear)
        {
            for (var y = calendar.GetYear(new DateTime(gregorianYear, 1, 1)) - 1; y <= calendar.GetYear(new DateTime(gregorianYear, 12, 31)) + 1; y++)
            {
                var d = calendar.ToDateTime(y, 10, 1, 0, 0, 0, 0);
                if (d.Year == gregorianYear) return d;
            }
            throw new InvalidOperationException();
        }
        var now = DateTime.UtcNow;
        var eid = EidStart(now.Year);
        if (eid.AddDays(3) > now) eid = EidStart(now.Year - 1);
        await SaleAsync(sara, eid.AddDays(-5), rice, 26);        // 26 in the 13-day window = 2 a day
        await SaleAsync(sara, eid.AddDays(-25), rice, 13);       // 13 in the 30 days before the window = 0.43 a day
        await SaleAsync(sara, now.AddDays(-5), rice, 30);

        var reply = await AskAsync("agle mahine eid hai pichle saal ka data dekh kar batao grocery ka stock kitna fisad barhana chahiye aur kaun se 3 items pehle short honge");

        Assert.Contains("Pichli Eid", reply);
        Assert.Contains("Grocery", reply);
        Assert.Contains("Roz ke piece: 2", reply);
        Assert.Contains("Rice (26)", reply);
        Assert.Contains("Isi raftaar par pehle kam parne wale", reply);
    }

    [Fact]
    public async Task DailySaleForFixedCosts_UsesRentAndBillsOverTheMargin()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var kurti = await ProductAsync("Kurti", 1800, cost: 1200);
        var now = DateTime.UtcNow;
        await SaleAsync(sara, now.AddDays(-45), kurti, 100);                         // 90-day margin 33.3%
        _db.Expenses.AddRange(
            new Expense { SellerId = _seller.Id, Amount = 30000, Category = "kiraya", CreatedAt = now.AddDays(-40) },
            new Expense { SellerId = _seller.Id, Amount = 6000, Category = "bijli", CreatedAt = now.AddDays(-40) },
            new Expense { SellerId = _seller.Id, Amount = 99999, Category = "petrol", CreatedAt = now.AddDays(-40) });
        await _db.SaveChangesAsync();

        var reply = await AskAsync("pichle 3 mahine ke trend se kiraya aur bijli ka bill nikalne ke liye roz kam se kam kitni sale karni paregi");

        Assert.Contains("(mahine ki average, pichle 3 mahine): Rs.12,000", reply);
        Assert.Contains("Aap ka margin (pichle 3 mahine): 33.3%", reply);
        Assert.Contains("Kam az kam sale: Rs.36,000 mahina = Rs.1,200 roz", reply);
    }

    [Fact]
    public async Task Declining_ListsProductsThatFellInThreeConsecutiveMonths()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var shawl = await ProductAsync("Shawl", 3500, stock: 9);
        var kurti = await ProductAsync("Kurti", 1800);
        var now = DateTime.UtcNow;
        await SaleAsync(sara, now.AddDays(-80), shawl, 10); await SaleAsync(sara, now.AddDays(-50), shawl, 6); await SaleAsync(sara, now.AddDays(-10), shawl, 2);
        await SaleAsync(sara, now.AddDays(-80), kurti, 5); await SaleAsync(sara, now.AddDays(-50), kurti, 5); await SaleAsync(sara, now.AddDays(-10), kurti, 5);

        var reply = await AskAsync("kaun sa product apne life cycle ke aakhri marhale par hai aur kya mujhe naya order rok dena chahiye");

        Assert.Contains("1. Shawl — 10 → 6 → 2 piece", reply);
        Assert.Contains("stock 9", reply);
        Assert.DoesNotContain("Kurti", reply);
    }

    [Fact]
    public async Task PriceMix_SaysWhetherPremiumItemsGainedShare()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        var lawn = await ProductAsync("Lawn Suit", 3500);
        var kurti = await ProductAsync("Kurti", 1000);
        var tee = await ProductAsync("Tee", 300);
        var now = DateTime.UtcNow;
        await SaleAsync(sara, now.AddDays(-45), lawn, 1); await SaleAsync(sara, now.AddDays(-45), tee, 10);       // premium 3500 / 6500
        await SaleAsync(sara, now.AddDays(-5), lawn, 3); await SaleAsync(sara, now.AddDays(-5), tee, 4);          // premium 10500 / 11700

        var reply = await AskAsync("kya customers ab mehnge lawn suit ki taraf ja rahe hain ya sasti kurtion ki sale barh rahi hai trend kis taraf shift ho raha hai");

        Assert.Contains("Pehle 30 din: mehnge maal ka hissa 53.8% sale (1 piece)", reply);
        Assert.Contains("Pichle 30 din: mehnge maal ka hissa 89.7% sale (3 piece)", reply);
        Assert.Contains("Mehnge maal ki taraf rujhan hai", reply);
        Assert.Contains("Sab se zyada barha: Lawn Suit (+2 piece)", reply);
        Assert.Contains("Sab se zyada ghata: Tee (-6 piece)", reply);
    }

    [Fact]
    public async Task Audit_FlagsLockedCapitalAndOldMoney_AndOffersTheNextTaps()
    {
        await SetupAsync();
        var sara = await CustomerAsync("Sara");
        await ProductAsync("Old Shawl", 3500, cost: 2500, stock: 4, createdDaysAgo: 200);
        var kurti = await ProductAsync("Kurti", 1800, cost: 1750);          // thin margin
        await SaleAsync(sara, DateTime.UtcNow.AddDays(-45), kurti, 1);       // unpaid 45 days

        var reply = await AskAsync("mere business ka audit karo");

        Assert.Contains("Business audit", reply);
        Assert.Contains("⚠️ Sarmaya phansa: Rs.10,000 ka maal 90 din se nahi bika (1 products)", reply);
        Assert.Contains("⚠️ Bazar mein phansa paisa: Rs.1,800 (1 orders), us mein Rs.1,800 30 din se purana", reply);
        Assert.Contains("⚠️ 1 product(s) ka margin 20% se kam", reply);
    }
}
