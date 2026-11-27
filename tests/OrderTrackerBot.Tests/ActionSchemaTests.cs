using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

// Stage 5b: required fields are told apart from invalid values; each issue names one field, and correct fields are not reported.
public class ActionSchemaTests
{
    private static AiOrderItemDraft Item(int quantity = 1) => new() { ProductName = "Shirt", Quantity = quantity };

    private static TurnAction Order(params AiOrderItemDraft[] items) =>
        new() { Action = TurnAction.CreateOrder, Orders = { new AiOrderDraft { CustomerName = "Sara", Items = items.ToList() } } };

    [Fact]
    public void CreateOrder_WithoutItems_IsMissingItems()
    {
        Assert.Equal(new[] { "order items" }, ActionSchema.Missing(Order()));
    }

    [Fact]
    public void CreateOrder_WalkInWithoutCustomer_IsNotMissing()
    {
        var walkIn = new TurnAction { Action = TurnAction.CreateOrder, Orders = { new AiOrderDraft { Items = { Item() } } } };
        Assert.Empty(ActionSchema.Missing(walkIn));
        Assert.Empty(ActionSchema.Invalid(walkIn));
    }

    [Fact]
    public void Quantity_MustBeAboveZero_AndOnlyThatFieldIsReported()
    {
        var action = Order(Item(quantity: 0), Item(quantity: 2));

        var invalid = ActionSchema.Invalid(action);

        Assert.Single(invalid);
        Assert.StartsWith("quantity", invalid[0]);
        Assert.Empty(ActionSchema.Missing(action));
    }

    [Fact]
    public void NegativePrice_IsInvalid_ZeroPriceOnAddProduct_IsAllowed()
    {
        var negative = new TurnAction { Action = TurnAction.AddProduct, Products = { new AiNewProduct { Name = "Mug", Price = -5 } } };
        var zero = new TurnAction { Action = TurnAction.AddProduct, Products = { new AiNewProduct { Name = "Mug", Price = 0 } } };

        Assert.Single(ActionSchema.Invalid(negative));
        Assert.Empty(ActionSchema.Invalid(zero));
    }

    [Fact]
    public void EditProduct_ZeroPrice_IsStillMissingPrice_AsBefore()
    {
        var action = new TurnAction { Action = TurnAction.EditProduct, Products = { new AiNewProduct { Name = "Mug", Price = 0 } } };
        Assert.Equal(new[] { "product and price" }, ActionSchema.Missing(action));
    }

    [Fact]
    public void Status_MustBeApproved()
    {
        Assert.Empty(ActionSchema.Invalid(new TurnAction { Action = TurnAction.UpdateStatus, Status = "shipped" }));
        Assert.Single(ActionSchema.Invalid(new TurnAction { Action = TurnAction.UpdateStatus, Status = "lost" }));
    }

    [Fact]
    public void EditCustomer_FieldMustBeOneOfTheSavedDetails()
    {
        var bad = new TurnAction { Action = TurnAction.EditCustomer, CustomerName = "Sara", Field = "email", Value = "a@b.com" };
        var good = new TurnAction { Action = TurnAction.EditCustomer, CustomerName = "Sara", Field = "phone", Value = "03001234567" };

        Assert.Single(ActionSchema.Invalid(bad));
        Assert.Empty(ActionSchema.Invalid(good));
    }

    [Fact]
    public void Payment_NonPositiveAmount_IsInvalid()
    {
        var action = new TurnAction { Action = TurnAction.RecordPayment, OrderId = 3, Amount = 0 };
        Assert.Single(ActionSchema.Invalid(action));
    }

    [Fact]
    public void Missing_NullAction_IsEmpty()
    {
        Assert.Empty(ActionSchema.Missing(null));
        Assert.Empty(ActionSchema.Invalid(null));
    }
}
