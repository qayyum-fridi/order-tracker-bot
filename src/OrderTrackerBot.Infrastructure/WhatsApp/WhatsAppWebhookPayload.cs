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
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("from")] public string? From { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("text")] public InboundText? Text { get; set; }
        [JsonPropertyName("interactive")] public InboundInteractive? Interactive { get; set; }
    }

    public class InboundInteractive
    {
        [JsonPropertyName("button_reply")] public InboundButtonReply? ButtonReply { get; set; }
        [JsonPropertyName("list_reply")] public InboundListReply? ListReply { get; set; }
        [JsonPropertyName("nfm_reply")] public InboundFlowReply? FlowReply { get; set; }
    }

    public class InboundFlowReply
    {
        [JsonPropertyName("response_json")] public string? ResponseJson { get; set; }
    }

    public class InboundListReply
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
    }

    public class InboundButtonReply
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
    }

    public class InboundText
    {
        [JsonPropertyName("body")] public string? Body { get; set; }
    }

    /// <summary>Every (from, text) pair for a "text" message found in this payload.</summary>
    public IEnumerable<(string From, string Text, string? MessageId)> ExtractTextMessages()
    {
        foreach (var entry in Entries)
        foreach (var change in entry.Changes)
        foreach (var message in change.Value?.Messages ?? Enumerable.Empty<InboundMessage>())
        {
            if (message.Type == "text" && message.From is not null && message.Text?.Body is not null)
                yield return (message.From, message.Text.Body, message.Id);
            else if (message.Type == "interactive" && message.From is not null && message.Interactive?.ButtonReply?.Title is not null)
                yield return (message.From, message.Interactive.ButtonReply.Title, message.Id);
            else if (message.Type == "interactive" && message.From is not null && message.Interactive?.ListReply?.Id is not null)
                yield return (message.From, message.Interactive.ListReply.Id, message.Id);
        }
    }

    /// <summary>Submitted WhatsApp Flow forms: the JSON payload the form's "complete" action produced.</summary>
    public IEnumerable<(string From, string ResponseJson, string? MessageId)> ExtractFlowSubmissions()
    {
        foreach (var entry in Entries)
        foreach (var change in entry.Changes)
        foreach (var message in change.Value?.Messages ?? Enumerable.Empty<InboundMessage>())
        {
            if (message.Type == "interactive" && message.From is not null && message.Interactive?.FlowReply?.ResponseJson is not null)
                yield return (message.From, message.Interactive.FlowReply.ResponseJson, message.Id);
        }
    }

    private static readonly HashSet<string> UnsupportedMediaTypes = new() { "audio", "image", "video", "document", "sticker" };

    /// <summary>Voice notes, screenshots and other media the bot can't read yet — the seller still deserves a reply.</summary>
    public IEnumerable<(string From, string Type, string? MessageId)> ExtractUnsupportedMessages()
    {
        foreach (var entry in Entries)
        foreach (var change in entry.Changes)
        foreach (var message in change.Value?.Messages ?? Enumerable.Empty<InboundMessage>())
        {
            if (message.From is not null && message.Type is not null && UnsupportedMediaTypes.Contains(message.Type))
                yield return (message.From, message.Type, message.Id);
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
