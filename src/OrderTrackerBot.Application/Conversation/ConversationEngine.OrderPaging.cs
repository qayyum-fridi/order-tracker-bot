using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

// Order lists are paged like the catalog: the page size is the one set with "catalog size N" (kept in the same setting), and "orders next / prev / last /
// page N" re-run the list last shown on the asked page. The numbers on a page are the orders' positions in the whole list, so "order 13" still names the
// 13th order even when it is on page 2.
public partial class ConversationEngine
{
    /// <summary>The page asked for by "orders next" and friends, for the list about to be re-run; null = a fresh list starts at page 1.</summary>
    private int? _orderPageRequest;

    private sealed record OrderPageWindow(int Page, int Pages, int Skip, int Take, int Total);

    private int OrderPageSize(SessionContextData ctx) =>
        ctx.CatalogPageSize is >= MinCatalogPageSize and <= MaxCatalogPageSize ? ctx.CatalogPageSize : DefaultCatalogPageSize;

    /// <summary>The page of a list of <paramref name="total"/> orders, and the list is remembered so "orders next" can re-run it.</summary>
    private OrderPageWindow OrderPageFor(SessionContextData ctx, CommandKind kind, string? text, int total)
    {
        var size = OrderPageSize(ctx);
        var pages = Math.Max(1, (int)Math.Ceiling(total / (double)size));
        var page = Math.Clamp(_orderPageRequest ?? 1, 1, pages);
        ctx.OrderListKind = kind;
        ctx.OrderListText = text;
        ctx.OrderListPage = page;
        return new OrderPageWindow(page, pages, (page - 1) * size, size, total);
    }

    /// <summary>Sends one page of an order list; with more than one page it names the neighbours and carries the paging taps.</summary>
    private async Task ReplyOrderPageAsync(Seller seller, string text, OrderPageWindow paging, CancellationToken ct)
    {
        if (paging.Pages == 1)
        {
            await ReplyAsync(seller, text, ct);
            return;
        }

        var first = paging.Skip + 1;
        var last = Math.Min(paging.Skip + paging.Take, paging.Total);
        var nav = MessageTemplates.Get("orders-list.001", first, last, paging.Total, paging.Page, paging.Pages, paging.Take);
        var buttons = new List<string>();
        if (paging.Page > 1) buttons.Add("⬅️ orders prev");
        if (paging.Page < paging.Pages) buttons.Add("➡️ orders next");
        await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, text + "\n" + nav, buttons, ct);
    }

    /// <summary>"orders next", "orders prev", "orders last", "orders page 3", "orders size 25": re-runs the order list last shown, on the asked page.</summary>
    private async Task HandleOrderPageAsync(Seller seller, ConversationSession session, SessionContextData ctx, string action, CancellationToken ct)
    {
        if (ctx.OrderListKind is not { } kind)
        {
            await ReplyAsync(seller, MessageTemplates.Get("orders-list.002"), ct);
            return;
        }

        int page;
        if (action.StartsWith("size:", StringComparison.Ordinal) && int.TryParse(action[5..], out var size))
        {
            var clamped = Math.Clamp(size, MinCatalogPageSize, MaxCatalogPageSize);
            ctx.CatalogPageSize = clamped;
            await ReplyAsync(seller, clamped == size
                ? MessageTemplates.Get("orders-list.003", clamped)
                : MessageTemplates.Get("orders-list.004", clamped), ct);
            page = 1;
        }
        else if (action.StartsWith("page:", StringComparison.Ordinal) && int.TryParse(action[5..], out var typed)) page = typed;
        else page = action switch
        {
            "prev" => ctx.OrderListPage - 1,
            "last" => int.MaxValue,
            _ => ctx.OrderListPage + 1
        };

        _orderPageRequest = page;
        try
        {
            await ExecuteCommandAsync(seller, session, ctx, new ParsedCommand { Kind = kind, Text = ctx.OrderListText }, ct);
        }
        finally
        {
            _orderPageRequest = null;
        }
    }
}
