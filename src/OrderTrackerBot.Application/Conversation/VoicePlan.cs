namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// Stage 9 phase 1. Builds an <see cref="ActionPlanner"/> plan from one voice note's steps and reports its conflicts. Observation only: it never
/// changes which steps run or wait for a YES. Steps keyed by an order number share a record, so their effects are compared; other steps stand alone.
/// </summary>
public static class VoicePlan
{
    public static (IReadOnlyList<PlannedStep> Steps, IReadOnlyList<PlanConflict> Conflicts) Build(IReadOnlyList<string> steps)
    {
        var planned = new List<PlannedStep>(steps.Count);
        for (var i = 0; i < steps.Count; i++)
        {
            var command = CommandParser.TryParse(steps[i]);
            if (command is null)
            {
                planned.Add(new PlannedStep("unparsed", $"step:{i}", PlanEffect.Edit));
                continue;
            }
            var entity = command.Number is { } number ? $"order:{number}" : $"step:{i}";
            planned.Add(new PlannedStep(command.Kind.ToString(), entity, EffectOf(command.Kind)));
        }
        return (planned, ActionPlanner.FindConflicts(planned));
    }

    private static PlanEffect EffectOf(CommandKind kind) => kind switch
    {
        CommandKind.CancelOrder => PlanEffect.Cancel,
        CommandKind.DeleteProduct or CommandKind.DeleteCustomer => PlanEffect.Remove,
        CommandKind.MarkStatus or CommandKind.FuzzyStatusUpdate or CommandKind.MarkAllPendingShipped => PlanEffect.Advance,
        CommandKind.OrderPayment => PlanEffect.Pay,
        CommandKind.RestoreCustomer => PlanEffect.Restore,
        CommandKind.AddProduct or CommandKind.CustomerAdd => PlanEffect.Create,
        _ => PlanEffect.Edit,
    };
}
