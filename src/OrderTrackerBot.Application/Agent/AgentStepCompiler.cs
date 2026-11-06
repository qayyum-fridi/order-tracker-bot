using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Agent;

/// <summary>One typed message the engine feeds to its normal pipeline, exactly as if the seller had typed it. <see cref="Confirm"/> = waits for YES first.</summary>
public sealed record CompiledStep(string Text, bool Confirm);

/// <summary>
/// The typed steps for a verified plan, in order, up to the first step that cannot be decided (<see cref="Stop"/>).
/// Every step is built by code from verified ids and numbers, so the model never writes a command.
/// </summary>
public sealed record CompiledPlan(IReadOnlyList<CompiledStep> Steps, VerifiedStep? Stop);

/// <summary>Turns verified plan steps into the typed messages the existing handlers already understand (product line, "edit product: ...", "stock ...", "order for ...").</summary>
public sealed class AgentStepCompiler
{
    private readonly IAppDbContext _db;

    public AgentStepCompiler(IAppDbContext db) => _db = db;

    public async Task<CompiledPlan> CompileAsync(Seller seller, VerifiedPlan plan, CancellationToken ct = default)
    {
        var steps = new List<CompiledStep>();
        foreach (var verified in plan.Steps)
        {
            if (verified.Verdict is StepVerdict.Ask or StepVerdict.Reject) return new CompiledPlan(steps, verified);

            var confirm = verified.Verdict == StepVerdict.Confirm;
            List<string>? texts = verified.Step.Tool switch
            {
                "add_product" => new() { AddProductLine(verified.Step) },
                "edit_product" => await EditProductLinesAsync(seller, verified, ct),
                "draft_order" => await DraftOrderLinesAsync(seller, verified, ct),
                _ => null,
            };
            // Something the data no longer supports (a product with variants, a product gone since the check): stop here, nothing guessed.
            if (texts is null) return new CompiledPlan(steps, verified with { Verdict = StepVerdict.Reject, Reason = verified.Reason ?? "cannot_compile" });
            steps.AddRange(texts.Select(t => new CompiledStep(t, confirm)));
        }
        return new CompiledPlan(steps, null);
    }

    private static string Num(string raw) => decimal.Parse(raw, NumberStyles.Number, CultureInfo.InvariantCulture).ToString("0.##", CultureInfo.InvariantCulture);

    private static string AddProductLine(PlanStep step)
    {
        var line = $"{step.Args["name"].Trim()} - {Num(step.Args["price"])}";
        if (step.Args.TryGetValue("cost", out var cost)) line += $", cost {Num(cost)}";
        if (step.Args.TryGetValue("stock", out var stock)) line += $", stock {Num(stock)}";
        return line;
    }

    private async Task<List<string>?> EditProductLinesAsync(Seller seller, VerifiedStep verified, CancellationToken ct)
    {
        var product = await _db.Products.AsNoTracking().Where(p => p.Id == verified.ProductId && p.SellerId == seller.Id && p.IsActive)
            .Select(p => new { p.Name, p.Price }).FirstOrDefaultAsync(ct);
        if (product is null) return null;
        // Several listings of one name (sizes / colours): the typed commands would pick the wrong one, so say so instead.
        var sameName = await _db.Products.CountAsync(p => p.SellerId == seller.Id && p.IsActive && p.Name == product.Name, ct);
        if (sameName > 1) return null;

        var args = verified.Step.Args;
        var lines = new List<string>();
        if (args.TryGetValue("price", out var price)) lines.Add($"edit product: {product.Name} - {Num(price)}");
        if (args.TryGetValue("cost", out var cost))
        {
            var current = args.TryGetValue("price", out var p) ? Num(p) : product.Price.ToString("0.##", CultureInfo.InvariantCulture);
            lines.Add($"{product.Name} - {current}, cost {Num(cost)}");
        }
        if (args.TryGetValue("stock", out var stock)) lines.Add($"stock {product.Name} {Num(stock)}");
        return lines.Count == 0 ? null : lines;
    }

    private async Task<List<string>?> DraftOrderLinesAsync(Seller seller, VerifiedStep verified, CancellationToken ct)
    {
        var items = verified.Items ?? Array.Empty<ResolvedItem>();
        if (items.Count == 0) return null;
        var itemsText = string.Join(", ", items.Select(i => $"{i.Name} {i.Quantity}"));

        if (verified.CustomerId is { } customerId)
        {
            var name = await _db.Customers.AsNoTracking().Where(c => c.Id == customerId && c.SellerId == seller.Id && c.DeletedAt == null)
                .Select(c => c.Name).FirstOrDefaultAsync(ct);
            if (name is null) return null;
            // The saved customer's phone and address are filled in by "order for"; the next message is read as her items (plain code, no AI).
            return new List<string> { $"order for {name}", itemsText };
        }

        // A new customer: the ordinary order sentence, read by the normal order reader, which asks for the phone.
        var customerName = verified.Step.Args.GetValueOrDefault("customer_name")?.Trim();
        if (string.IsNullOrEmpty(customerName)) return null;
        return new List<string> { $"{customerName} ko {string.Join(", ", items.Select(i => $"{i.Quantity} {i.Name}"))}" };
    }
}
