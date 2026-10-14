using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

// "receipt design": the seller shapes their receipt live. Every instruction is saved at once and answered with a fresh preview PDF
// (their latest order, or a sample when there is none), so what they see is exactly what buyers will get. "done" leaves the mode.
//   color blue · title Invoice · layout center · logo off · phone off · buyer off · status off · text large · size 80mm · footer ... · reset · done
// The design lives on Seller.ReceiptDesignJson (see ReceiptDesign); paper size and footer keep their own settings.
public partial class ConversationEngine
{
    private static readonly string ReceiptDesignHelp =
        MessageTemplates.Get("receipt-design.006");

    private static readonly Regex DesignDone = new(@"^(?:done|save|finish|exit|ok|okay|theek\s*hai|thik\s*hai|bas|ho\s*gaya|khatam)\.?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private async Task HandleReceiptDesignStartAsync(Seller seller, ConversationSession session, CancellationToken ct)
    {
        if (_receiptPdf is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("branding.001"), ct);
            return;
        }
        SetState(session, ConversationState.AwaitingReceiptDesign);
        await ReplyAsync(seller, MessageTemplates.Get("receipt-design.001", ReceiptDesignHelp, ReceiptDesign.FromJson(seller.ReceiptDesignJson).Describe()), ct);
        await SendDesignPreviewAsync(seller, ct);
    }

    private async Task HandleReceiptDesignAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var text = message.Trim();
        if (DesignDone.IsMatch(text))
        {
            SetState(session, ConversationState.Idle);
            await ReplyAsync(seller,
                MessageTemplates.Get("receipt-design.002", ReceiptDesign.FromJson(seller.ReceiptDesignJson).Describe()), ct);
            return;
        }
        if (Regex.IsMatch(text, @"^(?:preview|dikhao|dekho)$", RegexOptions.IgnoreCase))
        {
            await SendDesignPreviewAsync(seller, ct);
            return;
        }
        if (Regex.IsMatch(text, @"^(?:reset|default|standard)$", RegexOptions.IgnoreCase))
        {
            seller.ReceiptDesignJson = null;
            await ReplyAsync(seller, MessageTemplates.Get("receipt-design.003"), ct);
            await SendDesignPreviewAsync(seller, ct);
            return;
        }
        if (Regex.Match(text, @"^(?:size|paper)\s*:?\s*(?<v>.+)$", RegexOptions.IgnoreCase) is { Success: true } sz)
        {
            await HandleReceiptSizeAsync(seller, sz.Groups["v"].Value, ct);
            await SendDesignPreviewAsync(seller, ct);
            return;
        }
        if (Regex.Match(text, @"^footer\s*:?\s*(?<v>.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline) is { Success: true } ft)
        {
            await HandleReceiptFooterAsync(seller, ft.Groups["v"].Value, ct);
            await SendDesignPreviewAsync(seller, ct);
            return;
        }

        var current = ReceiptDesign.FromJson(seller.ReceiptDesignJson);
        if (ReceiptDesign.TryApply(text, current, out var updated, out var error))
        {
            if (error is not null)
            {
                await ReplyAsync(seller, error, ct);
                return;
            }
            seller.ReceiptDesignJson = updated.ToJson();
            await SendDesignPreviewAsync(seller, ct);
            return;
        }

        // Anything else that is a real command leaves design mode and runs; the design is already saved.
        if (CommandParser.TryParse(text) is { } command)
        {
            SetState(session, ConversationState.Idle);
            await ExecuteCommandAsync(seller, session, ctx, command, ct);
            return;
        }
        await ReplyAsync(seller, MessageTemplates.Get("receipt-design.004", ReceiptDesignHelp), ct);
    }

    /// <summary>Sends the receipt as it would look now: the seller's latest order, or a sample. Never takes a receipt serial number.</summary>
    private async Task SendDesignPreviewAsync(Seller seller, CancellationToken ct)
    {
        var order = await _db.Orders.Include(o => o.Customer).Include(o => o.Items)
            .Where(o => o.SellerId == seller.Id).OrderByDescending(o => o.CreatedAt).FirstOrDefaultAsync(ct);
        var sample = order is null;
        order ??= SampleOrder(seller);

        var branding = await _db.SellerBrandings.AsNoTracking().FirstOrDefaultAsync(b => b.SellerId == seller.Id, ct);
        var (pdf, _) = await RenderReceiptAsync(seller, order, branding, ct, preview: true);
        var sent = await _sender.SendDocumentAsync(seller.WhatsAppPhoneNumber, pdf, "Receipt-preview.pdf", "application/pdf",
            sample ? "👀 Preview (sample order) — aur badlav likhein, ya done." : $"👀 Preview (Order #{order.Id}) — aur badlav likhein, ya done.", ct);
        if (!sent) await ReplyAsync(seller, MessageTemplates.Get("receipt-design.005"), ct);
    }

    // Never added to the context, so it can't be saved.
    private Order SampleOrder(Seller seller) => new()
    {
        Id = 1,
        SellerId = seller.Id,
        CreatedAt = _clock.GetUtcNow().UtcDateTime,
        Status = OrderStatus.Pending,
        PaymentMethod = OrderPaymentMethod.Cod,
        Customer = new Customer { Name = "Sara Khan", Phone = "03001234567", Address = "Model Town", City = "Lahore" },
        Items =
        {
            new OrderItem { ProductNameSnapshot = "Lawn Suit", Quantity = 2, UnitPrice = 3500 },
            new OrderItem { ProductNameSnapshot = "Dupatta", Quantity = 1, UnitPrice = 600 }
        },
        Subtotal = 7600,
        Total = 7600
    };
}
