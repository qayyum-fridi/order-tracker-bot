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
        Assert.Contains(result.Candidates, c => c.Action == TurnAction.RecordExpense);
        Assert.Contains(result.Candidates, c => c.Action == TurnAction.MonthlyExpense);
    }

    [Fact]
    public void Analyze_RepeatedActionIsOneCandidateWithStrongestScore()
    {
        var action = new TurnAction
        {
            Action = TurnAction.RecordExpense,
            Score = 7,
            Alternatives = [new ActionGuess(TurnAction.RecordExpense, 5)],
        };

        var result = ConversationPipeline.Analyze("rent 15000", action);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(0.7, candidate.Score!.Value, 6);
        Assert.False(candidate.IsAlternative);
    }

    [Fact]
    public void Analyze_DeterministicExactMatchIsAlternativeWhenModelPicksOtherAction()
    {
        var action = new TurnAction { Action = TurnAction.AddProduct, Score = 9 };

        var result = ConversationPipeline.Analyze("cancel 12", action, new ParsedCommand { Kind = CommandKind.CancelOrder });

        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains(result.Candidates, c => c.IsDeterministic && c.IsAlternative && c.Score == 1.0);
        Assert.Contains(result.Candidates, c => c.Action == TurnAction.AddProduct && !c.IsAlternative && c.Score is > 0.89 and < 0.91);
        // Exact 1.0 against 0.9: margin 0.1 is under the select margin, so it asks instead of running either.
        Assert.Equal(ConversationPipelineStage.Ambiguous, result.Stage);
    }

    [Fact]
    public void Analyze_ModerateScoreWithArgumentsSeenIsBelowSelectNotCloseCandidates()
    {
        // Intent 0.5 plus the amount (15000) found in the message lifts the composite to about 0.61: the clarify band, not reject.
        var action = new TurnAction { Action = TurnAction.RecordExpense, Score = 5, Value = "rent", Amount = 15000 };

        var result = ConversationPipeline.Analyze("rent 15000", action);

        Assert.Equal(ConversationPipelineStage.Ambiguous, result.Stage);
        Assert.Equal("below_select_score", result.Reason);
    }
}
