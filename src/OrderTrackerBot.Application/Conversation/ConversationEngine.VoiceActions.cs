using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// A voice note is read by an AI that returns typed steps, each labelled with an action. The AI is only told which actions are valid in the bot's
// current state, and the engine re-checks every step against that list, so a step the seller never could have meant ("done" while adding
// products, "setup" while answering a yes/no) is dropped instead of being run.
public partial class ConversationEngine
{
    private static readonly string[] ActionsAnyAnswer = { "reply", "command", "yes", "no", "choose", "help" };

    /// <summary>The step kinds the bot accepts for a voice note in this state: reply, command, yes, no, choose, edit_step, done, skip, help.</summary>
    internal static IReadOnlyList<string> AllowedVoiceActions(Seller seller, ConversationState state) => state switch
    {
        ConversationState.Idle when seller.OnboardingComplete => new[] { "command", "reply", "help" },
        ConversationState.Idle => new[] { "reply", "help" },
        ConversationState.OnboardingLanguage or ConversationState.AwaitingLanguageChoice or ConversationState.OnboardingStartChoice
            or ConversationState.OnboardingBusinessName or ConversationState.OnboardingCatalogSize => new[] { "reply", "choose", "help" },
        ConversationState.OnboardingOptionalDetails => new[] { "reply", "skip", "help" },
        ConversationState.OnboardingAddProduct => new[] { "reply", "done", "skip", "help" },
        ConversationState.AwaitingOrderEdit => new[] { "edit_step", "done", "command", "help" },
        ConversationState.AwaitingOrderConfirmation or ConversationState.AwaitingCancelConfirmation or ConversationState.AwaitingBulkStatusConfirmation
            or ConversationState.AwaitingDuplicateOrderConfirmation or ConversationState.AwaitingCodCollectedConfirmation or ConversationState.AwaitingResetConfirmation
            or ConversationState.AwaitingDeleteCustomerConfirmation or ConversationState.AwaitingLoyaltyDiscountConfirmation or ConversationState.AwaitingMultiOrderConfirmation
            or ConversationState.AwaitingSupportReplyConfirmation or ConversationState.AwaitingVoiceConfirmation or ConversationState.AwaitingImportConfirmation => new[] { "yes", "no", "reply", "help" },
        ConversationState.AwaitingClarificationChoice or ConversationState.AwaitingOrderGroupingChoice or ConversationState.AwaitingRuntimeFilterChoice
            or ConversationState.AwaitingBroadcastAudienceChoice or ConversationState.AwaitingBroadcastChannelChoice or ConversationState.AwaitingReceiptOrderChoice
            or ConversationState.AwaitingSupportQueryPick => new[] { "choose", "reply", "help" },
        _ => ActionsAnyAnswer
    };

    /// <summary>
    /// Keeps the steps whose action is valid now and whose text fits the action, and returns them as the text the engine will receive
    /// ("yes", "no", "done", "skip" and bare option numbers are written by the code, not taken from the model). "edit order N" makes
    /// edit_step and done valid for the steps after it, as in "edit order 12" / "price 1 = 1500" / "done".
    /// </summary>
    internal static List<string> ValidateVoiceSteps(ConversationState state, Seller seller, IReadOnlyList<string> steps, IReadOnlyList<string> actions)
    {
        var allowed = AllowedVoiceActions(seller, state);
        var inEdit = state == ConversationState.AwaitingOrderEdit;
        var kept = new List<string>();

        for (var i = 0; i < steps.Count; i++)
        {
            var action = actions[i].Trim().ToLowerInvariant();
            var text = steps[i].Trim();
            var valid = allowed.Contains(action) || (inEdit && action is "edit_step" or "done");
            if (!valid) continue;

            switch (action)
            {
                case "yes" or "no" or "done" or "skip":
                    kept.Add(action);
                    break;
                case "choose":
                    if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d{1,2}$")) kept.Add(text);
                    break;
                case "edit_step":
                    if (CommandParser.TryParseOrderEdit(text, out _)) kept.Add(text);
                    break;
                case "command":
                    var command = CommandParser.TryParse(text);
                    if (command is null)
                    {
                        // Not a real command: it is only acceptable as a plain reply (a dictated order, a product line).
                        if (allowed.Contains("reply")) kept.Add(text);
                        break;
                    }
                    inEdit = command.Kind == CommandKind.EditOrder;
                    kept.Add(text);
                    break;
                case "reply" or "help":
                    kept.Add(text);
                    break;
            }
        }
        return kept;
    }
}
