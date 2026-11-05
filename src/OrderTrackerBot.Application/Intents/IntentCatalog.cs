using System.Text.Json;
using System.Text.RegularExpressions;
using OrderTrackerBot.Application.Conversation;

namespace OrderTrackerBot.Application.Intents;

/// <summary>
/// One thing a seller can ask for, described as data (<c>Intents/intents.json</c>). The AI only picks <see cref="Id"/> and fills the slots;
/// the engine builds the typed command from <see cref="Template"/> and runs it through the same deterministic parser as typed text.
/// <see cref="Kind"/> is the <see cref="CommandKind"/> the built command must parse to, so a template that drifts cannot run another action.
/// </summary>
public sealed record IntentDefinition(
    string Id,
    string Entity,
    string Action,
    string Kind,
    string Description,
    IReadOnlyList<string> Examples,
    IReadOnlyList<IntentSlot> Slots,
    string Template,
    IReadOnlyList<string>? Keywords = null,
    bool RunOnHowTo = false);

/// <summary>
/// A value the command needs. <see cref="IsNumber"/> slots are checked as plain numbers before they go into the command.
/// <see cref="Sample"/> is a realistic value used only to show a worked example in a how-to answer; it is never run.
/// </summary>
public sealed record IntentSlot(string Name, bool Required, bool IsNumber, string? Sample = null);

/// <summary>What the router returned: the intent id, the slot values as the seller said them, and how sure it is (0 to 1).</summary>
public sealed record IntentRoute(string Intent, IReadOnlyDictionary<string, string> Slots, double Confidence);

/// <summary><see cref="Command"/> is the typed command to run; null when a required slot is missing (<see cref="Missing"/> names them).</summary>
public sealed record BuiltIntent(string? Command, IReadOnlyList<string> Missing);

public static class IntentCatalog
{
    private const string ResourceName = "Intents.intents.json";
    private const int MaxSlotLength = 60;
    private static readonly Regex Placeholder = new(@"\{(\w+)\}", RegexOptions.Compiled);
    private static readonly Regex Number = new(@"^\d+(\.\d+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The entry whose keywords mark a question as a "how do I ...?"; the engine answers such a question with the line to type, never by running it.</summary>
    public const string HowToId = "guide.howto";

    public static IReadOnlyList<IntentDefinition> All { get; } = Load();

    public static IntentDefinition? Find(string id) => All.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));

    /// <summary>The command as the seller would type it, with each value shown as [slot name]: the how-to for a missing or risky one.</summary>
    public static string Example(IntentDefinition intent) => Placeholder.Replace(intent.Template, m => $"[{m.Groups[1].Value}]");

    /// <summary>
    /// A worked example of the typed command (every slot that has a sample is filled), or null when a required slot has none.
    /// Shown next to <see cref="Example"/> in a how-to answer; never run.
    /// </summary>
    public static string? SampleCommand(IntentDefinition intent)
    {
        var samples = intent.Slots.Where(s => s.Sample is not null).ToDictionary(s => s.Name, s => s.Sample!, StringComparer.Ordinal);
        return Build(intent, samples).Command;
    }

    /// <summary>
    /// The intents a how-to question is closest to, best first: each word the message shares with an intent's examples, keywords, description and
    /// name counts for more the fewer intents use it ("staff" says more than "add"). Plain word overlap over catalog data, no model call.
    /// </summary>
    public static IReadOnlyList<(IntentDefinition Intent, double Score)> Closest(string message, int take = 3)
    {
        var words = IntentKeywords.Tokens(message);
        // The how-to words themselves ("kaise", "how", "tarika") say nothing about which command is meant.
        if (Find(HowToId) is { } howTo)
        {
            var howWords = IntentKeywords.Tokens(string.Join(' ', howTo.Keywords ?? Array.Empty<string>()));
            words.ExceptWith(howWords);
            words.RemoveWhere(w => howWords.Any(k => IntentKeywords.IsNear(w, k)));
        }
        // A word the catalog has never seen is read as its nearest catalog word ("kana" -> "karna"), so a typo still counts.
        var vocabulary = Vocabulary.Value;
        words = words.Select(w => vocabulary.Contains(w) ? w : TextDistance.Closest(w, vocabulary) ?? w).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (words.Count == 0) return Array.Empty<(IntentDefinition, double)>();

        var candidates = All.Where(i => i.Id != HowToId).Select(i => (Intent: i, Words: IntentWords(i))).ToList();
        var weight = words.ToDictionary(w => w, w => 1.0 / Math.Max(1, candidates.Count(c => c.Words.Contains(w))));
        return candidates
            .Select(c => (c.Intent, Score: words.Where(c.Words.Contains).Sum(w => weight[w])))
            .Where(c => c.Score > 0)
            .OrderByDescending(c => c.Score)
            .Take(take).ToList();
    }

    private static readonly Lazy<HashSet<string>> Vocabulary = new(() =>
        All.Where(i => i.Id != HowToId).SelectMany(IntentWords).ToHashSet(StringComparer.OrdinalIgnoreCase));

    private static HashSet<string> IntentWords(IntentDefinition intent) =>
        IntentKeywords.Tokens(string.Join(' ', intent.Examples.Concat(intent.Keywords ?? Array.Empty<string>()).Append(intent.Description).Append(intent.Entity).Append(intent.Action)));

    /// <summary>Fills the template from the slot values. Values that are blank, multi-line, too long or not a number (for number slots) count as missing.</summary>
    public static BuiltIntent Build(IntentDefinition intent, IReadOnlyDictionary<string, string> slots)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var slot in intent.Slots)
        {
            var value = slots.TryGetValue(slot.Name, out var raw) ? Clean(raw, slot.IsNumber) : null;
            if (value is not null) values[slot.Name] = value;
            else if (slot.Required) missing.Add(slot.Name);
        }
        if (missing.Count > 0) return new BuiltIntent(null, missing);

        var command = Placeholder.Replace(intent.Template, m => values.GetValueOrDefault(m.Groups[1].Value) ?? "");
        return new BuiltIntent(string.Join(' ', command.Split(' ', StringSplitOptions.RemoveEmptyEntries)), missing);
    }

    private static string? Clean(string raw, bool isNumber)
    {
        var value = raw.Trim();
        if (isNumber) value = value.Replace(",", "");
        if (value.Length == 0 || value.Length > MaxSlotLength || value.Contains('\n') || value.Contains('\r')) return null;
        if (isNumber) return Number.IsMatch(value) ? value : null;
        return value.Contains('{') || value.Contains('}') ? null : value;
    }

    private static IReadOnlyList<IntentDefinition> Load()
    {
        using var stream = typeof(IntentCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"{ResourceName} is not embedded in the Application assembly.");
        using var doc = JsonDocument.Parse(stream);

        var intents = new List<IntentDefinition>();
        foreach (var item in doc.RootElement.GetProperty("intents").EnumerateArray())
        {
            var slots = item.GetProperty("slots").EnumerateObject().Select(p => new IntentSlot(
                p.Name,
                p.Value.TryGetProperty("required", out var required) && required.GetBoolean(),
                p.Value.TryGetProperty("type", out var type) && type.GetString() == "number",
                p.Value.TryGetProperty("sample", out var sample) ? sample.GetString() : null)).ToList();
            var intent = new IntentDefinition(
                item.GetProperty("id").GetString()!,
                item.GetProperty("entity").GetString()!,
                item.GetProperty("action").GetString()!,
                item.GetProperty("kind").GetString()!,
                item.GetProperty("description").GetString()!,
                item.GetProperty("examples").EnumerateArray().Select(e => e.GetString()!).ToList(),
                slots,
                item.GetProperty("template").GetString()!,
                item.TryGetProperty("keywords", out var keywords) ? keywords.EnumerateArray().Select(k => k.GetString()!.ToLowerInvariant()).ToList() : null,
                item.TryGetProperty("runOnHowTo", out var runOnHowTo) && runOnHowTo.GetBoolean());
            Validate(intent);
            intents.Add(intent);
        }

        var duplicate = intents.GroupBy(i => i.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new InvalidOperationException($"Intent id \"{duplicate.Key}\" is listed twice in {ResourceName}.");
        return intents;
    }

    // A broken entry must fail at start-up, not when a seller's message happens to reach it.
    private static void Validate(IntentDefinition intent)
    {
        if (intent.Examples.Count == 0) throw new InvalidOperationException($"Intent \"{intent.Id}\" has no examples.");
        if (!Enum.TryParse<CommandKind>(intent.Kind, out _)) throw new InvalidOperationException($"Intent \"{intent.Id}\" names unknown kind \"{intent.Kind}\".");
        var declared = intent.Slots.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        foreach (Match m in Placeholder.Matches(intent.Template))
            if (!declared.Contains(m.Groups[1].Value))
                throw new InvalidOperationException($"Intent \"{intent.Id}\" uses {{{m.Groups[1].Value}}} in its template but declares no such slot.");
    }
}
