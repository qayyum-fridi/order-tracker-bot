using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

// Features:ModelFirst. One understanding call for a typed message, with the four core jobs (order, status, day summary, catalog) run from its
// fields. "shadow" only logs the decision; "on" runs it. Anything else, or an unusable answer, returns false and the older pipeline answers.
public partial class ConversationEngine
{
    private const int ModelFirstCatalogShortlist = 60;

    /// <param name="fromVoice">The text is a voice transcript.</param>
    /// <param name="image">A screenshot sent with the text (its caption); the picture is read too.</param>
    /// <param name="beforeRun">Runs once the action is known and is going to run (e.g. the voice echo), so it is only sent when the new path answers.</param>
    private async Task<bool> TryModelFirstAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct,
        bool fromVoice = false, AiImageInput? image = null, bool fromScreenshot = false, Func<Task>? beforeRun = null)
    {
        if (_turnInterpreter is null || _features.ModelFirst is not ("shadow" or "on")) return false;
        // Exact typed commands ("orders today", "mark 3 shipped") keep their fast path; loose keyword rules are asked about. A picture is never an exact command.
        if (image is null && CommandParser.TryParse(message) is { } exact && !LooseRuleKinds.Contains(exact.Kind)) return false;

        var catalog = await LoadCatalogAsync(seller, ct);
        var shortlist = SoundMatch.Shortlist(message, catalog, c => c.Label, ModelFirstCatalogShortlist);
        // The same context the voice rewrite already reads: latest orders, saved customers with their history, and the last chat lines.
        var voice = await LoadVoiceContextAsync(seller, catalog, ct);
        var context = new TurnContext
        {
            BusinessName = seller.BusinessName ?? "",
            Catalog = shortlist.Select(c => new AiCatalogItem { Name = c.Label, Price = c.Product.Price }).ToList(),
            CatalogSize = catalog.Count,
            RecentOrders = voice.Recent.Select(o => $"#{o.Id} {o.Customer?.Name} {o.Status}").ToList(),
            KnownCustomers = voice.KnownCustomers,
            RecentExchanges = voice.Exchanges,
            FromVoice = fromVoice,
        };

        var action = await _turnInterpreter.UnderstandAsync(context, message, image, ct);
        // Metadata only: the message holds customer names and phone numbers, so its text is never logged.
        _logger?.LogInformation("ModelFirst {Mode}: action={Action} length={Length}", _features.ModelFirst, action?.Action ?? "none", message.Length);
        if (_features.ModelFirst != "on" || action is null) return false;
        if (beforeRun is not null) await beforeRun();
        return await RunTurnActionAsync(seller, session, ctx, action, catalog, message, ct, fromScreenshot);
    }

    /// <summary>Runs the four core actions with the handlers the typed commands already use. False = not one of them (or not enough detail): the older pipeline answers.</summary>
    private async Task<bool> RunTurnActionAsync(Seller seller, ConversationSession session, SessionContextData ctx, TurnAction action,
        List<CatalogEntry> catalog, string message, CancellationToken ct, bool fromScreenshot)
    {
        // A price, cost or delivery charge the model returned must be a figure the seller actually said. Otherwise nothing runs; the seller is asked.
        var unquoted = UnquotedAmounts(action, message);
        if (unquoted.Count > 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("model-amount.001", string.Join(", ", unquoted)), ct);
            return true;
        }

        switch (action.Action)
        {
            case TurnAction.CreateOrder when action.Orders.Count > 0:
                await HandleAnalysisAsync(seller, session, ctx,
                    new AiMessageAnalysis { Intent = "new_order", IsOrderAttempt = true, Order = action.Orders[0], AdditionalOrders = action.Orders.Skip(1).ToList() },
                    catalog, fromScreenshot, ct, message);
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
                // A status the model picked (maybe from a misheard voice note) waits for YES/NO before the order changes.
                ctx.ModelStatusOrderId = statusId;
                ctx.ModelStatusValue = action.Status;
                SetState(session, ConversationState.AwaitingModelStatusConfirmation);
                await AskYesNoAsync(seller, MessageTemplates.Get("model-status.001", statusId, action.Status), ct);
                return true;

            case TurnAction.CancelOrder when action.OrderId is { } cancelId:
                await HandleCancelOrderRequestAsync(seller, session, ctx,
                    new ParsedCommand { Kind = CommandKind.CancelOrder, Number = cancelId }, ct);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// The money figures the model returned (unit prices, delivery charges, product prices and costs) that the message does not contain, in digits or in words.
    /// Quantities are not checked: a small count is often not written as an amount. Zero (free delivery) is not an amount to check.
    /// </summary>
    internal static IReadOnlyList<long> UnquotedAmounts(TurnAction action, string message)
    {
        var said = SpokenNumbers.Amounts(message);
        var figures = new List<decimal>();
        foreach (var order in action.Orders)
        {
            if (order.DeliveryCharge is { } delivery) figures.Add(delivery);
            foreach (var item in order.Items)
                if (item.StatedUnitPrice is { } unitPrice) figures.Add(unitPrice);
        }
        foreach (var product in action.Products)
        {
            if (product.Price is { } price) figures.Add(price);
            if (product.Cost is { } cost) figures.Add(cost);
        }
        return figures
            .Where(f => f > 0)
            .Select(f => (long)Math.Round(f, MidpointRounding.AwayFromZero))
            .Where(v => !said.Contains(v))
            .Distinct()
            .ToList();
    }

    /// <summary>The seller's answer to a status change the model picked: only YES runs it; anything else leaves the order as it was.</summary>
    private async Task HandleModelStatusConfirmationAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        SetState(session, ConversationState.Idle);
        var orderId = ctx.ModelStatusOrderId;
        var status = ctx.ModelStatusValue;
        ctx.ModelStatusOrderId = null;
        ctx.ModelStatusValue = null;
        if (orderId is null || status is null || !CommandParser.IsAffirmative(message))
        {
            await ReplyAsync(seller, MessageTemplates.Get("commands2.005"), ct);
            return;
        }
        await HandleMarkStatusAsync(seller, session, ctx,
            new ParsedCommand { Kind = CommandKind.MarkStatus, Number = orderId, Text = status }, ct);
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
