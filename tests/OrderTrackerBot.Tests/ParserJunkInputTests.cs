using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

public class ParserJunkInputTests
{
    [Theory]
    [InlineData("Order -1")]
    [InlineData("Orders -2")]
    [InlineData("Report -5")]
    public void CommandWordWithNegativeNumber_IsNotAProductLine(string line)
    {
        Assert.False(CommandParser.TryParseProductLine(line, out _));
        Assert.NotEqual(CommandKind.AddProduct, CommandParser.TryParse(line)?.Kind);
    }

    [Theory]
    [InlineData("Sector F-10")]
    [InlineData("Block 5 - 300")]
    [InlineData("Street 10 - 200")]
    [InlineData("House 500")]
    public void AddressFragment_IsNotAProductLine(string line)
    {
        Assert.False(CommandParser.TryParseProductLine(line, out _));
        Assert.NotEqual(CommandKind.AddProduct, CommandParser.TryParse(line)?.Kind);
    }

    [Fact]
    public void CommaSeparatedAddress_IsNotABulkProductAdd()
    {
        var kind = CommandParser.TryParse("House 500, Street 10")?.Kind;
        Assert.NotEqual(CommandKind.AddProductsBulk, kind);
        Assert.NotEqual(CommandKind.AddProduct, kind);
    }

    [Theory]
    [InlineData("House Dress - 1500", "House Dress", 1500)]
    [InlineData("Sector Lawn - 900", "Sector Lawn", 900)]
    [InlineData("Block Print Kurti - 2200", "Block Print Kurti", 2200)]
    public void ProductsWhoseNameStartsWithAnAddressWord_StillParse(string line, string name, decimal price)
    {
        Assert.True(CommandParser.TryParseProductLine(line, out var product));
        Assert.Equal(name, product!.Name);
        Assert.Equal(price, product.Price);
    }

    [Fact]
    public void InlineProducts_StillParse()
    {
        Assert.Equal(CommandKind.AddProductsBulk, CommandParser.TryParse("Shirt 200 and pants 800")?.Kind);
    }

    [Theory]
    [InlineData("Order 9999999999")]
    [InlineData("#9999999999")]
    [InlineData("mark 9999999999 shipped")]
    [InlineData("bill kitna hua order id 9999999999 ka?")]
    public void HugeNumberInCommand_DoesNotThrow(string message)
    {
        Assert.Null(Record.Exception(() => CommandParser.TryParse(message)));
    }

    [Fact]
    public void LargestInt_StillParsesAsAnOrderNumber()
    {
        var parsed = CommandParser.TryParse("Order 2147483647");
        Assert.Equal(CommandKind.OrderDetail, parsed?.Kind);
        Assert.Equal(int.MaxValue, parsed?.Number);
    }

    [Theory]
    [InlineData("Price 500")]
    [InlineData("Qty 2")]
    [InlineData("House 500")]
    [InlineData("Sector F 10")]
    [InlineData("stock Kurti 20")]
    public void SpacedLine_BookkeepingOrAddressWords_AreNotProducts(string line)
    {
        Assert.False(CommandParser.TryParseSpacedProductLine(line, out _));
    }

    [Fact]
    public void SpacedLine_NameAndPrice_IsAProduct()
    {
        Assert.True(CommandParser.TryParseSpacedProductLine("Lawn 400", out var product));
        Assert.Equal("Lawn", product!.Name);
        Assert.Equal(400m, product.Price);
    }

    [Fact]
    public void HugeNumberInOrderEdit_IsRejectedWithoutThrowing()
    {
        Assert.Null(Record.Exception(() => CommandParser.TryParseOrderEdit("price 1 = 99999999999999999999999999999", out _)));
        Assert.False(CommandParser.TryParseOrderEdit("price 1 = 99999999999999999999999999999", out _));
    }
}
