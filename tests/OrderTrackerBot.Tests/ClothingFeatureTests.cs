using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

// Test cases for the clothing features added in this branch. Pure functions only; the engine flows
// (draft Edit, size exchange, bulk delivered, sales-mode question) are listed at the bottom for the live run.
public class ClothingFeatureTests
{
    // Bulk "delivered" for today's shipped orders: a delivered word, a "today / all" word, no number, no request to see a list.
    [Theory]
    [InlineData("sab aaj wale delivered")]
    [InlineData("aaj ke sab delivered karo")]
    [InlineData("inka status delivered kar do")]
    [InlineData("sare delivered karo")]
    [InlineData("mark all today shipped as delivered")]
    public void BulkDelivered_Accepts(string message) =>
        Assert.True(CommandParser.IsBulkTodayDelivered(message));

    [Theory]
    [InlineData("Sara ka order deliver ho gaya")]   // one customer's status, not a bulk change
    [InlineData("Sara delivered karo")]            // "Sara" is a name here
    [InlineData("order 12 delivered")]                // a number: one order
    [InlineData("sab delivered orders dikhao")]       // asks to see, does not change
    [InlineData("delivery charge 200")]               // not a status
    [InlineData("delivered")]                         // no scope word
    public void BulkDelivered_Rejects(string message) =>
        Assert.False(CommandParser.IsBulkTodayDelivered(message));

    // The setup question's answer: a tap label or a typed phrase.
    [Theory]
    [InlineData("🏪 Sirf dukan par", "counter")]
    [InlineData("sales-counter", "counter")]
    [InlineData("🚚 Delivery bhi karta hoon", "delivery")]
    [InlineData("sales-delivery", "delivery")]
    [InlineData("thik hai", null)]
    public void SalesMode_ReadsTheAnswer(string message, string? expected) =>
        Assert.Equal(expected, ConversationEngine.ReadSalesMode(message));

    // A unit the seller names with "unit:" becomes the product unit; other attributes stay.
    [Theory]
    [InlineData("Lawn - 3500, unit: thaan", "thaan")]
    [InlineData("Lawn - 3500, unit: gaz", "gaz")]
    [InlineData("Lawn - 3500, unit: roll", "roll")]
    [InlineData("Lawn - 3500", "piece")]
    public void UnitAttribute_SetsTheUnit(string line, string unit)
    {
        Assert.True(CommandParser.TryParseProductLine(line, out ProductLine? product));
        Assert.Equal(unit, product!.UnitType);
    }

    // The screenshot line: one line per size, the site SKU never copied, the old price kept as a detail, the current price saved.
    [Fact]
    public void ScreenshotLine_OneSize_KeepsTheListingSimple() =>
        Assert.Equal("Knit Abaya Set - 9490, size M",
            ConversationEngine.ScreenshotProductLine(new AiNewProduct { Name = "Knit Abaya Set", Price = 9490, Sizes = new List<string> { "S", "M" } }, "M"));

    [Fact]
    public void ScreenshotLine_CrossedOutPrice_SavesTheLowerPrice() =>
        Assert.Equal("Beige Lawn 3PC - 7194, old price: 11990",
            ConversationEngine.ScreenshotProductLine(new AiNewProduct { Name = "Beige Lawn 3PC", Price = 11990, OldPrice = 7194 }));

    [Fact]
    public void ScreenshotLine_SiteSkuIsDropped() =>
        Assert.Equal("Knit Abaya Set - 9490",
            ConversationEngine.ScreenshotProductLine(new AiNewProduct { Name = "Knit Abaya Set", Price = 9490, Attributes = new Dictionary<string, string> { ["sku"] = "ABBAYAV92611_999" } }));

    // Standard size list: common spellings map to the standard size; unknown words are dropped, never guessed.
    [Theory]
    [InlineData("Small", "S")]
    [InlineData("Double XL", "XXL")]
    [InlineData("Extra Large", "XL")]
    [InlineData("One Size", "Free Size")]
    [InlineData("Jumbo", null)]
    public void StandardSizes_Map(string raw, string? expected) =>
        Assert.Equal(expected, StandardSizes.Normalize(raw));

    // Pasted product blocks are confirmed like a screenshot: several lines, or a long text.
    [Theory]
    [InlineData("Basic Hijab - 1990", false)]
    [InlineData("Basic Hijab - 1990\nKnit Abaya Set - 9490\nSuit - 7590", true)]
    public void PastedBlock_Detection(string message, bool expected) =>
        Assert.Equal(expected, ConversationEngine.IsPastedProductBlock(message));
}

// Engine-level cases to run on the live harness (they need the database and the fake AI):
//  1. Draft Edit: "edit" keeps the draft; "phone 0300...", "address ...", "name ..." change it and the draft is shown again.
//  2. Draft Edit: "phone 123" (too short) is refused with order-flow.edit.002, draft unchanged.
//  3. Draft Edit: "payment cod" sets the payment; "payment crypto" is refused (order-flow.edit.003).
//  4. Draft Edit: "item 1 qty 3" changes the quantity and re-applies the discount code; "item 9 qty 2" is refused (order-flow.edit.004).
//  5. Draft Edit: "item 2 remove" removes a line; removing the last line is refused (order-flow.edit.005).
//  6. Size exchange: "exchange 12 1 to L" with an L sibling moves stock both ways; undo restores it.
//  7. Size exchange: a variant that does not exist is refused (order-exchange.005); two matches are refused (order-exchange.006).
//  8. Bulk delivered: "sab aaj wale delivered" lists today's shipped orders; YES marks them delivered; "undo" puts them back.
//  9. Bulk delivered: no shipped order today -> bulk.delivered.002, nothing changes.
// 10. Setup: after setup the sales-mode question appears; "Sirf dukan par" sets SalesMode=counter; the next order asks no phone.
// 11. Category: a product line with an unknown category is refused (category.004); after "X category banao" it saves.
// 12. Screenshot: a product with no readable name asks for the name (picture-product.005) instead of proposing.
