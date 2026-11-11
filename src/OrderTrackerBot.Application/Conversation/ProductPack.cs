using System.Globalization;
using System.Text.Json;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// A product bought and sold by the piece that also comes in a bigger pack (a carton, box, peti, dozen): how many pieces one pack holds and,
/// when the seller has one, the price of a whole pack. Everything is kept per PIECE (price, cost, stock, order quantities); the pack only converts:
/// "3 carton" in stock or in an order is 3 x the pieces, a carton's cost is divided by the pieces, a carton's price is the rate per piece of that line.
/// Kept in <see cref="Product.AttributesJson"/> (keys <see cref="NameKey"/>, <see cref="QtyKey"/>, <see cref="PriceKey"/>), like the supplier: no schema change.
/// </summary>
public sealed record ProductPack(string Name, int Qty, decimal? Price)
{
    public const string NameKey = "pack";
    public const string QtyKey = "pack qty";
    public const string PriceKey = "pack price";
    private const string DefaultName = "carton";

    /// <summary>The price of one piece when a whole pack is bought; null when the seller gave no pack price.</summary>
    public decimal? PiecePrice => Price is { } price ? Math.Round(price / Qty, 2, MidpointRounding.AwayFromZero) : null;

    public static ProductPack? Of(Product product)
    {
        if (string.IsNullOrWhiteSpace(product.AttributesJson)) return null;
        try
        {
            return Of(JsonSerializer.Deserialize<Dictionary<string, string>>(product.AttributesJson));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ProductPack? Of(IEnumerable<KeyValuePair<string, string>>? attributes)
    {
        if (attributes is null) return null;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in attributes) map[key] = value;
        if (!map.TryGetValue(QtyKey, out var qtyText) || !int.TryParse(qtyText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var qty) || qty < 2) return null;
        var name = map.TryGetValue(NameKey, out var n) && !string.IsNullOrWhiteSpace(n) ? n.Trim() : DefaultName;
        decimal? price = map.TryGetValue(PriceKey, out var p) && decimal.TryParse(p, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : null;
        return new ProductPack(name, qty, price);
    }

    /// <summary>"carton = 10 piece, carton Rs.1,200" for the seller's product details.</summary>
    public string Describe() => Price is { } price ? $"{Name} = {Qty} piece, {Name} {Formatters.Money(price)}" : $"{Name} = {Qty} piece";

    /// <summary>"30 (3 carton)" / "34 (3 carton + 4)": a piece count with its whole packs, for stock.</summary>
    public string StockText(int pieces) =>
        pieces < Qty ? pieces.ToString(CultureInfo.InvariantCulture)
        : pieces % Qty == 0 ? $"{pieces} ({pieces / Qty} {Name})"
        : $"{pieces} ({pieces / Qty} {Name} + {pieces % Qty})";

    public static bool IsPackKey(string key) =>
        key.Equals(NameKey, StringComparison.OrdinalIgnoreCase) || key.Equals(QtyKey, StringComparison.OrdinalIgnoreCase) || key.Equals(PriceKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Turns what the model read about a pack ("carton 1000 ka aata hai, 10 piece, 3 carton hain, carton 1200 ka bechta hun") into per-piece figures:
    /// cost = pack cost / pieces, stock = packs x pieces, price = pack price / pieces when no piece price was said, plus the pack attributes.
    /// Only numbers the seller said are used (<paramref name="said"/>); the divisions are done here, never by the model, and a per-piece price or cost
    /// the model worked out itself (not said) is replaced by the one computed here. <c>Derived</c> lists the computed figures.
    /// A product with no usable pack size is returned as it came, without its pack fields.
    /// </summary>
    public static (AiNewProduct Product, IReadOnlyList<decimal> Derived) FromModel(AiNewProduct p, IReadOnlySet<long> said)
    {
        bool Said(decimal? value) => value is { } v && v > 0 && v == Math.Floor(v) && said.Contains((long)v);
        if (p.PackQty is not { } qty || qty < 2 || !said.Contains(qty)) return (p, Array.Empty<decimal>());

        var derived = new List<decimal>();
        var packPrice = Said(p.PackPrice) ? p.PackPrice : null;
        var packCost = Said(p.PackCost) ? p.PackCost : null;
        var stockPacks = p.StockPacks is { } packs && packs > 0 && said.Contains(packs) ? p.StockPacks : null;

        var price = Said(p.Price) ? p.Price : null;
        if (price is null && packPrice is { } sellPack) derived.Add((price = Math.Round(sellPack / qty, 2, MidpointRounding.AwayFromZero)).Value);
        var cost = Said(p.Cost) ? p.Cost : null;
        if (cost is null && packCost is { } buyPack) derived.Add((cost = Math.Round(buyPack / qty, 2, MidpointRounding.AwayFromZero)).Value);
        var stock = p.Stock is { } pieces && said.Contains(pieces) ? p.Stock : null;
        if (stock is null && stockPacks is { } have) derived.Add((stock = have * qty).Value);

        var attributes = new Dictionary<string, string>(p.Attributes, StringComparer.OrdinalIgnoreCase)
        {
            [NameKey] = string.IsNullOrWhiteSpace(p.PackName) ? DefaultName : p.PackName!.Trim().ToLowerInvariant(),
            [QtyKey] = qty.ToString(CultureInfo.InvariantCulture),
        };
        if (packPrice is { } listed) attributes[PriceKey] = listed.ToString("0.##", CultureInfo.InvariantCulture);

        return (new AiNewProduct
        {
            Name = p.Name, Price = price, Cost = cost, Stock = stock, Attributes = attributes,
            PriceLow = p.PriceLow, PriceHigh = p.PriceHigh, Colors = p.Colors, Sizes = p.Sizes,
        }, derived);
    }
}
