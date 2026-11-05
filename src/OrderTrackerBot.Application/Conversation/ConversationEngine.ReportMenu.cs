namespace OrderTrackerBot.Application.Conversation;

using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;

// "reports": the report list as one tap menu, so the common read-only reports need no typing.
// Each row id is a typed command; typing it still works.
public partial class ConversationEngine
{
    internal static readonly MenuRow[] ReportMenuRows =
    {
        new("orders today", "Orders today"),
        new("unpaid orders", "Unpaid orders"),
        new("customers", "Customers"),
        new("expenses today", "Expenses today"),
        new("stock", "Stock"),
        new("aaj ka hisaab", "Today's account"),
    };

    private Task SendReportMenuAsync(Seller seller, CancellationToken ct) =>
        _sender.SendListMessageAsync(seller.WhatsAppPhoneNumber, MessageTemplates.Get("reports.menu.001"), "Reports chunein",
            new[] { new MenuSection(MessageTemplates.Get("reports.menu.section"), ReportMenuRows) }, ct);
}
