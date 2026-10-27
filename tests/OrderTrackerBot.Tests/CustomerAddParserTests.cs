using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

// "customer add Sara, Model Town, 0300…": a buyer saved before any order, read by plain rules (no AI).
public class CustomerAddParserTests
{
    [Theory]
    [InlineData("customer add Sara, Model Town, 03001234567", "Sara", "03001234567", "Model Town", null)]
    [InlineData("customer add Sara Khan, Model Town, Lahore", "Sara Khan", null, "Model Town", "Lahore")]
    [InlineData("add customer Sara", "Sara", null, null, null)]
    [InlineData("naya client hai Muhammad Ali, address Model Town Lahore, phone baad mein", "Muhammad Ali", null, "Model Town Lahore", null)]
    [InlineData("new client Bilal phone 0300-1234567 city Karachi", "Bilal", "0300-1234567", null, "Karachi")]
    [InlineData("nayi customer Ayesha, pata Gulberg, shehar Lahore", "Ayesha", null, "Gulberg", "Lahore")]
    [InlineData("نیا کلائنٹ محمد علی، پتہ ماڈل ٹاؤن لاہور", "محمد علی", null, "ماڈل ٹاؤن لاہور", null)]
    public void ReadsTheNameAndWhateverDetailsWereGiven(string text, string name, string? phone, string? address, string? city)
    {
        var parsed = CommandParser.TryParse(text);
        Assert.NotNull(parsed);
        Assert.Equal(CommandKind.CustomerAdd, parsed!.Kind);
        Assert.Equal(name, parsed.Text);
        Assert.Equal(phone, parsed.Text2);
        Assert.Equal(address, parsed.Text3);
        Assert.Equal(city, parsed.Text4);
    }

    [Theory]
    [InlineData("customer list")]
    [InlineData("customers")]
    [InlineData("new order")]
    [InlineData("naya order Sara 2 kurti")]
    [InlineData("new customer 123")]
    [InlineData("add customer order Sara")]
    [InlineData("new customer kya hai?")]
    [InlineData("customer add")]
    public void OtherPhrases_AreNotACustomerAdd(string text) =>
        Assert.NotEqual(CommandKind.CustomerAdd, CommandParser.TryParse(text)?.Kind);

    [Fact]
    public void CustomerCorrection_StaysACorrection() =>
        Assert.Equal(CommandKind.CustomerUpdate, CommandParser.TryParse("customer Sara phone 03001234567")!.Kind);
}
