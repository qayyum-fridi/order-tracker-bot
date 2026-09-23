using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Screens 16 / 16h: promote to past customers — channel, audience, send, then "campaign status".
// WhatsApp needs a Meta-approved template (WhatsApp:BroadcastTemplateName); no SMS provider is wired yet,
// so SMS sends are recorded as not_configured rather than pretending to deliver.
public partial class ConversationEngine
{
    private async Task HandleBroadcastRequestAsync(Seller seller, ConversationSession session, SessionContextData ctx, ParsedCommand cmd, CancellationToken ct)
    {
        ctx.BroadcastMessageText = cmd.Text;
        SetState(session, ConversationState.AwaitingBroadcastChannelChoice);
        await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, "Konse channel se bhejna hai?", new[] { "WhatsApp", "SMS", "Dono" }, ct);
    }

    private async Task HandleBroadcastChannelChoiceAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var choice = message.Trim().ToLowerInvariant();
        var channel = choice switch
        {
            "whatsapp" or "1" => "whatsapp",
            "sms" or "2" => "sms",
            "dono" or "both" or "3" => "both",
            _ => null
        };

        if (channel is null || ctx.BroadcastMessageText is null)
        {
            ctx.BroadcastMessageText = null;
            SetState(session, ConversationState.Idle);
            if (CommandParser.TryParse(message) is { } command) await ExecuteCommandAsync(seller, session, ctx, command, ct);
            else await ReplyAsync(seller, "Theek hai, promotion nahi bheja.", ct);
            return;
        }

        ctx.BroadcastChannel = channel;
        var audiences = await AudienceCountsAsync(seller, ct);
        SetState(session, ConversationState.AwaitingBroadcastAudienceChoice);
        await ReplyAsync(seller,
            "Kitne customers ko bhejna hai?\n\n" +
            $"1️⃣ Sab ({audiences.All} customers)\n" +
            $"2️⃣ Sirf repeat customers ({audiences.Repeat})\n" +
            $"3️⃣ 30 din se inactive ({audiences.Inactive})", ct);
    }

    private async Task<(int All, int Repeat, int Inactive)> AudienceCountsAsync(Seller seller, CancellationToken ct) =>
        ((await AudienceAsync(seller, "all", ct)).Count, (await AudienceAsync(seller, "repeat_customers", ct)).Count, (await AudienceAsync(seller, "inactive_30d", ct)).Count);

    private async Task<List<Customer>> AudienceAsync(Seller seller, string filter, CancellationToken ct)
    {
        var customers = await _db.Customers.Include(c => c.Orders)
            .Where(c => c.SellerId == seller.Id && c.DeletedAt == null && c.Phone != null && c.Phone != "")
            .ToListAsync(ct);
        var inactiveSince = DateTime.UtcNow.AddDays(-30);
        return filter switch
        {
            "repeat_customers" => customers.Where(c => c.Orders.Count(o => o.Status != OrderStatus.Cancelled) > 1).ToList(),
            "inactive_30d" => customers.Where(c => !c.Orders.Any(o => o.CreatedAt >= inactiveSince)).ToList(),
            _ => customers
        };
    }

    private async Task HandleBroadcastAudienceChoiceAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        SetState(session, ConversationState.Idle);
        var text = ctx.BroadcastMessageText;
        var channel = ctx.BroadcastChannel ?? "whatsapp";
        ctx.BroadcastMessageText = null;
        ctx.BroadcastChannel = null;

        var filter = message.Trim() switch { "1" => "all", "2" => "repeat_customers", "3" => "inactive_30d", _ => null };
        if (filter is null || text is null)
        {
            await ReplyAsync(seller, "Theek hai, promotion nahi bheja. Dobara: \"broadcast: [message]\"", ct);
            return;
        }

        var audience = await AudienceAsync(seller, filter, ct);
        if (audience.Count == 0)
        {
            await ReplyAsync(seller, "Is group mein koi customer (phone ke saath) nahi hai.", ct);
            return;
        }

        var campaign = new Campaign { SellerId = seller.Id, Channel = channel, Message = text, TargetFilter = filter };
        _db.Campaigns.Add(campaign);

        int waSent = 0, waFailed = 0;
        foreach (var customer in audience)
        {
            if (channel is "whatsapp" or "both")
            {
                var ok = await _sender.SendTemplateMessageAsync(ToWhatsAppNumber(customer.Phone!), new[] { $"{seller.BusinessName}: {text}" }, ct);
                if (ok) waSent++; else waFailed++;
                campaign.Sends.Add(new CampaignSend { CustomerId = customer.Id, CustomerPhone = customer.Phone!, Channel = "whatsapp", Status = ok ? "sent" : "failed" });
            }
            if (channel is "sms" or "both")
                campaign.Sends.Add(new CampaignSend { CustomerId = customer.Id, CustomerPhone = customer.Phone!, Channel = "sms", Status = "not_configured" });
        }

        await ReplyAsync(seller, $"✅ {audience.Count} customers ko bheja gaya:\n{ChannelSummary(channel, waSent, waFailed, audience.Count)}\n\n\"campaign status\" se detail dekhein.", ct);
    }

    private static string ChannelSummary(string channel, int waSent, int waFailed, int smsCount)
    {
        var lines = new List<string>();
        if (channel is "whatsapp" or "both")
            lines.Add(waSent == 0 && waFailed > 0
                ? $"📱 WhatsApp: nahi gaya — broadcast template abhi approve/configure nahi hua ({waFailed} customers)"
                : $"📱 WhatsApp: {waSent} sent, {waFailed} failed");
        if (channel is "sms" or "both")
            lines.Add($"💬 SMS: {smsCount} queued — SMS service abhi connect nahi hui, message nahi gaya");
        return string.Join("\n", lines);
    }

    private async Task HandleCampaignStatusAsync(Seller seller, CancellationToken ct)
    {
        var campaign = await _db.Campaigns.Include(c => c.Sends)
            .Where(c => c.SellerId == seller.Id).OrderByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct);
        if (campaign is null)
        {
            await ReplyAsync(seller, "Abhi tak koi campaign nahi bheja. Try karein: \"sab customers ko batao: naya stock aaya\"", ct);
            return;
        }

        var customerIds = campaign.Sends.Where(s => s.CustomerId != null).Select(s => s.CustomerId!.Value).Distinct().ToList();
        var responses = await _db.Orders
            .Where(o => o.SellerId == seller.Id && customerIds.Contains(o.CustomerId) && o.CreatedAt >= campaign.CreatedAt && o.Status != OrderStatus.Cancelled)
            .ToListAsync(ct);

        var wa = campaign.Sends.Where(s => s.Channel == "whatsapp").ToList();
        var sms = campaign.Sends.Count(s => s.Channel == "sms");
        var title = campaign.Message.Length > 30 ? campaign.Message[..30] + "…" : campaign.Message;
        var channelName = campaign.Channel switch { "both" => "WhatsApp + SMS", "sms" => "SMS", _ => "WhatsApp" };

        await ReplyAsync(seller,
            $"📊 Last Campaign — \"{title}\" ({Formatters.DaysAgo(campaign.CreatedAt)}):\n" +
            $"Channel: {channelName}\n" +
            $"Bheja gaya: {customerIds.Count} customers ko\n" +
            $"{ChannelSummary(campaign.Channel, wa.Count(s => s.Status == "sent"), wa.Count(s => s.Status != "sent"), sms)}\n" +
            $"👀 Response: {responses.Select(o => o.CustomerId).Distinct().Count()} customers ne order kiya ({Formatters.Money(responses.Sum(o => o.Total))})", ct);
    }

    /// <summary>Pakistani local format (0300...) to the international form WhatsApp expects (92300...).</summary>
    private static string ToWhatsAppNumber(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("0092")) return digits[2..];
        if (digits.StartsWith("0") && digits.Length == 11) return "92" + digits[1..];
        return digits;
    }
}
