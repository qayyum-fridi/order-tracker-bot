using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderTrackerBot.Application.Abstractions;

namespace OrderTrackerBot.Infrastructure.WhatsApp;

/// <summary>Two-step Cloud API media download: resolve the media id to a short-lived URL, then fetch it (both need the bearer token).</summary>
public class WhatsAppMediaClient : IWhatsAppMediaClient
{
    private const long MaxBytes = 5 * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly WhatsAppOptions _options;
    private readonly ILogger<WhatsAppMediaClient> _logger;

    public WhatsAppMediaClient(HttpClient httpClient, IOptions<WhatsAppOptions> options, ILogger<WhatsAppMediaClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<(byte[] Bytes, string MimeType)?> DownloadAsync(string mediaId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.AccessToken)) return null;

        try
        {
            using var metaRequest = new HttpRequestMessage(HttpMethod.Get, $"{_options.GraphApiBaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(mediaId)}");
            metaRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.AccessToken);
            using var metaResponse = await _httpClient.SendAsync(metaRequest, cancellationToken);
            metaResponse.EnsureSuccessStatusCode();
            var meta = await metaResponse.Content.ReadFromJsonAsync<MediaInfo>(cancellationToken: cancellationToken);
            if (meta?.Url is null) return null;
            if (meta.FileSize > MaxBytes) return null;

            using var fileRequest = new HttpRequestMessage(HttpMethod.Get, meta.Url);
            fileRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.AccessToken);
            using var fileResponse = await _httpClient.SendAsync(fileRequest, cancellationToken);
            fileResponse.EnsureSuccessStatusCode();
            var bytes = await fileResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            return (bytes, meta.MimeType ?? "image/jpeg");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download WhatsApp media {MediaId}", mediaId);
            return null;
        }
    }

    private sealed class MediaInfo
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("mime_type")] public string? MimeType { get; set; }
        [JsonPropertyName("file_size")] public long FileSize { get; set; }
    }
}
