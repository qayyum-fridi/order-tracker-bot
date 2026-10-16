using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.Persistence;

namespace OrderTrackerBot.Api.Client;

/// <summary>Adds, edits and soft-deletes the seller's customers. Deleting keeps their orders; the customer is hidden like the WhatsApp "delete customer".</summary>
public sealed class ClientCustomers(AppDbContext db, ClientQueries queries)
{
    private static readonly string[] PriceGroups = ["A", "B", "C"];
    private static readonly Regex PhonePattern = new(@"^\+?[\d\s-]{7,20}$", RegexOptions.Compiled);

    public static string? Validate(CustomerInput input)
    {
        if (Clean(input.Name) is null) return "Enter the customer's name.";
        if (Clean(input.Name)!.Length > 120) return "Keep the name to 120 characters.";
        if (Clean(input.Phone) is { } phone && (!PhonePattern.IsMatch(phone) || phone.Count(char.IsDigit) < 7)) return "Enter a valid phone number.";
        if (Clean(input.City)?.Length > 80) return "Keep the city to 80 characters.";
        if (Clean(input.Address)?.Length > 300) return "Keep the address to 300 characters.";
        if (Clean(input.PreferredContact)?.Length > 40) return "Keep preferred contact to 40 characters.";
        if (Clean(input.Notes)?.Length > 500) return "Keep notes to 500 characters.";
        if (Clean(input.PriceGroup) is { } group && !PriceGroups.Contains(group.ToUpperInvariant())) return "Choose price group A, B or C, or leave it empty.";
        return null;
    }

    public async Task<(string? Problem, CustomerDto? Value)> CreateAsync(int sellerId, CustomerInput input, CancellationToken ct)
    {
        var problem = Validate(input);
        if (problem is not null) return (problem, null);

        var customer = new Customer { SellerId = sellerId, Name = Clean(input.Name)! };
        Apply(customer, input);
        db.Customers.Add(customer);
        await db.SaveChangesAsync(ct);
        return (null, await queries.CustomerAsync(sellerId, customer.Id, ct));
    }

    public async Task<(string? Problem, CustomerDto? Value)> UpdateAsync(int sellerId, int id, CustomerInput input, CancellationToken ct)
    {
        var problem = Validate(input);
        if (problem is not null) return (problem, null);

        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == id && c.SellerId == sellerId && c.DeletedAt == null, ct);
        if (customer is null) return ("Customer not found.", null);
        customer.Name = Clean(input.Name)!;
        Apply(customer, input);
        await db.SaveChangesAsync(ct);
        return (null, await queries.CustomerAsync(sellerId, id, ct));
    }

    public async Task<bool> DeleteAsync(int sellerId, int id, CancellationToken ct)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == id && c.SellerId == sellerId && c.DeletedAt == null, ct);
        if (customer is null) return false;
        customer.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static void Apply(Customer customer, CustomerInput input)
    {
        customer.Phone = Clean(input.Phone);
        customer.City = Clean(input.City);
        customer.Address = Clean(input.Address);
        customer.PreferredContact = Clean(input.PreferredContact);
        customer.Notes = Clean(input.Notes);
        customer.PriceGroup = Clean(input.PriceGroup)?.ToUpperInvariant();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
