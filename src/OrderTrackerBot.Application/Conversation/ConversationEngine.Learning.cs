using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace OrderTrackerBot.Application.Conversation;

// Per-seller learning: when the bot did not understand a sentence and the seller then PICKED the right command (or typed it), that pair is remembered for this seller,
// so the same sentence is understood next time without asking. Only the seller's own confirmed choice is stored, and never a risky (money / cancel / status) command.
public partial class ConversationEngine
{
    private const int MaxLearnedPhrases = 100;

    /// <summary>Lower case, letters and digits only, single spaces: "Customer  STATUS bhejo!" and "customer status bhejo" are the same phrase.</summary>
    internal static string NormalizePhrase(string text) =>
        Regex.Replace(Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}\s]", " "), @"\s+", " ").Trim();

    /// <summary>Remembers phrase -> command text for this seller (oldest forgotten first). Returns true when something was stored.</summary>
    private bool LearnPhrase(SessionContextData ctx, string? phrase, string? commandText)
    {
        if (string.IsNullOrWhiteSpace(phrase) || string.IsNullOrWhiteSpace(commandText)) return false;
        var key = NormalizePhrase(phrase);
        if (key.Length < 3 || key.Length > 120) return false;
        // The phrase already IS a command (the rules would understand it anyway), or the command is a risky one: nothing to learn.
        if (CommandParser.TryParse(phrase) is not null) return false;
        if (CommandParser.TryParse(commandText) is not { } command || ActionRisk.RiskyCommands.Contains(command.Kind)) return false;

        var learned = ctx.LearnedPhrases ??= new Dictionary<string, string>();
        if (!learned.ContainsKey(key) && learned.Count >= MaxLearnedPhrases) learned.Remove(learned.Keys.First());
        learned[key] = commandText.Trim();
        _logger?.LogInformation("Learned phrase: {Kind} (seller-confirmed)", command.Kind);
        return true;
    }

    /// <summary>The command this seller once taught for this exact sentence, or null.</summary>
    private ParsedCommand? LearnedCommandFor(SessionContextData ctx, string message)
    {
        if (ctx.LearnedPhrases is not { Count: > 0 } learned) return null;
        if (!learned.TryGetValue(NormalizePhrase(message), out var commandText)) return null;
        var command = CommandParser.TryParse(commandText);
        return command is not null && !ActionRisk.RiskyCommands.Contains(command.Kind) ? command : null;
    }
}
