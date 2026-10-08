using OrderTrackerBot.Application.Abstractions;

namespace OrderTrackerBot.Application.Conversation;

// Whenever the bot is unsure, the seller gets something to tap instead of having to type or speak again.
public partial class ConversationEngine
{
    /// <summary>One tappable answer: <paramref name="Label"/> is what the seller reads, <paramref name="Value"/> is the message the bot receives when it is tapped.</summary>
    internal sealed record ChoiceOption(string Label, string Value);

    private static string ShortenChoice(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    /// <summary>
    /// Sends <paramref name="body"/> with tappable options. Up to 3 short options whose label is exactly the text to send become one-tap reply
    /// buttons (a tapped button arrives as its label); anything else becomes a list (a tapped row arrives as its Value) with a Menu row as the way out.
    /// With no options it is a plain text message.
    /// </summary>
    private async Task SendChoicesAsync(string phone, string body, IReadOnlyList<ChoiceOption> options, CancellationToken ct)
    {
        var unique = options.GroupBy(o => o.Value, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).Take(9).ToList();
        if (unique.Count == 0)
        {
            await _sender.SendTextMessageAsync(phone, body, ct);
            return;
        }

        if (unique.Count <= 3 && unique.All(o => o.Label.Length <= 20 && o.Label == o.Value))
        {
            await _sender.SendButtonsMessageAsync(phone, body, unique.Select(o => o.Label).ToList(), ct);
            return;
        }

        var rows = unique.Select(o => new MenuRow(o.Value, ShortenChoice(o.Label, 24))).ToList();
        if (!rows.Any(r => r.Id.Equals("menu", StringComparison.OrdinalIgnoreCase))) rows.Add(new MenuRow("menu", "📋 Menu"));
        // The full wording of each option stays readable in the message itself (row titles are cut at 24 characters).
        var full = string.Join("\n", unique.Select((o, i) => $"{i + 1}️⃣ {ShortenChoice(o.Label, 80)}"));
        var text = ShortenChoice($"{body}\n\n{full}", 1000);
        await _sender.SendListMessageAsync(phone, text, "Chunein 👇", new[] { new MenuSection("Options", rows) }, ct);
    }
}
