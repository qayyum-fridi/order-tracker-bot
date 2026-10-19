using System.Text.Json;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

// A product line is shown back to the seller (name, colour, price, stock) and saved only after YES.
// Off when Features:ProductConfirmation is false (unit tests keep the direct save).
public partial class ConversationEngine
{
    // One or more product lines (typed, or spoken and read by the AI) are shown back; they are saved together on YES.
    private async Task AskProductConfirmationAsync(Seller seller, SessionContextData ctx, IReadOnlyList<ProductLine> lines, CancellationToken ct)
    {
        ctx.PendingProductJson = JsonSerializer.Serialize(lines);
        var body = lines.Count == 1
            ? MessageTemplates.Get("commands.050", lines[0].Name, lines[0].Extras?.Color ?? "-", Formatters.Money(lines[0].Price), lines[0].Extras?.Stock?.ToString() ?? "-")
            : MessageTemplates.Get("commands.052", string.Join("\n", lines.Select(l =>
                $"• {l.Name} (rang: {l.Extras?.Color ?? "-"}) - {Formatters.Money(l.Price)}, stock: {l.Extras?.Stock?.ToString() ?? "-"}")));
        await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, body, new[] { "✅ Save", "❌ Cancel" }, ct);
    }

    // Runs before any other handling while products wait: YES saves them, NO drops them, any other message drops them and runs as usual.
    private async Task<bool> TryHandlePendingProductAsync(Seller seller, SessionContextData ctx, string message, CancellationToken ct)
    {
        if (ctx.PendingProductJson is null) return false;
        var pending = JsonSerializer.Deserialize<List<ProductLine>>(ctx.PendingProductJson);
        ctx.PendingProductJson = null;
        if (pending is null || pending.Count == 0) return false;
        if (CommandParser.IsAffirmative(message))
        {
            foreach (var line in pending)
                await HandleAddProductAsync(seller, new ParsedCommand { Kind = CommandKind.AddProduct, Text = line.Name, Amount = line.Price, Product = line }, ct);
            return true;
        }
        if (CommandParser.IsNegative(message))
        {
            await ReplyAsync(seller, MessageTemplates.Get("commands.051"), ct);
            return true;
        }
        return false;
    }
}
