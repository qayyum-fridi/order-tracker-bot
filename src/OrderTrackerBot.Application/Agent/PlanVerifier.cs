using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Agent;

/// <summary>What the engine may do with one planned step.</summary>
public enum StepVerdict
{
    /// <summary>Safe to run now.</summary>
    Run,
    /// <summary>Valid, but changes existing data: the seller says YES first.</summary>
    Confirm,
    /// <summary>Cannot be decided from the data (two matches, a missing customer): ask the seller, using <see cref="VerifiedStep.Choices"/>.</summary>
    Ask,
    /// <summary>Not valid (an id that is not this seller's, an amount the seller never said). Nothing runs from this step.</summary>
    Reject
}

public sealed record ResolvedItem(int? ProductId, string Name, int Quantity, bool Unmatched);

/// <summary><see cref="Reason"/> is a short code (not seller text) for the engine to turn into a template.</summary>
public sealed record VerifiedStep(PlanStep Step, StepVerdict Verdict, string? Reason = null, int? ProductId = null, int? CustomerId = null,
    IReadOnlyList<ResolvedItem>? Items = null, IReadOnlyList<string>? Choices = null);

public sealed record VerifiedPlan(IReadOnlyList<VerifiedStep> Steps)
{
    /// <summary>The steps that may run now: everything before the first step that needs a YES, an answer, or is rejected.</summary>
    public IReadOnlyList<VerifiedStep> Runnable => Steps.TakeWhile(s => s.Verdict == StepVerdict.Run).ToList();

    /// <summary>The step that stopped the plan, or null when every step can run.</summary>
    public VerifiedStep? Blocked => Steps.FirstOrDefault(s => s.Verdict != StepVerdict.Run);
}

/// <summary>
/// Checks a plan against the seller's own data before anything runs. The planner proposes; this decides. It never calls the AI.
/// Rules: an id must be one the model was shown (in <see cref="SellerContext"/>) and must exist for this seller; every price, cost, stock and
/// quantity above 1 must be a number the seller wrote or said; a risky tool needs YES; an unclear person or product becomes a question.
/// </summary>
public sealed class PlanVerifier
{
    public const decimal MaxAmount = 10_000_000m;

    // Words that say WHICH field of a product the owner means. The model sometimes puts a cost into price ("Kurti ki cost 900 hai" -> price=900); the number was said,
    // so the amount check passes and a YES would change the selling price. When the message names one field and the plan edits the other, the owner is asked.
    private static readonly System.Text.RegularExpressions.Regex CostWords = new(
        @"(?<![\p{L}\p{N}])(?:cost|costing|lagat|kharid|kharidi|kharida|purchase|buying|خرید|خریداری|لاگت|کوسٹ)(?![\p{L}\p{N}])",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    // "barha do" / "kam kar do" / "sasta" say HOW MUCH to change, not the new value: a number next to them is not a price (seen live: "rate 100 barha do" became price 100).
    private static readonly System.Text.RegularExpressions.Regex RelativeWords = new(
        @"(?<![\p{L}\p{N}])(?:barha|barhao|barhado|barhaido|badha|badhao|badhado|zyada|ziada|mehnga|mehenga|increase|kam|ghata|ghatao|ghatado|kum|sasta|sasti|decrease|reduce|bhaw\s+kam)(?![\p{L}\p{N}])|بڑھا|بڑھاؤ|کم\s+کر|سستا|سستی|مہنگا|گھٹا|گھٹاؤ",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    // Titles that name a person, not a customer: "madam ne kaha 2 kurti" must not create a customer called "madam".
    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "madam", "madame", "mam", "maam", "ma'am", "sir", "bhai", "bhaiya", "ji", "aap", "apka", "apki", "bibi", "baji", "didi", "aunty", "uncle",
        "customer", "client", "sister", "brother", "bhabhi", "miss", "bhai jaan", "baji ji",
        "بھائی", "باجی", "آپ", "میڈم", "صاحب", "بی بی",
    };

    // "ek dozen" is 12 without the digit being written.
    private static readonly System.Text.RegularExpressions.Regex DozenWords = new(
        @"(?<![\p{L}\p{N}])(?:dozen|darjan)(?![\p{L}\p{N}])|درجن",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex PriceWords = new(
        @"(?<![\p{L}\p{N}])(?:price|pirce|prise|rate|qeemat|qimat|kimat|keemat|bhav|bhaw|selling|sale\s+price|قیمت|ریٹ|بھاؤ|پرائس)(?![\p{L}\p{N}])",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly IAppDbContext _db;

    public PlanVerifier(IAppDbContext db) => _db = db;

    public async Task<VerifiedPlan> VerifyAsync(Seller seller, AgentPlan plan, SellerContext context, string message, CancellationToken ct = default)
    {
        var said = SpokenNumbers.Amounts(message).Concat(SpokenNumbers.SmallCounts(message).Select(c => (long)c)).ToHashSet();
        var addedNames = new List<string>();   // products an earlier step of this plan adds, so a later order line may use them
        var steps = new List<VerifiedStep>();

        foreach (var step in plan.Steps)
        {
            var verified = step.Tool switch
            {
                "add_product" => VerifyAddProduct(step, message, said),
                "edit_product" => await VerifyEditProductAsync(seller, step, context, message, said, ct),
                "draft_order" => await VerifyDraftOrderAsync(seller, step, context, message, said, addedNames, ct),
                _ => new VerifiedStep(step, StepVerdict.Reject, "unknown_tool"),
            };
            if (verified.Verdict != StepVerdict.Reject && step.Tool == "add_product" && step.Args.TryGetValue("name", out var added))
                addedNames.Add(added.Trim());
            steps.Add(verified);
        }
        return new VerifiedPlan(steps);
    }

    private static VerifiedStep VerifyAddProduct(PlanStep step, string message, IReadOnlySet<long> said)
    {
        var name = step.Args.GetValueOrDefault("name")?.Trim() ?? "";
        if (name.Length is 0 or > 100 || !name.Any(char.IsLetter)) return new VerifiedStep(step, StepVerdict.Reject, "bad_name");
        if (!step.Args.ContainsKey("price")) return new VerifiedStep(step, StepVerdict.Ask, "price_missing");
        if (AmountProblem(step, new[] { "price", "cost", "stock" }, message, said) is { } problem) return new VerifiedStep(step, StepVerdict.Reject, problem);
        return new VerifiedStep(step, StepVerdict.Run);
    }

    private async Task<VerifiedStep> VerifyEditProductAsync(Seller seller, PlanStep step, SellerContext ctx, string message, IReadOnlySet<long> said, CancellationToken ct)
    {
        if (!TryId(step, "product_id", out var productId)) return new VerifiedStep(step, StepVerdict.Reject, "product_id_missing");
        if (ctx.Products.All(p => p.Id != productId)) return new VerifiedStep(step, StepVerdict.Reject, "id_not_in_context");
        if (!await _db.Products.AnyAsync(p => p.Id == productId && p.SellerId == seller.Id && p.IsActive, ct))
            return new VerifiedStep(step, StepVerdict.Reject, "unknown_product");
        if (Rivals(ctx.Products.Select(p => (p.Id, p.Score, p.Name, p.Name)), productId) is { } rivalProducts)
            return new VerifiedStep(step, StepVerdict.Ask, "ambiguous_product", Choices: rivalProducts);
        if (RelativeWords.IsMatch(message) && new[] { "price", "cost", "stock" }.Any(step.Args.ContainsKey))
            return new VerifiedStep(step, StepVerdict.Ask, "relative_change", ProductId: productId);
        var mentionsCost = CostWords.IsMatch(message);
        var mentionsPrice = PriceWords.IsMatch(message);
        if ((step.Args.ContainsKey("price") && !step.Args.ContainsKey("cost") && mentionsCost && !mentionsPrice)
            || (step.Args.ContainsKey("cost") && !step.Args.ContainsKey("price") && mentionsPrice && !mentionsCost))
            return new VerifiedStep(step, StepVerdict.Ask, "field_unclear", ProductId: productId);
        // The model sometimes copies a value the product already has into an edit ("cost 900" arrives with price=1800, the catalog price). Setting a value to what it
        // is changes nothing, so an unsaid value equal to the stored one is dropped; any other unsaid number is still rejected below.
        var current = await _db.Products.AsNoTracking().Where(p => p.Id == productId).Select(p => new { p.Price, p.CostPrice, p.StockQty }).FirstAsync(ct);
        var args = new Dictionary<string, string>(step.Args);
        foreach (var (name, stored) in new[] { ("price", (decimal?)current.Price), ("cost", current.CostPrice), ("stock", current.StockQty) })
            if (args.TryGetValue(name, out var raw) && decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
                && stored == value && !(value == decimal.Truncate(value) && said.Contains((long)value)))
                args.Remove(name);
        step = step with { Args = args };
        if (!new[] { "price", "cost", "stock" }.Any(step.Args.ContainsKey)) return new VerifiedStep(step, StepVerdict.Reject, "nothing_to_change");
        if (AmountProblem(step, new[] { "price", "cost", "stock" }, message, said) is { } problem) return new VerifiedStep(step, StepVerdict.Reject, problem);
        return new VerifiedStep(step, StepVerdict.Confirm, "edits_existing_product", ProductId: productId);
    }

    private async Task<VerifiedStep> VerifyDraftOrderAsync(Seller seller, PlanStep step, SellerContext ctx, string message, IReadOnlySet<long> said,
        IReadOnlyList<string> addedNames, CancellationToken ct)
    {
        // Who is the customer?
        int? customerId = null;
        string? customerReason = null;
        if (step.Args.ContainsKey("customer_id"))
        {
            if (!TryId(step, "customer_id", out var id)) return new VerifiedStep(step, StepVerdict.Reject, "customer_id_bad");
            if (ctx.Customers.All(c => c.Id != id)) return new VerifiedStep(step, StepVerdict.Reject, "id_not_in_context");
            if (!await _db.Customers.AnyAsync(c => c.Id == id && c.SellerId == seller.Id && c.DeletedAt == null, ct))
                return new VerifiedStep(step, StepVerdict.Reject, "unknown_customer");
            // The id is real, but the message may name several customers equally well ("Sara" with two Saras): the model must not pick one for the seller.
            if (Rivals(ctx.Customers.Select(c => (c.Id, c.Score, c.Name, c.City is { Length: > 0 } ? $"{c.Name} ({c.City})" : c.Name)), id) is { } rivalCustomers)
                return new VerifiedStep(step, StepVerdict.Ask, "ambiguous_customer", Choices: rivalCustomers);
            customerId = id;
        }
        else if (step.Args.GetValueOrDefault("customer_name")?.Trim() is { Length: > 0 } customerName)
        {
            var matches = await CustomerMatchesAsync(seller.Id, customerName, ct);
            if (matches.Count > 1) return new VerifiedStep(step, StepVerdict.Ask, "ambiguous_customer", Choices: matches.Select(m => m.Label).ToList());
            if (matches.Count == 1) customerId = matches[0].Id;
            else if (SellerContextBuilder.HasArabicScript(customerName) && ctx.Customers.Count > 0)
                // A name in Urdu script that matched nobody is most likely a saved customer written in Roman script ("بلال" for "Bilal"): ask instead of creating a second one.
                return new VerifiedStep(step, StepVerdict.Ask, "ambiguous_customer", Choices: ctx.Customers.Take(5).Select(c => c.City is { Length: > 0 } ? $"{c.Name} ({c.City})" : c.Name).ToList());
            else if (GenericNames.Contains(customerName.Trim()))
                return new VerifiedStep(step, StepVerdict.Ask, "customer_missing");   // "madam", "bhai": a title, not a name
            else customerReason = "new_customer";   // not saved yet: the order flow asks for the phone
        }
        else if (!step.Args.ContainsKey("phone")) return new VerifiedStep(step, StepVerdict.Ask, "customer_missing");

        // What is ordered?
        if (step.Items.Count == 0) return new VerifiedStep(step, StepVerdict.Reject, "no_items");
        var items = new List<ResolvedItem>();
        foreach (var item in step.Items)
        {
            if (item.Quantity > 1 && !said.Contains(item.Quantity) && !(item.Quantity == 12 && DozenWords.IsMatch(message))) return new VerifiedStep(step, StepVerdict.Reject, "quantity_not_said");
            if (item.ProductId is { } pid)
            {
                if (ctx.Products.All(p => p.Id != pid)) return new VerifiedStep(step, StepVerdict.Reject, "id_not_in_context");
                var product = await _db.Products.AsNoTracking().Where(p => p.Id == pid && p.SellerId == seller.Id && p.IsActive).Select(p => p.Name).FirstOrDefaultAsync(ct);
                if (product is null) return new VerifiedStep(step, StepVerdict.Reject, "unknown_product");
                if (Rivals(ctx.Products.Select(p => (p.Id, p.Score, p.Name, p.Name)), pid) is { } rivalItems)
                    return new VerifiedStep(step, StepVerdict.Ask, "ambiguous_product", Choices: rivalItems);
                items.Add(new ResolvedItem(pid, product, item.Quantity, false));
                continue;
            }

            var candidates = await ProductMatchesAsync(seller.Id, item.Name, ct);
            if (candidates.Count > 1) return new VerifiedStep(step, StepVerdict.Ask, "ambiguous_product", Choices: candidates.Select(c => c.Name).ToList());
            if (candidates.Count == 1) items.Add(new ResolvedItem(candidates[0].Id, candidates[0].Name, item.Quantity, false));
            else if (addedNames.Any(n => string.Equals(n, item.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
                items.Add(new ResolvedItem(null, item.Name.Trim(), item.Quantity, false));   // added by an earlier step of this plan
            else items.Add(new ResolvedItem(null, item.Name.Trim(), item.Quantity, true));  // the order flow asks add / map, as for any unknown product
        }
        return new VerifiedStep(step, StepVerdict.Run, customerReason, CustomerId: customerId, Items: items);
    }

    /// <summary>Customers whose name contains every word given ("Sara" finds "Sara Khan"). An exact full-name match wins over partial ones.</summary>
    private async Task<List<(int Id, string Label)>> CustomerMatchesAsync(int sellerId, string name, CancellationToken ct)
    {
        var words = SellerContextBuilder.Words(name);
        if (words.Count == 0) return new();
        var all = await _db.Customers.AsNoTracking().Where(c => c.SellerId == sellerId && c.DeletedAt == null)
            .OrderByDescending(c => c.Id).Take(500).Select(c => new { c.Id, c.Name, c.City }).ToListAsync(ct);
        var exact = all.Where(c => string.Equals(c.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        var chosen = exact.Count > 0 ? exact : all.Where(c => { var cw = SellerContextBuilder.Words(c.Name); return words.All(w => cw.Contains(w)); }).ToList();
        return chosen.Take(5).Select(c => (c.Id, c.City is { Length: > 0 } ? $"{c.Name} ({c.City})" : c.Name)).ToList();
    }

    /// <summary>Active products whose name contains every word of <paramref name="name"/> ("Polo" finds "Polo Shirt"). An exact name wins over partial ones.</summary>
    private async Task<List<(int Id, string Name)>> ProductMatchesAsync(int sellerId, string name, CancellationToken ct)
    {
        var words = SellerContextBuilder.Words(name);
        if (words.Count == 0) return new();
        var all = await _db.Products.AsNoTracking().Where(p => p.SellerId == sellerId && p.IsActive)
            .OrderByDescending(p => p.Id).Take(500).Select(p => new { p.Id, p.Name }).ToListAsync(ct);
        var exact = all.Where(p => string.Equals(p.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        var chosen = exact.Count > 0 ? exact : all.Where(p => { var pw = SellerContextBuilder.Words(p.Name); return words.All(w => pw.Contains(w)); }).ToList();
        return chosen.Take(5).Select(p => (p.Id, p.Name)).ToList();
    }

    /// <summary>
    /// The labels of the chosen entity and every other one that competes with it for the same words of the message (their names share a word and the other matches at
    /// least as well), or null when the chosen one stands alone. The planner picks an id; this stops it choosing between equals ("Sara" with Sara Khan and Sara Ali)
    /// on the seller's behalf. Entities that merely appear in the same message ("Kurti" and "Dupatta") do not compete.
    /// </summary>
    private static List<string>? Rivals(IEnumerable<(int Id, double Score, string Name, string Label)> hits, int chosen)
    {
        var scored = hits.Where(h => h.Score > 0).ToList();
        var me = scored.FirstOrDefault(h => h.Id == chosen);
        if (me.Label is null) return null;   // not named by the message at all: the other checks decide
        var myWords = SellerContextBuilder.Words(me.Name);
        var rivals = scored.Where(h => h.Id != chosen && h.Score >= me.Score && SellerContextBuilder.Words(h.Name).Any(myWords.Contains)).ToList();
        return rivals.Count == 0 ? null : new[] { me }.Concat(rivals).Select(h => h.Label).ToList();
    }

    private static bool TryId(PlanStep step, string arg, out int id)
    {
        id = 0;
        return step.Args.TryGetValue(arg, out var raw) && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    }

    /// <summary>The first money/stock argument that is not a sane number the seller wrote or said, as a code; null when all are fine.</summary>
    private static string? AmountProblem(PlanStep step, IEnumerable<string> names, string message, IReadOnlySet<long> said)
    {
        foreach (var name in names)
        {
            if (!step.Args.TryGetValue(name, out var raw)) continue;
            if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) || value < 0 || value > MaxAmount) return name + "_invalid";
            var whole = value == decimal.Truncate(value);
            var ok = whole ? said.Contains((long)value) : message.Contains(raw, StringComparison.Ordinal);
            if (!ok) return name + "_not_said";
        }
        return null;
    }
}
