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
    string Template);

/// <summary>A value the command needs. <see cref="IsNumber"/> slots are checked as plain numbers before they go into the command.</summary>
public sealed record IntentSlot(string Name, bool Required, bool IsNumber);

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

    public static IReadOnlyList<IntentDefinition> All { get; } = Load();

    public static IntentDefinition? Find(string id) => All.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));

    /// <summary>The command as the seller would type it, with each value shown as [slot name]: the how-to for a missing or risky one.</summary>
    public static string Example(IntentDefinition intent) => Placeholder.Replace(intent.Template, m => $"[{m.Groups[1].Value}]");

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
                p.Value.TryGetProperty("type", out var type) && type.GetString() == "number")).ToList();
            var intent = new IntentDefinition(
                item.GetProperty("id").GetString()!,
                item.GetProperty("entity").GetString()!,
                item.GetProperty("action").GetString()!,
                item.GetProperty("kind").GetString()!,
                item.GetProperty("description").GetString()!,
                item.GetProperty("examples").EnumerateArray().Select(e => e.GetString()!).ToList(),
                slots,
                item.GetProperty("template").GetString()!);
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
