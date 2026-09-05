namespace OrderTrackerBot.Application.Abstractions;

public interface IWhatsAppSender
{
    Task SendTextMessageAsync(string toPhoneNumber, string text, CancellationToken cancellationToken = default);
}
