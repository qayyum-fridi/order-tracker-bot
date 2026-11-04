using Microsoft.Extensions.Logging;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Phase 1 of docs/dynamic-understanding-plan.md: a yes / no word list is a quick path, not a gate. A reply the list does not know goes to the AI, which must quote
// the words it based its reading on; the engine checks the quote is really in the message. Only harmless questions: cancel / reset / delete / bulk status / COD collected keep
// the exact words.
public partial class ConversationEngine
{
    private static readonly HashSet<ConversationState> DynamicYesNoStates = new()
    {
        ConversationState.AwaitingOrderConfirmation, ConversationState.AwaitingMultiOrderConfirmation, ConversationState.AwaitingDuplicateOrderConfirmation,
        ConversationState.AwaitingLoyaltyDiscountConfirmation, ConversationState.AwaitingSupportReplyConfirmation, ConversationState.AwaitingImportConfirmation,
    };

    /// <summary>"yes" or "no" when the AI reads the message that way and the words it quotes are in the message; null for everything else (the old flow then runs unchanged).</summary>
    private async Task<string?> TryReadYesNoAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        if (!_features.DynamicAnswers || !DynamicYesNoStates.Contains(session.State)) return null;
        var text = message.Trim();
        // The plain words, buttons, commands and longer or numeric messages are not this path's business.
        if (text.Length == 0 || text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 6 || text.Any(char.IsDigit) || text.Contains('\n')
            || CommandParser.IsAffirmative(text) || CommandParser.IsNegative(text) || CommandParser.IsAcknowledgement(text)
            || Array.IndexOf(ConfirmButtonLabels, text) >= 0 || CommandParser.TryParse(text) is not null || HedgeWord.IsMatch(text))
            return null;

        var reading = await _ai.ReadYesNoWithQuoteAsync(DescribeVoiceSituation(seller, ctx), text, ct);
        if (reading is not { Answer: "yes" or "no" } || !QuoteIsInMessage(reading.Quote, text)) return null;
        _logger?.LogInformation("Dynamic answer read: {Answer} in {State}", reading.Answer, session.State);
        return reading.Answer;
    }

    /// <summary>
    /// The 1-based option of a numbered list that a reply in words means ("pehla wala", "joote bechta hoon"), read by the AI; null when it is not sure, the quote is not in the
    /// message, or the old flow should handle the message. Used where the old flow would otherwise treat the reply as unknown. Behind the same flag as yes / no.
    /// </summary>
    private async Task<int?> TryReadChoiceAsync(string situation, IReadOnlyList<string> options, string message, CancellationToken ct)
    {
        if (!_features.DynamicAnswers || options.Count == 0) return null;
        var text = message.Trim();
        if (text.Length == 0 || text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 8 || text.Any(char.IsDigit) || text.Contains('\n')
            || CommandParser.TryParse(text) is not null || HedgeWord.IsMatch(text))
            return null;

        var reading = await _ai.ReadChoiceWithQuoteAsync(situation, options, text, ct);
        if (reading is not { Option: >= 1 } || reading.Option > options.Count || !QuoteIsInMessage(reading.Quote, text)) return null;
        _logger?.LogInformation("Dynamic choice read: option {Option} of {Count}", reading.Option, options.Count);
        return reading.Option;
    }

    /// <summary>The quoted words must be in the message (letters and digits only, case ignored, so spacing and punctuation do not matter).</summary>
    private static bool QuoteIsInMessage(string? quote, string message)
    {
        static string Squash(string value) => System.Text.RegularExpressions.Regex.Replace(value, @"[^\p{L}\p{N}]+", "").ToLowerInvariant();
        var wanted = Squash(quote ?? "");
        return wanted.Length >= 2 && Squash(message).Contains(wanted);
    }
}
