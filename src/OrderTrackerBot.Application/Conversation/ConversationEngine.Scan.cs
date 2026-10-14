using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

// Barcode scanner support. A USB / Bluetooth scanner works like a keyboard: it types the code and presses Enter, so a scan reaches the bot
// as a plain text message. A code that matches a product's barcode (Product.Sku) is added to a scan cart; "scan done" turns the cart into an order
// draft (the usual customer name / phone / confirm steps follow). Nothing here reads a barcode from a photo.
//   barcode <product> <code> | barcode remove <product> | scan | scan done | scan clear | scan cart | scan <code>
public partial class ConversationEngine
{
    private static readonly Regex ScannedCode = new(@"^[A-Za-z0-9\-]{4,40}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string ScanHelpText =
        MessageTemplates.Get("scan.014");

    /// <summary>True for a message that looks like a scanned code: one token of letters/digits/dashes with at least three digits ("8961234567890", "KRT-001").</summary>
    public static bool LooksLikeScannedCode(string? text) =>
        text is not null && ScannedCode.IsMatch(text.Trim()) && text.Count(char.IsDigit) >= 3;

    /// <summary>
    /// A bare code that is some product's barcode goes to the scan cart; anything else (a phone number, a number that matches no product) is left to the normal flow.
    /// </summary>
    private async Task<bool> TryHandleScannedCodeAsync(Seller seller, SessionContextData ctx, string message, CancellationToken ct)
    {
        if (!LooksLikeScannedCode(message)) return false;
        var product = await FindProductByBarcodeAsync(seller, message.Trim(), ct);
        if (product is null) return false;
        await AddScannedProductAsync(seller, ctx, product, ct);
        return true;
    }

    private Task<Product?> FindProductByBarcodeAsync(Seller seller, string code, CancellationToken ct)
    {
        var lower = code.ToLower();
        return _db.Products.FirstOrDefaultAsync(p => p.SellerId == seller.Id && p.IsActive && p.Sku != null && p.Sku.ToLower() == lower, ct);
    }

    private async Task AddScannedProductAsync(Seller seller, SessionContextData ctx, Product product, CancellationToken ct)
    {
        ctx.ScanCart ??= new Dictionary<int, int>();
        ctx.ScanCart[product.Id] = ctx.ScanCart.GetValueOrDefault(product.Id) + 1;
        var quantity = ctx.ScanCart[product.Id];

        var (_, total) = await ScanCartTotalAsync(seller, ctx, ct);
        await ReplyAsync(seller,
            MessageTemplates.Get("scan.001", Formatters.ProductLabel(product), Formatters.Money(product.Price), quantity, (product.StockQty is { } stock && quantity > stock ? $"⚠️ Stock sirf {stock} hai.\n" : ""), ctx.ScanCart.Values.Sum(), Formatters.Money(total)), ct);
    }

    private async Task<(List<string> Lines, decimal Total)> ScanCartTotalAsync(Seller seller, SessionContextData ctx, CancellationToken ct)
    {
        var cart = ctx.ScanCart ?? new Dictionary<int, int>();
        var ids = cart.Keys.ToList();
        var products = await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && ids.Contains(p.Id)).ToListAsync(ct);
        var lines = products.Select(p => $"{cart[p.Id]}x {Formatters.ProductLabel(p)} — {Formatters.Money(p.Price * cart[p.Id])}").ToList();
        return (lines, products.Sum(p => p.Price * cart[p.Id]));
    }

    private async Task HandleScanCommandAsync(Seller seller, ConversationSession session, SessionContextData ctx, string? text, CancellationToken ct)
    {
        var word = (text ?? "").Trim();
        switch (word.ToLowerInvariant())
        {
            case "" or "help":
                await ReplyAsync(seller, ScanHelpText, ct);
                return;

            case "clear" or "cancel" or "khali":
                ctx.ScanCart = null;
                await ReplyAsync(seller, MessageTemplates.Get("scan.002"), ct);
                return;

            case "cart" or "list" or "show":
            {
                if (ctx.ScanCart is not { Count: > 0 })
                {
                    await ReplyAsync(seller, MessageTemplates.Get("scan.003"), ct);
                    return;
                }
                var (lines, total) = await ScanCartTotalAsync(seller, ctx, ct);
                await ReplyAsync(seller, MessageTemplates.Get("scan.004", string.Join("\n", lines), Formatters.Money(total)), ct);
                return;
            }

            case "done" or "order" or "finish":
                await FinishScanCartAsync(seller, session, ctx, ct);
                return;
        }

        var product = await FindProductByBarcodeAsync(seller, word, ct);
        if (product is null)
        {
            await ReplyAsync(seller,
                MessageTemplates.Get("scan.005", word, word, word), ct);
            return;
        }
        await AddScannedProductAsync(seller, ctx, product, ct);
    }

    /// <summary>The scan cart becomes an order draft; the usual steps (customer name, phone, confirm) take it from there.</summary>
    private async Task FinishScanCartAsync(Seller seller, ConversationSession session, SessionContextData ctx, CancellationToken ct)
    {
        if (ctx.ScanCart is not { Count: > 0 })
        {
            await ReplyAsync(seller, MessageTemplates.Get("scan.006"), ct);
            return;
        }

        var catalog = await LoadCatalogAsync(seller, ct);
        var draft = new AiOrderDraft();
        foreach (var (productId, quantity) in ctx.ScanCart)
        {
            var entry = catalog.FirstOrDefault(c => c.Product.Id == productId);
            if (entry is null) continue; // switched off since it was scanned
            draft.Items.Add(new AiOrderItemDraft { ProductName = entry.Label, Quantity = quantity, MatchedCatalogProductName = entry.Label });
        }
        ctx.ScanCart = null;
        if (draft.Items.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("scan.007"), ct);
            return;
        }

        ctx.PendingOrder = BuildPendingOrder(draft, catalog, fromScreenshot: false);
        await ContinueResolvingDraftAsync(seller, session, ctx, ct);
    }

    private async Task HandleBarcodeSetAsync(Seller seller, ConversationSession session, SessionContextData ctx, string name, string? code, CancellationToken ct)
    {
        var products = await _db.Products.Where(p => p.SellerId == seller.Id && p.IsActive).ToListAsync(ct);
        var exact = products.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || Formatters.ProductLabel(p).Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        var matches = exact.Count > 0 ? exact : products.Where(p => p.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matches.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("scan.008", name), ct);
            return;
        }
        if (matches.Count > 1)
        {
            // Several products fit the words: let the seller tap the right one (each option is the exact command to run).
            if (code is null)
                await ReplyAsync(seller, MessageTemplates.Get("scan.009", string.Join(", ", matches.Take(6).Select(p => p.Name))), ct);
            else
                await AskClarificationAsync(seller, session, ctx, $"\"{name}\" se kai products milte hain — kis ka barcode {code} hai?",
                    matches.Take(9).Select(p => $"barcode {p.Name} {code}").ToList(), ct);
            return;
        }

        var product = matches[0];
        if (code is null)
        {
            product.Sku = null;
            await ReplyAsync(seller, MessageTemplates.Get("scan.010", product.Name), ct);
            return;
        }
        if (code.Count(char.IsDigit) < 3)
        {
            await ReplyAsync(seller, MessageTemplates.Get("scan.011"), ct);
            return;
        }
        var lower = code.ToLower();
        var other = await _db.Products.FirstOrDefaultAsync(p => p.SellerId == seller.Id && p.Id != product.Id && p.Sku != null && p.Sku.ToLower() == lower, ct);
        if (other is not null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("scan.012", other.Name, other.Name), ct);
            return;
        }

        product.Sku = code;
        await ReplyAsync(seller, MessageTemplates.Get("scan.013", product.Name, code), ct);
    }
}
