using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace OrderTrackerBot.Infrastructure.Backup;

/// <summary>Google Drive v3 over plain HTTP (no SDK): OAuth refresh-token exchange, multipart upload, list, download, delete.</summary>
public sealed class GoogleDriveBackupStorage : IBackupStorage, IDriveLogStore
{
    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string FilesUrl = "https://www.googleapis.com/drive/v3/files";
    private const string UploadUrl = "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields=id";

    private readonly HttpClient _http;
    private readonly BackupOptions _options;
    private string? _accessToken;
    private DateTime _accessTokenExpiresUtc;

    public GoogleDriveBackupStorage(HttpClient http, IOptions<BackupOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public async Task UploadAsync(string name, Stream content, CancellationToken cancellationToken)
    {
        var metadata = JsonSerializer.Serialize(new { name, parents = new[] { _options.FolderId } });
        using var body = new MultipartContent("related");
        body.Add(new StringContent(metadata, Encoding.UTF8, "application/json"));
        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
        body.Add(file);

        using var request = await AuthorizedAsync(HttpMethod.Post, UploadUrl, cancellationToken);
        request.Content = body;
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "upload", cancellationToken);
    }

    public async Task<IReadOnlyList<BackupFile>> ListNewestFirstAsync(CancellationToken cancellationToken)
    {
        var query = $"'{_options.FolderId}' in parents and trashed = false and name contains '{BackupOptions.FilePrefix}'";
        var url = $"{FilesUrl}?q={Uri.EscapeDataString(query)}&orderBy=createdTime%20desc&pageSize=1000&fields=files(id,name,createdTime)";

        using var request = await AuthorizedAsync(HttpMethod.Get, url, cancellationToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "list", cancellationToken);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var files = new List<BackupFile>();
        foreach (var f in json.RootElement.GetProperty("files").EnumerateArray())
        {
            var name = f.GetProperty("name").GetString() ?? "";
            if (!name.StartsWith(BackupOptions.FilePrefix, StringComparison.Ordinal)) continue;
            files.Add(new BackupFile(
                f.GetProperty("id").GetString()!, name,
                DateTimeOffset.Parse(f.GetProperty("createdTime").GetString()!, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime));
        }
        return files.OrderByDescending(f => f.CreatedUtc).ToList();
    }

    public async Task DownloadAsync(BackupFile file, Stream destination, CancellationToken cancellationToken)
    {
        using var request = await AuthorizedAsync(HttpMethod.Get, $"{FilesUrl}/{Uri.EscapeDataString(file.Id)}?alt=media", cancellationToken);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, "download", cancellationToken);
        await response.Content.CopyToAsync(destination, cancellationToken);
    }

    public async Task DeleteAsync(BackupFile file, CancellationToken cancellationToken)
    {
        using var request = await AuthorizedAsync(HttpMethod.Delete, $"{FilesUrl}/{Uri.EscapeDataString(file.Id)}", cancellationToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "delete", cancellationToken);
    }

    private string? _logFolderId;

    public async Task UploadOrReplaceAsync(string folderName, string fileName, string localPath, CancellationToken cancellationToken)
    {
        var folderId = _logFolderId ??= await FindOrCreateFolderAsync(folderName, cancellationToken);
        var bytes = await File.ReadAllBytesAsync(localPath, cancellationToken);
        var existingId = await FindIdAsync($"'{folderId}' in parents and name = '{fileName}' and trashed = false", cancellationToken);

        if (existingId is not null)
        {
            using var update = await AuthorizedAsync(HttpMethod.Patch,
                $"https://www.googleapis.com/upload/drive/v3/files/{Uri.EscapeDataString(existingId)}?uploadType=media", cancellationToken);
            update.Content = new ByteArrayContent(bytes);
            update.Content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
            using var updated = await _http.SendAsync(update, cancellationToken);
            await EnsureSuccessAsync(updated, "log update", cancellationToken);
            return;
        }

        var metadata = JsonSerializer.Serialize(new { name = fileName, parents = new[] { folderId } });
        using var body = new MultipartContent("related");
        body.Add(new StringContent(metadata, Encoding.UTF8, "application/json"));
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        body.Add(file);
        using var create = await AuthorizedAsync(HttpMethod.Post, UploadUrl, cancellationToken);
        create.Content = body;
        using var created = await _http.SendAsync(create, cancellationToken);
        await EnsureSuccessAsync(created, "log upload", cancellationToken);
    }

    private async Task<string> FindOrCreateFolderAsync(string name, CancellationToken cancellationToken)
    {
        var existing = await FindIdAsync(
            $"'{_options.FolderId}' in parents and mimeType = 'application/vnd.google-apps.folder' and name = '{name}' and trashed = false", cancellationToken);
        if (existing is not null) return existing;

        var metadata = JsonSerializer.Serialize(new { name, mimeType = "application/vnd.google-apps.folder", parents = new[] { _options.FolderId } });
        using var request = await AuthorizedAsync(HttpMethod.Post, $"{FilesUrl}?fields=id", cancellationToken);
        request.Content = new StringContent(metadata, Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "folder create", cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return json.RootElement.GetProperty("id").GetString()!;
    }

    private async Task<string?> FindIdAsync(string query, CancellationToken cancellationToken)
    {
        using var request = await AuthorizedAsync(HttpMethod.Get,
            $"{FilesUrl}?q={Uri.EscapeDataString(query)}&pageSize=1&fields=files(id)", cancellationToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "find", cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var files = json.RootElement.GetProperty("files");
        return files.GetArrayLength() == 0 ? null : files[0].GetProperty("id").GetString();
    }

    private async Task<HttpRequestMessage> AuthorizedAsync(HttpMethod method, string url, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(cancellationToken));
        return request;
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_accessToken is not null && DateTime.UtcNow < _accessTokenExpiresUtc) return _accessToken;

        using var response = await _http.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["refresh_token"] = _options.RefreshToken,
            ["grant_type"] = "refresh_token"
        }), cancellationToken);
        await EnsureSuccessAsync(response, "token refresh", cancellationToken);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        _accessToken = json.RootElement.GetProperty("access_token").GetString()!;
        var seconds = json.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        _accessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, seconds - 120));
        return _accessToken;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string action, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"Google Drive {action} failed: {(int)response.StatusCode} {detail}");
    }
}
