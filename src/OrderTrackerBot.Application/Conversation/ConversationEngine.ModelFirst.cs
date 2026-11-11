using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

// Features:ModelFirst. One understanding call for a message (typed, spoken or a screenshot), with the core jobs (orders, customers, catalog, day summary)
// run from its fields. "shadow" only logs the decision; "on" runs it. For an idle seller whose words are no typed command this call is the whole answer;
// in any other state an action it cannot run returns false and the older pipeline answers.
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
        _logger?.LogInformation("ModelFirst {Mode}: action={Action} actions={Count} length={Length}", _features.ModelFirst, action?.Action ?? "none",
            action is null ? 0 : 1 + action.Also.Count, message.Length);
        if (_features.ModelFirst != "on" || action is null) return false;

        // An idle, set-up seller whose words are no typed command gets the whole answer from this one call: a question, a how-to or "not yet",
        // never the older keyword router or voice rewrite (they guessed a command from loose words, e.g. "contact number" became a support request).
        var final = image is null && seller.OnboardingComplete && session.State == ConversationState.Idle && CommandParser.TryParse(message) is null;
        var actions = MergeOrderActions(action);

        if (actions.Count == 1)
        {
            var runnable = await CanRunAsync(seller, action, message, ct);
            if (!runnable && !final) return false;
            if (beforeRun is not null) await beforeRun();
            if (runnable && await RunTurnActionAsync(seller, session, ctx, action, catalog, message, ct, fromScreenshot)) return true;
            if (!final) return false;
            await ReplyNotRunAsync(seller, action, ct);
            return true;
        }

        // Several requests in one message: all of them must be runnable, or the older pipeline answers the whole message (unless this one is final).
        var ready = new List<bool>();
        foreach (var a in actions) ready.Add(await CanRunAsync(seller, a, message, ct));
        if (!ready.All(r => r) && !final) return false;
        if (beforeRun is not null) await beforeRun();
        for (var i = 0; i < actions.Count; i++)
        {
            if (!ready[i])
            {
                await ReplyNotRunAsync(seller, actions[i], ct);
                continue;
            }
            await RunOrQueueAsync(seller, session, ctx, actions[i], catalog, message, ct, fromScreenshot);
        }
        return true;
    }

    /// <summary>
    /// The actions of one message in order. Several create_order entries become one, so the draft handler takes them as one order with extra customers
    /// (the same as a typed multi-customer order) instead of each one replacing the draft before it.
    /// </summary>
    private static List<TurnAction> MergeOrderActions(TurnAction first)
    {
        var all = new List<TurnAction> { first };
        all.AddRange(first.Also);
        if (all.Count(a => a.Action == TurnAction.CreateOrder) < 2) return all;
        var orders = all.Where(a => a.Action == TurnAction.CreateOrder).SelectMany(a => a.Orders).ToList();
        var merged = new TurnAction { Action = TurnAction.CreateOrder, Orders = orders };
        var result = new List<TurnAction>();
        var placed = false;
        foreach (var a in all)
        {
            if (a.Action != TurnAction.CreateOrder) { result.Add(a); continue; }
            if (!placed) { result.Add(merged); placed = true; }
        }
        return result;
    }

    /// <summary>
    /// Runs one action now, unless a yes/no question is open or earlier actions of this message are still waiting: then it waits in
    /// <see cref="SessionContextData.ModelQueue"/> and runs once the seller's answer has been handled (<see cref="DrainModelQueueAsync"/>).
    /// </summary>
    private async Task RunOrQueueAsync(Seller seller, ConversationSession session, SessionContextData ctx, TurnAction action, List<CatalogEntry> catalog,
        string message, CancellationToken ct, bool fromScreenshot)
    {
        if (session.State != ConversationState.Idle || ctx.ModelQueue.Count > 0)
        {
            ctx.ModelQueue.Add(new QueuedModelTurn(Plain(action), message));
            return;
        }
        if (!await RunTurnActionAsync(seller, session, ctx, action, catalog, message, ct, fromScreenshot))
            await ReplyNotRunAsync(seller, action, ct);
    }

    /// <summary>A copy of the action without the list of further actions (the queue keeps one action per entry).</summary>
    private static TurnAction Plain(TurnAction action) => new()
    {
        Action = action.Action, Orders = action.Orders, Products = action.Products, OrderId = action.OrderId, CustomerName = action.CustomerName,
        Status = action.Status, Period = action.Period, PendingOnly = action.PendingOnly, Customer = action.Customer, Field = action.Field,
        Value = action.Value, Topic = action.Topic, Question = action.Question, OrderEdits = action.OrderEdits,
    };

    /// <summary>
    /// Runs the queued actions of the same message once no question is open, in order. A queued action that opens a yes/no question stops the loop
    /// until that answer; the rest stay queued. Called at the end of every incoming message.
    /// </summary>
    private async Task DrainModelQueueAsync(string fromPhoneNumber, CancellationToken ct)
    {
        var seller = await LoadOrCreateSellerAsync(fromPhoneNumber, ct);
        if (seller.Session is not { } session) return;
        var ctx = SessionContextData.FromJson(session.ContextJson);
        if (ctx.ModelQueue.Count == 0 || session.State != ConversationState.Idle) return;
        var catalog = await LoadCatalogAsync(seller, ct);
        while (ctx.ModelQueue.Count > 0 && session.State == ConversationState.Idle)
        {
            var next = ctx.ModelQueue[0];
            ctx.ModelQueue.RemoveAt(0);
            await PersistAsync(session, ctx, ct);
            if (!await RunTurnActionAsync(seller, session, ctx, next.Action, catalog, next.Message, ct, false))
                await ReplyNotRunAsync(seller, next.Action, ct);
            await PersistAsync(session, ctx, ct);
        }
    }

    /// <summary>True when the action has the details its handler needs, so the echo ("Maine suna") is sent only when this path answers.</summary>
    private async Task<bool> CanRunAsync(Seller seller, TurnAction action, string message, CancellationToken ct) => action.Action switch
    {
        TurnAction.CreateOrder => action.Orders.Count > 0,
        TurnAction.AddProduct => action.Products.Count > 0,
        TurnAction.EditProduct => action.Products.Count > 0 && action.Products[0].Price is > 0,
        TurnAction.AddStock => action.Products.Count > 0 && (action.Products[0].Stock is > 0 || action.Products[0].StockPacks is > 0),
        TurnAction.ShowCatalog or TurnAction.ShowSummary or TurnAction.ShowOrders or TurnAction.ShowCustomer or TurnAction.HowTo or TurnAction.ContactSupport => true,
        TurnAction.ShowOrder => SaidOrderId(action, message) is not null || await ModelCustomerAsync(seller, action.CustomerName, ct) is not null,
        TurnAction.UpdateStatus => action.Status is not null && (SaidOrderId(action, message) is not null
                                   || (SaidName(action.CustomerName, message) && await ModelCustomerAsync(seller, action.CustomerName, ct) is not null)),
        TurnAction.CancelOrder => SaidOrderId(action, message) is not null,
        TurnAction.AddCustomer => action.Customer is not null,
        TurnAction.EditCustomer => action.CustomerName is not null && action.Field is not null && !string.IsNullOrWhiteSpace(action.Value),
        TurnAction.EditOrder => action.OrderEdits.Count > 0 && (SaidOrderId(action, message) is not null
                                || (SaidName(action.CustomerName, message) && await FindLatestOrderByCustomerNameAsync(seller, action.CustomerName!, ct) is not null)),
        TurnAction.ExchangeSize => action.ItemName is not null && !string.IsNullOrWhiteSpace(action.Value) && (SaidOrderId(action, message) is not null
                                || (SaidName(action.CustomerName, message) && await FindLatestOrderByCustomerNameAsync(seller, action.CustomerName!, ct) is not null)),
        TurnAction.CreateCategory => !string.IsNullOrWhiteSpace(action.Value),
        _ => false,
    };

    /// <summary>
    /// The order number the model returned, only when the seller said that number (digits or words: "order 3", "teen number", "تین نمبر").
    /// A number the seller never said is not used ("تین نمبر آرڈر پہنچ گیا" once came back as order 1): the customer's latest order, or a question, answers instead.
    /// </summary>
    internal static int? SaidOrderId(TurnAction action, string message)
    {
        if (action.OrderId is not { } id) return null;
        return SpokenNumbers.DigitAmounts(message).Contains(id) || SpokenNumbers.Amounts(message).Contains(id) || SpokenNumbers.SmallCounts(message).Contains(id) ? id : null;
    }

    /// <summary>The reply when nothing ran: the model's own question, or what the bot can do, so the seller is never left with a guess.</summary>
    private async Task ReplyNotRunAsync(Seller seller, TurnAction action, CancellationToken ct)
    {
        var profile = Industry(seller);
        var reply = action.Action switch
        {
            TurnAction.Clarify when !string.IsNullOrWhiteSpace(action.Question) => MessageTemplates.Get("model-first.clarify", action.Question!.Trim()),
            TurnAction.HelpOrChat => MessageTemplates.Get("model-first.help", profile.MainLine, profile.OrderExample),
            TurnAction.OutOfScope => MessageTemplates.Get("model-first.scope", profile.MainLine, profile.OrderExample),
            TurnAction.Undo => MessageTemplates.Get("model-first.howto.undo"),
            _ => MessageTemplates.Get("model-first.unclear", profile.MainLine, profile.OrderExample),
        };
        await ReplyAsync(seller, reply, ct);
    }

    /// <summary>The saved customer the model's name points at: an exact name, or the only customer whose name contains it. Null when none or several match.</summary>
    private async Task<Customer?> ModelCustomerAsync(Seller seller, string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var wanted = name.Trim().ToLower();
        var matches = await _db.Customers.Where(c => c.SellerId == seller.Id && c.DeletedAt == null && c.Name.ToLower().Contains(wanted)).Take(5).ToListAsync(ct);
        return matches.FirstOrDefault(c => c.Name.ToLower() == wanted) ?? (matches.Count == 1 ? matches[0] : null);
    }

    /// <summary>Runs the four core actions with the handlers the typed commands already use. False = not one of them (or not enough detail): the older pipeline answers.</summary>
    private async Task<bool> RunTurnActionAsync(Seller seller, ConversationSession session, SessionContextData ctx, TurnAction action,
        List<CatalogEntry> catalog, string message, CancellationToken ct, bool fromScreenshot)
    {
        // A price, cost or delivery charge the model returned must be a figure the seller actually said. Otherwise nothing runs; the seller is asked.
        // Figures and details taken from the shop's own records are not "unsaid": the catalog price of an item, the usual delivery charge,
        // and a saved customer's name and phone ("Bilal ko do kurti" comes back as Bilal Ahmed, his number and Rs.1800 each).
        var savedCustomers = await _db.Customers.Where(c => c.SellerId == seller.Id && c.DeletedAt == null).Select(c => new { c.Name, c.Phone }).ToListAsync(ct);
        var recordAmounts = catalog.Select(c => (long)Math.Round(c.Product.Price, MidpointRounding.AwayFromZero))
            .Concat(catalog.Select(c => ProductPack.Of(c.Product)?.Price).Where(p => p is not null).Select(p => (long)Math.Round(p!.Value, MidpointRounding.AwayFromZero)))
            .Append((long)Math.Round(seller.DefaultDeliveryCharge, MidpointRounding.AwayFromZero)).ToHashSet();
        // A product that comes in a carton / box: the per-piece cost, stock and price are worked out here from what the seller said about the pack.
        var saidNumbers = SaidNumbers(message);
        var derivedFigures = new List<decimal>();
        if (action.Action == TurnAction.AddProduct)
        {
            var perPiece = action.Products.Select(p => ProductPack.FromModel(p, saidNumbers)).ToList();
            derivedFigures.AddRange(perPiece.SelectMany(p => p.Derived));
            action = new TurnAction { Action = action.Action, Products = perPiece.Select(p => p.Product).ToList() };
        }
        // A product screenshot: its figures are in the picture, not in the words, so they are shown for YES instead of refused as unsaid.
        if ((fromScreenshot || IsPastedProductBlock(message)) && action.Action == TurnAction.AddProduct && action.Products.Count > 0)
        {
            await ProposeScreenshotProductsAsync(seller, session, ctx, action, ct);
            return true;
        }

        var recordPhones = savedCustomers.Select(c => new string((c.Phone ?? "").Where(char.IsDigit).ToArray())).Where(d => d.Length >= 7)
            .Select(d => d.Length > 10 ? d[^10..] : d).ToHashSet(StringComparer.Ordinal);
        var recordNames = savedCustomers.Select(c => c.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unquoted = UnquotedAmounts(action, message, recordAmounts, derivedFigures).Select(a => a.ToString())
            .Concat(UnquotedDetails(action, message, recordPhones, recordNames)).ToList();
        if (unquoted.Count > 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("model-amount.001", string.Join(", ", unquoted)), ct);
            return true;
        }

        var orderId = SaidOrderId(action, message);
        switch (action.Action)
        {
            case TurnAction.CreateOrder when action.Orders.Count > 0:
                await HandleAnalysisAsync(seller, session, ctx,
                    new AiMessageAnalysis { Intent = "new_order", IsOrderAttempt = true, Order = action.Orders[0], AdditionalOrders = action.Orders.Skip(1).ToList() },
                    catalog, fromScreenshot, ct, message);
                return true;

            case TurnAction.AddProduct when action.Products.Count > 0:
                // The handler keeps only numbers and details found in the seller's words; the figures worked out from the pack are added to those words.
                var support = derivedFigures.Select(d => d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
                    .Concat(action.Products.SelectMany(p => p.Attributes.Where(a => ProductPack.IsPackKey(a.Key)).Select(a => a.Value)));
                await HandleNewProductsAsync(seller, ctx, action.Products, string.Join(' ', support.Prepend(message)), ct);
                return true;

            case TurnAction.AddStock when action.Products.Count > 0:
                await HandleModelStockAsync(seller, action.Products[0], saidNumbers, ct);
                return true;

            case TurnAction.ShowCatalog:
                await HandleCatalogAsync(seller, ctx, null, ct);
                return true;

            case TurnAction.ShowSummary:
                await HandleTodaysSummaryAsync(seller, ModelFirstPeriod(action.Period), ct);
                return true;

            case TurnAction.ShowOrders when !action.PendingOnly && action.Period is null && await ModelCustomerAsync(seller, action.CustomerName, ct) is { } orderCustomer:
                await HandleCustomerOrderLookupAsync(seller, new ParsedCommand { Kind = CommandKind.CustomerOrderLookup, Text = orderCustomer.Name }, ct);
                return true;

            case TurnAction.ShowOrders when action.PendingOnly:
                await HandlePendingOrdersAsync(seller, ctx, ct);
                return true;

            case TurnAction.ShowOrders:
                await HandleOrdersTodayAsync(seller, ctx, ModelFirstPeriod(action.Period), ct);
                return true;

            case TurnAction.ShowOrder when orderId is { } showId:
                await HandleOrderDetailAsync(seller, ctx, showId, ct);
                return true;

            case TurnAction.ShowOrder when await ModelCustomerAsync(seller, action.CustomerName, ct) is { } detailCustomer:
                await HandleCustomerOrderLookupAsync(seller, new ParsedCommand { Kind = CommandKind.CustomerOrderLookup, Text = detailCustomer.Name }, ct);
                return true;

            case TurnAction.UpdateStatus when orderId is { } statusId && action.Status is not null:
                // A status the model picked (maybe from a misheard voice note) waits for YES/NO before the order changes.
                ctx.ModelStatusOrderId = statusId;
                ctx.ModelStatusValue = action.Status;
                SetState(session, ConversationState.AwaitingModelStatusConfirmation);
                await AskYesNoAsync(seller, MessageTemplates.Get("model-status.001", statusId, action.Status), ct);
                return true;

            case TurnAction.CancelOrder when orderId is { } cancelId:
                await HandleCancelOrderRequestAsync(seller, session, ctx,
                    new ParsedCommand { Kind = CommandKind.CancelOrder, Number = cancelId }, ct);
                return true;

            case TurnAction.UpdateStatus when action.Status is not null && SaidName(action.CustomerName, message)
                                              && await ModelCustomerAsync(seller, action.CustomerName, ct) is { } statusCustomer
                                              && await FindLatestOrderByCustomerNameAsync(seller, statusCustomer.Name, ct) is { } latest:
                ctx.ModelStatusOrderId = latest.Id;
                ctx.ModelStatusValue = action.Status;
                SetState(session, ConversationState.AwaitingModelStatusConfirmation);
                await AskYesNoAsync(seller, MessageTemplates.Get("model-status.002", latest.Id, statusCustomer.Name, action.Status), ct);
                return true;

            case TurnAction.EditProduct when action.Products.Count > 0 && action.Products[0].Price is > 0:
                ctx.ModelEdit = new ModelEditData { Kind = TurnAction.EditProduct, Name = action.Products[0].Name, Amount = action.Products[0].Price };
                SetState(session, ConversationState.AwaitingModelStatusConfirmation);
                await AskYesNoAsync(seller, MessageTemplates.Get("model-edit.001", action.Products[0].Name, Formatters.Money(action.Products[0].Price!.Value)), ct);
                return true;

            case TurnAction.AddCustomer when action.Customer is { } buyer:
                await HandleCustomerAddAsync(seller,
                    new ParsedCommand { Kind = CommandKind.CustomerAdd, Text = buyer.Name, Text2 = buyer.Phone, Text3 = buyer.Address, Text4 = buyer.City }, ct);
                return true;

            case TurnAction.EditCustomer when action.CustomerName is { } editName && action.Field is { } field && !string.IsNullOrWhiteSpace(action.Value):
                var newValue = action.Value!.Trim();
                if (field == "phone" && !IsPlausiblePhone(newValue))
                {
                    await ReplyAsync(seller, MessageTemplates.Get("model-edit.003", newValue), ct);
                    return true;
                }
                ctx.ModelEdit = new ModelEditData { Kind = TurnAction.EditCustomer, Name = (await ModelCustomerAsync(seller, editName, ct))?.Name ?? editName, Field = field, Value = newValue };
                SetState(session, ConversationState.AwaitingModelStatusConfirmation);
                await AskYesNoAsync(seller, MessageTemplates.Get("model-edit.002", ctx.ModelEdit.Name, ModelFieldLabel(field), newValue), ct);
                return true;

            case TurnAction.ShowCustomer when await ModelCustomerAsync(seller, action.CustomerName, ct) is { } shown:
                await HandleCustomerDetailAsync(seller, ctx, new ParsedCommand { Kind = CommandKind.CustomerDetail, Text = shown.Name }, ct);
                return true;

            case TurnAction.ShowCustomer when !string.IsNullOrWhiteSpace(action.CustomerName):
                await HandleCustomerSearchAsync(seller, action.CustomerName!.Trim(), ct);
                return true;

            case TurnAction.ShowCustomer:
                await HandleCustomerListAsync(seller, ctx, ct);
                return true;

            case TurnAction.EditOrder when action.OrderEdits.Count > 0:
                await ProposeOrderEditAsync(seller, session, ctx, action, orderId, message, ct);
                return true;

            case TurnAction.ExchangeSize:
                await ProposeSizeExchangeAsync(seller, session, ctx, action, orderId, ct);
                return true;

            case TurnAction.CreateCategory:
                await HandleCreateCategoryAsync(seller, action.Value!, ct);
                return true;

            case TurnAction.HowTo:
                var profile = Industry(seller);
                var topic = action.Topic is not null && action.Topic != "other" ? action.Topic : "other";
                // Each topic's text takes only the examples it shows, from this seller's kind of shop.
                var examples = topic switch
                {
                    TurnAction.AddProduct => new object[] { profile.MainLine, profile.MainName },
                    TurnAction.EditProduct => new object[] { profile.MainName },
                    TurnAction.CreateOrder => new object[] { profile.OrderExample },
                    "other" => new object[] { profile.MainLine, profile.OrderExample },
                    _ => Array.Empty<object>(),
                };
                await ReplyAsync(seller, MessageTemplates.Get("model-first.howto." + topic.Replace("_", ""), examples), ct);
                return true;

            case TurnAction.ContactSupport:
                await HandleSupportContactAsync(seller, ctx, new ParsedCommand { Kind = CommandKind.SupportContact }, ct);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// True when some word of the name is in the message, by sound (so "Sara" in Roman matches an Urdu-script transcript). A status change is offered for a
    /// customer's latest order only when the seller named that customer: the model once added a customer nobody mentioned to "تین نمبر آرڈر پہنچ گیا".
    /// </summary>
    internal static bool SaidName(string? name, string message)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var said = SoundMatch.Keys(message).ToHashSet(StringComparer.Ordinal);
        return SoundMatch.Keys(name).Any(said.Contains);
    }

    /// <summary>Every number the seller said, in digits or in words (amounts and small counts).</summary>
    internal static IReadOnlySet<long> SaidNumbers(string message) =>
        SpokenNumbers.DigitAmounts(message).Concat(SpokenNumbers.Amounts(message)).Concat(SpokenNumbers.SmallCounts(message).Select(c => (long)c)).ToHashSet();

    /// <summary>
    /// "tel ke 2 carton aur aaye" / "kurti 5 aur aa gayi": stock added to one existing product, in pieces or in whole packs (packs x the product's pieces
    /// per pack). The count must be a number the seller said; packs of a product with no pack size are asked about, never guessed.
    /// </summary>
    private async Task HandleModelStockAsync(Seller seller, AiNewProduct said, IReadOnlySet<long> saidNumbers, CancellationToken ct)
    {
        var wanted = said.Name.Trim();
        var products = await _db.Products.Where(p => p.SellerId == seller.Id && p.IsActive).ToListAsync(ct);
        var matches = products.Where(p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) matches = products.Where(p => p.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1)
        {
            await ReplyAsync(seller, matches.Count == 0
                ? MessageTemplates.Get("commands.026", wanted)
                : MessageTemplates.Get("stock.005", wanted, string.Join(", ", matches.Select(Formatters.ProductLabel))), ct);
            return;
        }

        var product = matches[0];
        var pack = ProductPack.Of(product);
        int pieces;
        if (said.StockPacks is { } packs && packs > 0 && saidNumbers.Contains(packs))
        {
            if (pack is null)
            {
                await ReplyAsync(seller, MessageTemplates.Get("pack.001", Formatters.ProductLabel(product)), ct);
                return;
            }
            pieces = packs * pack.Qty;
        }
        else if (said.Stock is { } count && count > 0 && saidNumbers.Contains(count)) pieces = count;
        else
        {
            await ReplyAsync(seller, MessageTemplates.Get("pack.002", Formatters.ProductLabel(product)), ct);
            return;
        }

        product.StockQty = (product.StockQty ?? 0) + pieces;
        await ReplyAsync(seller, MessageTemplates.Get("pack.003", Formatters.ProductLabel(product), pieces, pack?.StockText(product.StockQty.Value) ?? product.StockQty.Value.ToString()), ct);
    }

    private static string ModelFieldLabel(string field) => field switch { "phone" => "phone", "address" => "address", "city" => "shehar", _ => "naam" };

    /// <summary>
    /// The money figures the model returned (unit prices, delivery charges, product prices and costs) that the message does not contain, in digits or in words.
    /// Quantities are not checked: a small count is often not written as an amount. Zero (free delivery) is not an amount to check.
    /// </summary>
    /// <param name="recordAmounts">Order figures the shop's records already hold (catalog prices, the usual delivery charge): never "unsaid" in an order.</param>
    /// <param name="derived">Per-piece figures the engine itself worked out from a pack (<see cref="ProductPack.FromModel"/>): not the model's, so not checked.</param>
    internal static IReadOnlyList<long> UnquotedAmounts(TurnAction action, string message, IReadOnlySet<long>? recordAmounts = null, IReadOnlyList<decimal>? derived = null)
    {
        var said = SpokenNumbers.Amounts(message);
        var figures = new List<decimal>();
        foreach (var order in action.Orders)
        {
            if (order.DeliveryCharge is { } delivery && recordAmounts?.Contains((long)Math.Round(delivery, MidpointRounding.AwayFromZero)) != true) figures.Add(delivery);
            foreach (var item in order.Items)
                if (item.StatedUnitPrice is { } unitPrice && recordAmounts?.Contains((long)Math.Round(unitPrice, MidpointRounding.AwayFromZero)) != true) figures.Add(unitPrice);
        }
        foreach (var product in action.Products)
        {
            if (product.Price is { } price && derived?.Contains(price) != true) figures.Add(price);
            if (product.Cost is { } cost && derived?.Contains(cost) != true) figures.Add(cost);
        }
        return figures
            .Where(f => f > 0)
            .Select(f => (long)Math.Round(f, MidpointRounding.AwayFromZero))
            .Where(v => !said.Contains(v))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// The phone numbers and customer names the model returned that the message does not contain. A phone is matched on its last 10 digits;
    /// a name is matched by sound (so an Urdu-script transcript still matches a Roman name). Empty when none are missing.
    /// </summary>
    /// <param name="recordPhones">Last 10 digits of saved customers' phones.</param>
    /// <param name="recordNames">Saved customers' names: the model returns the saved name for a customer the seller named in short.</param>
    internal static IReadOnlyList<string> UnquotedDetails(TurnAction action, string message, IReadOnlySet<string>? recordPhones = null, IReadOnlySet<string>? recordNames = null)
    {
        var missing = new List<string>();
        var messageDigits = SpokenNumbers.SpokenDigits(message);
        var messageSounds = SoundMatch.Keys(message).ToHashSet(StringComparer.Ordinal);

        foreach (var order in action.Orders)
        {
            if (order.Phone is { } phone && new string(phone.Where(char.IsDigit).ToArray()) is { Length: >= 7 } digits)
            {
                var last10 = digits.Length > 10 ? digits[^10..] : digits;
                if (!messageDigits.Contains(last10, StringComparison.Ordinal) && recordPhones?.Contains(last10) != true) missing.Add(phone);
            }
            if (order.CustomerName is { } name && recordNames?.Contains(name.Trim()) != true
                && SoundMatch.Keys(name) is { Count: > 0 } keys && keys.Any(k => !messageSounds.Contains(k)))
                missing.Add(name);
        }
        var phones = new List<string?> { action.Customer?.Phone, action.Field == "phone" ? action.Value : null };
        foreach (var phone in phones)
            if (phone is not null && new string(phone.Where(char.IsDigit).ToArray()) is { Length: >= 7 } digits)
            {
                var last10 = digits.Length > 10 ? digits[^10..] : digits;
                if (!messageDigits.Contains(last10, StringComparison.Ordinal)) missing.Add(phone);
            }
        if (action.Customer?.Name is { } newName && SoundMatch.Keys(newName) is { Count: > 0 } newKeys && newKeys.Any(k => !messageSounds.Contains(k)))
            missing.Add(newName);
        return missing.Distinct().ToList();
    }

    /// <summary>The seller's answer to a status change the model picked: only YES runs it; anything else leaves the order as it was.</summary>
    private async Task HandleModelStatusConfirmationAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        SetState(session, ConversationState.Idle);
        var orderId = ctx.ModelStatusOrderId;
        var status = ctx.ModelStatusValue;
        ctx.ModelStatusOrderId = null;
        ctx.ModelStatusValue = null;
        var edit = ctx.ModelEdit;
        ctx.ModelEdit = null;
        if (edit is not null && CommandParser.IsAffirmative(message))
        {
            if (edit.Kind == TurnAction.EditProduct)
                await HandleEditProductAsync(seller, new ParsedCommand { Kind = CommandKind.EditProduct, Text = edit.Name, Amount = edit.Amount }, ct);
            else if (edit.Kind == TurnAction.EditOrder)
                await ApplyModelOrderEditAsync(seller, session, ctx, edit, ct);
            else if (edit.Kind == PictureProductKind)
                await SaveScreenshotProductsAsync(seller, ctx, edit.Lines, edit.Field, ct);
            else if (edit.Kind == TurnAction.ExchangeSize)
                await HandleSizeExchangeAsync(seller, ctx, new ParsedCommand { Kind = CommandKind.SizeExchange, Number = edit.OrderId, Text = edit.ItemNo?.ToString(), Text2 = edit.Value }, ct);
            else
                await HandleCustomerUpdateAsync(seller, ctx, new ParsedCommand { Kind = CommandKind.CustomerUpdate, Text = edit.Name, Text2 = edit.Field, Text3 = edit.Value }, ct);
            return;
        }
        if (edit is { Kind: PictureProductKind })
        {
            // Not a YES: nothing from the screenshot is saved. A corrected line sent next is read as typed.
            await ReplyAsync(seller, MessageTemplates.Get("picture-product.003"), ct);
            return;
        }
        if (edit is not null || orderId is null || status is null || !CommandParser.IsAffirmative(message))
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
