using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>"Did you mean": one misspelt keyword gets a question; several, an ambiguous one, or a seller's own name never do.</summary>
public class WordFixTests
{
    private static readonly (string Word, string IntentId)[] Vocabulary =
    {
        ("stock", "stock.show"), ("catalog", "catalog.show"), ("expenses", "expense.list"), ("profit", "profit.show"),
    };

    [Fact]
    public void OneMisspeltKeyword_GetsTheCorrectedMessageAndItsIntent()
    {
        var fix = ConversationEngine.FindOneWordFix("stok ka hisaab batao", Vocabulary, new HashSet<string>());

        Assert.NotNull(fix);
        Assert.Equal("stok", fix!.From);
        Assert.Equal("stock", fix.To);
        Assert.Equal("stock ka hisaab batao", fix.Corrected);
        Assert.Equal("stock.show", fix.Intent);
    }

    [Fact]
    public void KnownKeyword_GetsNoQuestion()
    {
        Assert.Null(ConversationEngine.FindOneWordFix("stock dikhao", Vocabulary, new HashSet<string>()));
    }

    [Fact]
    public void TwoMisspeltWords_GetNoQuestion()
    {
        Assert.Null(ConversationEngine.FindOneWordFix("stok aur profyt dikhao", Vocabulary, new HashSet<string>()));
    }

    [Fact]
    public void ASellersOwnName_IsNeverCorrected()
    {
        // "Stoc" is one letter from "stock", but it is a customer's name: it is left alone.
        Assert.Null(ConversationEngine.FindOneWordFix("Stoc ka order", Vocabulary, new HashSet<string> { "stoc" }));
        // Without the name in the protected list the same word is a clear typo.
        Assert.NotNull(ConversationEngine.FindOneWordFix("Stoc ka order", Vocabulary, new HashSet<string>()));
    }

    [Fact]
    public void WordEquallyCloseToTwoKeywords_GetsNoQuestion()
    {
        var vocabulary = new[] { ("bikri", "a"), ("bikra", "b") };

        Assert.Null(ConversationEngine.FindOneWordFix("bikrx", vocabulary, new HashSet<string>()));
    }

    [Fact]
    public void ShortWords_AreNeverCorrected()
    {
        Assert.Null(ConversationEngine.FindOneWordFix("sto kar", Vocabulary, new HashSet<string>()));
    }
}
