using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

// "Sara ka phone 0300…", "customer Sara address House 5": one-line customer corrections, undoable.
public partial class ConversationEngine
{
    private async Task HandleCustomerUpdateAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var wanted = cmd.Text!.Trim();
        var customers = await _db.Customers.Where(c => c.SellerId == seller.Id && c.DeletedAt == null).ToListAsync(ct);
        var matches = customers.Where(c => string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
            matches = customers.Where(c => c.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matches.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("customers.003", wanted), ct);
            return;
        }
        if (matches.Count > 1)
        {
            await ReplyAsync(seller,
                MessageTemplates.Get("customer-update.001", wanted, matches.Count, string.Join("\n", matches.Select(c => $"• {c.Name}{(string.IsNullOrWhiteSpace(c.Phone) ? "" : $" ({c.Phone})")}")), matches[0].Name, cmd.Text2), ct);
            return;
        }

        var customer = matches[0];
        var previous = cmd.Text2 switch { "phone" => customer.Phone, "address" => customer.Address, "city" => customer.City, _ => customer.Name };
        _db.ActionLogs.Add(new ActionLog
        {
            SellerId = seller.Id, ActionType = ActionType.CustomerUpdated,
            PayloadJson = JsonSerializer.Serialize(new { CustomerId = customer.Id, Field = cmd.Text2, Previous = previous })
        });
        switch (cmd.Text2)
        {
            case "phone": customer.Phone = cmd.Text3; break;
            case "address": customer.Address = cmd.Text3; break;
            case "city": customer.City = cmd.Text3; break;
            default: customer.Name = cmd.Text3!; break;
        }

        var label = cmd.Text2 switch { "phone" => "phone", "address" => "address", "city" => "shehar", _ => "naam" };
        await ReplyAsync(seller,
            MessageTemplates.Get("customer-update.002", (cmd.Text2 == "name" ? previous : customer.Name), label, cmd.Text3, (string.IsNullOrWhiteSpace(previous) ? "" : $" (pehle: {previous})")), ct);
        SuggestNext($"order for {customer.Name}", $"{customer.Name} ka order");
    }

    private async Task UndoCustomerUpdateAsync(Seller seller, ActionLog log, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(log.PayloadJson);
        var id = doc.RootElement.GetProperty("CustomerId").GetInt32();
        var field = doc.RootElement.GetProperty("Field").GetString();
        var previous = doc.RootElement.GetProperty("Previous").GetString();
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id && c.SellerId == seller.Id, ct);
        if (customer is null) return;
        switch (field)
        {
            case "phone": customer.Phone = previous; break;
            case "address": customer.Address = previous; break;
            case "city": customer.City = previous; break;
            default: customer.Name = previous ?? customer.Name; break;
        }
        await ReplyAsync(seller, MessageTemplates.Get("customer-update.003", customer.Name, field, previous ?? "(khali)"), ct);
    }
}
