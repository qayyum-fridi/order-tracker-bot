using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

public sealed class ConversationPipelineTests
{
    [Fact]
    public void Analyze_NormalizesAndExtractsPhoneAndNumbers()
    {
        var result = ConversationPipeline.Analyze("\u200F Sara ko 2 shirts, phone ۰۳۰۰۱۲۳۴۵۶۷");

        Assert.Equal("Sara ko 2 shirts, phone 03001234567", result.Input);
        Assert.Contains(result.Entities, e => e.Kind == "phone" && e.Value == "03001234567");
        Assert.Contains(result.Entities, e => e.Kind == "number" && e.Value == "2");
        Assert.Equal(ConversationPipelineStage.ProduceCandidates, result.Stage);
    }

    [Theory]
    [InlineData("order 12 shipped", "12")]
    [InlineData("#12 cancel", "12")]
    [InlineData("order no. 7", "7")]
    public void Analyze_OrderIdOnlyWithOrderMarker(string input, string id)
    {
        var result = ConversationPipeline.Analyze(input);

        Assert.Contains(result.Entities, e => e.Kind == "order_id" && e.Value == id);
        Assert.DoesNotContain(result.Entities, e => e.Kind == "number" && e.Value == id);
    }

    [Fact]
    public void Analyze_QuantityBeforeProductIsNeverOrderId()
    {
        var result = ConversationPipeline.Analyze("2 shirts add karo");

        Assert.DoesNotContain(result.Entities, e => e.Kind == "order_id");
        Assert.Contains(result.Entities, e => e.Kind == "number" && e.Value == "2");
    }

    [Fact]
    public void Analyze_ReportsMissingFieldsBeforeSafetyValidation()
    {
        var result = ConversationPipeline.Analyze("product add karo", new TurnAction { Action = TurnAction.AddProduct });

        Assert.Equal(ConversationPipelineStage.MissingFields, result.Stage);
        Assert.Contains("product", result.MissingFields[0]);
        Assert.False(result.IsReadyForValidation);
    }

    [Fact]
    public void Analyze_ReportsCloseModelAlternativesAsAmbiguous()
    {
        var action = new TurnAction
        {
            Action = TurnAction.RecordExpense,
            Score = 7,
            Alternatives = [new ActionGuess(TurnAction.MonthlyExpense, 6)],
            Value = "rent",
            Amount = 15000,
        };

        var result = ConversationPipeline.Analyze("rent 15000", action);

        Assert.Equal(ConversationPipelineStage.Ambiguous, result.Stage);
        Assert.True(result.IsAmbiguous);
        Assert.Equal(2, result.Candidates.Count);
    }
}
