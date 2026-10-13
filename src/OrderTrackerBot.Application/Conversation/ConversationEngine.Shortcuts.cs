using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Quick-action buttons: three tappable shortcuts that follow a finished reply, so sellers never have to remember command words.
// Only when nothing is pending (state Idle), the turn replied, and the reply didn't already carry its own buttons/list.
// Button labels are real commands — a tapped button arrives as that text.
public partial class ConversationEngine
{
    // Guide is always the first button: it is the way back to the illustrated and step-by-step guides.
    private static string[] ShortcutLabels(string language) => Lang.Normalize(language) switch
    {
        Lang.UrduScript => new[] { "📖 گائیڈ", "➕ نیا آرڈر", "📦 آج کے آرڈرز" },
        Lang.English => new[] { "📖 Guide", "➕ New order", "📦 Orders today" },
        _ => new[] { "📖 Guide", "➕ Naya order", "📦 Orders today" }
    };

    private async Task TrySendShortcutBarAsync(string phone, CancellationToken ct)
    {
        if (!_features.ShortcutButtons) return;
        if (_turnSeller is not { OnboardingComplete: true, Session: { } session } seller || _turnCtx is not { } ctx) return;
        if (session.State != ConversationState.Idle || ctx.ShortcutsOff || _turn.Sent == 0 || _turn.Interactive) return;

        var urdu = Lang.Normalize(seller.PreferredLanguage) == Lang.UrduScript;
        if (_nextActions is { Count: > 0 } next)
        {
            // What the seller most likely wants after this very action (each label is a runnable command); the generic bar comes back on the next plain reply.
            await _sender.SendButtonsMessageAsync(phone, urdu ? "👉 اگلا قدم؟" : "👉 Agla qadam?", next, ct);
            await PersistAsync(session, ctx, ct);
            return;
        }
        var body = ctx.ShortcutIntroShown
            ? (urdu ? "⚡ فوری بٹن" : "⚡ Quick actions")
            : urdu
                ? "⚡ یہ بٹن ہر جواب کے بعد نیچے ملتے رہیں گے — بس دبائیں۔ بند کرنے کے لیے لکھیں: shortcut off"
                : "⚡ Yeh buttons har jawab ke neeche milte rahenge — bas dabayein.\nBand karne ke liye likhein: \"shortcut off\"";
        await _sender.SendButtonsMessageAsync(phone, body, ShortcutLabels(seller.PreferredLanguage), ct);

        ctx.ShortcutIntroShown = true;
        await PersistAsync(session, ctx, ct); // also saves the bar's message-log row
    }

    private async Task HandleShortcutsToggleAsync(Seller seller, SessionContextData ctx, string value, CancellationToken ct)
    {
        ctx.ShortcutsOff = value == "off";
        await ReplyAsync(seller, ctx.ShortcutsOff
            ? "✅ Shortcut buttons band kar diye. Dobara chalane ke liye likhein: \"shortcut on\""
            : "✅ Shortcut buttons chalu — ab har jawab ke neeche Guide / Naya order / Orders today milenge.", ct);
    }

    /// <summary>
    /// Offers up to three follow-up commands as buttons after this turn's reply. A button title is at most 20 characters, so a longer
    /// command is dropped rather than cut (a cut command would not run); an empty result keeps the generic bar.
    /// </summary>
    private void SuggestNext(params string[] commands) =>
        _nextActions = commands.Where(c => c.Length <= 20 && CommandParser.TryParse(c) is not null).Distinct().Take(3).ToList();
}
