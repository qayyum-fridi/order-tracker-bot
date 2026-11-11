using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using Xunit;

namespace OrderTrackerBot.Tests;

// Product-line details the seller writes ("expiry: 2027-03") land in their own columns; bad values stay as plain details.
public class ProductTypedDetailsTests
{
    private static Product NewProduct() => new() { Name = "Ghee Dalda", Price = 1850 };

    [Fact]
    public void Expiry_month_means_the_last_day_of_that_month()
    {
        var product = NewProduct();
        Assert.True(ProductTypedDetails.TrySet(product, "expiry", "2027-03"));
        Assert.Equal(new DateTime(2027, 3, 31), product.ExpiryDate);
    }

    [Fact]
    public void Full_expiry_date_is_kept()
    {
        var product = NewProduct();
        Assert.True(ProductTypedDetails.TrySet(product, "expiry", "2027-03-15"));
        Assert.Equal(new DateTime(2027, 3, 15), product.ExpiryDate);
    }

    [Fact]
    public void Bad_expiry_is_not_mapped_so_it_stays_a_detail()
    {
        var product = NewProduct();
        Assert.False(ProductTypedDetails.TrySet(product, "expiry", "soon"));
        Assert.Null(product.ExpiryDate);
    }

    [Theory]
    [InlineData("batch", "GD-204", "BatchNumber")]
    [InlineData("model", "X200", "Model")]
    [InlineData("brand", "Dalda", "Brand")]
    [InlineData("allergens", "milk, nuts", "Allergens")]
    [InlineData("dimensions", "30 x 20 x 10 cm", "Dimensions")]
    [InlineData("agegroup", "kids", "AgeGroup")]
    [InlineData("sizechart", "S 36, M 38", "SizeChart")]
    public void Text_details_go_to_their_column(string key, string value, string column)
    {
        var product = NewProduct();
        Assert.True(ProductTypedDetails.TrySet(product, key, value));
        Assert.Equal(value, typeof(Product).GetProperty(column)!.GetValue(product));
    }

    [Fact]
    public void Warranty_and_prep_are_numbers()
    {
        var product = NewProduct();
        Assert.True(ProductTypedDetails.TrySet(product, "warranty", "12"));
        Assert.True(ProductTypedDetails.TrySet(product, "prep", "20"));
        Assert.Equal(12, product.WarrantyMonths);
        Assert.Equal(20, product.PrepMinutes);
        Assert.False(ProductTypedDetails.TrySet(product, "warranty", "long"));
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("nahi", false)]
    public void Made_to_order_is_yes_or_no(string value, bool expected)
    {
        var product = NewProduct();
        Assert.True(ProductTypedDetails.TrySet(product, "madetoorder", value));
        Assert.Equal(expected, product.MadeToOrder);
    }

    [Fact]
    public void Unknown_keys_are_not_typed()
    {
        Assert.False(ProductTypedDetails.TrySet(NewProduct(), "fabric", "cotton"));
    }
}
