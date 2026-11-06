using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Agent;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

public sealed class PlanVerifierTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly PlanVerifier _verifier;
    private readonly SellerContextBuilder _contextBuilder;
    private readonly Seller _seller;

    public PlanVerifierTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _verifier = new PlanVerifier(_db);
        _contextBuilder = new SellerContextBuilder(_db);
        _seller = new Seller { WhatsAppPhoneNumber = "923001110000", BusinessName = "Shop" };
        _db.Sellers.Add(_seller);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Product AddProduct(string name, decimal price = 500, int? sellerId = null)
    {
        var p = new Product { SellerId = sellerId ?? _seller.Id, Name = name, Price = price };
        _db.Products.Add(p);
        _db.SaveChanges();
        return p;
    }

    private Customer AddCustomer(string name, string? city = null)
    {
        var c = new Customer { SellerId = _seller.Id, Name = name, City = city };
        _db.Customers.Add(c);
        _db.SaveChanges();
        return c;
    }

    private static PlanStep Step(string tool, Dictionary<string, string>? args = null, params PlanItem[] items) =>
        new(tool, args ?? new Dictionary<string, string>(), items);

    private async Task<VerifiedPlan> VerifyAsync(string message, params PlanStep[] steps)
    {
        var ctx = await _contextBuilder.BuildAsync(_seller, message, default);
        return await _verifier.VerifyAsync(_seller, new AgentPlan(steps, null, null, 0.9), ctx, message, default);
    }

    // ---- add_product ----

    [Fact]
    public async Task AddProduct_WithAPriceTheSellerWrote_Runs()
    {
        var plan = await VerifyAsync("Kurti 500 add karo", Step("add_product", new() { ["name"] = "Kurti", ["price"] = "500" }));
        Assert.Equal(StepVerdict.Run, plan.Steps[0].Verdict);
        Assert.Null(plan.Blocked);
    }

    [Fact]
    public async Task AddProduct_APriceTheSellerNeverSaid_IsRejected()
    {
        var plan = await VerifyAsync("Kurti 500 add karo", Step("add_product", new() { ["name"] = "Kurti", ["price"] = "700" }));
        Assert.Equal((StepVerdict.Reject, "price_not_said"), (plan.Steps[0].Verdict, plan.Steps[0].Reason));
    }

    [Fact]
    public async Task AddProduct_APriceSpokenInWords_IsAccepted()
    {
        var plan = await VerifyAsync("Kurti teen hazar ka add karo", Step("add_product", new() { ["name"] = "Kurti", ["price"] = "3000" }));
        Assert.Equal(StepVerdict.Run, plan.Steps[0].Verdict);
    }

    [Theory]
    [InlineData("Kurti 500 add karo", "-5", "price_invalid")]
    [InlineData("Kurti 99999999 add karo", "99999999", "price_invalid")]
    [InlineData("Kurti 500 add karo", "abc", "price_invalid")]
    public async Task AddProduct_InsanePrice_IsRejected(string message, string price, string reason)
    {
        var plan = await VerifyAsync(message, Step("add_product", new() { ["name"] = "Kurti", ["price"] = price }));
        Assert.Equal((StepVerdict.Reject, reason), (plan.Steps[0].Verdict, plan.Steps[0].Reason));
    }

    [Fact]
    public async Task AddProduct_WithoutAPrice_AsksForIt_AndANameWithoutLettersIsRejected()
    {
        var noPrice = await VerifyAsync("Kurti add karo", Step("add_product", new() { ["name"] = "Kurti" }));
        Assert.Equal((StepVerdict.Ask, "price_missing"), (noPrice.Steps[0].Verdict, noPrice.Steps[0].Reason));

        var badName = await VerifyAsync("500 add karo", Step("add_product", new() { ["name"] = "500", ["price"] = "500" }));
        Assert.Equal("bad_name", badName.Steps[0].Reason);
    }

    // ---- edit_product ----

    [Fact]
    public async Task EditProduct_OfARealProduct_NeedsYes()
    {
        var polo = AddProduct("Polo Shirt");
        var plan = await VerifyAsync("Polo shirt ka price 600 kar do", Step("edit_product", new() { ["product_id"] = polo.Id.ToString(), ["price"] = "600" }));
        Assert.Equal(StepVerdict.Confirm, plan.Steps[0].Verdict);
        Assert.Equal(polo.Id, plan.Steps[0].ProductId);
    }

    [Fact]
    public async Task EditProduct_AnIdTheModelWasNeverShown_IsRejected()
    {
        AddProduct("Polo Shirt");
        var foreign = AddProduct("Secret", 1, sellerId: AddOtherSeller());
        var plan = await VerifyAsync("Polo shirt ka price 600 kar do", Step("edit_product", new() { ["product_id"] = foreign.Id.ToString(), ["price"] = "600" }));
        Assert.Equal((StepVerdict.Reject, "id_not_in_context"), (plan.Steps[0].Verdict, plan.Steps[0].Reason));
    }

    [Fact]
    public async Task EditProduct_AnUnsaidValueEqualToTheStoredOne_IsDroppedNotRejected()
    {
        // Seen live: "Kurti ki cost 900 hai" came back as price=1800 (the catalog price) + cost=900.
        var kurti = AddProduct("Kurti", 1800);
        var plan = await VerifyAsync("Kurti ki cost 900 hai", Step("edit_product", new() { ["product_id"] = kurti.Id.ToString(), ["price"] = "1800", ["cost"] = "900" }));

        Assert.Equal(StepVerdict.Confirm, plan.Steps[0].Verdict);
        Assert.Equal(new[] { "cost", "product_id" }, plan.Steps[0].Step.Args.Keys.OrderBy(k => k));
    }

    [Fact]
    public async Task EditProduct_AnUnsaidValueThatDiffersFromTheStoredOne_IsStillRejected_AndNothingLeftIsRejected()
    {
        var kurti = AddProduct("Kurti", 1800);
        var invented = await VerifyAsync("Kurti ki cost 900 hai", Step("edit_product", new() { ["product_id"] = kurti.Id.ToString(), ["price"] = "2500", ["cost"] = "900" }));
        Assert.Equal("price_not_said", invented.Steps[0].Reason);

        var nothing = await VerifyAsync("Kurti", Step("edit_product", new() { ["product_id"] = kurti.Id.ToString(), ["price"] = "1800" }));
        Assert.Equal("nothing_to_change", nothing.Steps[0].Reason);

        // a value the seller did say stays even when it equals the stored one
        var said = await VerifyAsync("Kurti ka price 1800 hi rakhna", Step("edit_product", new() { ["product_id"] = kurti.Id.ToString(), ["price"] = "1800" }));
        Assert.Equal(StepVerdict.Confirm, said.Steps[0].Verdict);
    }

    [Fact]
    public async Task EditProduct_AnotherSellersProduct_IsRejected_EvenIfTheContextListedIt()
    {
        var foreign = AddProduct("Secret", 1, sellerId: AddOtherSeller());
        var ctx = new SellerContext { Products = new[] { new ProductHit(foreign.Id, "Secret", 1, null, 1) } };   // a forged / buggy context
        var plan = await _verifier.VerifyAsync(_seller, new AgentPlan(new[] { Step("edit_product", new() { ["product_id"] = foreign.Id.ToString(), ["price"] = "600" }) }, null, null, 1),
            ctx, "secret 600", default);
        Assert.Equal("unknown_product", plan.Steps[0].Reason);
    }

    [Fact]
    public async Task EditProduct_WithNothingToChange_OrAnUnsaidAmount_IsRejected()
    {
        var polo = AddProduct("Polo Shirt");
        var none = await VerifyAsync("Polo shirt", Step("edit_product", new() { ["product_id"] = polo.Id.ToString() }));
        Assert.Equal("nothing_to_change", none.Steps[0].Reason);

        var unsaid = await VerifyAsync("Polo shirt ka price 600 kar do", Step("edit_product", new() { ["product_id"] = polo.Id.ToString(), ["price"] = "650" }));
        Assert.Equal("price_not_said", unsaid.Steps[0].Reason);
    }

    // ---- draft_order ----

    [Fact]
    public async Task DraftOrder_ResolvesCustomerAndProductsByNameFromTheDatabase()
    {
        var sara = AddCustomer("Sara Khan", "Lahore");
        var kurti = AddProduct("Kurti");
        var plan = await VerifyAsync("Sara ko 2 Kurti bhej do",
            Step("draft_order", new() { ["customer_name"] = "Sara" }, new PlanItem(null, "Kurti", 2)));

        var step = plan.Steps[0];
        Assert.Equal(StepVerdict.Run, step.Verdict);
        Assert.Equal(sara.Id, step.CustomerId);
        Assert.Equal(new ResolvedItem(kurti.Id, "Kurti", 2, false), step.Items!.Single());
    }

    [Fact]
    public async Task DraftOrder_TwoCustomersWithTheNameWord_AsksWhichOne()
    {
        AddCustomer("Sara Khan", "Lahore");
        AddCustomer("Sara Ali", "Karachi");
        AddProduct("Kurti");
        var plan = await VerifyAsync("Sara ko Kurti", Step("draft_order", new() { ["customer_name"] = "Sara" }, new PlanItem(null, "Kurti", 1)));

        Assert.Equal((StepVerdict.Ask, "ambiguous_customer"), (plan.Steps[0].Verdict, plan.Steps[0].Reason));
        Assert.Equal(2, plan.Steps[0].Choices!.Count);
        Assert.Contains("Sara Khan (Lahore)", plan.Steps[0].Choices!);
    }

    [Fact]
    public async Task DraftOrder_AnIdChosenBetweenTwoEquallyNamedCustomers_BecomesAQuestion()
    {
        // Seen live: the model answered "Sara ko 2 Kurti" with customer_id of Sara Ali. The id is real and was shown, but the message does not pick her.
        AddCustomer("Sara Khan", "Lahore");
        var ali = AddCustomer("Sara Ali", "Karachi");
        AddProduct("Kurti");
        var plan = await VerifyAsync("Sara ko 2 Kurti bhej do", Step("draft_order", new() { ["customer_id"] = ali.Id.ToString() }, new PlanItem(null, "Kurti", 2)));

        Assert.Equal((StepVerdict.Ask, "ambiguous_customer"), (plan.Steps[0].Verdict, plan.Steps[0].Reason));
        Assert.Contains("Sara Khan (Lahore)", plan.Steps[0].Choices!);
        Assert.Contains("Sara Ali (Karachi)", plan.Steps[0].Choices!);
    }

    [Fact]
    public async Task DraftOrder_AnIdForTheCustomerTheMessageNamesBest_Runs()
    {
        AddCustomer("Sara Khan", "Lahore");
        var ali = AddCustomer("Sara Ali", "Karachi");
        AddProduct("Kurti");
        var plan = await VerifyAsync("Sara Ali ko 2 Kurti bhej do", Step("draft_order", new() { ["customer_id"] = ali.Id.ToString() }, new PlanItem(null, "Kurti", 2)));

        Assert.Equal(StepVerdict.Run, plan.Steps[0].Verdict);
        Assert.Equal(ali.Id, plan.Steps[0].CustomerId);
    }

    [Fact]
    public async Task ProductIds_ChosenBetweenVariantsTheMessageNamesEqually_BecomeAQuestion()
    {
        AddProduct("Kurti white");
        var black = AddProduct("Kurti black");
        var edit = await VerifyAsync("Kurti ka price 900 kar do", Step("edit_product", new() { ["product_id"] = black.Id.ToString(), ["price"] = "900" }));
        Assert.Equal((StepVerdict.Ask, "ambiguous_product"), (edit.Steps[0].Verdict, edit.Steps[0].Reason));

        AddCustomer("Sara");
        var order = await VerifyAsync("Sara ko Kurti", Step("draft_order", new() { ["customer_name"] = "Sara" }, new PlanItem(black.Id, "Kurti", 1)));
        Assert.Equal((StepVerdict.Ask, "ambiguous_product"), (order.Steps[0].Verdict, order.Steps[0].Reason));
    }

    [Fact]
    public async Task DraftOrder_AnExactFullName_BeatsPartialMatches()
    {
        var exact = AddCustomer("Sara");
        AddCustomer("Sara Khan");
        AddProduct("Kurti");
        var plan = await VerifyAsync("Sara ko Kurti", Step("draft_order", new() { ["customer_name"] = "Sara" }, new PlanItem(null, "Kurti", 1)));
        Assert.Equal(exact.Id, plan.Steps[0].CustomerId);
    }

    [Fact]
    public async Task DraftOrder_UnknownCustomer_RunsAsANewOne_AndNoCustomerAtAllAsks()
    {
        AddProduct("Kurti");
        var fresh = await VerifyAsync("Zainab ko Kurti", Step("draft_order", new() { ["customer_name"] = "Zainab" }, new PlanItem(null, "Kurti", 1)));
        Assert.Equal((StepVerdict.Run, "new_customer"), (fresh.Steps[0].Verdict, fresh.Steps[0].Reason));
        Assert.Null(fresh.Steps[0].CustomerId);

        var nobody = await VerifyAsync("2 Kurti ka order", Step("draft_order", null, new PlanItem(null, "Kurti", 2)));
        Assert.Equal("customer_missing", nobody.Steps[0].Reason);
    }

    [Fact]
    public async Task DraftOrder_ProductVariants_AskWhichOne_AndPartialNamesResolve()
    {
        AddCustomer("Sara");
        var polo = AddProduct("Polo Shirt");
        AddProduct("Kurti white");
        AddProduct("Kurti black");

        var partial = await VerifyAsync("Sara ko polo", Step("draft_order", new() { ["customer_name"] = "Sara" }, new PlanItem(null, "Polo", 1)));
        Assert.Equal(polo.Id, partial.Steps[0].Items!.Single().ProductId);

        var variants = await VerifyAsync("Sara ko kurti", Step("draft_order", new() { ["customer_name"] = "Sara" }, new PlanItem(null, "Kurti", 1)));
        Assert.Equal((StepVerdict.Ask, "ambiguous_product"), (variants.Steps[0].Verdict, variants.Steps[0].Reason));
        Assert.Equal(2, variants.Steps[0].Choices!.Count);
    }

    [Fact]
    public async Task DraftOrder_AProductAddedByAnEarlierStep_IsNotUnmatched_ButAnUnknownOneIs()
    {
        AddCustomer("Sara");
        var plan = await VerifyAsync("Kurti 500 add karo phir Sara ko 2 Kurti aur 1 Shawl",
            Step("add_product", new() { ["name"] = "Kurti", ["price"] = "500" }),
            Step("draft_order", new() { ["customer_name"] = "Sara" }, new PlanItem(null, "Kurti", 2), new PlanItem(null, "Shawl", 1)));

        var items = plan.Steps[1].Items!;
        Assert.Equal(StepVerdict.Run, plan.Steps[1].Verdict);
        Assert.False(items[0].Unmatched);
        Assert.True(items[1].Unmatched);
    }

    [Fact]
    public async Task DraftOrder_AQuantityTheSellerNeverSaid_IsRejected_AndNoItemsIsRejected()
    {
        AddCustomer("Sara");
        AddProduct("Kurti");
        var qty = await VerifyAsync("Sara ko Kurti", Step("draft_order", new() { ["customer_name"] = "Sara" }, new PlanItem(null, "Kurti", 5)));
        Assert.Equal("quantity_not_said", qty.Steps[0].Reason);

        var none = await VerifyAsync("Sara ka order", Step("draft_order", new() { ["customer_name"] = "Sara" }));
        Assert.Equal("no_items", none.Steps[0].Reason);
    }

    [Fact]
    public async Task DraftOrder_AQuantityInWords_IsAccepted()
    {
        AddCustomer("Sara");
        AddProduct("Kurti");
        var plan = await VerifyAsync("Sara ko do Kurti", Step("draft_order", new() { ["customer_name"] = "Sara" }, new PlanItem(null, "Kurti", 2)));
        Assert.Equal(StepVerdict.Run, plan.Steps[0].Verdict);
    }

    // ---- the whole plan ----

    [Fact]
    public async Task Plan_StopsAtTheFirstStepThatNeedsYes_AndLaterStepsAreNotRunnable()
    {
        AddCustomer("Sara");
        var polo = AddProduct("Polo Shirt");
        AddProduct("Kurti");
        var plan = await VerifyAsync("Hat 100 add karo, Polo shirt 600 kar do, Sara ko Kurti",
            Step("add_product", new() { ["name"] = "Hat", ["price"] = "100" }),
            Step("edit_product", new() { ["product_id"] = polo.Id.ToString(), ["price"] = "600" }),
            Step("draft_order", new() { ["customer_name"] = "Sara" }, new PlanItem(null, "Kurti", 1)));

        Assert.Equal(new[] { StepVerdict.Run, StepVerdict.Confirm, StepVerdict.Run }, plan.Steps.Select(s => s.Verdict));
        Assert.Single(plan.Runnable);
        Assert.Equal("edit_product", plan.Blocked!.Step.Tool);
    }

    private int AddOtherSeller()
    {
        var other = new Seller { WhatsAppPhoneNumber = "923009990000", BusinessName = "Other" };
        _db.Sellers.Add(other);
        _db.SaveChanges();
        return other.Id;
    }
}
