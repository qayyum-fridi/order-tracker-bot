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
        // Basic operations (mark one order shipped or paid, advance, counter sale, customer phone or address fix) are NOT here:
        // they run straight away from voice, so the daily work never waits for a YES.
        CommandKind.MarkAllPendingShipped, CommandKind.FuzzyStatusUpdate, CommandKind.CancelOrder,
        CommandKind.DeleteProduct, CommandKind.DeleteCustomer, CommandKind.EditProduct, CommandKind.PriceTiers,
        CommandKind.OrderDeliveryCharge, CommandKind.OrderCourierFee, CommandKind.SupplierSet, CommandKind.Broadcast,
        CommandKind.CashDrawer, CommandKind.CashMovement, CommandKind.Payout, CommandKind.ExpenseEdit, CommandKind.ExpenseDelete
    };

    // Money-changing words only. A step that is not a parsed command (e.g. "price 5000") still counts as risky when it names one of these.
    private static readonly Regex RiskyWord = new(
        @"\b(price|rate|delivery|discount|cancel|edit|returned|remove)\b",
        RegexOptions.IgnoreCase);

    /// <summary>Commands whose whole text is the instruction, so a "nahi" / "mat" / "galat" in it means the seller took it back. Not free-text ones (broadcast, names, delivery "nahi").</summary>
    public static readonly IReadOnlySet<CommandKind> RetractionSensitive = new HashSet<CommandKind>
    {
        CommandKind.MarkStatus, CommandKind.MarkAllPendingShipped, CommandKind.CancelOrder, CommandKind.OrderPayment
    };

    private static readonly Regex Retraction = new(
        @"(?<![\p{L}\p{N}])(?:nahi|nahin|nhi|nai|mat|galat|sorry|wrong|dont|don't|not)(?![\p{L}\p{N}])|نہیں|مت|غلط",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True when the message parses to a retraction-sensitive command but also says the seller took it back ("mark 5 shipped, nahi sorry").</summary>
    public static bool IsRetractedCommand(string message) =>
        CommandParser.TryParse(message) is { } command && RetractionSensitive.Contains(command.Kind) && Retraction.IsMatch(message);

    /// <summary>True when the step names a money or order-state word, or parses to a risky command.</summary>
    public static bool IsRiskyStep(string step) =>
        RiskyWord.IsMatch(step) || CommandParser.TryParse(step) is { } command && RiskyCommands.Contains(command.Kind);
}
