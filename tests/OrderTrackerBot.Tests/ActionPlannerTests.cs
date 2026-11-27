using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

// Stage 5a: action plans keep their order, contradictory steps on one record are found, unrelated records stay independent.
public class ActionPlannerTests
{
    private static PlannedStep Step(string action, string key, PlanEffect effect) => new(action, key, effect);

    [Fact]
    public void SequentialSameRecord_IsAPlan_NoConflict()
    {
        // "create order and then send it": create + advance on the same order, not contradictory.
        var steps = new[] { Step("create_order", "order:new", PlanEffect.Create), Step("mark_shipped", "order:new", PlanEffect.Advance) };

        Assert.Empty(ActionPlanner.FindConflicts(steps));
        Assert.Equal(new[] { (0, 1) }, ActionPlanner.Dependencies(steps));
    }

    [Fact]
    public void CreateThenCancel_SameRecord_IsAConflict()
    {
        // "create order and cancel it": contradictory on the same record, so the plan is blocked.
        var steps = new[] { Step("create_order", "order:new", PlanEffect.Create), Step("cancel_order", "order:new", PlanEffect.Cancel) };

        var conflict = Assert.Single(ActionPlanner.FindConflicts(steps));
        Assert.Equal((0, 1), (conflict.First, conflict.Second));
        Assert.Equal("order:new", conflict.EntityKey);
    }

    [Fact]
    public void CancelAndShip_SameOrder_IsAConflict_EitherOrder()
    {
        var steps = new[] { Step("mark_shipped", "order:12", PlanEffect.Advance), Step("cancel_order", "order:12", PlanEffect.Cancel) };
        Assert.Single(ActionPlanner.FindConflicts(steps));
        Assert.True(ActionPlanner.Contradicts(PlanEffect.Cancel, PlanEffect.Advance));
    }

    [Fact]
    public void CancelOrder_AndCancelAnotherOrder_AreIndependent()
    {
        var steps = new[] { Step("cancel_order", "order:12", PlanEffect.Cancel), Step("cancel_order", "order:13", PlanEffect.Cancel) };

        Assert.Empty(ActionPlanner.FindConflicts(steps));
        Assert.Empty(ActionPlanner.Dependencies(steps));
    }

    [Fact]
    public void RemoveThenEdit_SameCustomer_IsAConflict()
    {
        var steps = new[] { Step("delete_customer", "customer:Sara", PlanEffect.Remove), Step("edit_customer", "customer:Sara", PlanEffect.Edit) };
        Assert.Single(ActionPlanner.FindConflicts(steps));
    }

    [Fact]
    public void Thresholds_AreConfigurable_AndDefaultToProvisional()
    {
        var candidates = new[]
        {
            new ConversationCandidate("a", 0.90),
            new ConversationCandidate("b", 0.80),
        };

        // Default: margin 0.10 is below 0.15, so the two are close alternatives: Clarify.
        Assert.Equal(CandidateDecision.Clarify, CandidateSelector.Decide(candidates).Decision);

        // A tuned margin lets the clear top candidate run.
        var tuned = new SelectionThresholds(SelectScore: 0.85, SelectMargin: 0.05, ClarifyScore: 0.55);
        Assert.Equal(CandidateDecision.Select, CandidateSelector.Decide(candidates, thresholds: tuned).Decision);
    }
}
