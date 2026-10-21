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
        var votes = ctx.LearnedVotes ??= new Dictionary<string, int>();
        if (!learned.ContainsKey(key) && learned.Count >= MaxLearnedPhrases)
        {
            var oldest = learned.Keys.First();
            learned.Remove(oldest);
            votes.Remove(oldest);
        }
        // The same choice twice teaches the phrase; a different choice starts over (a wrongly tapped option must not become permanent).
        var sameChoice = learned.TryGetValue(key, out var previous) && string.Equals(previous, commandText.Trim(), StringComparison.OrdinalIgnoreCase);
        votes[key] = sameChoice ? votes.GetValueOrDefault(key) + 1 : 1;
        learned[key] = commandText.Trim();
        _logger?.LogInformation("Learned phrase: {Kind} (seller-confirmed, votes {Votes})", command.Kind, votes[key]);
        return true;
    }

    /// <summary>A phrase is used once the seller has chosen the same command for it this many times.</summary>
    private const int VotesToUse = 2;

    /// <summary>The command this seller taught for this exact sentence (confirmed twice), or null.</summary>
    private ParsedCommand? LearnedCommandFor(SessionContextData ctx, string message)
    {
        if (ctx.LearnedPhrases is not { Count: > 0 } learned) return null;
        var key = NormalizePhrase(message);
        if (!learned.TryGetValue(key, out var commandText) || (ctx.LearnedVotes?.GetValueOrDefault(key) ?? 0) < VotesToUse) return null;
        var command = CommandParser.TryParse(commandText);
        return command is not null && !ActionRisk.RiskyCommands.Contains(command.Kind) ? command : null;
    }

    private static readonly HashSet<string> PhraseFillerWords = new(StringComparer.OrdinalIgnoreCase)
    { "ka", "ki", "ke", "ko", "do", "de", "hai", "kar", "karo", "karna", "please", "plz", "na", "yar", "bhai", "zara", "jara", "mujhe", "aap" };

    /// <summary>Content words of a phrase: filler removed, a trailing "s" dropped ("sales" = "sale").</summary>
    internal static HashSet<string> PhraseTokens(string phrase) =>
        NormalizePhrase(phrase).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !PhraseFillerWords.Contains(w))
            .Select(w => w.Length > 3 && w.EndsWith('s') ? w[..^1] : w)
            .ToHashSet();

    /// <summary>
    /// The taught phrase (confirmed twice, command not risky) that LOOKS like this sentence: at least two shared content words and half of all content words shared.
    /// Only a candidate: the AI decides whether the two really ask for the same thing.
    /// </summary>
    internal (string Phrase, string Command)? SimilarLearnedPhrase(SessionContextData ctx, string message)
    {
        if (ctx.LearnedPhrases is not { Count: > 0 } learned) return null;
        var mine = PhraseTokens(message);
        if (mine.Count < 2) return null;
        (string Phrase, string Command, double Score)? best = null;
        foreach (var (phrase, commandText) in learned)
        {
            if ((ctx.LearnedVotes?.GetValueOrDefault(phrase) ?? 0) < VotesToUse) continue;
            var theirs = PhraseTokens(phrase);
            var shared = mine.Intersect(theirs).Count();
            var score = shared / (double)mine.Union(theirs).Count();
            if (shared >= 2 && score >= 0.5 && (best is null || score > best.Value.Score)) best = (phrase, commandText, score);
        }
        return best is null ? null : (best.Value.Phrase, best.Value.Command);
    }
}
