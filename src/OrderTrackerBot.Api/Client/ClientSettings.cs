using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;

namespace OrderTrackerBot.Api.Client;

/// <summary>Store settings and payment methods for the dashboard's Config page. Every write is scoped to the seller.</summary>
public sealed class ClientSettings(AppDbContext db)
{
    public const int MaxPaymentMethods = 10;
    private static readonly string[] ReceiptSizes = ["A4", "A5", "80mm", "58mm"];
    private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);
    private static readonly Regex TaxNumber = new(@"^\d{1,13}$", RegexOptions.Compiled);

    public async Task<SellerSettingsDto?> GetAsync(int sellerId, CancellationToken ct)
    {
        var seller = await db.Sellers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sellerId, ct);
        if (seller is null) return null;
        var methods = await db.PaymentMethods.AsNoTracking().Where(m => m.SellerId == sellerId).OrderBy(m => m.Id).ToListAsync(ct);
        return ToDto(seller, methods);
    }

    /// <summary>Returns a problem in plain words, or null with the saved settings.</summary>
    public async Task<(string? Problem, SellerSettingsDto? Value)> UpdateAsync(int sellerId, SellerSettingsInput input, CancellationToken ct)
    {
        var problem = Validate(input);
        if (problem is not null) return (problem, null);

        var seller = await db.Sellers.FirstOrDefaultAsync(s => s.Id == sellerId, ct);
        if (seller is null) return ("Seller not found.", null);

        seller.BusinessName = Clean(input.BusinessName);
        seller.City = Clean(input.City);
        seller.BusinessType = Clean(input.BusinessType);
        seller.Email = Clean(input.Email);
        seller.InstagramHandle = Clean(input.InstagramHandle)?.TrimStart('@');
        seller.DefaultDeliveryCharge = input.DefaultDeliveryCharge ?? seller.DefaultDeliveryCharge;
        seller.SalesTaxRate = input.SalesTaxRate ?? seller.SalesTaxRate;
        seller.Ntn = Clean(input.Ntn);
        seller.Strn = Clean(input.Strn);
        seller.ReceiptSize = Clean(input.ReceiptSize) ?? "A5";
        seller.ReceiptHeader = Clean(input.ReceiptHeader);
        seller.ReceiptFooter = Clean(input.ReceiptFooter);
        seller.ReceiptContact = Clean(input.ReceiptContact);
        await db.SaveChangesAsync(ct);
        return (null, await GetAsync(sellerId, ct));
    }

    public async Task<(string? Problem, PaymentMethodDto? Value)> AddPaymentMethodAsync(int sellerId, PaymentMethodInput input, CancellationToken ct)
    {
        if (!Enum.TryParse<SellerPaymentMethodType>(input.Type, ignoreCase: true, out var type) || !Enum.IsDefined(type))
            return ("Choose JazzCash, Easypaisa, Bank or Safepay.", null);
        var account = Clean(input.AccountNumberOrId);
        if (account is null) return ("Enter the account number or ID.", null);
        if (account.Length > 60 || account.Any(char.IsControl)) return ("Keep the account number to 60 characters on one line.", null);

        var existing = await db.PaymentMethods.Where(m => m.SellerId == sellerId).ToListAsync(ct);
        if (existing.Count >= MaxPaymentMethods) return ($"Add at most {MaxPaymentMethods} payment methods.", null);
        if (existing.Any(m => m.Type == type && m.AccountNumberOrId == account)) return ("This payment method is already saved.", null);

        var method = new SellerPaymentMethod { SellerId = sellerId, Type = type, AccountNumberOrId = account };
        db.PaymentMethods.Add(method);
        await db.SaveChangesAsync(ct);
        return (null, new PaymentMethodDto(method.Id, method.Type.ToString(), method.AccountNumberOrId));
    }

    public async Task<bool> RemovePaymentMethodAsync(int sellerId, int id, CancellationToken ct)
    {
        var method = await db.PaymentMethods.FirstOrDefaultAsync(m => m.SellerId == sellerId && m.Id == id, ct);
        if (method is null) return false;
        db.PaymentMethods.Remove(method);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>The first problem found, in words a shop owner can act on. Null when the input is fine.</summary>
    public static string? Validate(SellerSettingsInput input)
    {
        if (Length(input.BusinessName) > 120) return "Keep the business name to 120 characters.";
        if (Length(input.City) > 80) return "Keep the city to 80 characters.";
        if (Length(input.BusinessType) > 80) return "Keep the business type to 80 characters.";
        if (Clean(input.Email) is { } email && (email.Length > 120 || !EmailPattern.IsMatch(email))) return "Enter a valid email address.";
        if (Length(input.InstagramHandle) > 30) return "Keep the Instagram handle to 30 characters.";
        if (input.DefaultDeliveryCharge is < 0 or > 1_000_000) return "Delivery charge must be between 0 and 1,000,000.";
        if (input.SalesTaxRate is < 0 or > 100) return "Sales tax must be between 0 and 100 percent.";
        if (Clean(input.Ntn) is { } ntn && !TaxNumber.IsMatch(ntn)) return "NTN must be digits only.";
        if (Clean(input.Strn) is { } strn && !TaxNumber.IsMatch(strn)) return "STRN must be digits only.";
        if (Clean(input.ReceiptSize) is { } size && !ReceiptSizes.Contains(size)) return "Choose a receipt size from the list.";
        if (Length(input.ReceiptHeader) > 200 || Length(input.ReceiptFooter) > 200 || Length(input.ReceiptContact) > 200)
            return "Keep receipt header, footer and contact to 200 characters each.";
        return null;
    }

    private static SellerSettingsDto ToDto(Seller s, IReadOnlyList<SellerPaymentMethod> methods) => new(
        s.BusinessName, s.City, s.BusinessType, s.Email, s.InstagramHandle,
        s.DefaultDeliveryCharge, s.SalesTaxRate, s.Ntn, s.Strn,
        s.ReceiptSize ?? "A5", s.ReceiptHeader, s.ReceiptFooter, s.ReceiptContact,
        methods.Select(m => new PaymentMethodDto(m.Id, m.Type.ToString(), m.AccountNumberOrId)).ToList());

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static int Length(string? value) => Clean(value)?.Length ?? 0;
}
