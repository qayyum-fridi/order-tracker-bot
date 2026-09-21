namespace OrderTrackerBot.Application.Abstractions;

/// <summary>A tappable list row. <paramref name="Id"/> is sent back as the seller's message text, so use a valid typed command.</summary>
public sealed record MenuRow(string Id, string Title);

public sealed record MenuSection(string Title, IReadOnlyList<MenuRow> Rows);

public interface IWhatsAppSender
{
    /// <summary>Sends a list message (max 10 rows total); the tapped row's Id arrives as a normal text message.</summary>
    Task SendListMessageAsync(string toPhoneNumber, string bodyText, string buttonLabel, IReadOnlyList<MenuSection> sections, CancellationToken cancellationToken = default);

    Task SendTextMessageAsync(string toPhoneNumber, string text, CancellationToken cancellationToken = default);

    /// <summary>Sends up to 3 quick-reply buttons; a tapped button arrives as a normal text message with the button's label.</summary>
    Task SendButtonsMessageAsync(string toPhoneNumber, string bodyText, IReadOnlyList<string> buttonLabels, CancellationToken cancellationToken = default);
}
