using System.Text.RegularExpressions;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// One list of actions that change money or order state. Voice steps use it to decide whether a YES is needed before they run,
/// and the voice rewrite gate uses it to decide whether a refused rewrite may be offered as choices.
/// </summary>
public static class ActionRisk
{
    public static readonly IReadOnlySet<CommandKind> RiskyCommands = new HashSet<CommandKind>
    {
        CommandKind.MarkStatus, CommandKind.MarkAllPendingShipped, CommandKind.FuzzyStatusUpdate, CommandKind.CancelOrder,
        CommandKind.DeleteProduct, CommandKind.DeleteCustomer, CommandKind.EditProduct, CommandKind.PriceTiers,
        CommandKind.OrderPayment, CommandKind.OrderDeliveryCharge, CommandKind.CustomerUpdate, CommandKind.Broadcast
    };

    // Money or order-state words. A step that is not a parsed command (e.g. "price 5000") still counts as risky when it names one of these.
    private static readonly Regex RiskyWord = new(
        @"\b(price|rate|delivery|discount|cancel|edit|paid|advance|payment|cod|shipped|delivered|returned|status|customer|remove|add)\b",
        RegexOptions.IgnoreCase);

    /// <summary>True when the step names a money or order-state word, or parses to a risky command.</summary>
    public static bool IsRiskyStep(string step) =>
        RiskyWord.IsMatch(step) || CommandParser.TryParse(step) is { } command && RiskyCommands.Contains(command.Kind);
}
