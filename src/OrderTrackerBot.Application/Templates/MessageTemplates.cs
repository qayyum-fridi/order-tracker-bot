using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrderTrackerBot.Application.Templates;

/// <summary>
/// The bot's wording. Every user-facing message is a keyed entry in one of the embedded <c>Templates/Messages/*.json</c> files, written in
/// Roman Urdu (the source language); English / Urdu-script sellers get the same text through <c>TranslatingSender</c>.
/// Placeholders are <c>{0}</c>, <c>{1}</c>, ... and are filled in order: the caller formats numbers, money and dates, so a template never has a format string.
/// Keys are unique across all files; a file name is only for grouping (onboarding, orders, reports, ...).
/// </summary>
public static class MessageTemplates
{
    private const string ResourcePrefix = "Templates.Messages.";
    private const string JsonExtension = ".json";
    private static readonly Regex PlaceholderPattern = new(@"\{(\d+)\}", RegexOptions.Compiled);
    private static readonly IReadOnlyDictionary<string, string> Entries = Load();

    /// <summary>The text for <paramref name="key"/> with its placeholders filled from <paramref name="args"/>.</summary>
    public static string Get(string key, params object?[] args)
    {
        if (!Entries.TryGetValue(key, out var template))
            throw new KeyNotFoundException($"Message template \"{key}\" is not in any Templates/Messages file.");
        if (args.Length == 0) return template;
        return PlaceholderPattern.Replace(template, m =>
        {
            var index = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            // Current culture, as string interpolation and "+" use, so converted messages format numbers exactly as before.
            return index < args.Length ? Convert.ToString(args[index], CultureInfo.CurrentCulture) ?? "" : m.Value;
        });
    }

    /// <summary>Every key with its Roman Urdu template (used by the tests).</summary>
    internal static IReadOnlyDictionary<string, string> All => Entries;

    private static Dictionary<string, string> Load()
    {
        var assembly = typeof(MessageTemplates).Assembly;
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        var options = new JsonSerializerOptions();
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(JsonExtension, StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Message template file {resource} is missing from the build.");
            var file = JsonSerializer.Deserialize<Dictionary<string, string>>(stream, options)
                ?? throw new InvalidOperationException($"Message template file {resource} is empty.");
            foreach (var (key, text) in file)
            {
                if (string.IsNullOrWhiteSpace(text))
                    throw new InvalidOperationException($"Message template \"{key}\" in {resource} is empty.");
                if (!entries.TryAdd(key, text))
                    throw new InvalidOperationException($"Message template key \"{key}\" in {resource} is already defined in another file.");
            }
        }
        return entries;
    }
}
