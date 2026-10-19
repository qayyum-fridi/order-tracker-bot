using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Application.Time;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>What the dashboard asks the bot to send to the shop's own WhatsApp chat. Kind: receipt | customer | report | export.</summary>
public sealed record DashboardSendRequest(string? Kind, int? OrderId, int? CustomerId, string? From, string? To, IReadOnlyList<string>? Datasets, string? Period);

/// <summary>Sent is true when the message went out. Reason is a short code the dashboard turns into a sentence.</summary>
public sealed record DashboardSendResult(bool Sent, string Reason);

public partial class ConversationEngine
{
    private static readonly string[] ExportDatasets = ["orders", "customers", "catalog", "discounts"];

    /// <summary>Sends a receipt, a customer card, a report summary or an export to the shop's WhatsApp chat, using the same handlers as the chat commands.</summary>
    public async Task<DashboardSendResult> SendFromDashboardAsync(int sellerId, DashboardSendRequest request, CancellationToken ct)
    {
        var seller = await _db.Sellers.FirstAsync(s => s.Id == sellerId, ct);
        switch (request.Kind)
        {
            case "receipt":
                return await SendReceiptFromDashboardAsync(seller, request.OrderId, ct);
            case "customer":
                return await SendCustomerCardAsync(seller, request.CustomerId, ct);
            case "report":
                return await SendReportSummaryAsync(seller, request.From, request.To, ct);
            case "export":
                var datasets = (request.Datasets ?? []).Where(d => ExportDatasets.Contains(d)).ToList();
                if (datasets.Count == 0) return new DashboardSendResult(false, "export-needs-dataset");
                // The export handler sends the file to the chat itself, and answers in the chat if it can't.
                await HandleExportAsync(seller, new ExportRequest(datasets, request.Period), ct);
                return new DashboardSendResult(true, "export-sent");
            default:
                return new DashboardSendResult(false, "unknown-kind");
        }
    }

    private async Task<DashboardSendResult> SendReceiptFromDashboardAsync(Seller seller, int? orderId, CancellationToken ct)
    {
        if (_receiptPdf is null) return new DashboardSendResult(false, "no-receipt-pdf");
        var order = orderId is null ? null : await _db.Orders.Include(o => o.Customer).Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.SellerId == seller.Id && o.Id == orderId, ct);
        if (order is null) return new DashboardSendResult(false, "order-not-found");

        var branding = await _db.SellerBrandings.AsNoTracking().FirstOrDefaultAsync(b => b.SellerId == seller.Id, ct);
        var (pdf, numberIsNew) = await RenderReceiptAsync(seller, order, branding, ct);
        var sent = await _sender.SendDocumentAsync(seller.WhatsAppPhoneNumber, pdf, $"Receipt-{order.Id}.pdf", "application/pdf",
            $"🧾 {(order.SalesTaxRate > 0 ? "Tax invoice" : "Receipt")} No. {order.ReceiptNumber} (Order #{order.Id}) — {order.Customer?.Name}, {Formatters.Money(order.Total)}", ct);
        if (!sent)
        {
            // Same rule as the chat receipt: never use up a receipt number the seller didn't get.
            if (numberIsNew) order.ReceiptNumber = null;
            await _db.SaveChangesAsync(ct);
            return new DashboardSendResult(false, "send-failed");
        }
        await _db.SaveChangesAsync(ct);
        return new DashboardSendResult(true, "receipt-sent");
    }

    private async Task<DashboardSendResult> SendCustomerCardAsync(Seller seller, int? customerId, CancellationToken ct)
    {
        var customer = customerId is null ? null : await _db.Customers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.SellerId == seller.Id && c.Id == customerId, ct);
        if (customer is null) return new DashboardSendResult(false, "customer-not-found");

        var orders = await _db.Orders.AsNoTracking()
            .Where(o => o.SellerId == seller.Id && o.CustomerId == customer.Id && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned)
            .Select(o => new { o.Total, o.CreatedAt }).ToListAsync(ct);
        var last = orders.OrderByDescending(o => o.CreatedAt).FirstOrDefault();
        var text = MessageTemplates.Get("dashsend.customer", customer.Name, customer.Phone ?? "-", customer.City ?? "-", orders.Count,
            Formatters.Money(orders.Sum(o => o.Total)), last is null ? "-" : last.CreatedAt.ToString("dd MMM yyyy", CultureInfo.InvariantCulture));
        await _sender.SendTextMessageAsync(seller.WhatsAppPhoneNumber, text, ct);
        return new DashboardSendResult(true, "customer-sent");
    }

    private async Task<DashboardSendResult> SendReportSummaryAsync(Seller seller, string? from, string? to, CancellationToken ct)
    {
        const string format = "yyyy-MM-dd";
        if (!DateOnly.TryParseExact(from, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !DateOnly.TryParseExact(to, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
            || end < start || end.DayNumber - start.DayNumber > 366)
            return new DashboardSendResult(false, "bad-range");

        // Days are the shop's own days, so the window starts and ends at local midnight.
        var tz = SellerClock.Resolve(seller.TimeZoneId);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(start.ToDateTime(TimeOnly.MinValue), tz);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(end.AddDays(1).ToDateTime(TimeOnly.MinValue), tz);
        var orders = await _db.Orders.AsNoTracking()
            .Where(o => o.SellerId == seller.Id && o.CreatedAt >= startUtc && o.CreatedAt < endUtc)
            .Select(o => new { o.Status, o.Total }).ToListAsync(ct);
        var sales = orders.Where(o => o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned).ToList();
        var revenue = sales.Sum(o => o.Total);
        var average = sales.Count == 0 ? 0 : revenue / sales.Count;

        var text = MessageTemplates.Get("dashsend.report", start.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
            end.ToString("dd MMM yyyy", CultureInfo.InvariantCulture), sales.Count, Formatters.Money(revenue), Formatters.Money(average),
            orders.Count(o => o.Status == OrderStatus.Cancelled), orders.Count(o => o.Status == OrderStatus.Returned));
        await _sender.SendTextMessageAsync(seller.WhatsAppPhoneNumber, text, ct);
        return new DashboardSendResult(true, "report-sent");
    }
}
