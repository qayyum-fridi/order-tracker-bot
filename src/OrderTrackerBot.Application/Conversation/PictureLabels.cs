using System.Text.RegularExpressions;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// The word that tells the bot what a picture is: expense, wages, customer, order or other. It comes as the picture's caption, or as the next
/// short text or voice message ("yeh expense hai"). Plain word lists, no AI: the model only reads the picture once the type is known.
/// </summary>
public static class PictureLabels
{
    public const string Expense = "expense";
    public const string Wages = "wages";
    public const string Customer = "customer";
    public const string Order = "order";
    public const string Other = "other";
    public const string Products = "products";

    private static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["expense"] = Expense, ["expenses"] = Expense, ["kharcha"] = Expense, ["kharch"] = Expense, ["خرچ"] = Expense, ["خرچہ"] = Expense, ["bill"] = Expense,
        ["wage"] = Wages, ["wages"] = Wages, ["salary"] = Wages, ["salaries"] = Wages, ["tankhwah"] = Wages, ["tankhah"] = Wages, ["tankwa"] = Wages, ["dihari"] = Wages, ["تنخواہ"] = Wages,
        ["customer"] = Customer, ["client"] = Customer, ["gahak"] = Customer, ["گاہک"] = Customer, ["کسٹمر"] = Customer, ["خریدار"] = Customer,
        ["order"] = Order, ["orders"] = Order, ["آرڈر"] = Order,
        ["other"] = Other, ["misc"] = Other, ["baqi"] = Other, ["دیگر"] = Other,
        ["product"] = Products, ["products"] = Products, ["catalog"] = Products, ["catalogue"] = Products, ["pricelist"] = Products, ["ratelist"] = Products,
        ["saman"] = Products, ["maal"] = Products, ["پروڈکٹ"] = Products, ["پروڈکٹس"] = Products, ["سامان"] = Products,
    };

    // Filler around the label word: "yeh expense hai", "mark as wages", "this is a customer".
    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "yeh", "ye", "this", "is", "it", "isay", "ise", "ek", "a", "an", "as", "mark", "marked", "hai", "hain", "ka", "ki", "ke", "karo", "kar", "do", "label", "type",
        "picture", "pic", "photo", "tasveer", "ko", "یہ", "ہے", "ہیں", "ایک", "کا", "کی", "کے",
    };

    private static string[] Tokens(string text) =>
        Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(t => t.Length > 0).ToArray();

    /// <summary>A short reply that is only the label ("expense", "yeh wages hai", "mark as customer"). Anything with another word or a number is not a label.</summary>
    public static bool TryParseReply(string? message, out string kind)
    {
        kind = "";
        if (string.IsNullOrWhiteSpace(message)) return false;
        var tokens = Tokens(message);
        if (tokens.Length is 0 or > 6) return false;
        var rest = tokens.Where(t => !Filler.Contains(t)).ToArray();
        if (rest.Length != 1 || !Words.TryGetValue(rest[0], out var found)) return false;
        kind = found;
        return true;
    }

    /// <summary>A caption that starts with the label ("expense", "wages Ali", "kharcha petrol 500"); <paramref name="note"/> is what follows it.</summary>
    public static bool TryParseCaption(string? caption, out string kind, out string note)
    {
        kind = ""; note = "";
        if (string.IsNullOrWhiteSpace(caption)) return false;
        if (TryParseReply(caption, out kind)) return true;
        var parts = caption.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !Words.TryGetValue(parts[0].Trim(':', ',', '-'), out var found)) return false;
        kind = found;
        note = parts.Length > 1 ? parts[1].Trim() : "";
        if (note.Length > 120) note = note[..120];
        return true;
    }
}
