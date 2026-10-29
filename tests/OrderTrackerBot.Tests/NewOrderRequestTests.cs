using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

// Asking to start a new order without giving its details, in any wording, date words included.
public class NewOrderRequestTests
{
    [Theory]
    [InlineData("naya order bana dein")]
    [InlineData("naya order bana do")]
    [InlineData("aaj ki tareekh ka naya order bana dein")]
    [InlineData("ek naya order bana dein aaj ka")]
    [InlineData("order banao")]
    [InlineData("new order create kar do")]
    [InlineData("make a new order for today")]
    [InlineData("create order")]
    [InlineData("یہ ایک نیا آرڈر بنا دیں آج کی تاریخ کا")]
    [InlineData("نیا آرڈر بنا دیں")]
    [InlineData("آرڈر بنائیں")]
    [InlineData("naya order likh do")]
    public void NewOrderWording_IsNewOrderHelp(string text)
    {
        var parsed = CommandParser.TryParse(text) ?? throw new Xunit.Sdk.XunitException($"not a command: {text}");
        Assert.Equal(CommandKind.NewOrderHelp, parsed.Kind);
    }

    [Theory]
    [InlineData("naya expense bana do", "expense")]
    [InlineData("expense add karna hai", "expense")]
    [InlineData("naya kharcha likhna hai", "expense")]
    [InlineData("wage bana do", "wage")]
    [InlineData("tankhwah add karni hai", "wage")]
    [InlineData("naya loss bana dein", "loss")]
    [InlineData("nuqsan likh do", "loss")]
    [InlineData("naya report bana do", "report")]
    [InlineData("نیا خرچہ بنا دیں", "expense")]
    [InlineData("نئی تنخواہ بنا دیں", "wage")]
    [InlineData("naya payment bana do", "payment")]
    public void NounWithCreateVerb_ShowsItsHowTo(string text, string topic)
    {
        var parsed = CommandParser.TryParse(text) ?? throw new Xunit.Sdk.XunitException($"not a command: {text}");
        Assert.Equal(CommandKind.HowTo, parsed.Kind);
        Assert.Equal(topic, parsed.Text);
    }

    [Theory]
    [InlineData("customers dikhao", CommandKind.CustomerList)]
    [InlineData("show customers", CommandKind.CustomerList)]
    [InlineData("products dikhao", CommandKind.Catalog)]
    [InlineData("expenses dikhao", CommandKind.ExpenseList)]
    [InlineData("show expenses", CommandKind.ExpenseList)]
    [InlineData("wages list", CommandKind.WageList)]
    [InlineData("losses dikhao", CommandKind.LossList)]
    public void NounWithViewVerb_RunsItsListCommand(string text, CommandKind kind)
    {
        var parsed = CommandParser.TryParse(text) ?? throw new Xunit.Sdk.XunitException($"not a command: {text}");
        Assert.Equal(kind, parsed.Kind);
    }

    [Theory]
    [InlineData("product edit karna hai", "change-product")]
    [InlineData("customer edit karna hai", "change-customer")]
    [InlineData("expense delete", "change-expense")]
    [InlineData("expense delete karna hai", "change-expense")]
    [InlineData("wage edit karna hai", "change-wage")]
    [InlineData("loss delete karo", "change-loss")]
    [InlineData("product hatao", "change-product")]
    [InlineData("پروڈکٹ تبدیل کرنا ہے", "change-product")]
    public void NounWithEditOrDeleteVerb_ShowsItsChangeHelp(string text, string topic)
    {
        var parsed = CommandParser.TryParse(text) ?? throw new Xunit.Sdk.XunitException($"not a command: {text}");
        Assert.Equal(CommandKind.HowTo, parsed.Kind);
        Assert.Equal(topic, parsed.Text);
    }

    [Theory]
    [InlineData("delete customer Sara")]
    [InlineData("edit product: Kurti - 1900")]
    [InlineData("expense delete 12")]
    [InlineData("customers kitne hain")]
    public void NounWithDetails_IsNotTheHelpRule(string text)
    {
        var parsed = CommandParser.TryParse(text);
        Assert.False(parsed is { Kind: CommandKind.HowTo, Text: var t } && t.StartsWith("change-", StringComparison.Ordinal), $"unexpected change help for: {text}");
    }

    [Fact]
    public void NewCustomerWithoutName_ShowsCustomerForm()
    {
        var parsed = CommandParser.TryParse("naya customer bana dein") ?? throw new Xunit.Sdk.XunitException("not a command");
        Assert.Equal(CommandKind.DetailedForm, parsed.Kind);
        Assert.Equal("customer", parsed.Text);
    }

    [Theory]
    [InlineData("expense 500 packaging")]
    [InlineData("wage Ali 5000")]
    [InlineData("loss 2 Kurti damaged")]
    [InlineData("expenses today")]
    [InlineData("losses")]
    public void NounWithoutCreateVerbOrWithDetails_IsNotHowTo(string text)
    {
        var parsed = CommandParser.TryParse(text);
        Assert.False(parsed is { Kind: CommandKind.HowTo }, $"unexpected HowTo for: {text}");
    }

    [Theory]
    [InlineData("order 12 bana do")]
    [InlineData("naya order 2 kurti 1500")]
    [InlineData("naya order ka report")]
    [InlineData("edit order bana do")]
    [InlineData("cancel naya order")]
    [InlineData("naya order kitne aaye")]
    [InlineData("naya customer bana dein")]
    [InlineData("orders today")]
    [InlineData("Naya order add karna chahte hain")]
    public void NotANewOrderRequest_IsNotNewOrderHelp(string text)
    {
        var parsed = CommandParser.TryParse(text);
        Assert.False(parsed is { Kind: CommandKind.NewOrderHelp }, $"unexpected NewOrderHelp for: {text}");
    }
}
