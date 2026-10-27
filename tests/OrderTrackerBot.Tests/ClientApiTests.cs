using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Api.Admin;
using OrderTrackerBot.Api.Client;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;

namespace OrderTrackerBot.Tests;

public sealed class ClientApiTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public ClientApiTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void Tokens_are_prefixed_random_and_stored_only_as_a_hash()
    {
        var a = ClientTokens.Create();
        var b = ClientTokens.Create();

        Assert.StartsWith(ClientTokens.Prefix, a);
        Assert.NotEqual(a, b);
        Assert.Equal(ClientTokens.Hash(a), ClientTokens.Hash(a));
        Assert.Equal(64, ClientTokens.Hash(a).Length);
        Assert.DoesNotContain(a, ClientTokens.Hash(a));
    }

    [Fact]
    public async Task Auth_filter_accepts_only_an_active_sellers_token()
    {
        const string token = "otc_active_seller_token";
        const string disabledToken = "otc_disabled_seller_token";
        var active = await SellerAsync("923000000004", token: token);
        await SellerAsync("923000000005", SellerStatus.Disabled, disabledToken);
        var filter = new ClientAuthFilter(_db);

        var ok = FilterContext($"Bearer {token}");
        await filter.OnAuthorizationAsync(ok);
        Assert.Null(ok.Result);
        Assert.Equal(active.Id, (int)ok.HttpContext.Items[ClientAuthFilter.SellerIdItem]!);

        foreach (var header in new string?[] { null, "Bearer wrong", "Basic abc", $"Bearer {disabledToken}" })
        {
            var rejected = FilterContext(header);
            await filter.OnAuthorizationAsync(rejected);
            Assert.IsType<UnauthorizedObjectResult>(rejected.Result);
        }
    }

    [Fact]
    public async Task Dashboard_counts_only_sales_and_compares_with_the_previous_period()
    {
        var seller = await SellerAsync("923000000001");
        var customer = await CustomerAsync(seller.Id, "Ali");
        var product = await ProductAsync(seller.Id, "Shirt", "Clothing", 500m);
        var today = AdminDateRange.Today();
        Assert.True(AdminDateRange.TryResolve(today.AddDays(-1).ToString(AdminDateRange.DateFormat), today.ToString(AdminDateRange.DateFormat), 2, out var range, out _));

        await OrderAsync(seller.Id, customer.Id, product, OrderStatus.Delivered, 1000m, DateTime.UtcNow);
        await OrderAsync(seller.Id, customer.Id, product, OrderStatus.Cancelled, 500m, DateTime.UtcNow);
        await OrderAsync(seller.Id, customer.Id, product, OrderStatus.Returned, 300m, DateTime.UtcNow);
        await OrderAsync(seller.Id, customer.Id, product, OrderStatus.Delivered, 500m, DateTime.UtcNow.AddDays(-3));

        var dash = await new ClientQueries(_db).DashboardAsync(seller.Id, range, default);

        Assert.Equal(1000m, dash.Kpis.Revenue);
        Assert.Equal(1, dash.Kpis.Orders);
        Assert.Equal(100.0, dash.Kpis.RevenueDeltaPct);
        Assert.Equal(1, dash.StatusCounts["Cancelled"]);
        Assert.Equal(1, dash.StatusCounts["Returned"]);
        Assert.Equal(2, dash.RevenueByDay.Count);
        Assert.Equal(1000m, dash.RevenueByDay.Sum(d => d.Revenue));
    }

    [Fact]
    public async Task Queries_never_return_another_sellers_rows()
    {
        var mine = await SellerAsync("923000000002");
        var theirs = await SellerAsync("923000000003");
        var theirCustomer = await CustomerAsync(theirs.Id, "Other");
        var theirProduct = await ProductAsync(theirs.Id, "Secret", null, 10m);
        var theirOrder = await OrderAsync(theirs.Id, theirCustomer.Id, theirProduct, OrderStatus.Pending, 10m, DateTime.UtcNow);

        var queries = new ClientQueries(_db);
        Assert.Null(await queries.OrderAsync(mine.Id, theirOrder.Id, default));
        Assert.Empty((await queries.ProductsAsync(mine.Id, null, null, "all", 1, 25, default)).Items);
        Assert.Empty((await queries.CustomersAsync(mine.Id, null, 1, 25, default)).Items);
    }

    [Fact]
    public async Task Customers_hide_deleted_and_total_only_counted_sales()
    {
        var seller = await SellerAsync("923000000006");
        var ali = await CustomerAsync(seller.Id, "Ali");
        var product = await ProductAsync(seller.Id, "Soap", null, 100m);
        await OrderAsync(seller.Id, ali.Id, product, OrderStatus.Delivered, 300m, DateTime.UtcNow);
        await OrderAsync(seller.Id, ali.Id, product, OrderStatus.Cancelled, 900m, DateTime.UtcNow);
        var gone = await CustomerAsync(seller.Id, "Gone");
        gone.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var page = await new ClientQueries(_db).CustomersAsync(seller.Id, null, 1, 25, default);

        var only = Assert.Single(page.Items);
        Assert.Equal("Ali", only.Name);
        Assert.Equal(2, only.OrderCount);
        Assert.Equal(300m, only.TotalSpent);
    }

    [Fact]
    public async Task Products_read_subcategory_from_attributes_json()
    {
        var seller = await SellerAsync("923000000007");
        await ProductAsync(seller.Id, "Shirt", "Clothing", 500m, attributesJson: "{\"subcategory\":\"Shirts\",\"fabric\":\"cotton\"}");

        var page = await new ClientQueries(_db).ProductsAsync(seller.Id, null, null, "active", 1, 25, default);

        Assert.Equal("Shirts", Assert.Single(page.Items).Subcategory);
    }

    [Fact]
    public async Task Categories_group_sales_by_category_and_subcategory()
    {
        var seller = await SellerAsync("923000000008");
        var customer = await CustomerAsync(seller.Id, "Ali");
        var shirt = await ProductAsync(seller.Id, "Shirt", "Clothing", 1000m, attributesJson: "{\"subcategory\":\"Shirts\"}");
        var gift = await ProductAsync(seller.Id, "Gift wrap", null, 100m);
        await OrderAsync(seller.Id, customer.Id, shirt, OrderStatus.Delivered, 1000m, DateTime.UtcNow);
        await OrderAsync(seller.Id, customer.Id, gift, OrderStatus.Delivered, 100m, DateTime.UtcNow);

        var categories = await new ClientQueries(_db).CategoriesAsync(seller.Id, default);

        var clothing = categories.Single(c => c.Name == "Clothing");
        Assert.Equal(1000m, clothing.Revenue);
        Assert.Equal("Shirts", Assert.Single(clothing.Subcategories).Name);
        Assert.Equal(100m, categories.Single(c => c.Name == "Uncategorized").Revenue);
    }

    [Fact]
    public async Task Order_detail_includes_lines_with_totals()
    {
        var seller = await SellerAsync("923000000009");
        var customer = await CustomerAsync(seller.Id, "Ali");
        var product = await ProductAsync(seller.Id, "Soap", null, 100m);
        var order = await OrderAsync(seller.Id, customer.Id, product, OrderStatus.Pending, 200m, DateTime.UtcNow, quantity: 2);

        var detail = await new ClientQueries(_db).OrderAsync(seller.Id, order.Id, default);

        Assert.NotNull(detail);
        var line = Assert.Single(detail!.Items);
        Assert.Equal(2, line.Quantity);
        Assert.Equal(200m, line.LineTotal);
        Assert.Equal("Pending", detail.Status);
    }

    [Fact]
    public async Task Orders_filter_by_status_and_search_by_client_name_or_number()
    {
        var seller = await SellerAsync("923000000011");
        var ali = await CustomerAsync(seller.Id, "Ali");
        var sara = await CustomerAsync(seller.Id, "Sara");
        var product = await ProductAsync(seller.Id, "Soap", null, 100m);
        var aliPending = await OrderAsync(seller.Id, ali.Id, product, OrderStatus.Pending, 100m, DateTime.UtcNow);
        await OrderAsync(seller.Id, sara.Id, product, OrderStatus.Delivered, 100m, DateTime.UtcNow);
        var queries = new ClientQueries(_db);

        var pending = await queries.OrdersAsync(seller.Id, OrderStatus.Pending, null, null, null, 1, 25, default);
        Assert.Equal(aliPending.Id, Assert.Single(pending.Items).Id);

        var byName = await queries.OrdersAsync(seller.Id, null, null, null, "sara", 1, 25, default);
        Assert.Equal("Sara", Assert.Single(byName.Items).CustomerName);

        var byNumber = await queries.OrdersAsync(seller.Id, null, null, null, $"#{aliPending.Id}", 1, 25, default);
        Assert.Equal(aliPending.Id, Assert.Single(byNumber.Items).Id);

        var paged = await queries.OrdersAsync(seller.Id, null, null, null, null, 2, 1, default);
        Assert.Equal(2, paged.TotalCount);
        Assert.Single(paged.Items);
    }

    [Fact]
    public async Task Setup_reports_missing_steps_until_they_are_done()
    {
        var seller = await SellerAsync("923000000010");
        var queries = new ClientQueries(_db);

        var before = await queries.SetupAsync(seller.Id, default);
        Assert.Equal(4, before!.StepsDone);
        Assert.Equal(7, before.StepsTotal);
        Assert.False(before.Steps.Single(s => s.Key == "product").Done);

        await ProductAsync(seller.Id, "Shirt", "Clothing", 500m);
        var after = await queries.SetupAsync(seller.Id, default);
        Assert.True(after!.Steps.Single(s => s.Key == "product").Done);
    }

    // ----- helpers -----

    private static AuthorizationFilterContext FilterContext(string? authorization)
    {
        var http = new DefaultHttpContext();
        if (authorization is not null) http.Request.Headers["Authorization"] = authorization;
        return new AuthorizationFilterContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>());
    }

    [Fact]
    public async Task Analysis_uses_the_dashboard_rules_and_adds_profit_payment_mix_and_repeat_rate()
    {
        var seller = await SellerAsync("923000000002");
        var ali = await CustomerAsync(seller.Id, "Ali");
        var sara = await CustomerAsync(seller.Id, "Sara");
        var shirt = await ProductAsync(seller.Id, "Shirt", "Clothing", 500m);
        var today = AdminDateRange.Today();
        Assert.True(AdminDateRange.TryResolve(today.AddDays(-1).ToString(AdminDateRange.DateFormat), today.ToString(AdminDateRange.DateFormat), 2, out var range, out _));

        var cashSale = await OrderAsync(seller.Id, ali.Id, shirt, OrderStatus.Delivered, 1000m, DateTime.UtcNow);
        cashSale.Items.Single().UnitCost = 300m;
        var manualSale = await OrderAsync(seller.Id, ali.Id, shirt, OrderStatus.Delivered, 600m, DateTime.UtcNow);
        manualSale.Items.Single().UnitCost = 200m;
        manualSale.PaymentMethod = OrderPaymentMethod.Manual;
        await OrderAsync(seller.Id, sara.Id, shirt, OrderStatus.Cancelled, 500m, DateTime.UtcNow);
        await OrderAsync(seller.Id, sara.Id, shirt, OrderStatus.Delivered, 400m, DateTime.UtcNow.AddDays(-1));
        await OrderAsync(seller.Id, ali.Id, shirt, OrderStatus.Delivered, 500m, DateTime.UtcNow.AddDays(-3));
        await _db.SaveChangesAsync();

        var report = await new ClientQueries(_db).AnalysisAsync(seller.Id, range, default);

        Assert.Equal(2000m, report.Kpis.Revenue);
        Assert.Equal(3, report.Kpis.Orders);
        Assert.Equal(2, report.Kpis.ActiveCustomers);
        Assert.Equal(300.0, report.Kpis.RevenueDeltaPct);
        Assert.Equal(2, report.Daily.Count);
        Assert.Equal(2000m, report.Daily.Sum(d => d.Revenue));
        Assert.Equal(1, report.StatusCounts["Cancelled"]);
        Assert.Equal(3, report.StatusCounts["Delivered"]);
        var product = Assert.Single(report.TopProducts);
        Assert.Equal(("Shirt", 3, 2000m, 1100m), (product.ProductName, product.Units, product.Revenue, product.Profit));
        Assert.Equal(1100m, report.GrossProfit);
        Assert.Equal(["Cod", "Manual"], report.PaymentMix.Select(p => p.Method));
        Assert.Equal(50.0, report.RepeatCustomerRate);
        Assert.Equal(2, report.NewCustomers);
    }

    [Fact]
    public async Task Reports_follow_the_dashboard_rules_and_leave_out_cancelled_orders()
    {
        var seller = await SellerAsync("923000000003");
        var ali = await CustomerAsync(seller.Id, "Ali");
        var sara = await CustomerAsync(seller.Id, "Sara");
        var shirt = await ProductAsync(seller.Id, "Shirt", "Clothing", 500m);
        var today = AdminDateRange.Today();
        var day = today.ToString(AdminDateRange.DateFormat);
        Assert.True(AdminDateRange.TryResolve(day, day, 1, out var range, out _));

        var cod = await OrderAsync(seller.Id, ali.Id, shirt, OrderStatus.Delivered, 1000m, DateTime.UtcNow);
        cod.Subtotal = 800m;
        cod.DeliveryCharge = 200m;
        var manual = await OrderAsync(seller.Id, sara.Id, shirt, OrderStatus.Delivered, 400m, DateTime.UtcNow);
        manual.PaymentMethod = OrderPaymentMethod.Manual;
        manual.PaymentStatus = PaymentStatus.Paid;
        await OrderAsync(seller.Id, sara.Id, shirt, OrderStatus.Cancelled, 900m, DateTime.UtcNow);
        await _db.SaveChangesAsync();
        var queries = new ClientQueries(_db);

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var sales = await queries.ReportAsync(seller.Id, "sales", range, "day", default);
        Assert.Equal(today.ToString("d MMM yyyy", culture), sales!.Rows.Single()["period"]);
        Assert.Equal(2m, sales.Totals["orders"]);
        Assert.Equal(200m, sales.Totals["delivery"]);
        Assert.Equal(1400m, sales.Totals["total"]);

        var byMonth = await queries.ReportAsync(seller.Id, "sales", range, "month", default);
        Assert.Equal(today.ToString("MMMM yyyy", culture), byMonth!.Rows.Single()["period"]);

        var cash = await queries.ReportAsync(seller.Id, "cash", range, "day", default);
        var cashDay = cash!.Rows.Single();
        Assert.Equal(0m, Convert.ToDecimal(cashDay["cod"]));
        Assert.Equal(400m, Convert.ToDecimal(cashDay["transfer"]));
        Assert.Equal(1000m, Convert.ToDecimal(cashDay["due"]));

        var balances = await queries.ReportAsync(seller.Id, "balances", range, "day", default);
        var owed = Assert.Single(balances!.Rows);
        Assert.Equal("Ali", owed["customer"]);
        Assert.Equal(1000m, Convert.ToDecimal(owed["due"]));

        var products = await queries.ReportAsync(seller.Id, "products", range, "day", default);
        Assert.Equal("Clothing", products!.Rows.Single()["category"]);
        Assert.Null(products.Rows.Single()["cost"]);
        Assert.Contains("Profit counts only", products.Note);

        var customers = await queries.ReportAsync(seller.Id, "customers", range, "day", default);
        Assert.Equal("Ali", customers!.Rows[0]["customer"]);
        Assert.Equal(1000m, Convert.ToDecimal(customers.Rows[0]["spent"]));

        var orders = await queries.ReportAsync(seller.Id, "orders", range, "day", default);
        Assert.Equal(2, Convert.ToInt32(orders!.Rows.Single(r => (string)r["status"]! == "Delivered")["orders"]));
        Assert.Equal(1, Convert.ToInt32(orders.Rows.Single(r => (string)r["status"]! == "Cancelled")["orders"]));

        var returns = await queries.ReportAsync(seller.Id, "returns", range, "day", default);
        Assert.Equal(1, Convert.ToInt32(Assert.Single(returns!.Rows)["cancelled"]));

        var inventory = await queries.ReportAsync(seller.Id, "inventory", range, "day", default);
        Assert.Equal("Not tracked", inventory!.Rows.Single()["status"]);

        Assert.Null(await queries.ReportAsync(seller.Id, "payments", range, "day", default));
    }

    private async Task<Seller> SellerAsync(string phone, SellerStatus status = SellerStatus.Active, string? token = null)
    {
        var seller = new Seller
        {
            WhatsAppPhoneNumber = phone,
            BusinessName = "Shop " + phone,
            City = "Lahore",
            BusinessType = "Clothing",
            OnboardingComplete = true,
            Status = status,
            ClientApiTokenHash = token is null ? null : ClientTokens.Hash(token),
            CreatedAt = DateTime.UtcNow.AddDays(-30),
        };
        _db.Sellers.Add(seller);
        await _db.SaveChangesAsync();
        return seller;
    }

    private async Task<Customer> CustomerAsync(int sellerId, string name)
    {
        var customer = new Customer { SellerId = sellerId, Name = name, Phone = "0300" + name.Length, City = "Lahore" };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();
        return customer;
    }

    private async Task<Product> ProductAsync(int sellerId, string name, string? category, decimal price, string? attributesJson = null)
    {
        var product = new Product { SellerId = sellerId, Name = name, Category = category, Price = price, AttributesJson = attributesJson };
        _db.Products.Add(product);
        await _db.SaveChangesAsync();
        return product;
    }

    private async Task<Order> OrderAsync(
        int sellerId, int customerId, Product product, OrderStatus status, decimal total, DateTime createdAt, int quantity = 1)
    {
        var order = new Order
        {
            SellerId = sellerId,
            CustomerId = customerId,
            Status = status,
            Subtotal = total,
            Total = total,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            Items = { new OrderItem { ProductId = product.Id, ProductNameSnapshot = product.Name, UnitPrice = total / quantity, Quantity = quantity } },
        };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();
        return order;
    }
}
