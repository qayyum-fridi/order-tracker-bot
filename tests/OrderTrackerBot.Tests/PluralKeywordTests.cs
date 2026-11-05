using OrderTrackerBot.Application.Intents;
using Xunit;

namespace OrderTrackerBot.Tests;

// A plural word routes only when no exact keyword matched, and an exact match is never changed by it.
public class PluralKeywordTests
{
    private static IntentDefinition Intent(string id, params string[] keywords) =>
        new(id, "e", "a", "Kind", "d", new[] { "x" }, Array.Empty<IntentSlot>(), "t", keywords);

    private static readonly IntentDefinition[] Catalog =
    {
        Intent("order.show", "order"),
        Intent("expense.add", "expense"),
        Intent("cust.list", "customers"),
    };

    [Fact]
    public void PluralFallback_FindsTheSingularKeyword()
    {
        Assert.Equal(new[] { "order.show" }, IntentKeywords.MatchPlural("orders dikhao", Catalog));
    }

    [Fact]
    public void PluralFallback_FindsThePluralKeyword_ForASingularWord()
    {
        Assert.Equal(new[] { "cust.list" }, IntentKeywords.MatchPlural("customer", Catalog));
    }

    [Fact]
    public void ExactMatch_StaysExact()
    {
        Assert.Equal(new[] { "cust.list" }, IntentKeywords.Match("customers", Catalog));
        Assert.Empty(IntentKeywords.Match("orders", Catalog));
    }
}
