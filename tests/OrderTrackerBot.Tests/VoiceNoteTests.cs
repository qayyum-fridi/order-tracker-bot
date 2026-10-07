using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Infrastructure.Ai;
using OrderTrackerBot.Infrastructure.WhatsApp;
using Xunit;

namespace OrderTrackerBot.Tests;

public class VoiceNoteTests
{
    [Fact]
    public void Payload_ExtractsVoiceNotes_AndNoLongerTreatsThemAsUnsupported()
    {
        const string json = """
            {"entry":[{"changes":[{"value":{"messages":[
              {"from":"923001234567","id":"wamid.V","type":"audio","audio":{"id":"media-9","mime_type":"audio/ogg; codecs=opus"}},
              {"from":"923001234567","id":"wamid.S","type":"sticker"}
            ]}}]}]}
            """;
        var payload = JsonSerializer.Deserialize<WhatsAppWebhookPayload>(json)!;

        Assert.Equal(("923001234567", "media-9", (string?)"wamid.V"), Assert.Single(payload.ExtractAudioMessages()));
        Assert.Equal("sticker", Assert.Single(payload.ExtractUnsupportedMessages()).Type);
    }

    private sealed class FakeTranscriptionApi(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }
        public string? Url { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri!.ToString();
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    private static OpenAiAudioTranscriber Transcriber(HttpMessageHandler handler, string apiKey = "k") =>
        new(new HttpClient(handler), Options.Create(new OpenAiOptions { ApiKey = apiKey }), NullLogger<OpenAiAudioTranscriber>.Instance);

    [Fact]
    public async Task Transcriber_SendsModelPromptAndOggFile_AndReturnsTrimmedText()
    {
        var api = new FakeTranscriptionApi(HttpStatusCode.OK, "{\"text\":\"  Ayesha 2 lawn suit 03001234567  \"}");

        var text = await Transcriber(api).TranscribeAsync(new byte[] { 1, 2, 3 }, "audio/ogg; codecs=opus", new[] { "Hassan Ali", "Khaddar Chadar" });

        Assert.Equal("Ayesha 2 lawn suit 03001234567", text);
        Assert.EndsWith("/audio/transcriptions", api.Url);
        Assert.Contains("whisper-1", api.RequestBody);
        Assert.Contains("voice.ogg", api.RequestBody);
        Assert.Contains("Roman Urdu", api.RequestBody); // the style prompt
        Assert.Contains("Names: Hassan Ali, Khaddar Chadar.", api.RequestBody); // the seller's own names as a hint
    }

    [Fact]
    public async Task Transcriber_WithoutKeyOrOnError_ReturnsNull()
    {
        Assert.False(Transcriber(new FakeTranscriptionApi(HttpStatusCode.OK, "{}"), apiKey: "").IsConfigured);
        Assert.Null(await Transcriber(new FakeTranscriptionApi(HttpStatusCode.InternalServerError, "oops")).TranscribeAsync(new byte[] { 1 }, "audio/ogg", null));
    }
}
