using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Changing a saved order's items from speech or text ("order 3 mein kurti ki quantity 2 kar do", "Ali ke order se shirt nikal do").
// The model's changes are checked against the order and the seller's own words, shown for YES, and on YES run through the same
// edit instructions as "edit order", so the stock, totals and undo behave exactly as for a typed edit.
public partial class ConversationEngine
{
    /// <summary>Shows the changes for a saved order and waits for YES. Nothing changes until the seller answers.</summary>
    private async Task ProposeOrderEditAsync(Seller seller, ConversationSession session, SessionContextData ctx, TurnAction action, int? saidOrderId,
        string message, CancellationToken ct)
    {
        var orderId = saidOrderId ?? (await FindLatestOrderByCustomerNameAsync(seller, action.CustomerName!, ct))?.Id;
        var order = orderId is null ? null : await LoadOrderForEditAsync(seller, orderId.Value, ct);
        if (order is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("order-edit.014"), ct);
            return;
        }
        if (order.Status is OrderStatus.Cancelled or OrderStatus.Returned)
        {
            await ReplyAsync(seller, MessageTemplates.Get("order-edit.001", order.Id, Formatters.Status(order.Status)), ct);
            return;
        }
        if (IsDispatched(order))
        {
            await ReplyAsync(seller, DispatchedLockText(order), ct);
            return;
        }

        var items = order.Items.OrderBy(i => i.Id).ToList();
        var said = SaidNumbers(message);
        var lines = new List<string>();
        var described = new List<string>();
        foreach (var edit in action.OrderEdits)
        {
            var bad = MessageTemplates.Get("model-edit.005", order.Id, DescribeOp(edit));
            if (edit.Op == "add")
            {
                // A product added to the order: its name as said, and a count the seller said (one when none was said).
                var name = edit.Item?.Trim();
                var count = edit.Quantity ?? 1;
                if (string.IsNullOrWhiteSpace(name) || count < 1 || count > MaxEditQuantity || (edit.Quantity is { } said1 && !said.Contains(said1)))
                {
                    await ReplyAsync(seller, bad, ct);
                    return;
                }
                lines.Add($"add {name} {count}");
                described.Add(MessageTemplates.Get("model-edit.009", name, count));
                continue;
            }

            var index = FindItemIndex(items, edit.Item);
            if (index is null)
            {
                await ReplyAsync(seller, bad, ct);
                return;
            }
            var label = items[index.Value - 1].ProductNameSnapshot;
            switch (edit.Op)
            {
                case "set_quantity" when edit.Quantity is { } qty && qty >= 1 && qty <= MaxEditQuantity && said.Contains(qty):
                    lines.Add($"{index} = {qty}");
                    described.Add(MessageTemplates.Get("model-edit.006", label, qty));
                    break;
                case "set_price" when edit.Price is { } price && price > 0 && said.Contains((long)Math.Round(price, MidpointRounding.AwayFromZero)):
                    var text = price.ToString("0.##", CultureInfo.InvariantCulture);
                    lines.Add($"price {index} = {text}");
                    described.Add(MessageTemplates.Get("model-edit.007", label, text));
                    break;
                case "remove":
                    lines.Add($"remove {index}");
                    described.Add(MessageTemplates.Get("model-edit.008", label));
                    break;
                default:
                    // A quantity or price the seller did not say is never applied.
                    await ReplyAsync(seller, bad, ct);
                    return;
            }
        }
        if (lines.Count == 0) return;

        ctx.ModelEdit = new ModelEditData { Kind = TurnAction.EditOrder, Name = order.Id.ToString(CultureInfo.InvariantCulture), Lines = lines };
        SetState(session, ConversationState.AwaitingModelStatusConfirmation);
        await AskYesNoAsync(seller, MessageTemplates.Get("model-edit.004", order.Id, string.Join("\n", described.Select(d => "• " + d))), ct);
    }

    /// <summary>On YES: opens "edit order" for the order and runs each change as the seller's typed edit instruction, then closes edit mode.</summary>
    private async Task ApplyModelOrderEditAsync(Seller seller, ConversationSession session, SessionContextData ctx, ModelEditData edit, CancellationToken ct)
    {
        if (!int.TryParse(edit.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var orderId)) return;
        var order = await LoadOrderForEditAsync(seller, orderId, ct);
        if (order is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("order-edit.014"), ct);
            return;
        }
        if (order.Status is OrderStatus.Cancelled or OrderStatus.Returned)
        {
            await ReplyAsync(seller, MessageTemplates.Get("order-edit.001", order.Id, Formatters.Status(order.Status)), ct);
            return;
        }
        if (IsDispatched(order))
        {
            await ReplyAsync(seller, DispatchedLockText(order), ct);
            return;
        }

        await OpenOrderEditAsync(seller, session, ctx, order, ct);
        foreach (var line in edit.Lines)
        {
            if (session.State != ConversationState.AwaitingOrderEdit) break;
            await HandleOrderEditAsync(seller, session, ctx, line, ct);
        }
        if (session.State == ConversationState.AwaitingOrderEdit)
            await HandleOrderEditAsync(seller, session, ctx, "done", ct);
    }

    /// <summary>The item number ("1" = first line of the order, by the order's own numbering) for a product name the seller said; null when none or several match.</summary>
    internal static int? FindItemIndex(IReadOnlyList<OrderItem> items, string? spoken)
    {
        if (string.IsNullOrWhiteSpace(spoken)) return null;
        var wanted = spoken.Trim();
        var exact = Enumerable.Range(0, items.Count).Where(i => string.Equals(items[i].ProductNameSnapshot, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0] + 1;
        var partial = Enumerable.Range(0, items.Count)
            .Where(i => items[i].ProductNameSnapshot.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                        || wanted.Contains(items[i].ProductNameSnapshot, StringComparison.OrdinalIgnoreCase)).ToList();
        return partial.Count == 1 ? partial[0] + 1 : null;
    }

    private static string DescribeOp(AiOrderEdit edit) =>
        edit.Op + (edit.Item is null ? "" : " " + edit.Item) + (edit.Quantity is { } q ? " " + q.ToString(CultureInfo.InvariantCulture) : "")
        + (edit.Price is { } p ? " " + p.ToString("0.##", CultureInfo.InvariantCulture) : "");
}
