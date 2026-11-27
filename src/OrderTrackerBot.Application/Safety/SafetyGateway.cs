using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Safety;

/// <summary>What the gateway decided. <see cref="Allow"/> and <see cref="RequireConfirmation"/> are the only ones that let anything proceed.</summary>
public enum SafetyDecision { Allow, RequireConfirmation, Block, Stale, Expired }

public sealed record SafetyResult(SafetyDecision Decision, string? ConfirmationId = null, int? OrderId = null, string? Value = null, string? Reason = null);

/// <summary>
/// Stage 7. The one place that decides whether a write may run: the user is the seller, the target belongs to the seller, the action's risk
/// (confirm or allow), and, for a confirmed write, that the YES belongs to this confirmation, is still valid, and the target has not changed.
/// It never writes the business records itself; the caller executes after <see cref="SafetyDecision.Allow"/>.
/// Batch 1 covers the order-status writes: cancel and status change. Other actions join <see cref="ConfirmedActions"/> in later batches.
/// </summary>
public sealed class SafetyGateway
{
    public static readonly TimeSpan ConfirmationTtl = TimeSpan.FromMinutes(10);

    /// <summary>Actions that need a YES before they run. Anything else is allowed as before.</summary>
    public static readonly IReadOnlySet<string> ConfirmedActions = new HashSet<string> { TurnAction.CancelOrder, TurnAction.UpdateStatus };

    private readonly IAppDbContext _db;

    public SafetyGateway(IAppDbContext db) => _db = db;

    /// <summary>The order's version: its last change time and status. Any change to either makes an earlier YES stale.</summary>
    public static string VersionOf(Order order) => $"{order.UpdatedAt.Ticks}|{order.Status}";

    /// <summary>First check, before the YES question. Returns the confirmation to ask under when one is needed.</summary>
    public async Task<SafetyResult> AuthorizeAsync(Seller seller, string userId, string action, int? orderId, string? value, DateTime now, CancellationToken ct)
    {
        if (!string.Equals(userId, seller.WhatsAppPhoneNumber, StringComparison.Ordinal)) return new(SafetyDecision.Block, Reason: "user");

        Order? order = null;
        if (orderId is { } id)
        {
            order = await _db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id && o.SellerId == seller.Id, ct);
            if (order is null) return new(SafetyDecision.Block, OrderId: id, Reason: "ownership");
        }

        if (!ConfirmedActions.Contains(action)) return new(SafetyDecision.Allow, OrderId: orderId, Value: value);
        if (order is null) return new(SafetyDecision.Block, Reason: "target");

        var record = new ConfirmationRecord
        {
            Id = Guid.NewGuid().ToString("N"), SellerId = seller.Id, UserId = userId, Action = action, OrderId = orderId, Value = value,
            RecordVersion = VersionOf(order), Status = "Pending", CreatedAt = now, ExpiresAt = now + ConfirmationTtl,
        };
        _db.ConfirmationRecords.Add(record);
        return new(SafetyDecision.RequireConfirmation, record.Id, orderId, value);
    }

    /// <summary>
    /// Second check, when the YES arrives. Only a pending, unexpired confirmation for this seller and user whose target is unchanged is consumed,
    /// and only once: the consume is one conditional update, so two YES messages cannot both run.
    /// </summary>
    public async Task<SafetyResult> ReauthorizeAsync(Seller seller, string userId, string confirmationId, DateTime now, CancellationToken ct)
    {
        var record = await _db.ConfirmationRecords.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == confirmationId && c.SellerId == seller.Id && c.UserId == userId, ct);
        if (record is null) return new(SafetyDecision.Block, Reason: "unknown");
        if (record.Status != "Pending") return new(SafetyDecision.Block, Reason: "used");
        if (now >= record.ExpiresAt)
        {
            await SetStatusAsync(confirmationId, "Expired", ct);
            return new(SafetyDecision.Expired);
        }

        if (record.OrderId is { } orderId)
        {
            var order = await _db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId && o.SellerId == seller.Id, ct);
            if (order is null)
            {
                await SetStatusAsync(confirmationId, "Cancelled", ct);
                return new(SafetyDecision.Block, OrderId: orderId, Reason: "ownership");
            }
            if (VersionOf(order) != record.RecordVersion)
            {
                await SetStatusAsync(confirmationId, "Stale", ct);
                return new(SafetyDecision.Stale, OrderId: orderId);
            }
        }

        var consumed = await _db.ConfirmationRecords.Where(c => c.Id == confirmationId && c.Status == "Pending")
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, "Consumed").SetProperty(c => c.ConsumedAt, now), ct);
        if (consumed != 1) return new(SafetyDecision.Block, Reason: "used");
        return new(SafetyDecision.Allow, confirmationId, record.OrderId, record.Value);
    }

    /// <summary>The seller said no (or something else): the question closes and nothing runs.</summary>
    public Task RejectAsync(string confirmationId, CancellationToken ct) => SetStatusAsync(confirmationId, "Rejected", ct);

    private Task SetStatusAsync(string confirmationId, string status, CancellationToken ct) =>
        _db.ConfirmationRecords.Where(c => c.Id == confirmationId && c.Status == "Pending")
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, status), ct);
}
