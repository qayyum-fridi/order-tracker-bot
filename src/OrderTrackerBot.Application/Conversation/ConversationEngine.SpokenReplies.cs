namespace OrderTrackerBot.Application.Conversation;

// Spoken replies (Features:SpokenReplies): when the seller's message was a voice note, the bot's answer is also sent back as a voice note,
// after the typed text. Only short answers are read aloud; a list, a long summary or a link stays text. A failed speech call sends nothing extra.
public partial class ConversationEngine
{
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
        if (_lastReply is not { } text || !IsSpeakable(text)) return;
        var seller = await LoadOrCreateSellerAsync(fromPhoneNumber, ct);
        var audio = await speech.SynthesizeAsync(text, ct);
        if (audio is null) return;
        await _sender.SendVoiceNoteAsync(seller.WhatsAppPhoneNumber, audio, ct);
    }
}
