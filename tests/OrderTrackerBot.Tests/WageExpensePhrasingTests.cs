using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

// MVP: wages and expenses said the way a seller talks, without the fixed command word. Plain parser rules, no AI.
public class WageExpensePhrasingTests
{
    [Theory]
    [InlineData("Ali ki salary 20000 de di", "Ali", 20000, null)]
    [InlineData("Ali ko 20000 salary diya", "Ali", 20000, null)]
    [InlineData("Ali ko 5000 tankhwah di", "Ali", 5000, null)]
    [InlineData("worker ko 3000 diye", "worker", 3000, null)]
    [InlineData("mazdoor ko 1500 di", "mazdoor", 1500, null)]
    [InlineData("Bilal ko 800 dihari di", "Bilal", 800, 1)]
    public void WagePayment_IsRead(string text, string worker, int amount, int? days)
    {
        var c = CommandParser.TryParse(text);
        Assert.NotNull(c);
        Assert.Equal(CommandKind.Wage, c!.Kind);
        Assert.Equal(worker, c.Text);
        Assert.Equal(amount, c.Amount);
        Assert.Equal(days, c.Number);
    }

    [Theory]
    [InlineData("is mahine salary kitni gayi")]
    [InlineData("Ali ki tankhwah kitni hui")]
    public void WageQuestion_ListsWages(string text) =>
        Assert.Equal(CommandKind.WageList, CommandParser.TryParse(text)?.Kind);

    [Theory]
    [InlineData("kiraya 15000 diya", "kiraya", 15000)]
    [InlineData("bijli ka bill 8000", "bijli ka bill", 8000)]
    [InlineData("petrol 1000", "petrol", 1000)]
    [InlineData("aaj chai pe 200 lage", "chai", 200)]
    [InlineData("rent 30000 paid", "rent", 30000)]
    [InlineData("800 ka kharcha tha rickshaw", "rickshaw", 800)]
    [InlineData("packaging ke 500 lag gaye", "packaging", 500)]
    [InlineData("expense 500 packaging", "packaging", 500)]
    public void Spending_IsReadAsExpense(string text, string note, int amount)
    {
        var c = CommandParser.TryParse(text);
        Assert.NotNull(c);
        Assert.Equal(CommandKind.Expense, c!.Kind);
        Assert.Equal(note, c.Text);
        Assert.Equal(amount, c.Amount);
    }

    [Theory]
    [InlineData("Sara ko 500 di")]          // a person, not a cost
    [InlineData("Shirt 1800")]              // not an expense word
    [InlineData("Ali ko 5000 diye")]
    [InlineData("bill 12")]                 // receipt of order 12
    public void PeopleAndProducts_AreNotExpenses(string text) =>
        Assert.NotEqual(CommandKind.Expense, CommandParser.TryParse(text)?.Kind);

    [Fact]
    public void OldForms_StillMeanWhatTheyDid()
    {
        Assert.Equal(CommandKind.Wage, CommandParser.TryParse("salary Ali 20000")?.Kind);
        Assert.Equal("set", CommandParser.TryParse("Ali ki salary 25000 kar do")?.Text2);
        Assert.Equal(CommandKind.WageList, CommandParser.TryParse("salary list")?.Kind);
    }
}
