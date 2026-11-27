using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

// Stage 9 phase 1: the voice plan is observation only. These cases pin what the planner records for a note's steps.
public class VoicePlanTests
{
    [Fact]
    public void SingleStep_IsOneStep_NoConflict()
    {
        var plan = VoicePlan.Build(new[] { "cancel 12" });
        Assert.Single(plan.Steps);
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void IndependentSteps_AreAllRecorded_WithoutAFalseConflict()
    {
        var plan = VoicePlan.Build(new[] { "cancel 12", "cancel 13" });
        Assert.Equal(2, plan.Steps.Count);
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void SameOrder_CompatibleEffects_NoConflict()
    {
        var plan = VoicePlan.Build(new[] { "mark 12 shipped", "mark 12 delivered" });
        Assert.Equal(2, plan.Steps.Count);
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void SameOrder_ConflictingEffects_IsRecorded()
    {
        var plan = VoicePlan.Build(new[] { "cancel 12", "mark 12 shipped" });
        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal("order:12", conflict.EntityKey);
    }

    [Fact]
    public void DifferentOrders_NoUnrelatedConflict()
    {
        var plan = VoicePlan.Build(new[] { "cancel 12", "mark 13 shipped" });
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void EmptyOrUnparsedSteps_AreSafelyHandled()
    {
        var plan = VoicePlan.Build(new[] { "", "xyz qwerty" });
        Assert.Equal(2, plan.Steps.Count);
        Assert.Empty(plan.Conflicts);
    }
}
