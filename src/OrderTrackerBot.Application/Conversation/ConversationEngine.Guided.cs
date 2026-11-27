using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Guided questions: the model understood WHAT the seller wants, but a detail is missing ("Sara ka order cancel kar do" has no order number) or points
// at several records ("Ali" is two customers). Instead of the generic "samajh nahi aaya", the bot lists the candidates as taps; a tap fills the
// detail and the same action runs, so every write still waits for its usual YES. A missing value (a new phone, a new price) gets one specific question.
public partial class ConversationEngine
{
    private const int GuidedChoiceLimit = 8;

    /// <summary>How deep a filled-in guided action has asked again (RunGuidedAsync): one follow-up question at most, so it can never loop.</summary>
    private int _guidedDepth;

    /// <summary>
    /// True when a guided question was sent (or the only candidate was used). False = nothing to offer: the caller sends its usual reply.
    /// <paramref name="beforeAsk"/> runs before anything is sent (the voice echo).
    /// </summary>
    private async Task<bool> TryGuideAsync(Seller seller, ConversationSession session, SessionContextData ctx, TurnAction action, string message,
        Func<Task>? beforeAsk, CancellationToken ct)
    {
        switch (action.Action)
        {
            case TurnAction.CancelOrder:
            case TurnAction.UpdateStatus when action.Status is not null:
            case TurnAction.EditOrder when action.OrderEdits.Count > 0:
            case TurnAction.ExchangeSize when action.ItemName is not null && !string.IsNullOrWhiteSpace(action.Value):
            case TurnAction.ShowOrder:
                return await GuideOrderAsync(seller, session, ctx, action, message, beforeAsk, ct);

            // A payment with no order the bot can find: the unpaid orders to tap. With the order known but no amount said: ask how much came.
            case TurnAction.RecordPayment when await ModelPaymentOrderAsync(seller, action, message, ct) is null:
                return await GuideOrderAsync(seller, session, ctx, action, message, beforeAsk, ct);

            case TurnAction.RecordPayment:
                if (beforeAsk is not null) await beforeAsk();
                var payFor = (await ModelPaymentOrderAsync(seller, action, message, ct))!;
                await ReplyAsync(seller, MessageTemplates.Get("guided.pay.amount", payFor.Customer?.Name ?? $"#{payFor.Id}", payFor.Id,
                    Formatters.Money(OrderMoney.Balance(payFor))), ct);
                return true;

            // A tracking number with no order the bot can find: the orders to tap.
            case TurnAction.SetTracking when !string.IsNullOrWhiteSpace(action.Value) && Compact(message).Contains(Compact(action.Value), StringComparison.OrdinalIgnoreCase):
                return await GuideOrderAsync(seller, session, ctx, action, message, beforeAsk, ct);

            case TurnAction.EditCustomer or TurnAction.DeleteCustomer when !string.IsNullOrWhiteSpace(action.CustomerName):
                return await GuideCustomerAsync(seller, session, ctx, action, message, beforeAsk, ct);

            case TurnAction.DeleteCustomer:
                if (beforeAsk is not null) await beforeAsk();
                await ReplyAsync(seller, MessageTemplates.Get("guided.customer.delete"), ct);
                return true;

            case TurnAction.DeleteProduct:
                return await GuideProductAsync(seller, session, ctx, action, message, beforeAsk, ct);

            case TurnAction.EditProduct when action.Products.Count > 0 && action.Products[0].Price is not > 0 && !string.IsNullOrWhiteSpace(action.Products[0].Name):
                if (beforeAsk is not null) await beforeAsk();
                await ReplyAsync(seller, MessageTemplates.Get("guided.product.price", action.Products[0].Name.Trim()), ct);
                return true;

            default:
                return false;
        }
    }

    /// <summary>A runnable edit_customer whose name fits several saved customers ("Ali" = Ali Raza and Ali Hassan) must not guess one of them.</summary>
    private async Task<bool> NeedsCustomerChoiceAsync(Seller seller, TurnAction action, CancellationToken ct)
    {
        if (action.Action is not (TurnAction.EditCustomer or TurnAction.DeleteCustomer) || string.IsNullOrWhiteSpace(action.CustomerName)) return false;
        var reference = await new ReferenceResolver(_db).ResolveCustomerAsync(seller.Id, action.CustomerName!, ct);
        return reference.Status == ReferenceStatus.Multiple;
    }

    private Task<List<Customer>> CustomersNamedAsync(Seller seller, string name, CancellationToken ct)
    {
        var wanted = name.Trim().ToLower();
        return _db.Customers.Where(c => c.SellerId == seller.Id && c.DeletedAt == null && c.Name.ToLower().Contains(wanted))
            .OrderBy(c => c.Name).Take(GuidedChoiceLimit).ToListAsync(ct);
    }

    private async Task<bool> GuideOrderAsync(Seller seller, ConversationSession session, SessionContextData ctx, TurnAction action, string message,
        Func<Task>? beforeAsk, CancellationToken ct)
    {
        var named = !string.IsNullOrWhiteSpace(action.CustomerName) && SaidName(action.CustomerName, message)
            ? await CustomersNamedAsync(seller, action.CustomerName!, ct)
            : new List<Customer>();
        var ids = named.Select(c => c.Id).ToList();
        var query = _db.Orders.Include(o => o.Customer).Include(o => o.Items).Where(o => o.SellerId == seller.Id);
        if (ids.Count > 0) query = query.Where(o => ids.Contains(o.CustomerId));
        // A cancelled or returned order cannot be cancelled, edited or moved on; only "show" may pick it.
        if (action.Action != TurnAction.ShowOrder) query = query.Where(o => o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned);
        if (action.Action == TurnAction.RecordPayment) query = query.Where(o => o.PaymentStatus != PaymentStatus.Paid);
        var orders = await query.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id).Take(GuidedChoiceLimit).ToListAsync(ct);
        if (orders.Count == 0) return false;

        if (beforeAsk is not null) await beforeAsk();
        // The named customer has one open order: that is the one meant. The action still asks its own YES before anything changes.
        if (orders.Count == 1 && ids.Count > 0)
        {
            await RunGuidedAsync(seller, session, ctx, new QueuedModelTurn(Plain(action), message), orders[0].Id, null, ct);
            return true;
        }

        var question = action.Action switch
        {
            TurnAction.CancelOrder => MessageTemplates.Get("guided.order.cancel"),
            TurnAction.UpdateStatus => MessageTemplates.Get("guided.order.status",
                Enum.TryParse<OrderStatus>(action.Status, true, out var status) ? Formatters.Status(status) : action.Status),
            TurnAction.EditOrder => MessageTemplates.Get("guided.order.edit"),
            TurnAction.ExchangeSize => MessageTemplates.Get("guided.order.exchange"),
            TurnAction.RecordPayment => MessageTemplates.Get("guided.order.pay"),
            TurnAction.SetTracking => MessageTemplates.Get("guided.order.tracking"),
            _ => MessageTemplates.Get("guided.order.show"),
        };
        if (!string.IsNullOrWhiteSpace(action.CustomerName) && ids.Count == 0)
            question = MessageTemplates.Get("guided.nocustomer", action.CustomerName!.Trim()) + "\n" + question;

        var options = orders.Select(o => new ChoiceOption(OrderChoiceLabel(o), "#" + o.Id)).ToList();
        AwaitGuidedChoice(session, ctx, action, message, options);
        await SendChoicesAsync(seller.WhatsAppPhoneNumber, question, options, ct);
        return true;
    }

    private async Task<bool> GuideCustomerAsync(Seller seller, ConversationSession session, SessionContextData ctx, TurnAction action, string message,
        Func<Task>? beforeAsk, CancellationToken ct)
    {
        var matches = await CustomersNamedAsync(seller, action.CustomerName!, ct);
        if (matches.Count == 0) return false;
        if (beforeAsk is not null) await beforeAsk();

        if (matches.Count > 1)
        {
            var options = matches.Select(c => new ChoiceOption(string.IsNullOrWhiteSpace(c.Phone) ? c.Name : $"{c.Name} · {c.Phone}", "@" + c.Id)).ToList();
            AwaitGuidedChoice(session, ctx, action, message, options);
            await SendChoicesAsync(seller.WhatsAppPhoneNumber, MessageTemplates.Get("guided.customer.which", action.CustomerName!.Trim(), matches.Count), options, ct);
            return true;
        }

        // One customer: a delete runs as it is (it asks its own YES).
        if (action.Action == TurnAction.DeleteCustomer) return false;
        // One customer, but the new value (or which detail) was not said: ask for exactly that, with the sentence to say.
        var name = matches[0].Name;
        await ReplyAsync(seller, action.Field is { } field
            ? MessageTemplates.Get("guided.customer.value", name, ModelFieldLabel(field), field switch
            {
                "phone" => "03001234567",
                "city" => "Lahore",
                "name" => name,
                _ => "Model Town Lahore",
            })
            : MessageTemplates.Get("guided.customer.what", name), ct);
        return true;
    }

    /// <summary>"Ek product hatana hai": the products to tap, newest first. A tap fills the name and the delete asks its YES.</summary>
    private async Task<bool> GuideProductAsync(Seller seller, ConversationSession session, SessionContextData ctx, TurnAction action, string message,
        Func<Task>? beforeAsk, CancellationToken ct)
    {
        var products = await _db.Products.Where(p => p.SellerId == seller.Id && p.IsActive)
            .OrderByDescending(p => p.Id).Take(GuidedChoiceLimit).ToListAsync(ct);
        if (products.Count == 0) return false;
        if (beforeAsk is not null) await beforeAsk();
        var options = products.Select(p => new ChoiceOption($"{Formatters.ProductLabel(p)} · {Formatters.Money(p.Price)}", "%" + p.Id)).ToList();
        AwaitGuidedChoice(session, ctx, action, message, options);
        await SendChoicesAsync(seller.WhatsAppPhoneNumber, MessageTemplates.Get("guided.product.delete"), options, ct);
        return true;
    }

    private static string OrderChoiceLabel(Order o)
    {
        var items = string.Join(", ", o.Items.Select(i => $"{i.Quantity} {i.ProductNameSnapshot}"));
        return $"#{o.Id} {o.Customer?.Name} — {items}";
    }

    private void AwaitGuidedChoice(ConversationSession session, SessionContextData ctx, TurnAction action, string message, IReadOnlyList<ChoiceOption> options)
    {
        ctx.GuidedTurn = new QueuedModelTurn(Plain(action), message);
        ctx.ClarificationOptions = options.Select(o => o.Value).ToList();
        SetState(session, ConversationState.AwaitingClarificationChoice);
    }

    /// <summary>
    /// The answer to a guided question: a tap ("#12" / "@5"), or the option's number ("2"). Anything else is a new message, handled as if idle.
    /// False = no guided question is open.
    /// </summary>
    private async Task<bool> TryHandleGuidedChoiceAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        if (ctx.GuidedTurn is not { } turn) return false;
        var options = ctx.ClarificationOptions ?? new List<string>();
        var text = message.Trim();
        var pick = options.FirstOrDefault(o => string.Equals(o, text, StringComparison.OrdinalIgnoreCase))
                   ?? (int.TryParse(text, out var n) && n >= 1 && n <= options.Count ? options[n - 1] : null);

        ctx.GuidedTurn = null;
        ctx.ClarificationOptions = null;
        SetState(session, ConversationState.Idle);
        if (pick is null)
        {
            await HandleIdleAsync(seller, session, ctx, message, ct);
            return true;
        }

        if (pick.StartsWith('#') && int.TryParse(pick[1..], out var orderId))
            await RunGuidedAsync(seller, session, ctx, turn, orderId, null, ct);
        else if (pick.StartsWith('@') && int.TryParse(pick[1..], out var customerId)
                 && await _db.Customers.FirstOrDefaultAsync(c => c.Id == customerId && c.SellerId == seller.Id && c.DeletedAt == null, ct) is { } customer)
            await RunGuidedAsync(seller, session, ctx, turn, null, customer.Name, ct);
        else if (pick.StartsWith('%') && int.TryParse(pick[1..], out var productId)
                 && await _db.Products.FirstOrDefaultAsync(p => p.Id == productId && p.SellerId == seller.Id && p.IsActive, ct) is { } product)
            await RunGuidedAsync(seller, session, ctx, turn, null, null, ct, Formatters.ProductLabel(product));
        else if (pick.StartsWith('~'))
            await RunVariantPickAsync(seller, session, ctx, turn, pick, ct);   // ConversationEngine.VariantMatch.cs
        else
            await HandleIdleAsync(seller, session, ctx, message, ct);
        return true;
    }

    /// <summary>
    /// Runs the stored action with the chosen order or customer filled in. The detail is added to the seller's words, so the same checks that a
    /// spoken detail passes (the order number was said, the name was said) pass for a tapped one. Still missing a value (the new phone): ask for it.
    /// </summary>
    private async Task RunGuidedAsync(Seller seller, ConversationSession session, SessionContextData ctx, QueuedModelTurn turn, int? orderId, string? customerName,
        CancellationToken ct, string? productName = null)
    {
        var a = turn.Action;
        var filled = new TurnAction
        {
            Action = a.Action, Orders = a.Orders,
            Products = productName is null ? a.Products : new List<AiNewProduct> { new() { Name = productName } },
            OrderId = orderId ?? a.OrderId, CustomerName = customerName ?? a.CustomerName,
            Status = a.Status, Period = a.Period, PendingOnly = a.PendingOnly, Customer = a.Customer, Field = a.Field, Value = a.Value, Topic = a.Topic,
            Question = a.Question, OrderEdits = a.OrderEdits, ItemName = a.ItemName, Amount = a.Amount, PaidInFull = a.PaidInFull, Courier = a.Courier,
        };
        var said = turn.Message + (orderId is { } id ? $" order {id}" : "") + (customerName is { } chosen ? $" {chosen}" : "") + (productName is { } p ? $" {p}" : "");
        if (!await CanRunAsync(seller, filled, said, ct))
        {
            // One follow-up question at most (the amount after the order was tapped); never a loop back into the same order list.
            if (_guidedDepth == 0)
            {
                _guidedDepth++;
                try { if (await TryGuideAsync(seller, session, ctx, filled, said, null, ct)) return; }
                finally { _guidedDepth--; }
            }
            await ReplyNotRunAsync(seller, filled, ct);
            return;
        }
        var catalog = await LoadCatalogAsync(seller, ct);
        if (!await RunTurnActionAsync(seller, session, ctx, filled, catalog, said, ct, false))
            await ReplyNotRunAsync(seller, filled, ct);
    }
}
