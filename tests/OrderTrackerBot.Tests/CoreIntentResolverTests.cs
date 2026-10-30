using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Application.Intents;
using Xunit;

namespace OrderTrackerBot.Tests;

// Draft: each core intent + slots must resolve to a command the deterministic parser reads as the expected kind.
public class CoreIntentResolverTests
{
    [Theory]
    [InlineData("", "", "", "", "orders today", "OrdersToday")]
    [InlineData("pending", "", "", "", "pending orders", "PendingOrders")]
    [InlineData("unpaid", "", "", "", "unpaid orders", "UnpaidOrders")]
    [InlineData("cod", "", "", "", "cod pending", "CodPending")]
    [InlineData("", "last month", "", "", "orders last month", "OrdersToday")]
    [InlineData("", "", "12", "", "order 12", "OrderDetail")]
    [InlineData("", "", "", "Sara", "Sara ka order", "CustomerOrderLookup")]
    public void OrderQuery_ResolvesToItsCommand(string status, string period, string order, string customer, string expected, string kind)
    {
        var slots = new Dictionary<string, string> { ["status"] = status, ["period"] = period, ["order"] = order, ["customer"] = customer };
        var r = CoreIntentResolver.Resolve("ORDER_QUERY", slots);
        Assert.Equal(expected, r.Command);
        Assert.Equal(kind, CommandParser.TryParse(r.Command!)!.Kind.ToString());
    }

    [Fact]
    public void CreateProduct_WithoutOptionalFields_LeavesNoGap()
    {
        var r = CoreIntentResolver.Resolve(new RouterOutput(CoreIntent.CATALOG_MANAGE, new Dictionary<string, string>
        { ["action"] = "create", ["product"] = "Kurti", ["price"] = "1800", ["cost"] = "", ["stock"] = "", ["category"] = "" }));
        Assert.Equal("Kurti - 1800", r.Command);
        Assert.Equal("AddProduct", CommandParser.TryParse(r.Command!)!.Kind.ToString());
    }

    [Fact]
    public void CreateProduct_WithoutPrice_IsAGapWithAUserMessage()
    {
        var r = CoreIntentResolver.Resolve(new RouterOutput(CoreIntent.CATALOG_MANAGE, new Dictionary<string, string>
        { ["action"] = "create", ["product"] = "Kurti" }));
        Assert.Null(r.Command);
        Assert.False(string.IsNullOrWhiteSpace(r.Gap));
    }

    [Fact]
    public void RouterOutput_ResolvesLikeTheStringForm()
    {
        var r = CoreIntentResolver.Resolve(new RouterOutput(CoreIntent.ORDER_QUERY, new Dictionary<string, string> { ["status"] = "pending" }));
        Assert.Equal("pending orders", r.Command);
    }

    [Fact]
    public void OrderQuery_StatusWithPeriod_IsAGapNotAGuess()
    {
        var r = CoreIntentResolver.Resolve("ORDER_QUERY", new Dictionary<string, string> { ["status"] = "pending", ["period"] = "today" });
        Assert.Null(r.Command);
        Assert.NotNull(r.Gap);
    }

    [Theory]
    [InlineData("show", "catalog", "Catalog")]
    [InlineData("edit_price", "Kurti", "EditProduct")]
    [InlineData("delete", "Kurti", "DeleteProduct")]
    public void CatalogManage_ResolvesToItsCommand(string action, string product, string kind)
    {
        var slots = new Dictionary<string, string> { ["action"] = action, ["product"] = product, ["price"] = "2000" };
        var r = CoreIntentResolver.Resolve("CATALOG_MANAGE", slots);
        Assert.NotNull(r.Command);
        Assert.Equal(kind, CommandParser.TryParse(r.Command!)!.Kind.ToString());
    }

    [Fact]
    public void CatalogManage_CreateWithDetails_ParsesAsAProduct()
    {
        var r = CoreIntentResolver.Resolve("CATALOG_MANAGE", new Dictionary<string, string>
        { ["action"] = "create", ["product"] = "Lawn Suit", ["price"] = "3500", ["cost"] = "2400", ["stock"] = "10", ["category"] = "Lawn" });
        Assert.Equal("AddProduct", CommandParser.TryParse(r.Command!)!.Kind.ToString());
    }

    [Theory]
    [InlineData("sales", "today", "", "aaj ka hisaab", "TodaysSummary")]
    [InlineData("profit", "last month", "", "profit last month", "Profit")]
    [InlineData("net", "", "", "monthly net", "MonthlyNet")]
    [InlineData("slow_seller", "", "", "slow movers", "SlowMovers")]
    public void BusinessAnalytics_ResolvesToItsCommand(string metric, string period, string groupBy, string _, string kind)
    {
        var r = CoreIntentResolver.Resolve("BUSINESS_ANALYTICS", new Dictionary<string, string> { ["metric"] = metric, ["period"] = period, ["group_by"] = groupBy });
        Assert.NotNull(r.Command);
        Assert.Equal(kind, CommandParser.TryParse(r.Command!)!.Kind.ToString());
    }

    [Fact]
    public void BusinessAnalytics_CityGroup_IsAGap()
    {
        var r = CoreIntentResolver.Resolve("BUSINESS_ANALYTICS", new Dictionary<string, string> { ["metric"] = "sales", ["group_by"] = "city" });
        Assert.Null(r.Command);
        Assert.NotNull(r.Gap);
    }

    [Theory]
    [InlineData("show", "stock", "Stock")]
    [InlineData("low", "low stock", "Stock")]
    [InlineData("untrack", "stock Kurti off", "Stock")]
    public void Stock_ResolvesToItsCommand(string action, string _, string kind)
    {
        var r = CoreIntentResolver.Resolve("STOCK", new Dictionary<string, string> { ["action"] = action, ["product"] = "Kurti", ["qty"] = "20" });
        Assert.Equal(kind, CommandParser.TryParse(r.Command!)!.Kind.ToString());
    }

    [Theory]
    [InlineData("list", "customers", "CustomerList")]
    [InlineData("balance", "Bilal ka kitna udhaar hai", "Insight")]
    public void Customer_ResolvesToItsCommand(string action, string _, string kind)
    {
        var r = CoreIntentResolver.Resolve("CUSTOMER", new Dictionary<string, string> { ["action"] = action, ["customer"] = "Bilal" });
        Assert.Equal(kind, CommandParser.TryParse(r.Command!)!.Kind.ToString());
    }
}
