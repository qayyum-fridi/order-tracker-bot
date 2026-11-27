using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

public sealed class CandidateStageTests
{
    [Theory]
    [InlineData("CancelOrder", "cancel_order")]
    [InlineData("cancel_order", "cancel_order")]
    [InlineData("OrderDetail", "order_detail")]
    public void Canonical_MakesCommandAndModelNamesMeet(string name, string expected) =>
        Assert.Equal(expected, CandidateMerger.Canonical(name));

    [Fact]
    public void Merge_SameActionFromBothSources_IsOneCandidateWithStrongestEvidence()
    {
        var deterministic = new ConversationCandidate("cancel_order", null, IsDeterministic: true, Intent: 1, Pattern: 1);
        var model = new ConversationCandidate("cancel_order", null, Intent: 0.9);

        var merged = Assert.Single(CandidateMerger.Merge(new[] { deterministic, model }));

        Assert.Equal(1, merged.Intent);
        Assert.Equal(1, merged.Pattern);
        Assert.True(merged.IsDeterministic);
    }

    [Fact]
    public void Merge_SameActionDifferentArguments_StayCompeting()
    {
        var order12 = new ConversationCandidate("cancel_order", null, Intent: 0.9, Arguments: "12|||||");
        var order13 = new ConversationCandidate("cancel_order", null, Intent: 0.9, Arguments: "13|||||");

        Assert.Equal(2, CandidateMerger.Merge(new[] { order12, order13 }).Count);
    }

    [Fact]
    public void Merge_UnstatedArgumentIsNotADifference()
    {
        var model = new ConversationCandidate("cancel_order", null, Intent: 0.9, Arguments: "12|||||");
        var deterministic = new ConversationCandidate("cancel_order", null, IsDeterministic: true, Intent: 1, Pattern: 1);

        Assert.Single(CandidateMerger.Merge(new[] { deterministic, model }));
    }

    [Fact]
    public void Merge_KeepsTheUnionOfGeneratorSources()
    {
        var deterministic = new ConversationCandidate("cancel_order", null, IsDeterministic: true, Intent: 1, Sources: CandidateSources.Deterministic);
        var model = new ConversationCandidate("cancel_order", null, Intent: 0.9, Sources: CandidateSources.Model);

        var merged = Assert.Single(CandidateMerger.Merge(new[] { deterministic, model }));

        Assert.Equal(CandidateSources.Deterministic | CandidateSources.Model, merged.Sources);
    }

    [Fact]
    public void Select_ClearIntentWithMissingArguments_IsIncompleteNotSelect()
    {
        var winner = new ConversationCandidate("create_order", 0.95, Sources: CandidateSources.Model);

        var selection = CandidateSelector.Decide(new[] { winner }, missingFields: new[] { "order details" });

        Assert.Equal(CandidateDecision.Incomplete, selection.Decision);
        Assert.Equal("required_fields_missing", selection.Reason);
    }

    [Fact]
    public void Select_AmbiguousIntentWithMissingArguments_StaysClarify()
    {
        var first = new ConversationCandidate("cancel_order", 0.6);
        var second = new ConversationCandidate("update_status", 0.58);

        var selection = CandidateSelector.Decide(new[] { first, second }, missingFields: new[] { "order id" });

        Assert.Equal(CandidateDecision.Clarify, selection.Decision);
    }

    [Fact]
    public void Merge_PlanMembersAreNotAlsoListedAlone()
    {
        var plan = new ConversationCandidate("cancel_order+create_order", null, Plan: new[] { "cancel_order", "create_order" }, Intent: 0.9);
        var single = new ConversationCandidate("cancel_order", null, Intent: 0.9);

        var only = Assert.Single(CandidateMerger.Merge(new[] { plan, single }));

        Assert.NotNull(only.Plan);
    }

    [Fact]
    public void Select_ClearWinner_Selects()
    {
        var winner = new ConversationCandidate("cancel_order", 0.95);
        var rival = new ConversationCandidate("update_status", 0.5, IsAlternative: true);

        Assert.Equal(CandidateDecision.Select, CandidateSelector.Decide(new[] { winner, rival }).Decision);
    }

    [Fact]
    public void Select_ContradictoryOrderNumber_ClarifiesEvenWithHighScore()
    {
        var winner = new ConversationCandidate("cancel_order", 0.95);

        var selection = CandidateSelector.Decide(new[] { winner }, new[] { "conflicting_reference" });

        Assert.Equal(CandidateDecision.Clarify, selection.Decision);
        Assert.Equal("conflicting_reference", selection.Reason);
    }

    [Fact]
    public void Composite_MissingFeaturesAreLeftOut()
    {
        Assert.Equal(1.0, CandidateScorer.Composite(1, 1, null, null, null)!.Value, 9);
        Assert.Null(CandidateScorer.Composite(null, null, null, null, null));
    }
}
