using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>
/// Products read from the Sapphire screenshots (hijab, knit abaya set, unstitched suit, dashed suit name).
/// The read fields become one product line per product; the line must read back as the same product.
/// </summary>
public class ScreenshotProductTests
{
    [Fact]
    public void Hijab_LineKeepsNameAndPriceAndColourAndFabric()
    {
        var product = new AiNewProduct
        {
            Name = "Basic Hijab", Price = 1990, Colors = new List<string> { "Burgundy" },
            Attributes = new Dictionary<string, string> { ["category"] = "Hijabs", ["fabric"] = "100% Polyester", ["sku"] = "HIJABV926011" },
        };

        Assert.Equal("Basic Hijab - 1990, color Burgundy, category Hijabs, fabric: 100% Polyester", ConversationEngine.ScreenshotProductLine(product));
    }

    [Fact]
    public void KnitAbayaSet_SiteSkuIsNotCopied()
    {
        var product = new AiNewProduct
        {
            Name = "Knit Abaya Set", Price = 9490,
            Attributes = new Dictionary<string, string> { ["sku"] = "ABBAYAV92611_999" },
        };

        var line = ConversationEngine.ScreenshotProductLine(product);

        Assert.Equal("Knit Abaya Set - 9490", line);
        Assert.DoesNotContain("ABBAYAV", line);
    }

    [Fact]
    public void Suit_ColourAndPieceDetailsStayOnTheLine()
    {
        var product = new AiNewProduct
        {
            Name = "Unstitched 3 Piece Printed Suit", Price = 7590, Colors = new List<string> { "Mustard" },
            Attributes = new Dictionary<string, string>
            {
                ["fabric"] = "Premium Khaddar",
                ["dupatta"] = "Printed Tissue Silk 2.5m",
            },
        };

        Assert.Equal("Unstitched 3 Piece Printed Suit - 7590, color Mustard, fabric: Premium Khaddar, dupatta: Printed Tissue Silk 2.5m",
            ConversationEngine.ScreenshotProductLine(product));
    }

    [Fact]
    public void DashedName_IsKeptWhole_SoThePriceIsNotSplitOff()
    {
        var product = new AiNewProduct { Name = "3 PIECE - EMBROIDERED PREMIUM KHADDAR SUIT", Price = 6990 };

        Assert.Equal("3 PIECE EMBROIDERED PREMIUM KHADDAR SUIT - 6990", ConversationEngine.ScreenshotProductLine(product));
    }

    [Fact]
    public void ScreenshotLine_ReadsBackAsAProductWithTheSameNameAndPrice()
    {
        var product = new AiNewProduct
        {
            Name = "Basic Hijab", Price = 1990, Colors = new List<string> { "Burgundy" },
            Attributes = new Dictionary<string, string> { ["fabric"] = "100% Polyester" },
        };

        var parsed = CommandParser.TryParse(ConversationEngine.ScreenshotProductLine(product));

        Assert.NotNull(parsed);
        Assert.Equal(CommandKind.AddProduct, parsed!.Kind);
        Assert.Equal("Basic Hijab", parsed.Product!.Name);
        Assert.Equal(1990m, parsed.Product.Price);
    }

    [Fact]
    public void Sizes_OneListingPerSize_SameNameAndPrice()
    {
        var product = new AiNewProduct { Name = "Knit Abaya Set", Price = 9490, Sizes = new List<string> { "S", "M", "L" } };

        Assert.Equal("Knit Abaya Set - 9490, size M", ConversationEngine.ScreenshotProductLine(product, "M"));
    }

    [Theory]
    [InlineData("Small", "S")]
    [InlineData("Double XL", "XXL")]
    [InlineData("extra large", "XL")]
    [InlineData("m", "M")]
    [InlineData("Free size", "Free Size")]
    [InlineData("Foo", null)]
    public void StandardSizes_MapsCommonSpellings(string raw, string? expected) =>
        Assert.Equal(expected, StandardSizes.Normalize(raw));

    [Theory]
    [InlineData("Ready to wear category banao", false)]
    [InlineData("Basic Hijab - 1990", false)]
    [InlineData("Basic Hijab - 1990\nKnit Abaya Set - 9490\nUnstitched Suit - 7590", true)]
    public void PastedBlock_IsConfirmedLikeAScreenshot(string message, bool expected) =>
        Assert.Equal(expected, ConversationEngine.IsPastedProductBlock(message));
}
