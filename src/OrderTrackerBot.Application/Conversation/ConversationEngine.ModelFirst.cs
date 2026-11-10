using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Features:ModelFirst. One understanding call for a typed message, with the four core jobs (order, status, day summary, catalog) run from its
// fields. "shadow" only logs the decision; "on" runs it. Anything else, or an unusable answer, returns false and the older pipeline answers.
public partial class ConversationEngine
{
    private const int ModelFirstCatalogShortlist = 60;

    private async Task<bool> TryModelFirstAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        if (_turnInterpreter is null || _features.ModelFirst is not ("shadow" or "on")) return false;
        // Exact typed commands ("orders today", "mark 3 shipped") keep their fast path; loose keyword rules are asked about.
        if (CommandParser.TryParse(message) is { } exact && !LooseRuleKinds.Contains(exact.Kind)) return false;

        var catalog = await LoadCatalogAsync(seller, ct);
        var shortlist = SoundMatch.Shortlist(message, catalog, c => c.Label, ModelFirstCatalogShortlist);
        var recent = await _db.Orders.Include(o => o.Customer)
            .Where(o => o.SellerId == seller.Id).OrderByDescending(o => o.Id).Take(5).ToListAsync(ct);
        var context = new TurnContext
        {
            BusinessName = seller.BusinessName ?? "",
            Catalog = shortlist.Select(c => new AiCatalogItem { Name = c.Label, Price = c.Product.Price }).ToList(),
            CatalogSize = catalog.Count,
            RecentOrders = recent.Select(o => $"#{o.Id} {o.Customer?.Name} {o.Status}").ToList(),
        };

        var action = await _turnInterpreter.UnderstandAsync(context, message, ct);
        // Metadata only: the message holds customer names and phone numbers, so its text is never logged.
        _logger?.LogInformation("ModelFirst {Mode}: action={Action} length={Length}", _features.ModelFirst, action?.Action ?? "none", message.Length);
        if (_features.ModelFirst != "on" || action is null) return false;
        return await RunTurnActionAsync(seller, session, ctx, action, catalog, message, ct);
    }

    /// <summary>Runs the four core actions with the handlers the typed commands already use. False = not one of them (or not enough detail): the older pipeline answers.</summary>
    private async Task<bool> RunTurnActionAsync(Seller seller, ConversationSession session, SessionContextData ctx, TurnAction action,
        List<CatalogEntry> catalog, string message, CancellationToken ct)
    {
        switch (action.Action)
        {
            case TurnAction.CreateOrder when action.Orders.Count > 0:
                await HandleAnalysisAsync(seller, session, ctx,
                    new AiMessageAnalysis { Intent = "new_order", IsOrderAttempt = true, Order = action.Orders[0], AdditionalOrders = action.Orders.Skip(1).ToList() },
                    catalog, fromScreenshot: false, ct, message);
                return true;

            case TurnAction.AddProduct when action.Products.Count > 0:
                await HandleNewProductsAsync(seller, ctx, action.Products, message, ct);
                return true;

            case TurnAction.ShowCatalog:
                await HandleCatalogAsync(seller, ctx, null, ct);
                return true;

            case TurnAction.ShowSummary:
                await HandleTodaysSummaryAsync(seller, ModelFirstPeriod(action.Period), ct);
                return true;

            case TurnAction.ShowOrders when action.PendingOnly:
                await HandlePendingOrdersAsync(seller, ctx, ct);
                return true;

            case TurnAction.ShowOrders:
                await HandleOrdersTodayAsync(seller, ctx, ModelFirstPeriod(action.Period), ct);
                return true;

            case TurnAction.ShowOrder when action.OrderId is { } showId:
                await HandleOrderDetailAsync(seller, ctx, showId, ct);
                return true;

            case TurnAction.UpdateStatus when action.OrderId is { } statusId && action.Status is not null:
                await HandleMarkStatusAsync(seller, session, ctx,
                    new ParsedCommand { Kind = CommandKind.MarkStatus, Number = statusId, Text = action.Status }, ct);
                return true;

            case TurnAction.CancelOrder when action.OrderId is { } cancelId:
                await HandleCancelOrderRequestAsync(seller, session, ctx,
                    new ParsedCommand { Kind = CommandKind.CancelOrder, Number = cancelId }, ct);
                return true;

            default:
                return false;
        }
    }

    /// <summary>The model's period name mapped to the phrase the report code reads; null = today.</summary>
    private static string? ModelFirstPeriod(string? period) => period switch
    {
        "yesterday" => "yesterday",
        "last_month" => "lastmonth",
        "this_week" => "this week",
        "last_week" => "last week",
        "this_month" => "this month",
        "last_7_days" => "7 days",
        "last_30_days" => "30 days",
        _ => null,
    };
}
