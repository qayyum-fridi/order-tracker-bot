using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// One way to read a seller's answer to any list of choices (the kind of shop, what a picture is, ...): the item's number, then an exact match of
/// its label or value, then the AI by meaning (the same reading the industry question uses). No word list per question.
/// </summary>
public partial class ConversationEngine
{
    /// <summary>
    /// The value of the option the seller meant, or null when it is none of them. <paramref name="allowNumber"/> is false for free text such as a
    /// caption, where a bare number is not a list pick.
    /// </summary>
    private async Task<string?> ResolveChoiceAsync(Seller seller, SessionContextData ctx, string? message, IReadOnlyList<(string Label, string Value)> options,
        bool allowNumber, CancellationToken ct)
    {
        var text = message?.Trim();
        if (string.IsNullOrEmpty(text) || options.Count == 0) return null;
        if (allowNumber && int.TryParse(text, out var number)) return number >= 1 && number <= options.Count ? options[number - 1].Value : null;

        var exact = options.FirstOrDefault(o => string.Equals(o.Label, text, StringComparison.OrdinalIgnoreCase) || string.Equals(o.Value, text, StringComparison.OrdinalIgnoreCase));
        if (exact.Value is not null) return exact.Value;

        var byMeaning = await TryReadChoiceAsync(DescribeVoiceSituation(seller, ctx), options.Select(o => o.Label).ToList(), text, ct);
        return byMeaning is { } picked && picked >= 1 && picked <= options.Count ? options[picked - 1].Value : null;
    }
}
