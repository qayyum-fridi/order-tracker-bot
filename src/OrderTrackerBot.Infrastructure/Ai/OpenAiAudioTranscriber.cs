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

    // gpt-transcribe / gpt-live-transcribe take the seller's names as separate keyword hints and the expected languages;
    // whisper-1 only has the free-text prompt.
    private bool UsesHints => _options.TranscriptionModel.Contains("transcribe", StringComparison.OrdinalIgnoreCase)
                              && _options.TranscriptionModel.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)
                              && !_options.TranscriptionModel.StartsWith("gpt-4o", StringComparison.OrdinalIgnoreCase);

    private const int MaxKeywords = 50;
    private const int MaxKeywordChars = 40;

    private MultipartFormDataContent BuildForm(byte[] audio, string extension, IReadOnlyList<string>? vocabulary, bool withHints)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(_options.TranscriptionModel), "model" },
            { new ByteArrayContent(audio), "file", $"voice.{extension}" }
        };

        var hinted = UsesHints && withHints;
        var prompt = hinted ? _options.TranscriptionPrompt : BuildPrompt(_options.TranscriptionPrompt, vocabulary);
        if (!string.IsNullOrWhiteSpace(prompt)) form.Add(new StringContent(prompt), "prompt");

        if (hinted)
        {
            foreach (var keyword in (vocabulary ?? Array.Empty<string>()).Select(v => v?.Trim()).Where(v => !string.IsNullOrEmpty(v) && v.Length <= MaxKeywordChars)
                         .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxKeywords))
                form.Add(new StringContent(keyword!), "keywords[]");
            foreach (var language in _options.TranscriptionLanguages.Where(l => !string.IsNullOrWhiteSpace(l)))
                form.Add(new StringContent(language.Trim()), "languages[]");
        }
        return form;
    }

    public async Task<string?> TranscribeAsync(byte[] audio, string mimeType, IReadOnlyList<string>? vocabulary, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return null;

        try
        {
            // WhatsApp voice notes are "audio/ogg; codecs=opus"; OpenAI infers the format from the file extension.
            var extension = mimeType.Contains("ogg") ? "ogg" : mimeType.Contains("mpeg") ? "mp3" : mimeType.Contains("mp4") || mimeType.Contains("aac") ? "m4a" : "ogg";

            var response = await SendAsync(BuildForm(audio, extension, vocabulary, withHints: true), cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest && UsesHints)
            {
                // The keyword/language fields are new; if the API rejects them the voice note is still worth transcribing without hints.
                _logger.LogWarning("Transcription with keyword/language hints was rejected ({Body}); retrying without hints",
                    await response.Content.ReadAsStringAsync(cancellationToken));
                response.Dispose();
                response = await SendAsync(BuildForm(audio, extension, vocabulary, withHints: false), cancellationToken);
            }

            using (response)
            {
                response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadFromJsonAsync<TranscriptionResult>(cancellationToken: cancellationToken);
                return string.IsNullOrWhiteSpace(result?.Text) ? null : result.Text.Trim();
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Voice note transcription failed");
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(MultipartFormDataContent form, CancellationToken cancellationToken)
    {
        using (form)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/audio/transcriptions") { Content = form };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);
            return await _httpClient.SendAsync(request, cancellationToken);
        }
    }

    private sealed class TranscriptionResult
    {
        [JsonPropertyName("text")] public string? Text { get; set; }
    }
}
