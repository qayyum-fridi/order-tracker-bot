using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Screens 1c-1f: 14-day free trial, then Basic/Pro paid by JazzCash/Easypaisa to the founder and confirmed with "paid".
public partial class ConversationEngine
{
    private static readonly Regex PlanChoice = new(@"^(?:1️⃣\s*|2️⃣\s*)?(basic|pro)\b.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PaidWord = new(@"^(paid|payment\s+done|pay\s+kar\s+diya|bhej\s+diya)[.!]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Pro-only commands (mockup 1d: "Pro - + loyalty, discounts, reports"). Trial users get everything.
    private static readonly HashSet<CommandKind> ProOnlyCommands = new()
    {
        CommandKind.CreateDiscount, CommandKind.DiscountList, CommandKind.CreateLoyalty, CommandKind.LoyalCustomers,
        CommandKind.TrendingProducts, CommandKind.SlowMovers, CommandKind.Broadcast, CommandKind.CampaignStatus
    };

    private bool HasAccess(Seller seller, DateTime now) =>
        !_billing.Enabled
        || seller.SubscriptionActiveUntil > now
        || seller.TrialEndsAt is null
        || seller.TrialEndsAt > now;

    private void StartTrial(Seller seller)
    {
        if (_billing.Enabled && seller.TrialEndsAt is null)
            seller.TrialEndsAt = DateTime.UtcNow.Date.AddDays(_billing.TrialDays);
    }

    private string TrialStartedText(Seller seller) =>
        _billing.Enabled && seller.TrialEndsAt is { } end
            ? $"\n🎁 Aapka {_billing.TrialDays}-din FREE trial shuru ho gaya (koi card nahi chahiye).\nTrial khatam: {end:d MMMM}\n"
            : "";

    private string[] PlanButtons => new[] { $"Basic - Rs.{_billing.BasicPrice:0}", $"Pro - Rs.{_billing.ProPrice:0}" };

    private string PlansText =>
        $"1️⃣ Basic - Rs.{_billing.BasicPrice:0}/month (orders, tracking, COD)\n" +
        $"2️⃣ Pro - Rs.{_billing.ProPrice:0}/month (+ loyalty, discounts, reports)";

    /// <summary>Returns true when the message was fully handled by billing (plan choice, "paid", or access paused).</summary>
    private async Task<bool> TryHandleBillingAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        if (!_billing.Enabled) return false;
        var now = DateTime.UtcNow;
        StartTrial(seller); // sellers onboarded before billing existed start their trial on their next message

        var trimmed = message.Trim();
        var plan = PlanChoice.Match(trimmed);
        var askedToSubscribe = CommandParser.TryParse(trimmed)?.Kind == CommandKind.Subscribe;

        if (plan.Success && (session.State == ConversationState.AwaitingSubscriptionPayment || !HasAccess(seller, now) || trimmed.Contains("rs", StringComparison.OrdinalIgnoreCase)))
        {
            ctx.SelectedPlan = plan.Groups[1].Value.Equals("pro", StringComparison.OrdinalIgnoreCase) ? nameof(SubscriptionPlan.Pro) : nameof(SubscriptionPlan.Basic);
            SetState(session, ConversationState.AwaitingSubscriptionPayment);
            var price = ctx.SelectedPlan == nameof(SubscriptionPlan.Pro) ? _billing.ProPrice : _billing.BasicPrice;
            var payTo = string.IsNullOrWhiteSpace(_billing.PaymentNumber)
                ? "(payment number jald bheja jayega)"
                : $"JazzCash/Easypaisa: {_billing.PaymentNumber}\n({_billing.PaymentName})";
            await ReplyAsync(seller,
                $"✅ {ctx.SelectedPlan} plan select ho gaya — Rs.{price:0}/month.\n\nPayment karein:\n{payTo}\n\nPayment ke baad \"paid\" likh dein.", ct);
            return true;
        }

        if (session.State == ConversationState.AwaitingSubscriptionPayment && PaidWord.IsMatch(trimmed) && ctx.SelectedPlan is { } selected)
        {
            seller.Plan = Enum.Parse<SubscriptionPlan>(selected);
            var from = seller.SubscriptionActiveUntil > now ? seller.SubscriptionActiveUntil.Value : now;
            seller.SubscriptionActiveUntil = from.AddMonths(1);
            ctx.SelectedPlan = null;
            SetState(session, ConversationState.Idle);
            await ReplyAsync(seller, $"✅ Subscription active — agla mahina: {seller.SubscriptionActiveUntil:d MMMM}.\nShukriya! Kaam jaari rakhein.", ct);
            await _founderAlerts.NotifyAsync(seller.Id,
                $"[{seller.BusinessName}] says PAID for {seller.Plan} ({seller.WhatsAppPhoneNumber}) — verify the transfer.", ct);
            return true;
        }

        if (askedToSubscribe)
        {
            await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber,
                $"💳 Plans:\n\n{PlansText}\n\nPlan chunein:", PlanButtons, ct);
            return true;
        }

        if (!HasAccess(seller, now))
        {
            var what = seller.SubscriptionActiveUntil is null ? "free trial" : "subscription";
            await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber,
                $"⏸️ Aapka {what} khatam ho gaya hai.\n\nAapka data safe hai — subscribe karein dobara shuru karne ke liye:\n\n" +
                $"1️⃣ Basic - Rs.{_billing.BasicPrice:0}/month\n2️⃣ Pro - Rs.{_billing.ProPrice:0}/month", PlanButtons, ct);
            return true;
        }

        if (session.State == ConversationState.AwaitingSubscriptionPayment)
            SetState(session, ConversationState.Idle); // moved on without paying; keep working normally

        return false;
    }

    private bool IsBlockedByPlan(Seller seller, CommandKind kind) =>
        _billing.Enabled && seller.Plan == SubscriptionPlan.Basic && seller.SubscriptionActiveUntil > DateTime.UtcNow
        && ProOnlyCommands.Contains(kind);

    private Task SendProOnlyAsync(Seller seller, CancellationToken ct) =>
        _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber,
            $"⭐ Yeh feature Pro plan mein hai (Rs.{_billing.ProPrice:0}/month) — loyalty, discounts aur reports.", new[] { PlanButtons[1] }, ct);

    /// <summary>Screen 1d, sent by the scheduler 2 days before the trial ends.</summary>
    private async Task SendTrialEndingReminderAsync(Seller seller, CancellationToken ct)
    {
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var orders = await _db.Orders.Where(o => o.SellerId == seller.Id && o.CreatedAt >= monthStart && o.Status != OrderStatus.Cancelled).ToListAsync(ct);
        var customers = await _db.Customers.CountAsync(c => c.SellerId == seller.Id && c.DeletedAt == null, ct);
        var daysLeft = Math.Max(1, (int)Math.Ceiling((seller.TrialEndsAt!.Value - DateTime.UtcNow).TotalDays));

        await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber,
            $"⏳ Aapka free trial {daysLeft} din mein khatam ho raha hai.\n\nIs mahine aapne track kiya:\n" +
            $"📦 {orders.Count} orders\n💰 {Formatters.Money(orders.Sum(o => o.Total))} sales\n✅ {customers} customers ka record\n\n" +
            $"Continue karne ke liye:\n{PlansText}", PlanButtons, ct);
        seller.TrialReminderSentAt = DateTime.UtcNow;
    }
}
