namespace OrderTrackerBot.Application.Abstractions;

public interface IWhatsAppSender
{
    Task SendTextMessageAsync(string toPhoneNumber, string text, CancellationToken cancellationToken = default);

    /// <summary>Sends up to 3 quick-reply buttons; a tapped button arrives as a normal text message with the button's label.</summary>
    Task SendButtonsMessageAsync(string toPhoneNumber, string bodyText, IReadOnlyList<string> buttonLabels, CancellationToken cancellationToken = default);
}
