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
        await AwaitGuidedChoiceAsync(seller, session, ctx, action, message, question, options, ct);
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
            var which = MessageTemplates.Get("guided.customer.which", action.CustomerName!.Trim(), matches.Count);
            await AwaitGuidedChoiceAsync(seller, session, ctx, action, message, which, options, ct);
            await SendChoicesAsync(seller.WhatsAppPhoneNumber, which, options, ct);
            return true;
        }

        // One customer: a delete runs as it is (it asks its own YES).
        if (action.Action == TurnAction.DeleteCustomer) return false;
        // One customer, but the new value (or which detail) was not said: ask for exactly that, with the sentence to say.
        var name = matches[0].Name;
        if (action.Field is not { } field)
        {
            // No field said: the reply must name one ("phone 0300…", "city Lahore"); the record keeps the command until then.
            var what = MessageTemplates.Get("guided.customer.what", name);
            await AwaitGuidedValueAsync(seller, session, ctx, new TurnAction { Action = action.Action, CustomerName = name }, message, GuidedWhatField, what, ct);
            await ReplyAsync(seller, what, ct);
            return true;
        }
        // The value question is a PendingAction bound to this seller and user: the next reply is validated for this field, then run.
        var question = MessageTemplates.Get("guided.customer.value", name, ModelFieldLabel(field), field switch
        {
            "phone" => "03001234567",
            "city" => "Lahore",
            "name" => name,
            _ => "Model Town Lahore",
        });
        await AwaitGuidedValueAsync(seller, session, ctx, new TurnAction { Action = action.Action, CustomerName = name, Field = field }, message,
            field, question, ct);
        await ReplyAsync(seller, question, ct);
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
        var question = MessageTemplates.Get("guided.product.delete");
        await AwaitGuidedChoiceAsync(seller, session, ctx, action, message, question, options, ct);
        await SendChoicesAsync(seller.WhatsAppPhoneNumber, question, options, ct);
        return true;
    }

    private static string OrderChoiceLabel(Order o)
    {
        var items = string.Join(", ", o.Items.Select(i => $"{i.Quantity} {i.ProductNameSnapshot}"));
        return $"#{o.Id} {o.Customer?.Name} — {items}";
    }

    /// <summary>The one field a guided question asks for: which of the listed records is meant.</summary>
    private const string GuidedChoiceField = "choice";

    /// <summary>
    /// Opens a guided question. Stage 6: it is also saved as a PendingActions row bound to this seller and user, with the question and an
    /// expiry equal to <see cref="PickQuestionExpiry"/>, and any older waiting question is cancelled. The reply is checked against this row.
    /// </summary>
    private async Task AwaitGuidedChoiceAsync(Seller seller, ConversationSession session, SessionContextData ctx, TurnAction action, string message,
        string question, IReadOnlyList<ChoiceOption> options, CancellationToken ct)
    {
        ctx.GuidedTurn = new QueuedModelTurn(Plain(action), message);
        ctx.ClarificationOptions = options.Select(o => o.Value).ToList();
        SetState(session, ConversationState.AwaitingClarificationChoice);

        await TrackQuestionAsync(seller, ctx, action, GuidedChoiceField, question, ct);
    }

    /// <summary>The missing field of "what should I change for {customer}?": the reply itself names the field.</summary>
    private const string GuidedWhatField = "what";

    /// <summary>A guided question for one missing value ("Ali ka naya phone?"): the next reply is that value, checked by <see cref="ValueFromReply"/>.</summary>
    private async Task AwaitGuidedValueAsync(Seller seller, ConversationSession session, SessionContextData ctx, TurnAction action, string message,
        string field, string question, CancellationToken ct)
    {
        ctx.GuidedTurn = new QueuedModelTurn(Plain(action), message);
        ctx.ClarificationOptions = null;
        SetState(session, ConversationState.AwaitingClarificationChoice);
        await TrackQuestionAsync(seller, ctx, action, field, question, ct);
    }

    /// <summary>Saves the question's record (any older waiting question is cancelled) and links it to the session.</summary>
    private async Task TrackQuestionAsync(Seller seller, SessionContextData ctx, TurnAction action, string field, string question, CancellationToken ct)
    {
        var store = new ClarificationStore(_db);
        await store.CancelWaitingAsync(seller.Id, seller.WhatsAppPhoneNumber, ct);
        var pending = ClarificationManager.Create(Guid.NewGuid().ToString("N"), seller.Id, seller.WhatsAppPhoneNumber, action.Action,
            new[] { field }, question, _clock.GetUtcNow().UtcDateTime, PickQuestionExpiry);
        pending.Plan = Plain(action);   // the command the question belongs to; the reply completes this, not a new one
        await store.TrackAsync(pending, ct);
        ctx.PendingActionId = pending.Id;
        Trace.AskedBack();
    }

    private static readonly System.Text.RegularExpressions.Regex PhoneInReply = new(@"\+?\d[\d -]{6,}\d");
    private static readonly System.Text.RegularExpressions.Regex DetailWord = new(@"^(?<key>city|shehar|sheher|address|pata|name|naam)\b\s*", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex LinkWords = new(@"^(?:(?:ka|ki|ko|hai)\b|[:\-])\s*",System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// The field a free-text reply gives ("phone 0300…", "city Lahore", "pata Model Town", "naam Ali"). Rule-based: a phone number, or a field word
    /// followed by the value. Null when the reply names no field.
    /// </summary>
    private static (string Field, string Value)? DetectCustomerDetail(string reply)
    {
        var text = reply.Trim();
        var phone = PhoneInReply.Match(text);
        if (phone.Success)
        {
            var digits = new string(phone.Value.Where(char.IsDigit).ToArray());
            return digits.Length >= 7 ? ("phone", digits) : null;
        }
        var word = DetailWord.Match(text);
        if (!word.Success) return null;
        var value = text[word.Length..].Trim();
        while (LinkWords.Match(value) is { Success: true } link && link.Length > 0) value = value[link.Length..].Trim();
        if (value.Length == 0) return null;
        var field = word.Groups["key"].Value.ToLowerInvariant() switch
        {
            "city" or "shehar" or "sheher" => "city",
            "address" or "pata" => "address",
            _ => "name",
        };
        return (field, value);
    }

    /// <summary>A reply to a value question: the value if it fits the field, otherwise null (the question is then dropped as before).</summary>
    private static string? ValueFromReply(string field, string reply)
    {
        var text = reply.Trim();
        if (text.Length == 0) return null;
        return field == "phone" ? (text.Count(char.IsDigit) >= 7 ? text : null) : text;
    }

    /// <summary>The option a reply picks: the tap itself ("#12" / "@5"), or its number in the list ("2"). Null when it picks none.</summary>
    private static string? MatchGuidedOption(IReadOnlyList<string> options, string message)
    {
        var text = message.Trim();
        return options.FirstOrDefault(o => string.Equals(o, text, StringComparison.OrdinalIgnoreCase))
               ?? (int.TryParse(text, out var n) && n >= 1 && n <= options.Count ? options[n - 1] : null);
    }

    /// <summary>
    /// Stage 6: a tap or number sent for a guided question whose time has run out is not run as a new message (a stale "2" could mean
    /// anything now): the record is marked Expired and the seller is asked to start again. False = not that case; the usual paths go on.
    /// </summary>
    private async Task<bool> TryReplyStaleGuidedAnswerAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        if (session.State != ConversationState.AwaitingClarificationChoice || ctx.GuidedTurn is null || ctx.ModelQueue.Count > 0
            || ctx.PendingActionId is not { } id || ctx.ClarificationOptions is not { } options)
            return false;

        var store = new ClarificationStore(_db);
        var pending = await store.FindAsync(id, seller.Id, seller.WhatsAppPhoneNumber, ct);
        if (pending is { Status: PendingStatus.Waiting } && _clock.GetUtcNow().UtcDateTime < pending.ExpiresAt) return false;   // still open
        if (MatchGuidedOption(options, message) is null) return false;   // not an answer to it: dropped and handled as a new message as before

        if (pending is not null)
        {
            ClarificationManager.Expire(pending);
            await store.TrackAsync(pending, ct);
        }
        ctx.GuidedTurn = null;
        ctx.ClarificationOptions = null;
        ctx.PendingActionId = null;
        ctx.PickAskedAt = null;
        SetState(session, ConversationState.Idle);
        await ReplyAsync(seller, MessageTemplates.Get("guided.expired"), ct);
        return true;
    }

    /// <summary>
    /// The answer to a guided question: a tap ("#12" / "@5"), or the option's number ("2"). Anything else is a new message, handled as if idle.
    /// False = no guided question is open.
    /// </summary>
    private async Task<bool> TryHandleGuidedChoiceAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        if (ctx.GuidedTurn is not { } turn) return false;
        // A value question has no options: its reply is the value itself (HandleGuidedValueAsync).
        var valueQuestion = ctx.ClarificationOptions is null or { Count: 0 };
        var pick = valueQuestion ? null : MatchGuidedOption(ctx.ClarificationOptions!, message);
        var pendingId = ctx.PendingActionId;

        ctx.GuidedTurn = null;
        ctx.ClarificationOptions = null;
        ctx.PendingActionId = null;
        SetState(session, ConversationState.Idle);

        if (valueQuestion)
        {
            await HandleGuidedValueAsync(seller, session, ctx, turn, pendingId, message, ct);
            return true;
        }

        // Stage 6: the reply is matched to the question's own record (same seller, same user, still waiting, not expired).
        // Sessions saved before pending actions existed carry no id and keep the older path.
        string? settleId = null;
        if (pendingId is not null)
        {
            var store = new ClarificationStore(_db);
            var pending = await store.FindAsync(pendingId, seller.Id, seller.WhatsAppPhoneNumber, ct);
            var result = pending is null ? null
                : ClarificationManager.Answer(pending, seller.Id, seller.WhatsAppPhoneNumber, message, _clock.GetUtcNow().UtcDateTime, (_, _) => pick);
            switch (result?.Outcome)
            {
                case ClarificationOutcome.Ready:
                    // The answer is saved, but the record stays open until the action has run or been refused (SettleGuidedPendingAsync).
                    // The action still goes through the reference and safety checks in RunGuidedAsync, never straight to a handler.
                    // The tapped record is the resolved reference; the run below re-checks that it still exists for this seller.
                    if (pick[0] is '#' or '@' or '%' && int.TryParse(pick[1..], out var refId))
                        pending!.ResolvedReferences[pick[0] switch { '#' => "order", '@' => "customer", _ => "product" }] = refId;
                    await store.TrackAsync(pending!, ct);
                    settleId = pendingId;
                    break;
                case ClarificationOutcome.Invalid:
                    // Not an answer to this question: it stops waiting, and the message is handled as a new one.
                    ClarificationManager.Cancel(pending!);
                    await store.TrackAsync(pending!, ct);
                    break;
                default:
                    // No record, already closed, expired, or not this seller's: nothing from the old question runs.
                    if (pending is not null)
                    {
                        ClarificationManager.Expire(pending);
                        await store.TrackAsync(pending, ct);
                    }
                    if (pick is not null)
                    {
                        await ReplyAsync(seller, MessageTemplates.Get("guided.expired"), ct);
                        return true;
                    }
                    break;
            }
        }

        if (pick is null)
        {
            await HandleIdleAsync(seller, session, ctx, message, ct);
            return true;
        }

        if (pick.StartsWith('#') && int.TryParse(pick[1..], out var orderId))
            await RunGuidedAsync(seller, session, ctx, turn, orderId, null, ct, null, settleId);
        else if (pick.StartsWith('@') && int.TryParse(pick[1..], out var customerId)
                 && await new ReferenceResolver(_db).ResolveCustomerByIdAsync(seller.Id, customerId, ct) is { Status: ReferenceStatus.Resolved, Id: { } resolvedCustomer }
                 && await _db.Customers.AsNoTracking().FirstAsync(c => c.Id == resolvedCustomer, ct) is { } customer)
            await RunGuidedAsync(seller, session, ctx, turn, null, customer.Name, ct, null, settleId);
        else if (pick.StartsWith('%') && int.TryParse(pick[1..], out var productId)
                 && await new ReferenceResolver(_db).ResolveProductByIdAsync(seller.Id, productId, ct) is { Status: ReferenceStatus.Resolved, Id: { } resolvedProduct }
                 && await _db.Products.AsNoTracking().FirstAsync(p => p.Id == resolvedProduct, ct) is { } product)
            await RunGuidedAsync(seller, session, ctx, turn, null, null, ct, Formatters.ProductLabel(product), settleId);
        else if (pick.StartsWith('~'))
        {
            await RunVariantPickAsync(seller, session, ctx, turn, pick, ct);   // ConversationEngine.VariantMatch.cs
            await SettleGuidedPendingAsync(seller, settleId, true, ct);
        }
        else
        {
            await SettleGuidedPendingAsync(seller, settleId, false, ct);
            await HandleIdleAsync(seller, session, ctx, message, ct);
        }
        return true;
    }

    /// <summary>
    /// The reply to a value question ("Ali ka naya phone?"). A reply that fits the field is filled in and run like a tapped choice; anything else
    /// closes the question and is handled as a new message (no stale value is run).
    /// </summary>
    private async Task HandleGuidedValueAsync(Seller seller, ConversationSession session, SessionContextData ctx, QueuedModelTurn turn, string? pendingId,
        string message, CancellationToken ct)
    {
        var store = new ClarificationStore(_db);
        var pending = pendingId is null ? null : await store.FindAsync(pendingId, seller.Id, seller.WhatsAppPhoneNumber, ct);
        // For the "what should I change" question the reply names its own field; that field is kept alongside the value.
        string? namedField = null;
        var result = pending is null ? null
            : ClarificationManager.Answer(pending, seller.Id, seller.WhatsAppPhoneNumber, message, _clock.GetUtcNow().UtcDateTime, (f, r) =>
            {
                if (f != GuidedWhatField) return ValueFromReply(f, r);
                var detail = DetectCustomerDetail(r);
                namedField = detail?.Field;
                return detail?.Value;
            });
        if (result?.Outcome == ClarificationOutcome.Ready)
        {
            var (_, value) = pending!.Answers.Single();
            var field = namedField ?? pending.Answers.Keys.Single();
            await store.TrackAsync(pending, ct);
            // The command comes from the record it was asked under; the reply is the seller's words for this run (the value must be among them).
            var plan = pending.Plan ?? turn.Action;
            var filled = new QueuedModelTurn(new TurnAction { Action = plan.Action, CustomerName = plan.CustomerName, Field = field, Value = value }, message);
            await RunGuidedAsync(seller, session, ctx, filled, null, null, ct, null, pending.Id);
            return;
        }
        if (pending is not null && result?.Outcome == ClarificationOutcome.Invalid && pending.Attempts < ClarificationManager.MaxInvalidReplies)
        {
            // Ask again: the record keeps waiting for this field, so the seller is not stuck and can answer with the same question.
            await store.TrackAsync(pending, ct);
            ctx.GuidedTurn = turn;
            ctx.PendingActionId = pending.Id;
            SetState(session, ConversationState.AwaitingClarificationChoice);
            await ReplyAsync(seller, result.Question!, ct);
            return;
        }
        if (pending is not null)
        {
            // The limit is reached (or the question timed out): the question closes and the message is handled as a new one.
            if (result?.Outcome == ClarificationOutcome.Invalid) ClarificationManager.Cancel(pending);
            else ClarificationManager.Expire(pending);
            await store.TrackAsync(pending, ct);
        }
        await HandleIdleAsync(seller, session, ctx, message, ct);
    }

    /// <summary>Closes a guided question's record once its action has run (<paramref name="ran"/>) or was not run. A record a newer question already cancelled stays so.</summary>
    private async Task SettleGuidedPendingAsync(Seller seller, string? pendingId, bool ran, CancellationToken ct)
    {
        if (pendingId is null) return;
        var store = new ClarificationStore(_db);
        var pending = await store.FindAsync(pendingId, seller.Id, seller.WhatsAppPhoneNumber, ct);
        if (pending is not { Status: PendingStatus.Waiting }) return;
        if (ran) ClarificationManager.Complete(pending);
        else ClarificationManager.Cancel(pending);
        await store.TrackAsync(pending, ct);
    }

    /// <summary>
    /// Runs the stored action with the chosen order or customer filled in. The detail is added to the seller's words, so the same checks that a
    /// spoken detail passes (the order number was said, the name was said) pass for a tapped one. Still missing a value (the new phone): ask for it.
    /// </summary>
    private async Task RunGuidedAsync(Seller seller, ConversationSession session, SessionContextData ctx, QueuedModelTurn turn, int? orderId, string? customerName,
        CancellationToken ct, string? productName = null, string? settlePendingId = null)
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
                try
                {
                    if (await TryGuideAsync(seller, session, ctx, filled, said, null, ct))
                    {
                        await SettleGuidedPendingAsync(seller, settlePendingId, false, ct);
                        return;
                    }
                }
                finally { _guidedDepth--; }
            }
            await SettleGuidedPendingAsync(seller, settlePendingId, false, ct);
            await ReplyNotRunAsync(seller, filled, ct);
            return;
        }
        var catalog = await LoadCatalogAsync(seller, ct);
        var ran = await RunTurnActionAsync(seller, session, ctx, filled, catalog, said, ct, false);
        await SettleGuidedPendingAsync(seller, settlePendingId, ran, ct);
        if (!ran)
            await ReplyNotRunAsync(seller, filled, ct);
    }
}
