using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>What the dashboard shows after an upload. Nothing is saved until the commit.</summary>
public sealed record ClientImportPreview(
    string? Problem,
    IReadOnlyList<ClientImportColumn> MappedColumns,
    IReadOnlyList<string> IgnoredColumns,
    int RowCount,
    int NewCount,
    int SkippedCount,
    IReadOnlyList<string> Errors,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Sample);

public sealed record ClientImportColumn(string Source, string Field);

public sealed record ClientImportResult(int Created, int Skipped);

/// <summary>
/// Dashboard import. Same planning and save as the WhatsApp import (new rows only, existing ones skipped, undoable),
/// but the file comes in as bytes: the dashboard keeps it between preview and commit, and commit plans it again.
/// </summary>
public partial class ConversationEngine
{
    /// <summary>Plans an uploaded file for the seller. `kind` is "customers" or "catalog" and decides which rows count and are shown.</summary>
    public async Task<ClientImportPreview> PreviewClientImportAsync(int sellerId, string kind, byte[] bytes, string fileName, CancellationToken ct)
    {
        var seller = await _db.Sellers.FirstAsync(s => s.Id == sellerId, ct);
        var plan = await PlanImportBytesAsync(seller, bytes, fileName, ct);
        if (plan.Problem != ImportProblem.None)
            return new ClientImportPreview(plan.Problem.ToString(), [], [], 0, 0, 0, [], []);

        var customers = kind == "customers";
        var newCount = customers ? plan.NewCustomers.Count : plan.NewProducts.Count;
        var skipped = customers
            ? plan.ExistingCustomers + plan.FileDuplicateCustomers
            : plan.ExistingProducts + plan.FileDuplicateProducts;
        var errors = plan.Errors.ToList();
        var sample = customers
            ? plan.NewCustomers.Take(5).Select(c => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
            {
                ["Name"] = c.Name, ["Phone"] = c.Phone ?? "", ["City"] = c.City ?? "", ["Address"] = c.Address ?? "",
            }).ToList()
            : plan.NewProducts.Take(5).Select(p => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
            {
                ["Name"] = p.Name, ["Price"] = p.Price.ToString("0.##", CultureInfo.InvariantCulture), ["Unit"] = p.UnitType, ["Category"] = p.Category ?? "",
            }).ToList();

        var understood = plan.Mapping!.Sheets.Where(s => s.Kind == (customers ? ImportKind.Customers : ImportKind.Catalog));
        var mapped = understood.SelectMany(s => s.Understood.Select(u => new ClientImportColumn(u.Source, u.Field))).ToList();
        var ignored = understood.SelectMany(s => s.Ignored).ToList();

        return new ClientImportPreview(null, mapped, ignored,
            newCount + skipped + plan.Errors.Count, newCount, skipped, errors, sample);
    }

    /// <summary>Saves the new rows of an uploaded file. Returns null when the file has nothing new to save.</summary>
    public async Task<ClientImportResult?> CommitClientImportAsync(int sellerId, string kind, byte[] bytes, string fileName, CancellationToken ct)
    {
        var seller = await _db.Sellers.FirstAsync(s => s.Id == sellerId, ct);
        var plan = await PlanImportBytesAsync(seller, bytes, fileName, ct);
        if (plan.Problem != ImportProblem.None || !plan.HasNew) return null;

        var customers = kind == "customers";
        var created = customers ? plan.NewCustomers.Count : plan.NewProducts.Count;
        if (created == 0) return null;

        await SaveImportPlanAsync(seller, plan, customers, products: !customers, ct);
        var skipped = customers
            ? plan.ExistingCustomers + plan.FileDuplicateCustomers
            : plan.ExistingProducts + plan.FileDuplicateProducts;
        return new ClientImportResult(created, skipped);
    }

    /// <summary>Writes the planned rows and one DataImported log, so "undo" can remove them. Shared by WhatsApp and the dashboard.</summary>
    private async Task SaveImportPlanAsync(Seller seller, ImportPlan plan, bool customers, bool products, CancellationToken ct)
    {
        var newCustomers = customers ? plan.NewCustomers : new List<Customer>();
        var newProducts = products ? plan.NewProducts : new List<Product>();
        _db.Customers.AddRange(newCustomers);
        _db.Products.AddRange(newProducts);
        await _db.SaveChangesAsync(ct);
        _db.ActionLogs.Add(new ActionLog
        {
            SellerId = seller.Id, ActionType = ActionType.DataImported,
            PayloadJson = JsonSerializer.Serialize(new
            {
                CustomerIds = newCustomers.Select(c => c.Id).ToList(),
                ProductIds = newProducts.Select(p => p.Id).ToList()
            })
        });
        await _db.SaveChangesAsync(ct);
    }
}
