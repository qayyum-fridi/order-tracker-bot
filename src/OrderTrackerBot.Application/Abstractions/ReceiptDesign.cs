using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrderTrackerBot.Application.Abstractions;

/// <summary>
/// The seller's own receipt look, saved per seller ("receipt design"). Every field is "standard" at its default value, so an empty design renders the usual receipt.
/// Paper size and footer stay on the seller (<c>receipt size</c>, <c>receipt footer</c>); this is only the look.
/// </summary>
public sealed record ReceiptDesign(
    string? Accent = null,
    string? Title = null,
    bool Centered = false,
    bool HideLogo = false,
    bool HideShopPhone = false,
    bool HideBuyerDetails = false,
    bool HideOrderStatus = false,
    string? TextSize = null)
{
    public const int MaxTitleLength = 24;

    private static readonly Dictionary<string, string> Colours = new(StringComparer.OrdinalIgnoreCase)
    {
        ["green"] = "#0B6E4F", ["hara"] = "#0B6E4F", ["blue"] = "#1D4ED8", ["neela"] = "#1D4ED8", ["navy"] = "#1E3A8A",
        ["red"] = "#B91C1C", ["laal"] = "#B91C1C", ["maroon"] = "#7F1D1D", ["purple"] = "#7E22CE", ["jamni"] = "#7E22CE",
        ["pink"] = "#BE185D", ["gulabi"] = "#BE185D", ["orange"] = "#C2410C", ["teal"] = "#0F766E", ["black"] = "#111827", ["kala"] = "#111827",
        ["gold"] = "#A16207", ["brown"] = "#78350F"
    };

    private static readonly Regex Hex = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string ColourNames => "green, blue, navy, red, maroon, purple, pink, orange, teal, gold, brown, black — ya #RRGGBB";

    public static string? ParseColour(string? text)
    {
        var t = (text ?? "").Trim();
        if (Colours.TryGetValue(t, out var named)) return named;
        return Hex.IsMatch(t) ? t.ToUpperInvariant() : null;
    }

    public bool IsStandard => this == new ReceiptDesign();

    public static ReceiptDesign FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ReceiptDesign();
        try
        {
            var d = JsonSerializer.Deserialize<ReceiptDesign>(json) ?? new ReceiptDesign();
            // Values come from our own JSON, but a hand-edited row must never reach the PDF engine unchecked.
            return d with
            {
                Accent = d.Accent is not null && Hex.IsMatch(d.Accent) ? d.Accent : null,
                Title = string.IsNullOrWhiteSpace(d.Title) || d.Title.Length > MaxTitleLength ? null : d.Title,
                TextSize = d.TextSize is "small" or "large" ? d.TextSize : null
            };
        }
        catch (JsonException)
        {
            return new ReceiptDesign();
        }
    }

    public string? ToJson() => IsStandard ? null : JsonSerializer.Serialize(this);

    public string Describe() =>
        $"Rang: {Accent ?? "default (hara)"}\n" +
        $"Title: {Title ?? "default (RECEIPT)"}\n" +
        $"Layout: {(Centered ? "center" : "classic")}\n" +
        $"Logo: {(HideLogo ? "off" : "on")} · Shop phone: {(HideShopPhone ? "off" : "on")} · Buyer details: {(HideBuyerDetails ? "off" : "on")} · Order status: {(HideOrderStatus ? "off" : "on")}\n" +
        $"Text: {TextSize ?? "normal"}";

    /// <summary>One typed design instruction ("color blue", "title Invoice", "logo off", "text large", "layout center"). False when the message is not one; <paramref name="error"/> is set when it is one but its value is wrong.</summary>
    public static bool TryApply(string message, ReceiptDesign current, out ReceiptDesign result, out string? error)
    {
        result = current;
        error = null;
        var m = Regex.Match(message.Trim(), @"^(?<k>colou?r|rang|title|layout|logo|phone|buyer|customer|status|text|font)\s*:?\s*(?<v>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!m.Success) return false;
        var key = m.Groups["k"].Value.ToLowerInvariant();
        var v = m.Groups["v"].Value.Trim();
        var vl = v.ToLowerInvariant();

        switch (key)
        {
            case "color" or "colour" or "rang":
                if (vl is "default" or "reset") { result = current with { Accent = null }; return true; }
                if (ParseColour(v) is not { } colour) { error = $"Maaf kijiye, yeh rang samajh nahi aaya. Likhein: color blue — {ColourNames}."; return true; }
                result = current with { Accent = colour };
                return true;
            case "title":
                if (vl is "default" or "reset" or "off") { result = current with { Title = null }; return true; }
                if (v.Length == 0 || v.Length > MaxTitleLength) { error = $"Title 1 se {MaxTitleLength} huroof ka likhein, jaise: title Invoice"; return true; }
                result = current with { Title = v };
                return true;
            case "layout":
                if (vl is "center" or "centre" or "centered") { result = current with { Centered = true }; return true; }
                if (vl is "classic" or "left" or "default") { result = current with { Centered = false }; return true; }
                error = "Likhein: layout center ya layout classic.";
                return true;
            case "logo" or "phone" or "status":
            case "buyer" or "customer":
                if (key is "phone") v = Regex.Replace(v, @"^(?:shop|dukaan)\s*", "", RegexOptions.IgnoreCase);
                if (key is "buyer" or "customer") v = Regex.Replace(v, @"^(?:details?|phone|address)\s*", "", RegexOptions.IgnoreCase);
                if (key is "status") v = Regex.Replace(v, @"^(?:line|box)\s*", "", RegexOptions.IgnoreCase);
                vl = v.ToLowerInvariant();
                bool? on = vl is "on" or "yes" or "show" or "haan" ? true : vl is "off" or "no" or "hide" or "nahi" ? false : (bool?)null;
                if (on is null) { error = $"Likhein: {key} on ya {key} off."; return true; }
                var hide = !on.Value;
                result = key switch
                {
                    "logo" => current with { HideLogo = hide },
                    "phone" => current with { HideShopPhone = hide },
                    "status" => current with { HideOrderStatus = hide },
                    _ => current with { HideBuyerDetails = hide }
                };
                return true;
            default: // text / font
                if (vl is "small" or "chhota" or "chota") { result = current with { TextSize = "small" }; return true; }
                if (vl is "large" or "bara" or "bada") { result = current with { TextSize = "large" }; return true; }
                if (vl is "normal" or "default" or "medium") { result = current with { TextSize = null }; return true; }
                error = "Likhein: text small, text normal ya text large.";
                return true;
        }
    }
}
