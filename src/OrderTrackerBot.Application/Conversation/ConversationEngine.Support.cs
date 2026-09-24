using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

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
            ? $"💬 Comment query #{query.Number} ({query.CustomerName ?? "Instagram"}):\n\"{query.QueryText}\""
            : $"💬 Customer query samjha — {QueryContextLine(query, order)}\n❓ \"{query.QueryText}\"";
        await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber,
            $"{header}\n\nSuggested reply:\n\"{query.SuggestedReply}\"\n\n" +
            (isInstagram ? "Instagram par reply bhej dun?" : "Bhej dein? (YES / EDIT)"),
            isInstagram ? InstagramReplyButtons : WhatsAppReplyButtons, ct);
    }

    private static string QueryContextLine(SupportQuery query, Order? order)
    {
        var name = query.CustomerName ?? "Customer";
        if (order is null) return $"{name} (koi order record nahi mila)";

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
        if (order is null) return "No order found for this customer.";
        var facts = new List<string>
        {
            $"order #{order.Id}",
            "items: " + string.Join(", ", order.Items.Select(i => $"{i.Quantity}x {i.ProductNameSnapshot}")),
            $"status: {order.Status}",
            $"total: Rs.{order.Total:0}",
            $"payment: {order.PaymentMethod}, {order.PaymentStatus}",
            $"ordered on {order.CreatedAt:yyyy-MM-dd}"
        };
        if (order.ShippedAt is { } shipped) facts.Add($"shipped on {shipped:yyyy-MM-dd}");
        if (order.DeliveredAt is { } delivered) facts.Add($"delivered on {delivered:yyyy-MM-dd}");
        if (!string.IsNullOrWhiteSpace(order.TrackingNumber)) facts.Add($"courier/tracking: {TrackingText(order)}");
        return string.Join("; ", facts);
    }

    /// <summary>Non-AI draft built only from recorded facts (used when OpenAI isn't configured or fails).</summary>
    private static string TemplateSupportReply(Order? order) => order?.Status switch
    {
        null => "Shukriya aapke message ka! Hum aapka order check kar ke jald batate hain.",
        OrderStatus.Pending => $"Aapka order (#{order.Id}) confirm hai aur jald dispatch ho jayega. Shukriya!",
        OrderStatus.Shipped => string.IsNullOrWhiteSpace(order.TrackingNumber)
            ? "Aapka order raste mein hai, jald pohonch jayega. Shukriya!"
            : $"Aapka order raste mein hai. Tracking: {TrackingText(order)}. Shukriya!",
        OrderStatus.Delivered => $"Aapka order {order.DeliveredAt ?? order.UpdatedAt:dd MMM} ko deliver ho chuka hai. Koi masla ho to zaroor batayein!",
        _ => $"Aapka order (#{order.Id}) cancel ho chuka hai. Dobara order karna ho to batayein."
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
            await ReplyAsync(seller, "✏️ Apna reply likh dein — main wohi save kar dunga.", ct);
            return;
        }

        if (query is not null && (CommandParser.IsNegative(message) || text is "cancel" or "skip" or "baad mein"))
        {
            ClearSupportPrompt(session, ctx);
            await ReplyAsync(seller, $"Theek hai — query #{query.Number} open rahegi. \"support queries\" se baad mein dekh saktay hain.", ct);
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
            await ReplyAsync(seller, $"Theek hai — query #{query.Number} open rahegi.", ct);
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
                await ReplyAsync(seller, "✅ Comment ka reply bhej diya gaya (IG par).", ct);
                return;
            }

            await ReplyAsync(seller,
                "⚠️ Instagram par reply nahi ja saka (connection expire ho gaya ho sakta hai — \"connect instagram\" dobara karein).\n" +
                "Tab tak yeh reply copy kar ke khud comment par bhej dein 👇", ct);
            await ReplyAsync(seller, reply, ct);
            return;
        }

        query.Status = "replied";
        await ReplyAsync(seller, $"✅ Reply save ho gaya — {query.CustomerName ?? "customer"} ko forward kar dein 👇", ct);
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
                "💬 Koi open query nahi. 🎉\n\nJab customer kuch poochay, us ka sawal forward kar dein, e.g.:\n" +
                "\"mera order kab aayega? — Bilal ne poocha\"", ct);
            return;
        }

        var lines = queries.OrderBy(q => q.Number)
            .Select(q => $"{q.Number}. {q.CustomerName ?? "Customer"}{(q.Source == "instagram_comment" ? " 📷" : "")} - \"{Shorten(q.QueryText, 30)}\" - {q.Status.ToUpperInvariant()}");
        SetState(session, ConversationState.AwaitingSupportQueryPick);
        await ReplyAsync(seller,
            $"💬 Open Queries ({queries.Count}):\n\n{string.Join("\n", lines)}\n\n" +
            "\"mark 2 resolved\" ya reply draft karne ke liye number bhejein.", ct);
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
            await ReplyAsync(seller, $"Query #{number} nahi mili — \"support queries\" dobara dekhein.", ct);
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
            await ReplyAsync(seller, $"Query #{number} nahi mili — \"support queries\" se list dekhein.", ct);
            return;
        }

        query.Status = "resolved";
        await ReplyAsync(seller, $"✅ Query #{number} ({query.CustomerName ?? "customer"}) RESOLVED.", ct);
    }

    /// <summary>"reply 3" sends the drafted reply; "reply 3: apna text" sends custom text.</summary>
    private async Task HandleReplySupportQueryAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var query = await _db.SupportQueries.FirstOrDefaultAsync(q => q.SellerId == seller.Id && q.Number == cmd.Number, ct);
        if (query is null)
        {
            await ReplyAsync(seller, $"Query #{cmd.Number} nahi mili — \"support queries\" se list dekhein.", ct);
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
                $"✅ Instagram pehle se connect hai — @{existing.Username}\nNaye comments par notify karta rahunga.\n\n" +
                "\"comment leads\" se leads dekhein, ya \"disconnect instagram\" se hata dein.", ct);
            return;
        }

        if (!_instagram.IsConfigured)
        {
            await ReplyAsync(seller,
                "📷 Instagram auto-connect abhi is bot par active nahi hua — jald aa raha hai.\n\n" +
                "Tab tak comment ya DM ki screenshot forward kar dein, main order bana dunga.", ct);
            return;
        }

        await ReplyAsync(seller,
            "📷 Instagram connect karne ke liye:\n" +
            "1. Aapka IG account Business/Creator hona chahiye\n" +
            $"2. Yeh link kholein aur login karein: {_instagram.BuildConnectLink(seller.Id)}\n" +
            "3. Permission dein — sirf comments padhne/reply ke liye, DMs nahi\n\n" +
            "Connect hone ke baad, comments automatically check honge. (Link 30 minute tak valid hai.)", ct);
    }

    private async Task HandleDisconnectInstagramAsync(Seller seller, CancellationToken ct)
    {
        var existing = await _db.InstagramConnections.FirstOrDefaultAsync(c => c.SellerId == seller.Id, ct);
        if (existing is null)
        {
            await ReplyAsync(seller, "Instagram connect nahi hai. Connect karne ke liye \"connect instagram\" likhein.", ct);
            return;
        }

        _db.InstagramConnections.Remove(existing);
        await ReplyAsync(seller, $"✅ Instagram (@{existing.Username}) disconnect ho gaya. Ab comments check nahi honge.", ct);
    }

    /// <summary>Called by the OAuth callback once a seller's IG account is stored.</summary>
    public async Task HandleInstagramConnectedAsync(int sellerId, string? username, CancellationToken ct = default)
    {
        var seller = await _db.Sellers.FirstOrDefaultAsync(s => s.Id == sellerId, ct);
        if (seller is null) return;

        if (!string.IsNullOrWhiteSpace(username) && string.IsNullOrWhiteSpace(seller.InstagramHandle))
            seller.InstagramHandle = "@" + username.TrimStart('@');
        await ReplyAsync(seller, $"✅ Instagram connect ho gaya — @{username}\nAb naye comments par automatically notify karunga.", ct);
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
                    await ReplyAsync(seller, $"📷 Naya comment mila ({who}):\n\"{text}\"\n\n🤖 Yeh support query lagta hai (order nahi).", ct);
                    await PromptSupportReplyAsync(seller, session, ctx, query, null, ct);
                    await PersistAsync(session, ctx, ct);
                }
                else
                {
                    await ReplyAsync(seller,
                        $"📷 Naya comment mila ({who}):\n\"{text}\"\n\n🤖 Yeh support query lagta hai. Suggested reply:\n\"{query.SuggestedReply}\"\n\n" +
                        $"\"reply {query.Number}\" likh kar IG par bhej dein, ya \"reply {query.Number}: apna jawab\".", ct);
                }
                break;
            }

            case "order_interest":
                await ReplyAsync(seller,
                    $"📷 Naya comment mila ({who}):\n\"{text}\"\n\n" +
                    "🤖 Yeh order interest lagta hai. DM mein follow-up karein, phir yahan order log kar dein.\n" +
                    $"(\"lead {lead.Number} converted\" likh kar order se link karein.)", ct);
                break;

            default:
                await ReplyAsync(seller, $"📷 Naya comment mila ({who}):\n\"{text}\"\n\n\"comment leads\" se sab leads dekhein.", ct);
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
                ? "📷 Abhi koi naya comment lead nahi. Naye comments aate hi bata dunga."
                : "📷 Koi comment lead nahi — Instagram abhi connect nahi hai.\n\"connect instagram\" likh kar connect karein.", ct);
            return;
        }

        var lines = leads.OrderBy(l => l.Number).Select(l =>
            $"{l.Number}. {LeadName(l)} - \"{Shorten(l.CommentText, 30)}\" - {LeadStatusLabel(l.Status)}");
        await ReplyAsync(seller,
            $"📷 Naye Comment Leads ({leads.Count}):\n\n{string.Join("\n", lines)}\n\n" +
            "\"lead 1 converted\" likh kar order se link karein.\n\"lead 1 followed up\" / \"lead 1 dismiss\" bhi likh saktay hain.", ct);
    }

    private static string LeadName(CommentLead lead) =>
        string.IsNullOrWhiteSpace(lead.CommenterUsername) ? "Instagram" : "@" + lead.CommenterUsername;

    private static string LeadStatusLabel(string status) => status switch
    {
        "followed_up" => "FOLLOWED UP",
        "converting" => "ORDER PENDING",
        "converted_to_order" => "CONVERTED",
        _ => status.ToUpperInvariant()
    };

    /// <summary>"lead 1 converted [order 21]" / "lead 1 followed up" / "lead 1 dismiss".</summary>
    private async Task HandleLeadActionAsync(Seller seller, SessionContextData ctx, ParsedCommand cmd, CancellationToken ct)
    {
        var lead = await _db.CommentLeads.FirstOrDefaultAsync(l => l.SellerId == seller.Id && l.Number == cmd.Number, ct);
        if (lead is null)
        {
            await ReplyAsync(seller, $"Lead #{cmd.Number} nahi mila — \"comment leads\" se list dekhein.", ct);
            return;
        }

        switch (cmd.Text)
        {
            case "followed_up":
                lead.Status = "followed_up";
                await ReplyAsync(seller, $"✅ Lead #{lead.Number} — FOLLOWED UP.", ct);
                return;

            case "dismissed":
                lead.Status = "dismissed";
                await ReplyAsync(seller, $"🗑️ Lead #{lead.Number} dismiss kar diya.", ct);
                return;
        }

        if (cmd.Amount is { } orderNumber)
        {
            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == (int)orderNumber && o.SellerId == seller.Id, ct);
            if (order is null)
            {
                await ReplyAsync(seller, $"Order #{orderNumber} nahi mila.", ct);
                return;
            }

            lead.Status = "converted_to_order";
            lead.OrderId = order.Id;
            order.OrderSource ??= "instagram";
            await ReplyAsync(seller, $"✅ Lead #{lead.Number} CONVERTED — order #{order.Id} se link ho gaya.", ct);
            return;
        }

        lead.Status = "converting";
        ctx.ConvertingLeadId = lead.Id;
        await ReplyAsync(seller,
            $"👍 Lead #{lead.Number} ({LeadName(lead)}) — ab is customer ka order bhej dein (naam, product, phone).\n" +
            "Save hote hi lead CONVERTED mark ho jayega.\n\n" +
            $"(Order pehle hi save kar diya hai? \"lead {lead.Number} converted order [number]\" likhein.)", ct);
    }
}
