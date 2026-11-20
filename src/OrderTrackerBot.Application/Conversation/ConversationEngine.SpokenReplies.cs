using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

// Spoken replies (Features:SpokenReplies): when the seller's message was a voice note, the bot's answer is also sent back as a voice note,
// after the typed text. A short answer is read as it is; a list or a long answer gets a short spoken summary (its first line, how many items,
// and that the rest is in the text), so a seller who cannot read well still hears what happened. A failed speech call sends nothing extra.
public partial class ConversationEngine
{
    /// <summary>Longest first line kept in a spoken summary.</summary>
    private const int MaxSpokenHeadChars = 160;

    private static readonly Regex ListLine = new(@"^(?:\d{1,2}\s*[.)]|\d️?⃣|[•▪◦*-]\s|#\d)", RegexOptions.Compiled);

    /// <summary>
    /// What is read aloud for a reply: the reply itself when it is short; otherwise its first line (no emoji or formatting, at most
    /// <see cref="MaxSpokenHeadChars"/> characters), the number of list items when it is a list, and that the rest is in the text.
    /// </summary>
    internal static string SpokenVersion(string text)
    {
        // A short answer is read in full, without the emoji and formatting marks.
        if (IsSpeakable(text))
            return string.Join("\n", text.Split('\n').Select(SpeechText).Where(l => l.Length > 0));
        var lines = text.Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.Contains("http", StringComparison.OrdinalIgnoreCase)).ToList();
        var items = lines.Count(l => ListLine.IsMatch(l));
        var head = SpeechText(lines.FirstOrDefault(l => !ListLine.IsMatch(l)) ?? lines.FirstOrDefault() ?? "");
        if (head.Length > MaxSpokenHeadChars)
        {
            var cut = head.LastIndexOf(' ', MaxSpokenHeadChars);
            head = head[..(cut > 40 ? cut : MaxSpokenHeadChars)].TrimEnd(',', ';', ':', ' ');
        }
        return items > 1 ? MessageTemplates.Get("spoken.list", head, items) : MessageTemplates.Get("spoken.more", head);
    }

    /// <summary>The words of a line without emoji, symbols or WhatsApp formatting marks (a speech model would read them out or stumble on them).</summary>
    private static string SpeechText(string line)
    {
        var kept = new StringBuilder();
        foreach (var rune in line.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.OtherSymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.Format or UnicodeCategory.EnclosingMark
                || rune.Value is '*' or '_' or '`' or '~' or 0xFE0F) continue;
            kept.Append(rune.ToString());
        }
        return Regex.Replace(kept.ToString(), @"\s{2,}", " ").Trim();
    }
    /// <summary>Longest answer that is read aloud.</summary>
    internal const int MaxSpokenReplyChars = 300;

    /// <summary>Most lines an answer may have to be read aloud (a list is text).</summary>
    internal const int MaxSpokenReplyLines = 4;

    /// <summary>True for an answer short enough to be a voice note: few characters and lines, and no link.</summary>
    internal static bool IsSpeakable(string text) =>
        text.Length <= MaxSpokenReplyChars
        && text.Count(c => c == '\n') < MaxSpokenReplyLines
        && !text.Contains("http", StringComparison.OrdinalIgnoreCase);

    /// <summary>Sends the turn's last text reply as a voice note. Called once, after a voice note has been handled.</summary>
    private async Task SpeakReplyAsync(string fromPhoneNumber, CancellationToken ct)
    {
        if (!_features.SpokenReplies || _speech is not { IsConfigured: true } speech) return;
        if (_lastReply is not { } text) return;
        var spoken = SpokenVersion(text);
        if (string.IsNullOrWhiteSpace(spoken)) return;
        var seller = await LoadOrCreateSellerAsync(fromPhoneNumber, ct);
        var audio = await speech.SynthesizeAsync(spoken, ct);
        if (audio is null) return;
        await _sender.SendVoiceNoteAsync(seller.WhatsAppPhoneNumber, audio, ct);
    }
}
