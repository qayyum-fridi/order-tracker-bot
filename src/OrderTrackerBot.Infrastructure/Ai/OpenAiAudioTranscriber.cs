using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderTrackerBot.Application.Abstractions;

namespace OrderTrackerBot.Infrastructure.Ai;

/// <summary>
/// OpenAI audio transcription for seller voice notes (ported from PR #9). Returns null on any failure so the seller gets
/// the "send it as text" reply. The prompt nudges the model towards the seller's register (Roman Urdu/English, digits for phones).
/// </summary>
public class OpenAiAudioTranscriber : IAudioTranscriber
{
    private readonly HttpClient _httpClient;
    private readonly OpenAiOptions _options;
    private readonly ILogger<OpenAiAudioTranscriber> _logger;

    public OpenAiAudioTranscriber(HttpClient httpClient, IOptions<OpenAiOptions> options, ILogger<OpenAiAudioTranscriber> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    // The speech model reads at most ~224 tokens of prompt; the style hint comes first, names fill what is left.
    private const int MaxPromptChars = 600;

    internal static string BuildPrompt(string stylePrompt, IReadOnlyList<string>? vocabulary)
    {
        var prompt = stylePrompt ?? "";
        if (vocabulary is null || vocabulary.Count == 0) return prompt;

        var names = new List<string>();
        var length = prompt.Length + " Names: ".Length;
        foreach (var name in vocabulary.Select(v => v?.Trim()).Where(v => !string.IsNullOrEmpty(v)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (length + name!.Length + 2 > MaxPromptChars) break;
            names.Add(name);
            length += name.Length + 2;
        }
        return names.Count == 0 ? prompt : $"{prompt} Names: {string.Join(", ", names)}.";
    }

    public async Task<string?> TranscribeAsync(byte[] audio, string mimeType, IReadOnlyList<string>? vocabulary, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return null;

        try
        {
            // WhatsApp voice notes are "audio/ogg; codecs=opus"; OpenAI infers the format from the file extension.
            var extension = mimeType.Contains("ogg") ? "ogg" : mimeType.Contains("mpeg") ? "mp3" : mimeType.Contains("mp4") || mimeType.Contains("aac") ? "m4a" : "ogg";
            using var form = new MultipartFormDataContent
            {
                { new StringContent(_options.TranscriptionModel), "model" },
                { new ByteArrayContent(audio), "file", $"voice.{extension}" }
            };
            var prompt = BuildPrompt(_options.TranscriptionPrompt, vocabulary);
            if (!string.IsNullOrWhiteSpace(prompt)) form.Add(new StringContent(prompt), "prompt");

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/audio/transcriptions") { Content = form };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<TranscriptionResult>(cancellationToken: cancellationToken);
            return string.IsNullOrWhiteSpace(result?.Text) ? null : result.Text.Trim();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Voice note transcription failed");
            return null;
        }
    }

    private sealed class TranscriptionResult
    {
        [JsonPropertyName("text")] public string? Text { get; set; }
    }
}
