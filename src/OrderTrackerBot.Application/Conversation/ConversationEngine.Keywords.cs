using OrderTrackerBot.Application.Intents;

namespace OrderTrackerBot.Application.Conversation;

public partial class ConversationEngine
{
    /// <summary>
    /// Feature keywords (Roman Urdu / English / Urdu script) that mean the seller is talking about something the bot can do. Read at runtime from
    /// the feature-area rows (<see cref="KeywordStore"/>), so an area can be changed without a build. Every option is a typed command, so picking
    /// one runs it. Most specific areas first: the first hit wins, and generic words (product, order) come last.
    /// </summary>
    public static IReadOnlyList<CommandArea> CommandKeywordAreas => KeywordStore.CommandAreas;

    /// <summary>
    /// The seller said something we did not understand but it names a feature (a voice note is rewritten to text first, so this covers both):
    /// ask which command they mean instead of "I only do orders" or a generic "didn't understand".
    /// </summary>
    public static bool TryKeywordClarification(string? text, out string question, out List<string> options)
    {
        question = "";
        options = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return false;

        foreach (var area in CommandKeywordAreas)
        {
            if (!area.Keywords.IsMatch(text)) continue;
            question = area.Question;
            options = area.Options.ToList();
            return true;
        }

        return false;
    }
}
