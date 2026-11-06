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
            customerId = id;
        }
        else if (step.Args.GetValueOrDefault("customer_name")?.Trim() is { Length: > 0 } customerName)
        {
            var matches = await CustomerMatchesAsync(seller.Id, customerName, ct);
            if (matches.Count > 1) return new VerifiedStep(step, StepVerdict.Ask, "ambiguous_customer", Choices: matches.Select(m => m.Label).ToList());
            if (matches.Count == 1) customerId = matches[0].Id;
            else customerReason = "new_customer";   // not saved yet: the order flow asks for the phone
        }
        else if (!step.Args.ContainsKey("phone")) return new VerifiedStep(step, StepVerdict.Ask, "customer_missing");

        // What is ordered?
        if (step.Items.Count == 0) return new VerifiedStep(step, StepVerdict.Reject, "no_items");
        var items = new List<ResolvedItem>();
        foreach (var item in step.Items)
        {
            if (item.Quantity > 1 && !said.Contains(item.Quantity)) return new VerifiedStep(step, StepVerdict.Reject, "quantity_not_said");
            if (item.ProductId is { } pid)
            {
                if (ctx.Products.All(p => p.Id != pid)) return new VerifiedStep(step, StepVerdict.Reject, "id_not_in_context");
                var product = await _db.Products.AsNoTracking().Where(p => p.Id == pid && p.SellerId == seller.Id && p.IsActive).Select(p => p.Name).FirstOrDefaultAsync(ct);
                if (product is null) return new VerifiedStep(step, StepVerdict.Reject, "unknown_product");
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
