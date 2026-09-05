using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace OrderTrackerBot.Infrastructure.WhatsApp;

// Minimal shape of Meta's Cloud API webhook payload — only what the bot needs (inbound text messages).
public class WhatsAppWebhookPayload
{
    [JsonPropertyName("entry")] public List<Entry> Entries { get; set; } = new();

    public class Entry
    {
        [JsonPropertyName("changes")] public List<Change> Changes { get; set; } = new();
    }

    public class Change
    {
        [JsonPropertyName("value")] public ChangeValue? Value { get; set; }
    }

    public class ChangeValue
    {
        [JsonPropertyName("messages")] public List<InboundMessage>? Messages { get; set; }
    }

    public class InboundMessage
    {
        [JsonPropertyName("from")] public string? From { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("text")] public InboundText? Text { get; set; }
    }

    public class InboundText
    {
        [JsonPropertyName("body")] public string? Body { get; set; }
    }

    /// <summary>Every (from, text) pair for a "text" message found in this payload.</summary>
    public IEnumerable<(string From, string Text)> ExtractTextMessages()
    {
        foreach (var entry in Entries)
        foreach (var change in entry.Changes)
        foreach (var message in change.Value?.Messages ?? Enumerable.Empty<InboundMessage>())
        {
            if (message.Type == "text" && message.From is not null && message.Text?.Body is not null)
                yield return (message.From, message.Text.Body);
        }
    }
}

public static class WhatsAppSignatureValidator
{
    /// <summary>Verifies the X-Hub-Signature-256 header Meta sends with every webhook POST.</summary>
    public static bool IsValid(string appSecret, byte[] requestBody, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(appSecret)) return true; // not configured — skip (dev-only)
        if (string.IsNullOrEmpty(signatureHeader) || !signatureHeader.StartsWith("sha256=")) return false;

        var expectedHex = signatureHeader["sha256=".Length..];
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
        var computed = hmac.ComputeHash(requestBody);
        var computedHex = Convert.ToHexString(computed).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(computedHex), Encoding.UTF8.GetBytes(expectedHex));
    }
}
