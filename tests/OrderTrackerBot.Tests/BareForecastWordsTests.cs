using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

// The one-word forecast commands a seller types on their own: "stockending", "stockdays", "reorder".
public class BareForecastWordsTests
{
    private static void Topic(string text, string topic)
    {
        var parsed = CommandParser.TryParse(text) ?? throw new Xunit.Sdk.XunitException($"not a command: {text}");
        Assert.Equal(CommandKind.Insight, parsed.Kind);
        Assert.Equal(topic, parsed.Text);
    }

    [Theory]
    [InlineData("stockending")]
    [InlineData("stock ending")]
    [InlineData("stock khatam")]
    public void StockEnding_BareWord_IsStockEndingReport(string text) => Topic(text, "stockending");

    [Theory]
    [InlineData("stockdays")]
    [InlineData("stock days")]
    public void StockDays_BareWord_IsStockDaysReport(string text) => Topic(text, "stockdays");

    [Theory]
    [InlineData("reorder")]
    [InlineData("re-order")]
    [InlineData("restock")]
    public void Reorder_BareWord_IsReorderReport(string text) => Topic(text, "reorder");
}
