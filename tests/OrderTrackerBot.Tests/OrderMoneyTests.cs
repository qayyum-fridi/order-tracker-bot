using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using Xunit;

namespace OrderTrackerBot.Tests;

public class OrderMoneyTests
{
    [Theory]
    [InlineData(1000, 1200, 200)]
    [InlineData(1000, 1000, 0)]
    [InlineData(1000, 1250, 250)]
    public void Credit_IsOnlyTheExcessOverTheTotal(decimal total, decimal paid, decimal expected)
    {
        var order = new Order { Total = total, AmountPaid = paid, PaymentStatus = PaymentStatus.Paid };

        Assert.Equal(expected, OrderMoney.Credit(order));
    }

    [Fact]
    public void Balance_StaysZero_WhenOverpaid()
    {
        var order = new Order { Total = 1000, AmountPaid = 1250, PaymentStatus = PaymentStatus.Paid };

        Assert.Equal(0, OrderMoney.Balance(order));
    }

    [Fact]
    public void LegacyPaidOrder_WithZeroAmountPaid_HasNoCredit()
    {
        var order = new Order { Total = 5000, AmountPaid = 0, PaymentStatus = PaymentStatus.Paid };

        Assert.Equal(0, OrderMoney.Credit(order));
    }

    [Fact]
    public void State_ReportsCredit_WhenOverpaid()
    {
        var order = new Order { Total = 1000, AmountPaid = 1250, PaymentStatus = PaymentStatus.Paid };

        Assert.Contains("OVERPAID", OrderMoney.State(order));
    }
}
