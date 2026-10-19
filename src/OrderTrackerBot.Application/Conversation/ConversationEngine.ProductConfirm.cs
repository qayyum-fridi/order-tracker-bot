using System.Text.Json;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

// A product line is shown back to the seller (name, colour, price, stock) and saved only after YES.
// Off when Features:ProductConfirmation is false (unit tests keep the direct save).
public partial class ConversationEngine
{
    private async Task AskProductConfirmationAsync(Seller seller, SessionContextData ctx, ProductLine line, CancellationToken ct)
    {
        ctx.PendingProductJson = JsonSerializer.Serialize(line);
        await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber,
            MessageTemplates.Get("commands.050", line.Name, line.Extras?.Color ?? "-", Formatters.Money(line.Price), line.Extras?.Stock?.ToString() ?? "-"),
            new[] { "✅ Save", "❌ Cancel" }, ct);
    }

    // Runs before any other handling while a product waits: YES saves it, NO drops it, any other message drops it and runs as usual.
    private async Task<bool> TryHandlePendingProductAsync(Seller seller, SessionContextData ctx, string message, CancellationToken ct)
    {
        if (ctx.PendingProductJson is null) return false;
        var pending = JsonSerializer.Deserialize<ProductLine>(ctx.PendingProductJson);
        ctx.PendingProductJson = null;
        if (pending is null) return false;
        if (CommandParser.IsAffirmative(message))
        {
            await HandleAddProductAsync(seller, new ParsedCommand { Kind = CommandKind.AddProduct, Text = pending.Name, Amount = pending.Price, Product = pending }, ct);
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
