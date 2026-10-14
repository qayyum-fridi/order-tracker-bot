using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

// Screens 5d-5..5d-10: buyer support queries (forwarded on WhatsApp, or asked in an Instagram comment) and
// Instagram comment leads. The bot drafts replies and tracks leads; it never messages a buyer on WhatsApp itself —
// the seller forwards. Only a public IG comment reply is posted directly (manage-comments scope allows it).
public partial class ConversationEngine
{
    private static readonly string[] WhatsAppReplyButtons = { "Yes", "Edit" };
    private static readonly string[] InstagramReplyButtons = { "Reply bhejein", "Edit karein" };

    // ---------------------------------------------------------------- support queries

    /// <summary>Screen 5d-8: "mera order kab tak aayega? — Bilal ne poocha" → context + drafted reply → YES / EDIT.</summary>
    private async Task HandleForwardedQueryAsync(Seller seller, ConversationSession session, SessionContextData ctx,
        string? customerName, string question, CancellationToken ct)
    {
        var (customer, order) = await FindQueryContextAsync(seller, customerName, ct);
        var reply = await _ai.DraftSupportReplyAsync(seller.BusinessName ?? "", question, OrderFacts(order), ct)
                    ?? TemplateSupportReply(order);

        var query = new SupportQuery
        {
            SellerId = seller.Id,
            Number = await NextSupportQueryNumberAsync(seller, ct),
            CustomerName = customer?.Name ?? customerName,
            CustomerPhone = customer?.Phone,
            Source = "whatsapp",
            QueryText = question,
            SuggestedReply = reply,
            LinkedOrderId = order?.Id
        };
        _db.SupportQueries.Add(query);
        await _db.SaveChangesAsync(ct);

        await PromptSupportReplyAsync(seller, session, ctx, query, order, ct);
    }

    private async Task PromptSupportReplyAsync(Seller seller, ConversationSession session, SessionContextData ctx, SupportQuery query,
        Order? order, CancellationToken ct)
    {
        ctx.SupportQueryId = query.Id;
        SetState(session, ConversationState.AwaitingSupportReplyConfirmation);

        var isInstagram = query.Source == "instagram_comment";
        var header = isInstagram
            ? MessageTemplates.Get("support.039", query.Number, query.CustomerName ?? "Instagram", query.QueryText)
            : MessageTemplates.Get("support.040", QueryContextLine(query, order), query.QueryText);
        await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber,
            MessageTemplates.Get("support.001", header, query.SuggestedReply, (isInstagram ? "Instagram par reply bhej dun?" : "Bhej dein? (YES / EDIT)")),
            isInstagram ? InstagramReplyButtons : WhatsAppReplyButtons, ct);
    }

    private static string QueryContextLine(SupportQuery query, Order? order)
    {
        var name = query.CustomerName ?? "Customer";
        if (order is null) return MessageTemplates.Get("support.031", name);

        var parts = new List<string> { $"Order #{order.Id}" };
        var items = string.Join(", ", order.Items.Select(i => i.ProductNameSnapshot));
        if (items.Length > 0) parts.Add(items);
        parts.Add(Formatters.Status(order.Status));
        if (!string.IsNullOrWhiteSpace(order.TrackingNumber)) parts.Add($"tracking: {TrackingText(order)}");
        return $"{name} ({string.Join(", ", parts)})";
    }

    private static string TrackingText(Order order) =>
        string.Join(" ", new[] { order.TrackingCourier, order.TrackingNumber }.Where(s => !string.IsNullOrWhiteSpace(s)));

    private async Task<(Customer? Customer, Order? Order)> FindQueryContextAsync(Seller seller, string? customerName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(customerName)) return (null, null);

        var wanted = customerName.Trim().ToLower();
        var customers = await _db.Customers
            .Where(c => c.SellerId == seller.Id && c.DeletedAt == null && c.Name.ToLower().Contains(wanted))
            .ToListAsync(ct);
        var customer = customers.FirstOrDefault(c => c.Name.Equals(customerName.Trim(), StringComparison.OrdinalIgnoreCase))
                       ?? customers.FirstOrDefault();
        if (customer is null) return (null, null);

        // The order a "where is my order" question is about: the latest one still in progress, else the latest overall.
        var orders = await _db.Orders.Include(o => o.Items)
            .Where(o => o.SellerId == seller.Id && o.CustomerId == customer.Id)
            .OrderByDescending(o => o.CreatedAt)
            .Take(5)
            .ToListAsync(ct);
        var order = orders.FirstOrDefault(o => o.Status is OrderStatus.Pending or OrderStatus.Shipped) ?? orders.FirstOrDefault();
        return (customer, order);
    }

    private static string OrderFacts(Order? order)
    {
        if (order is null) return MessageTemplates.Get("support.032");
        var facts = new List<string>
        {
            $"order #{order.Id}",
            "items: " + string.Join(", ", order.Items.Select(i => $"{i.Quantity}x {i.ProductNameSnapshot}")),
            $"status: {order.Status}",
            $"total: Rs.{order.Total:0}",
            $"payment: {order.PaymentMethod}, {order.PaymentStatus}",
            MessageTemplates.Get("support.041", $"{order.CreatedAt:yyyy-MM-dd}")
        };
        if (order.ShippedAt is { } shipped) facts.Add($"shipped on {shipped:yyyy-MM-dd}");
        if (order.DeliveredAt is { } delivered) facts.Add($"delivered on {delivered:yyyy-MM-dd}");
        if (!string.IsNullOrWhiteSpace(order.TrackingNumber)) facts.Add($"courier/tracking: {TrackingText(order)}");
        return string.Join("; ", facts);
    }

    /// <summary>Non-AI draft built only from recorded facts (used when OpenAI isn't configured or fails).</summary>
    private static string TemplateSupportReply(Order? order) => order?.Status switch
    {
        null => MessageTemplates.Get("support.033"),
        OrderStatus.Pending => MessageTemplates.Get("support.034", order.Id),
        OrderStatus.Shipped => string.IsNullOrWhiteSpace(order.TrackingNumber)
            ? "Aapka order raste mein hai, jald pohonch jayega. Shukriya!"
            : $"Aapka order raste mein hai. Tracking: {TrackingText(order)}. Shukriya!",
        OrderStatus.Delivered => MessageTemplates.Get("support.035", $"{order.DeliveredAt ?? order.UpdatedAt:dd MMM}"),
        _ => MessageTemplates.Get("support.036", order.Id)
    };

    private async Task<int> NextSupportQueryNumberAsync(Seller seller, CancellationToken ct) =>
        (await _db.SupportQueries.Where(q => q.SellerId == seller.Id).MaxAsync(q => (int?)q.Number, ct) ?? 0) + 1;

    private async Task HandleSupportReplyConfirmationAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var query = ctx.SupportQueryId is { } id ? await _db.SupportQueries.FirstOrDefaultAsync(q => q.Id == id && q.SellerId == seller.Id, ct) : null;
        var text = message.Trim().ToLowerInvariant();

        if (query is not null && (CommandParser.IsAffirmative(message) || text is "reply bhejein" or "bhej dein" or "bhejo" or "send"))
        {
            ClearSupportPrompt(session, ctx);
            await DeliverSupportReplyAsync(seller, query, query.SuggestedReply ?? TemplateSupportReply(null), ct);
            return;
        }

        if (query is not null && text is "edit" or "edit karein" or "change")
        {
            SetState(session, ConversationState.AwaitingSupportReplyEdit);
            await ReplyAsync(seller, MessageTemplates.Get("support.002"), ct);
            return;
        }

        if (query is not null && (CommandParser.IsNegative(message) || text is "cancel" or "skip" or "baad mein"))
        {
            ClearSupportPrompt(session, ctx);
            await ReplyAsync(seller, MessageTemplates.Get("support.003", query.Number), ct);
            return;
        }

        // Anything else is a new message: drop the prompt (the query stays open) and handle it normally.
        ClearSupportPrompt(session, ctx);
        await HandleIdleAsync(seller, session, ctx, message, ct);
    }

    private async Task HandleSupportReplyEditAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var query = ctx.SupportQueryId is { } id ? await _db.SupportQueries.FirstOrDefaultAsync(q => q.Id == id && q.SellerId == seller.Id, ct) : null;
        ClearSupportPrompt(session, ctx);
        if (query is null) return;

        if (message.Trim().Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            await ReplyAsync(seller, MessageTemplates.Get("support.004", query.Number), ct);
            return;
        }

        await DeliverSupportReplyAsync(seller, query, message.Trim(), ct);
    }

    private static void ClearSupportPrompt(ConversationSession session, SessionContextData ctx)
    {
        ctx.SupportQueryId = null;
        SetState(session, ConversationState.Idle);
    }

    private async Task DeliverSupportReplyAsync(Seller seller, SupportQuery query, string reply, CancellationToken ct)
    {
        query.SuggestedReply = reply;

        if (query.Source == "instagram_comment" && query.IgCommentId is not null)
        {
            var connection = await _db.InstagramConnections.FirstOrDefaultAsync(c => c.SellerId == seller.Id, ct);
            if (connection is not null && await _instagram.ReplyToCommentAsync(connection, query.IgCommentId, reply, ct))
            {
                query.Status = "replied";
                await MarkLeadFollowedUpAsync(seller, query.IgCommentId, ct);
                await ReplyAsync(seller, MessageTemplates.Get("support.005"), ct);
                return;
            }

            await ReplyAsync(seller,
                MessageTemplates.Get("support.006"), ct);
            await ReplyAsync(seller, reply, ct);
            return;
        }

        query.Status = "replied";
        await ReplyAsync(seller, MessageTemplates.Get("support.007", query.CustomerName ?? "customer"), ct);
        await ReplyAsync(seller, reply, ct);
    }

    /// <summary>Screen 5d-9: open (and replied-but-unresolved) queries; a number opens that query's draft.</summary>
    private async Task HandleSupportQueriesListAsync(Seller seller, ConversationSession session, CancellationToken ct)
    {
        var queries = await _db.SupportQueries
            .Where(q => q.SellerId == seller.Id && q.Status != "resolved")
            .OrderByDescending(q => q.Number)
            .Take(10)
            .ToListAsync(ct);

        if (queries.Count == 0)
        {
            await ReplyAsync(seller,
                MessageTemplates.Get("support.008"), ct);
            return;
        }

        var lines = queries.OrderBy(q => q.Number)
            .Select(q => $"{q.Number}. {q.CustomerName ?? "Customer"}{(q.Source == "instagram_comment" ? " 📷" : "")} - \"{Shorten(q.QueryText, 30)}\" - {q.Status.ToUpperInvariant()}");
        SetState(session, ConversationState.AwaitingSupportQueryPick);
        await ReplyAsync(seller,
            MessageTemplates.Get("support.009", queries.Count, string.Join("\n", lines)), ct);
    }

    private async Task HandleSupportQueryPickAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        SetState(session, ConversationState.Idle);
        if (!int.TryParse(message.Trim().TrimEnd('.'), out var number))
        {
            await HandleIdleAsync(seller, session, ctx, message, ct);
            return;
        }

        var query = await _db.SupportQueries.FirstOrDefaultAsync(q => q.SellerId == seller.Id && q.Number == number, ct);
        if (query is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("support.010", number), ct);
            return;
        }

        var order = query.LinkedOrderId is { } orderId
            ? await _db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == orderId, ct)
            : null;
        await PromptSupportReplyAsync(seller, session, ctx, query, order, ct);
    }

    private async Task HandleResolveSupportQueryAsync(Seller seller, int number, CancellationToken ct)
    {
        var query = await _db.SupportQueries.FirstOrDefaultAsync(q => q.SellerId == seller.Id && q.Number == number, ct);
        if (query is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("support.011", number), ct);
            return;
        }

        query.Status = "resolved";
        await ReplyAsync(seller, MessageTemplates.Get("support.012", number, query.CustomerName ?? "customer"), ct);
    }

    /// <summary>"reply 3" sends the drafted reply; "reply 3: apna text" sends custom text.</summary>
    private async Task HandleReplySupportQueryAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var query = await _db.SupportQueries.FirstOrDefaultAsync(q => q.SellerId == seller.Id && q.Number == cmd.Number, ct);
        if (query is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("support.011", cmd.Number), ct);
            return;
        }

        await DeliverSupportReplyAsync(seller, query, cmd.Text ?? query.SuggestedReply ?? TemplateSupportReply(null), ct);
    }

    private static string Shorten(string text, int max)
    {
        var oneLine = text.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= max ? oneLine : oneLine[..(max - 1)].TrimEnd() + "…";
    }

    // ---------------------------------------------------------------- Instagram connect + comment leads (section 6c)

    /// <summary>Screen 5d-5: "connect instagram".</summary>
    private async Task HandleConnectInstagramAsync(Seller seller, CancellationToken ct)
    {
        var existing = await _db.InstagramConnections.FirstOrDefaultAsync(c => c.SellerId == seller.Id, ct);
        if (existing is not null)
        {
            await ReplyAsync(seller,
                MessageTemplates.Get("support.013", existing.Username), ct);
            return;
        }

        if (!_instagram.IsConfigured)
        {
            await ReplyAsync(seller,
                MessageTemplates.Get("support.014"), ct);
            return;
        }

        await ReplyAsync(seller,
            MessageTemplates.Get("support.015", _instagram.BuildConnectLink(seller.Id)), ct);
    }

    private async Task HandleDisconnectInstagramAsync(Seller seller, CancellationToken ct)
    {
        var existing = await _db.InstagramConnections.FirstOrDefaultAsync(c => c.SellerId == seller.Id, ct);
        if (existing is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("support.016"), ct);
            return;
        }

        _db.InstagramConnections.Remove(existing);
        await ReplyAsync(seller, MessageTemplates.Get("support.017", existing.Username), ct);
    }

    /// <summary>Called by the OAuth callback once a seller's IG account is stored.</summary>
    public async Task HandleInstagramConnectedAsync(int sellerId, string? username, CancellationToken ct = default)
    {
        var seller = await _db.Sellers.FirstOrDefaultAsync(s => s.Id == sellerId, ct);
        if (seller is null) return;

        if (!string.IsNullOrWhiteSpace(username) && string.IsNullOrWhiteSpace(seller.InstagramHandle))
            seller.InstagramHandle = "@" + username.TrimStart('@');
        await ReplyAsync(seller, MessageTemplates.Get("support.018", username), ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Screen 5d-6 / 5d-10: a comment webhook. Dedups on the comment id, classifies it (AI, else keywords), stores a lead,
    /// and notifies the seller. Questions also become a support query with a drafted public reply.
    /// </summary>
    public async Task HandleInstagramCommentAsync(string igAccountId, string commentId, string? commenterId, string? username,
        string text, string? mediaId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var connection = await _db.InstagramConnections.FirstOrDefaultAsync(c => c.IgUserId == igAccountId, ct);
        if (connection is null) return;
        if (commenterId == connection.IgUserId) return; // the seller's own reply (including ones this bot posted)
        if (await _db.CommentLeads.AnyAsync(l => l.IgCommentId == commentId, ct)) return; // webhook redelivery

        var seller = await _db.Sellers.Include(s => s.Session).FirstAsync(s => s.Id == connection.SellerId, ct);
        var classification = await _ai.ClassifyCommentAsync(seller.BusinessName ?? "", text, ct);
        var intent = classification?.Intent ?? ClassifyCommentByKeywords(text);

        var lead = new CommentLead
        {
            SellerId = seller.Id,
            Number = (await _db.CommentLeads.Where(l => l.SellerId == seller.Id).MaxAsync(l => (int?)l.Number, ct) ?? 0) + 1,
            IgCommentId = commentId,
            CommenterUsername = username,
            CommentText = text,
            PostId = mediaId,
            ClassifiedIntent = intent,
            Status = intent == "spam" ? "dismissed" : "new"
        };
        _db.CommentLeads.Add(lead);
        _db.MessageLogs.Add(new MessageLog { Phone = seller.WhatsAppPhoneNumber, Direction = "inbound", RawText = $"[ig comment @{username}] {text}" });

        var who = string.IsNullOrWhiteSpace(username) ? "Instagram" : "@" + username;
        switch (intent)
        {
            case "spam":
                break;

            case "support_query":
            {
                var query = new SupportQuery
                {
                    SellerId = seller.Id,
                    Number = await NextSupportQueryNumberAsync(seller, ct),
                    CustomerName = who,
                    Source = "instagram_comment",
                    QueryText = text,
                    SuggestedReply = classification?.SuggestedReply ?? "Shukriya! Tafseel ke liye humein DM kar dein 😊",
                    IgCommentId = commentId
                };
                _db.SupportQueries.Add(query);
                await _db.SaveChangesAsync(ct);

                // Only take over the chat when the seller isn't mid-flow; otherwise a typed "reply N" does the same.
                var session = seller.Session!;
                if (session.State == ConversationState.Idle)
                {
                    var ctx = SessionContextData.FromJson(session.ContextJson);
                    await ReplyAsync(seller, MessageTemplates.Get("support.019", who, text), ct);
                    await PromptSupportReplyAsync(seller, session, ctx, query, null, ct);
                    await PersistAsync(session, ctx, ct);
                }
                else
                {
                    await ReplyAsync(seller,
                        MessageTemplates.Get("support.020", who, text, query.SuggestedReply, query.Number, query.Number), ct);
                }
                break;
            }

            case "order_interest":
                await ReplyAsync(seller,
                    MessageTemplates.Get("support.021", who, text, lead.Number), ct);
                break;

            default:
                await ReplyAsync(seller, MessageTemplates.Get("support.022", who, text), ct);
                break;
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent redelivery already stored this comment id (unique index) — nothing else to do.
        }
    }

    private static readonly Regex PhoneInText = new(@"(?:\+?92|0)3\d{2}[\s-]?\d{7}", RegexOptions.Compiled);
    private static readonly Regex SpamWords = new(@"https?://|www\.|follow\s+(?:me|back)|dm\s+for\s+collab|promo(?:tion)?\s+(?:page|account)|earn\s+money|crypto", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OrderWords = new(@"\b(?:order|chahiye|chahye|lena|leni|book|buy|kitne?\s+ka|kitni\s+ki|price|prize|rate|qeemat|keemat|cost)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex QuestionWords = new(@"\?|\b(?:deliver|delivery|bhejte|shipping|size|sizes|available|stock|cod|kab|kahan|kaise|kya)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Non-AI fallback for comment classification: phone number or buy/price words = order interest.</summary>
    internal static string ClassifyCommentByKeywords(string text)
    {
        if (SpamWords.IsMatch(text)) return "spam";
        if (PhoneInText.IsMatch(text) || OrderWords.IsMatch(text)) return "order_interest";
        if (QuestionWords.IsMatch(text)) return "support_query";
        return "unclear";
    }

    private async Task MarkLeadFollowedUpAsync(Seller seller, string igCommentId, CancellationToken ct)
    {
        var lead = await _db.CommentLeads.FirstOrDefaultAsync(l => l.SellerId == seller.Id && l.IgCommentId == igCommentId, ct);
        if (lead is { Status: "new" }) lead.Status = "followed_up";
    }

    /// <summary>Screen 5d-7: open comment leads.</summary>
    private async Task HandleCommentLeadsListAsync(Seller seller, CancellationToken ct)
    {
        var leads = await _db.CommentLeads
            .Where(l => l.SellerId == seller.Id && (l.Status == "new" || l.Status == "followed_up" || l.Status == "converting"))
            .OrderByDescending(l => l.Number)
            .Take(10)
            .ToListAsync(ct);

        if (leads.Count == 0)
        {
            var connected = await _db.InstagramConnections.AnyAsync(c => c.SellerId == seller.Id, ct);
            await ReplyAsync(seller, connected
                ? MessageTemplates.Get("support.029")
                : MessageTemplates.Get("support.030"), ct);
            return;
        }

        var lines = leads.OrderBy(l => l.Number).Select(l =>
            $"{l.Number}. {LeadName(l)} - \"{Shorten(l.CommentText, 30)}\" - {LeadStatusLabel(l.Status)}");
        await ReplyAsync(seller,
            MessageTemplates.Get("support.023", leads.Count, string.Join("\n", lines)), ct);
    }

    private static string LeadName(CommentLead lead) =>
        string.IsNullOrWhiteSpace(lead.CommenterUsername) ? "Instagram" : "@" + lead.CommenterUsername;

    private static string LeadStatusLabel(string status) => status switch
    {
        "followed_up" => MessageTemplates.Get("support.037"),
        "converting" => MessageTemplates.Get("support.038"),
        "converted_to_order" => "CONVERTED",
        _ => status.ToUpperInvariant()
    };

    /// <summary>"lead 1 converted [order 21]" / "lead 1 followed up" / "lead 1 dismiss".</summary>
    private async Task HandleLeadActionAsync(Seller seller, SessionContextData ctx, ParsedCommand cmd, CancellationToken ct)
    {
        var lead = await _db.CommentLeads.FirstOrDefaultAsync(l => l.SellerId == seller.Id && l.Number == cmd.Number, ct);
        if (lead is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("support.024", cmd.Number), ct);
            return;
        }

        switch (cmd.Text)
        {
            case "followed_up":
                lead.Status = "followed_up";
                await ReplyAsync(seller, MessageTemplates.Get("support.025", lead.Number), ct);
                return;

            case "dismissed":
                lead.Status = "dismissed";
                await ReplyAsync(seller, MessageTemplates.Get("support.026", lead.Number), ct);
                return;
        }

        if (cmd.Amount is { } orderNumber)
        {
            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == (int)orderNumber && o.SellerId == seller.Id, ct);
            if (order is null)
            {
                await ReplyAsync(seller, MessageTemplates.Get("commands.028", orderNumber), ct);
                return;
            }

            lead.Status = "converted_to_order";
            lead.OrderId = order.Id;
            order.OrderSource ??= "instagram";
            await ReplyAsync(seller, MessageTemplates.Get("support.027", lead.Number, order.Id), ct);
            return;
        }

        lead.Status = "converting";
        ctx.ConvertingLeadId = lead.Id;
        await ReplyAsync(seller,
            MessageTemplates.Get("support.028", lead.Number, LeadName(lead), lead.Number), ct);
    }
}
