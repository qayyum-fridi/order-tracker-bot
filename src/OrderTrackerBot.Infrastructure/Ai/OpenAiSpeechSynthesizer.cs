using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderTrackerBot.Application.Abstractions;

namespace OrderTrackerBot.Infrastructure.Ai;

/// <summary>
/// OpenAI text-to-speech for spoken replies. Returns null on any failure, so the typed reply is the fallback.
/// Output is Ogg/Opus, the format WhatsApp plays as a voice note.
/// </summary>
public class OpenAiSpeechSynthesizer : ISpeechSynthesizer
{
    // The speech endpoint accepts at most 4096 characters; a longer reply is never spoken.
    private const int MaxInputChars = 4096;

    private readonly HttpClient _httpClient;
    private readonly OpenAiOptions _options;
    private readonly ILogger<OpenAiSpeechSynthesizer> _logger;

    public OpenAiSpeechSynthesizer(HttpClient httpClient, IOptions<OpenAiOptions> options, ILogger<OpenAiSpeechSynthesizer> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public async Task<byte[]?> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(text) || text.Length > MaxInputChars) return null;

        try
        {
            var body = new JsonObject
            {
                ["model"] = _options.SpeechModel,
                ["voice"] = _options.SpeechVoice,
                ["input"] = text,
                ["response_format"] = "opus",
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/audio/speech") { Content = JsonContent.Create(body) };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return audio.Length == 0 ? null : audio;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Speech synthesis failed; the typed reply is sent alone.");
            return null;
        }
    }
}
