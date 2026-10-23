using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using Xunit;

namespace OrderTrackerBot.Tests;

public class ProductCodeTests
{
    [Fact]
    public void Design_number_in_name_and_sku_are_codes()
    {
        var product = new Product { SellerId = 1, Name = "Design 1203", Sku = "LWN-77", Price = 3500 };

        var codes = ConversationEngine.ProductCodes(product);

        Assert.Contains("1203", codes);
        Assert.Contains("lwn-77", codes);
        Assert.DoesNotContain("design", codes);
    }

    [Fact]
    public void Design_attribute_is_a_code_and_matches_case_insensitively()
    {
        var product = new Product { SellerId = 1, Name = "Lawn Suit", Price = 2500, AttributesJson = "{\"design\":\"D-1203\"}" };

        Assert.True(ConversationEngine.HasProductCode(product, "d-1203"));
        Assert.False(ConversationEngine.HasProductCode(product, "lawn"));
    }

    [Fact]
    public void A_name_without_digits_has_no_name_code()
    {
        var product = new Product { SellerId = 1, Name = "Kurti", Price = 1800 };

        Assert.False(ConversationEngine.HasProductCode(product, "kurti"));
    }
}
