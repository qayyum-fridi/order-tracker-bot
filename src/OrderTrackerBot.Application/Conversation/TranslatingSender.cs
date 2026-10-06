using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// Sends the bot's (Roman Urdu) messages to the seller in their chosen language by translating them with the AI just before sending.
/// Best-effort: with no AI, a failed call, or a translation that changed any number (price, order #, phone), the original text is sent.
/// Button labels are never translated — a tapped button comes back as its label and the engine matches on it.
/// </summary>
internal sealed class TranslatingSender : IWhatsAppSender
{
    private const int MaxCachedTranslations = 5000;
    private const int MaxButtonLabel = 20, MaxRowOrSectionTitle = 24, MaxFlowCta = 30;
    private static readonly ConcurrentDictionary<string, string> Cache = new();
    private static readonly Regex Digits = new("[0-9]+", RegexOptions.Compiled);
    private static readonly ConcurrentDictionary<string, string> ButtonLabelOrigins = new();

    private readonly IWhatsAppSender _inner;
    private readonly IAiOrderAssistant _ai;

    public TranslatingSender(IWhatsAppSender inner, IAiOrderAssistant ai)
    {
        _inner = inner;
        _ai = ai;
    }

    /// <summary>The seller currently being served; read at send time so a language change takes effect on the very next message.</summary>
    public Seller? Seller { get; set; }

    private string? TargetFor(string toPhoneNumber)
    {
        if (Seller is null || Seller.WhatsAppPhoneNumber != toPhoneNumber) return null;
        var language = Lang.Normalize(Seller.PreferredLanguage);
        return language == Lang.RomanUrdu ? null : language;
    }

    public async Task SendTextMessageAsync(string toPhoneNumber, string text, CancellationToken cancellationToken = default)
    {
        var target = TargetFor(toPhoneNumber);
        if (target is not null) text = (await TranslateAsync(new[] { text }, target, cancellationToken))[0];
        await _inner.SendTextMessageAsync(toPhoneNumber, text, cancellationToken);
    }

    public async Task SendListMessageAsync(string toPhoneNumber, string bodyText, string buttonLabel, IReadOnlyList<MenuSection> sections, CancellationToken cancellationToken = default)
    {
        var target = TargetFor(toPhoneNumber);
        if (target is not null)
        {
            var texts = new List<string> { bodyText, buttonLabel };
            texts.AddRange(sections.Select(s => s.Title));
            texts.AddRange(sections.SelectMany(s => s.Rows).Select(r => r.Title));
            var translated = await TranslateAsync(texts, target, cancellationToken);

            var next = 2;
            var sectionTitles = sections.Select(s => FitOrOriginal(translated[next++], s.Title, MaxRowOrSectionTitle)).ToList();
            sections = sections
                .Select((s, i) => new MenuSection(sectionTitles[i],
                    s.Rows.Select(r => new MenuRow(r.Id, FitOrOriginal(translated[next++], r.Title, MaxRowOrSectionTitle))).ToList()))
                .ToList();
            bodyText = translated[0];
            buttonLabel = FitOrOriginal(translated[1], buttonLabel, MaxButtonLabel);
        }

        await _inner.SendListMessageAsync(toPhoneNumber, bodyText, buttonLabel, sections, cancellationToken);
    }

    public async Task<bool> SendFlowMessageAsync(string toPhoneNumber, string flowKind, string bodyText, string ctaLabel, CancellationToken cancellationToken = default)
    {
        var target = TargetFor(toPhoneNumber);
        if (target is not null)
        {
            var translated = await TranslateAsync(new[] { bodyText, ctaLabel }, target, cancellationToken);
            bodyText = translated[0];
            ctaLabel = FitOrOriginal(translated[1], ctaLabel, MaxFlowCta);
        }

        return await _inner.SendFlowMessageAsync(toPhoneNumber, flowKind, bodyText, ctaLabel, cancellationToken);
    }

    public async Task SendButtonsMessageAsync(string toPhoneNumber, string bodyText, IReadOnlyList<string> buttonLabels, CancellationToken cancellationToken = default)
    {
        var target = TargetFor(toPhoneNumber);
        if (target is not null)
        {
            // The language picker keeps its fixed labels; every other label is translated and remembered so a tap can be mapped back.
            var translatable = buttonLabels.Where(l => !Lang.ButtonLabels.Contains(l)).ToList();
            var translated = await TranslateAsync(translatable.Prepend(bodyText).ToList(), target, cancellationToken);
            bodyText = translated[0];

            var shown = translatable.Select((l, i) => FitOrOriginal(translated[i + 1], l, MaxButtonLabel)).ToList();
            var collides = shown.GroupBy(s => s).Any(g => g.Count() > 1) || shown.Any(s => Lang.ButtonLabels.Contains(s));
            if (collides) shown = translatable;

            if (ButtonLabelOrigins.Count > MaxCachedTranslations) ButtonLabelOrigins.Clear();
            var swap = new Dictionary<string, string>();
            for (var i = 0; i < translatable.Count; i++)
            {
                swap.TryAdd(translatable[i], shown[i]);
                if (shown[i] != translatable[i]) ButtonLabelOrigins[OriginKey(toPhoneNumber, shown[i])] = translatable[i];
            }
            buttonLabels = buttonLabels.Select(l => swap.TryGetValue(l, out var s) ? s : l).ToList();
        }

        await _inner.SendButtonsMessageAsync(toPhoneNumber, bodyText, buttonLabels, cancellationToken);
    }

    /// <summary>A tapped (translated) button arrives as its label; map it back to the original label the engine matches on.</summary>
    public string RestoreButtonLabel(string message) =>
        Seller is not null && ButtonLabelOrigins.TryGetValue(OriginKey(Seller.WhatsAppPhoneNumber, message.Trim()), out var original) ? original : message;

    private static string OriginKey(string phone, string label) => $"{phone}\u0001{label}";

    public Task<bool> SendTemplateMessageAsync(string toPhoneNumber, IReadOnlyList<string> bodyParameters, CancellationToken cancellationToken = default) =>
        _inner.SendTemplateMessageAsync(toPhoneNumber, bodyParameters, cancellationToken);

    private static string FitOrOriginal(string translated, string original, int maxLength) =>
        translated.Length <= maxLength ? translated : original;

    /// <summary>One result per input, in order; anything that could not be translated safely comes back unchanged.</summary>
    private async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string language, CancellationToken cancellationToken)
    {
        var result = texts.ToArray();
        var pending = new List<int>();
        for (var i = 0; i < texts.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(texts[i]) || IsAlreadyInLanguage(texts[i], language)) continue;
            if (Cache.TryGetValue(CacheKey(language, texts[i]), out var cached)) result[i] = cached;
            else pending.Add(i);
        }
        if (pending.Count == 0) return result;

        IReadOnlyList<string>? translated = null;
        try
        {
            translated = await _ai.TranslateAsync(pending.Select(i => texts[i]).ToList(), language, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Translation is a nicety: never lose the message because of it.
        }

        if (translated is null || translated.Count != pending.Count) return result;

        if (Cache.Count > MaxCachedTranslations) Cache.Clear();
        for (var k = 0; k < pending.Count; k++)
        {
            var original = texts[pending[k]];
            if (!SameNumbers(original, translated[k])) continue;
            result[pending[k]] = translated[k];
            Cache[CacheKey(language, original)] = translated[k];
        }
        return result;
    }

    private static string CacheKey(string language, string text) => $"{language}\u0001{text}";

    /// <summary>Urdu-script text (e.g. the already-localised screens) must not be sent through the translator again.</summary>
    private static bool IsAlreadyInLanguage(string text, string language)
    {
        if (language != Lang.UrduScript) return false;
        var letters = text.Count(char.IsLetter);
        return letters > 0 && text.Count(c => c >= '؀' && c <= 'ۿ') * 2 > letters;
    }

    /// <summary>A translation that changed a price, order number or phone number is rejected.</summary>
    private static bool SameNumbers(string original, string translated) =>
        Digits.Matches(original).Select(m => m.Value).OrderBy(v => v, StringComparer.Ordinal)
            .SequenceEqual(Digits.Matches(translated).Select(m => m.Value).OrderBy(v => v, StringComparer.Ordinal));
}
