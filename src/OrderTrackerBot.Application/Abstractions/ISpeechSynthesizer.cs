namespace OrderTrackerBot.Application.Abstractions;

/// <summary>
/// Turns a short reply into a voice note the seller can play. Returns null when speech is not configured or fails;
/// callers always send the typed text as well, so a failed voice note loses nothing.
/// </summary>
public interface ISpeechSynthesizer
{
    bool IsConfigured { get; }

    /// <summary>Audio bytes (Ogg/Opus, playable as a WhatsApp voice note) for <paramref name="text"/>; null on any failure.</summary>
    Task<byte[]?> SynthesizeAsync(string text, CancellationToken cancellationToken = default);
}
