using System.Text.RegularExpressions;

namespace OrderTrackerBot.Application.Conversation;

public enum InputModality
{
    Text,
    Voice,
    ImageCaption,
}

/// <summary>
/// The seller's message as received (<see cref="Original"/>, kept unchanged) and as normalized (<see cref="Text"/>, the only form the
/// parser and the model read). Each rewrite is one that keeps the meaning; anything ambiguous is left as the seller said it.
/// </summary>
public sealed record NormalizedInput(string Original, string Text, InputModality Modality, string Language);

/// <summary>
/// Single entry point for text, voice-transcript and image-caption input. Steps run in order; each owns one kind of rewrite.
/// Spoken amount words ("pachas hazar") are not rewritten here: <see cref="SpokenNumbers"/> reads them in context, because
/// a bare number word can be an order number ("order number aik do teen").
/// </summary>
public static class InputNormalizationService
{
    public static NormalizedInput Normalize(string? raw, InputModality modality = InputModality.Text)
    {
        var text = InputTextNormalizer.Normalize(raw);
        if (modality == InputModality.Voice) text = VoiceTranscriptNormalizer.Normalize(text);
        text = CurrencyNormalizer.Normalize(text);
        text = NumberNormalizer.Normalize(text);
        return new NormalizedInput(raw ?? string.Empty, text, modality, DetectLanguage(text));
    }

    // Urdu script means Urdu; anything else is Roman Urdu or English, which the parser handles together.
    private static string DetectLanguage(string text) =>
        text.Any(c => c is >= '؀' and <= 'ۿ') ? "ur" : "roman";
}

/// <summary>Removes speech-recognition fillers from a transcript. Only standalone filler words are removed, never inside a word.</summary>
public static class VoiceTranscriptNormalizer
{
    private static readonly Regex Filler = new(
        @"(?<![\p{L}\p{N}])(?:um+|uh+|hm+|mm+|ah+|er+m?)(?![\p{L}\p{N}])[,.]?\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Normalize(string text)
    {
        var cleaned = Filler.Replace(text, string.Empty).Trim();
        return cleaned.Length == 0 ? text : cleaned;
    }
}

/// <summary>Currency markers around an amount: "Rs. 5,000/-" becomes "5,000" and is then grouped by <see cref="NumberNormalizer"/>.</summary>
public static class CurrencyNormalizer
{
    private static readonly Regex Prefix = new(@"(?<![\p{L}\p{N}])(?:Rs|PKR|₨)\.?\s*(?=\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Suffix = new(@"(?<=\d)\s*/-(?![\p{L}\p{N}])", RegexOptions.Compiled);

    public static string Normalize(string text) => Suffix.Replace(Prefix.Replace(text, string.Empty), string.Empty);
}

/// <summary>Writes thousands-grouped amounts as plain digits ("5,000" becomes "5000"). Groups must be exactly three digits, so "1,23" is untouched.</summary>
public static class NumberNormalizer
{
    private static readonly Regex Grouped = new(@"(?<![\p{L}\p{N},])\d{1,3}(?:,\d{3})+(?![\p{N}])", RegexOptions.Compiled);

    public static string Normalize(string text) => Grouped.Replace(text, m => m.Value.Replace(",", string.Empty));
}
