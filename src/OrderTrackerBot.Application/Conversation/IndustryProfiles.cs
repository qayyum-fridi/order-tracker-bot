using System.Text.RegularExpressions;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// Everything that differs per kind of shop in what the bot SAYS: example product lines, an example order, tips and the data worth entering first.
/// Text is Roman Urdu like every other bot message; English / Urdu-script sellers get it through the same translation as all other messages
/// (<c>TranslatingSender</c>), so there is one template per industry, not one per language. Examples are product lines the parser accepts
/// (a test enforces it). Behaviour never changes with the industry: only wording and examples.
/// </summary>
public sealed record IndustryProfile(
    string Key,
    string Title,
    string Aliases,
    string[] ProductLines,
    string OrderExample,
    string[] Tips,
    string DataFocus)
{
    /// <summary>One simple example line ("Kurti - 1800"), for short prompts.</summary>
    public string SimpleProduct => ProductLines.Last(l => !l.Contains(',')) ;

    /// <summary>The category word of the full example ("lawn", "rashan"), for "category: ..." hints.</summary>
    public string Category => FullParts.FirstOrDefault(p => p.StartsWith("category ", StringComparison.OrdinalIgnoreCase))?["category ".Length..] ?? "general";

    private string[] FullParts => FullProduct.Split(',', StringSplitOptions.TrimEntries);

    /// <summary>"Sugar 5 kg - 500": the first example without its details.</summary>
    public string MainLine => FullParts[0];

    /// <summary>"Sugar 5 kg".</summary>
    public string MainName => MainLine.Split(" - ")[0];

    /// <summary>"Sugar 5 kg - 500, cost 440": the first example with its cost.</summary>
    public string CostLine => FullParts.Length > 1 ? FullParts[0] + ", " + FullParts[1] : MainLine;

    /// <summary>The product name of <see cref="SimpleProduct"/> ("Sugar 5 kg"), for "stock Sugar 5 kg 20".</summary>
    public string SimpleProductName => SimpleProduct.Split(" - ")[0];

    /// <summary>The complete line with cost, stock and category, which fills the most reports.</summary>
    public string FullProduct => ProductLines.First(l => l.Contains(','));

    private Regex? _aliases;
    internal bool Matches(string text) => (_aliases ??= new Regex(@"(?<![\p{L}\p{N}])(?:" + Aliases + @")(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).IsMatch(text);
}

public static class IndustryProfiles
{
    public static readonly IndustryProfile General = new(
        "general", "General dukaan", "general|dukaan|shop|store|other|mix|aam",
        new[] { "Water Bottle - 500, cost 350, stock 20, category general", "Notebook - 1200", "Rice 5 kg - 800" },
        "Ayesha, 2 water bottle, 03001234567",
        new[] { "Har product ke saath cost likhein, jaise \"Water Bottle - 500, cost 350\": isse profit sahi aata hai.", "Order aane par \"mark 3 shipped\" / \"mark 3 delivered\" zaroor likhein.", "Apni dukaan ki qism chunne ke liye \"industry\" likhein: phir examples aap ki dukaan ke mutabiq honge." },
        "cost price aur stock");

    public static readonly IReadOnlyList<IndustryProfile> All = new[]
    {
        new IndustryProfile("clothing", "Kapray / Lawn / Boutique", "cloth\\w*|kapr\\w*|kapd\\w*|lawn|boutique|garment\\w*|suit\\w*|stitch\\w*|fabric|abaya|shawl|dress\\w*|کپڑ\\w*",
            new[] { "3 Piece Lawn Suit - 3500, cost 2400, stock 20, category lawn", "Kurti - 1800, cost 1100, stock 15, category stitched", "Dupatta - 600" },
            "Ayesha, 2 lawn suit, 03001234567, Gulberg Lahore",
            new[]
            {
                "Design number product ke naam mein likhein, jaise \"Design 1203 - 3500\": order mein wohi naam bol dein.",
                "Size aur colour ke liye likhein: \"Kurti - 1800, size M, color blue\".",
                "Eid / lawn season se pehle poochein: \"kaunsa product khatam hone wala hai\"."
            },
            "cost price, category (lawn / stitched / bridal) aur stock"),
        new IndustryProfile("grocery", "Grocery / Kiryana", "grocer\\w*|kiryana|karyana|kariyana|rashan|ration|general\\s*store|mart|supermart|گروسری|راشن|کریانہ",
            new[] { "Sugar 5 kg - 500, cost 440, stock 40, category rashan", "Cooking Oil 1 ltr - 520", "Rice 10 kg - 2600" },
            "Bilal, 2 sugar 5 kg, 1 oil 1 ltr, 03001234567, DHA Karachi",
            new[]
            {
                "Weight ya pack naam ke saath likhein: \"Sugar 5 kg - 500\": 5 kg ka pack ek hi product hai.",
                "Stock zaroor likhein (\"stock Sugar 5 kg 40\"): kam hone par bot khud batata hai.",
                "Stock 3 ya us se kam reh jaye to bot khud warning bhejta hai."
            },
            "stock, pack size aur cost price"),
        new IndustryProfile("beauty", "Cosmetics / Jewellery", "cosmetic\\w*|beauty|makeup|jewel+e?ry|jewelry|fashion\\s+accessor\\w*|bags?|purse|watch\\w*|perfume\\w*|skin\\s*care|زیور\\w*",
            new[] { "Lipstick Matte Red - 950, cost 600, stock 30, category makeup", "Bridal Necklace Set - 8500", "Hand Bag - 3200" },
            "Sana, 1 lipstick matte red, 03001234567, Johar Town Lahore",
            new[]
            {
                "Shade / size / colour product ke naam mein likhein (\"Lipstick Matte Red\"): har shade ka stock alag rehta hai.",
                "Bar-code scanner ho to \"barcode Lipstick Matte Red 8964000123\" se code jod dein.",
                "Gift wrap jaisi baat ke liye \"note 12 gift wrap\" likhein: receipt par chhap jati hai."
            },
            "cost price, stock aur category"),
        new IndustryProfile("electronics", "Electronics / Mobile", "electronic\\w*|mobile\\w*|phone\\s*acc\\w*|gadget\\w*|computer\\w*|laptop\\w*|charger\\w*|appliance\\w*|الیکٹرانک\\w*|موبائل",
            new[] { "Fast Charger 20W - 1800, cost 1100, stock 25, category charger", "Earbuds Pro - 3500", "Phone Cover - 600" },
            "Hamza, 1 fast charger 20w, 03001234567, Saddar Rawalpindi",
            new[]
            {
                "Model / watt / variant naam mein likhein, taake galat cheez na bike.",
                "Har cheez ka barcode jodein (\"barcode Earbuds Pro 8964000999\"), phir counter par scan se bechein.",
                "Warranty ya serial ke liye \"note 12 serial 12345 warranty 1 saal\" likhein."
            },
            "cost price, stock aur barcode"),
        new IndustryProfile("food", "Food / Bakery", "food\\w*|bak\\w*|cake\\w*|kitchen|restaurant|sweet\\w*|mithai|catering|homemade|biryani|حلوائی|کھانا|بیکری",
            new[] { "Chocolate Cake 2 lb - 3200, cost 1500, category cakes", "Chicken Biryani Tray - 4500", "Brownies Box - 1200" },
            "Maryam, 1 chocolate cake 2 lb, 03001234567, Clifton Karachi",
            new[]
            {
                "Pehle se order lene wale kaam mein advance likhein: \"order 12 advance 1000\" ya order mein \"…, advance 1000\".",
                "Delivery charge order mein likhein (\"…, delivery 300\"), warna default \"delivery 300\" set kar lein.",
                "Customer ki khaas farmaish (\"photo cake, name Ali\") ke liye \"note 12 <likhai>\" likhein."
            },
            "advance payment, delivery charge aur cost price"),
        new IndustryProfile("hardware", "Hardware / Building", "hardware|building|construction|paint\\w*|tiles?|sanitary|electrical|pipe\\w*|cement|tools?|ہارڈ\\s*ویئر",
            new[] { "Wall Paint 4 ltr - 3800, cost 3000, stock 20, category paint", "PVC Pipe 1 inch - 450", "Cement Bag 50 kg - 1350" },
            "Usman, 5 pvc pipe 1 inch, 2 wall paint 4 ltr, 03001234567, Gujranwala",
            new[]
            {
                "Size / unit naam mein likhein (\"PVC Pipe 1 inch\"): ek hi cheez ke alag size alag products hain.",
                "Udhaar / baaqi paisa ke liye \"order 12 advance 5000\" likhein: baqi balance \"unpaid orders\" mein dikhta hai.",
                "Supplier ke hisab se maal dekhne ke liye \"supplier Wall Paint Ali Traders\" likhein."
            },
            "stock, cost price aur supplier")
    };

    public static IndustryProfile ByKey(string? key) => All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) ?? General;

    /// <summary>The profile for what the seller wrote as their business type ("Clothing", "kiryana store", "lawn boutique"); <see cref="General"/> when unknown or empty.</summary>
    public static IndustryProfile Resolve(string? businessType)
    {
        if (string.IsNullOrWhiteSpace(businessType)) return General;
        return All.FirstOrDefault(p => p.Matches(businessType)) ?? General;
    }
}
