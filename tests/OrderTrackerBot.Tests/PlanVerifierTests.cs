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
    public async Task EditProduct_AValueFiledUnderTheWrongField_MovesToTheFieldTheOwnerNamed()
    {
        var kurti = AddProduct("Kurti", 1800);
        var moved = await VerifyAsync("Kurti ki cost 900 hai", Step("edit_product", new() { ["product_id"] = kurti.Id.ToString(), ["price"] = "900" }));
        Assert.Equal(StepVerdict.Confirm, moved.Steps[0].Verdict);
        Assert.Equal("900", moved.Steps[0].Step.Args["cost"]);
        Assert.False(moved.Steps[0].Step.Args.ContainsKey("price"));
    }

    [Theory]
    [InlineData("expense 500 petrol")]
    [InlineData("customer add Zainab, Model Town, 03001234567")]
    [InlineData("customer add Zainab, 03001234567")]
    [InlineData("customer add Zainab")]
    public void EveryLineTheNewToolsWrite_ParsesAsItsCommand(string line) =>
        Assert.NotNull(OrderTrackerBot.Application.Conversation.CommandParser.TryParse(line));

    [Fact]
    public async Task NewTools_CompileToTheCommandsTheParserReads()
    {
        var kurti = AddProduct("Kurti");
        var expense = (await VerifyAsync("petrol 500", Step("add_expense", new() { ["amount"] = "500", ["note"] = "petrol" }))).Steps[0];
        var customer = (await VerifyAsync("Zainab, Model Town, 03001234567", Step("add_customer", new() { ["name"] = "Zainab", ["address"] = "Model Town", ["phone"] = "03001234567" }))).Steps[0];

        var compiled = await new AgentStepCompiler(_db).CompileAsync(_seller, new VerifiedPlan(new List<VerifiedStep> { expense, customer }), default);

        Assert.Equal(new[] { "expense 500 petrol", "customer add Zainab, Model Town, 03001234567" }, compiled.Steps.Select(c => c.Text));
        Assert.Equal(OrderTrackerBot.Application.Conversation.CommandKind.Expense, OrderTrackerBot.Application.Conversation.CommandParser.TryParse("expense 500 petrol")!.Kind);
        Assert.Equal(OrderTrackerBot.Application.Conversation.CommandKind.CustomerAdd, OrderTrackerBot.Application.Conversation.CommandParser.TryParse("customer add Zainab, Model Town, 03001234567")!.Kind);
    }

    [Fact]
    public async Task AddCustomer_PhoneTheMessageDoesNotContain_IsRejected()
    {
        var plan = await VerifyAsync("Zainab ko save karo", Step("add_customer", new() { ["name"] = "Zainab", ["phone"] = "03001234567" }));
        Assert.Equal("phone_not_said", plan.Steps[0].Reason);
    }

    [Fact]
    public async Task EditProduct_SoldOut_IsStockZeroWhenTheOwnerSaysSo()
    {
        var kurti = AddProduct("Kurti", 1800);
        var soldOut = await VerifyAsync("kurti khatam ho gayi hai", Step("edit_product", new() { ["product_id"] = kurti.Id.ToString(), ["stock"] = "0" }));
        Assert.Equal(StepVerdict.Confirm, soldOut.Steps[0].Verdict);

        var unsaid = await VerifyAsync("kurti ka rate badal do", Step("edit_product", new() { ["product_id"] = kurti.Id.ToString(), ["stock"] = "0" }));
        Assert.Equal("stock_not_said", unsaid.Steps[0].Reason);   // zero without a sold-out word is still not said
    }

    [Fact]
    public async Task EditProduct_SoldOut_WithTheStoredPriceAlsoCopied_IsStillSoldOut()
    {
        // Seen live: "kurti khatam ho gayi hai" came back as price=1800 (the stored price) + stock=0 and was rejected.
        var kurti = AddProduct("Kurti", 1800);
        var plan = await VerifyAsync("kurti khatam ho gayi hai", Step("edit_product", new() { ["product_id"] = kurti.Id.ToString(), ["price"] = "1800", ["stock"] = "0" }));
        Assert.Equal((StepVerdict.Confirm, "edits_existing_product"), (plan.Steps[0].Verdict, plan.Steps[0].Reason));
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

    [Theory]
    [InlineData("Kurti ki cost 900 hai", "price", "field_unclear")]                 // said cost, plan edits price: moves to cost
    [InlineData("kurti ki lagat 900 hai", "price", "field_unclear")]
    [InlineData("Kurti ka price 900 kar do", "cost", "field_unclear")]               // said price, plan edits cost
    [InlineData("Kurti ka rate 900 kar do", "cost", "field_unclear")]
    [InlineData("Kurti ki cost 900 hai", "cost", null)]                              // right field
    [InlineData("Kurti ka price 900 kar do", "price", null)]
    [InlineData("Kurti 900 kar do", "price", null)]                                  // names no field: nothing to contradict
    [InlineData("Kurti ki cost 900 aur price 1800", "price", null)]                  // names both
    public async Task EditProduct_TheFieldTheMessageNames_MustBeTheFieldThePlanEdits(string message, string field, string? reason)
    {
        var kurti = AddProduct("Kurti", 1800);
        var plan = await VerifyAsync(message, Step("edit_product", new() { ["product_id"] = kurti.Id.ToString(), [field] = field == "price" && message.Contains("1800") ? "1800" : "900" }));

        if (reason is null)
        {
            Assert.Equal(StepVerdict.Confirm, plan.Steps[0].Verdict);
            return;
        }
        // the owner named the other field: the value moves there (a Confirm), instead of a question
        var named = field == "price" ? "cost" : "price";
        Assert.Equal(StepVerdict.Confirm, plan.Steps[0].Verdict);
        Assert.True(plan.Steps[0].Step.Args.ContainsKey(named));
        Assert.False(plan.Steps[0].Step.Args.ContainsKey(field));
    }

    [Theory]
    [InlineData("Kurti ka rate 100 barha do")]          // "increase by 100": 100 is the change, not the price
    [InlineData("kurti ka price 300 kam kar do")]
    [InlineData("Kurti sasta kar do")]
    [InlineData("kurti ki cost 100 zyada")]
    public async Task EditProduct_AChangeWordedAsAnAmountOff_AsksForTheExactValue(string message)
    {
        // Seen live: "Kurti ka rate 100 barha do" was planned as price=100 and would have been a Confirm.
        var kurti = AddProduct("Kurti", 1800);
        var plan = await VerifyAsync(message, Step("edit_product", new() { ["product_id"] = kurti.Id.ToString(), ["price"] = "100" }));

        Assert.Equal((StepVerdict.Ask, "relative_change"), (plan.Steps[0].Verdict, plan.Steps[0].Reason));
    }

    [Fact]
    public async Task DraftOrder_ATitleLikeMadam_AsksForTheCustomerInsteadOfCreatingOne()
    {
        // Seen live: "madam ne kaha 2 kurti aur chahiye" created a new customer called "madam".
        AddProduct("Kurti");
        var plan = await VerifyAsync("madam ne kaha 2 kurti aur chahiye", Step("draft_order", new() { ["customer_name"] = "madam" }, new PlanItem(null, "Kurti", 2)));
        Assert.Equal((StepVerdict.Ask, "customer_missing"), (plan.Steps[0].Verdict, plan.Steps[0].Reason));
    }

    [Fact]
    public async Task DraftOrder_DeliveryAndAdvanceAreMoneyTheOwnerSaid_OrRejected()
    {
        AddCustomer("Bilal");
        AddProduct("Kurti");
        var said = await VerifyAsync("Bilal ko 2 Kurti, delivery 200, advance 500", Step("draft_order", new() { ["customer_name"] = "Bilal", ["delivery"] = "200", ["advance"] = "500" }, new PlanItem(null, "Kurti", 2)));
        Assert.Equal(StepVerdict.Run, said.Steps[0].Verdict);

        var invented = await VerifyAsync("Bilal ko 2 Kurti", Step("draft_order", new() { ["customer_name"] = "Bilal", ["delivery"] = "200" }, new PlanItem(null, "Kurti", 2)));
        Assert.Equal("delivery_not_said", invented.Steps[0].Reason);
    }

    [Fact]
    public async Task DraftOrder_WithMoney_CompilesOneSentenceThatKeepsThePhoneAddressDeliveryAndAdvance()
    {
        // The order reader asks for a phone when the sentence has none; a saved customer's phone is written into it, so the reader does not ask (seen live).
        var bilal = new Customer { SellerId = _seller.Id, Name = "Bilal", Phone = "03005556666" };
        _db.Customers.Add(bilal);
        _db.SaveChanges();
        AddProduct("Kurti");
        var plan = await VerifyAsync("Bilal ko 2 Kurti, delivery 200, advance 500, address Model Town", Step("draft_order",
            new() { ["customer_id"] = bilal.Id.ToString(), ["delivery"] = "200", ["advance"] = "500", ["address"] = "Model Town" }, new PlanItem(null, "Kurti", 2)));
        var compiled = await new AgentStepCompiler(_db).CompileAsync(_seller, plan, default);

        var text = Assert.Single(compiled.Steps).Text;
        Assert.Equal("Bilal ko 2 Kurti, phone 03005556666, delivery 200, advance 500, address Model Town", text);
    }

    [Fact]
    public async Task DraftOrder_EkDozenIsTwelve_WithoutTheDigitBeingWritten()
    {
        AddCustomer("Bilal");
        AddProduct("Kurti");
        var plan = await VerifyAsync("ek dozen kurti Bilal ko bhej do", Step("draft_order", new() { ["customer_name"] = "Bilal" }, new PlanItem(null, "Kurti", 12)));
        Assert.Equal(StepVerdict.Run, plan.Steps[0].Verdict);
        var noWord = await VerifyAsync("12 kurti Bilal ko", Step("draft_order", new() { ["customer_name"] = "Bilal" }, new PlanItem(null, "Kurti", 12)));
        Assert.Equal(StepVerdict.Run, noWord.Steps[0].Verdict);
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
    public async Task DraftOrder_ANameInUrduScriptThatMatchedNobody_AsksInsteadOfCreatingACustomer()
    {
        // Seen in a stress run: "بلال کو دو کرتی بھیج دیں" planned a NEW customer "بلال" although Bilal is saved in Roman script.
        var bilal = AddCustomer("Bilal", "Multan");
        AddProduct("Kurti");
        var ask = await VerifyAsync("بلال کو دو کرتی بھیج دیں", Step("draft_order", new() { ["customer_name"] = "بلال" }, new PlanItem(null, "Kurti", 2)));
        Assert.Equal((StepVerdict.Ask, "ambiguous_customer"), (ask.Steps[0].Verdict, ask.Steps[0].Reason));
        Assert.Contains("Bilal (Multan)", ask.Steps[0].Choices!);

        // the model that matched him by sound and used his id just runs
        var byId = await VerifyAsync("بلال کو دو کرتی بھیج دیں", Step("draft_order", new() { ["customer_id"] = bilal.Id.ToString() }, new PlanItem(null, "Kurti", 2)));
        Assert.Equal(StepVerdict.Run, byId.Steps[0].Verdict);
        Assert.Equal(bilal.Id, byId.Steps[0].CustomerId);

        // a Roman-script name nobody has is still a new customer
        var fresh = await VerifyAsync("Zainab ko 2 Kurti", Step("draft_order", new() { ["customer_name"] = "Zainab" }, new PlanItem(null, "Kurti", 2)));
        Assert.Equal("new_customer", fresh.Steps[0].Reason);
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
