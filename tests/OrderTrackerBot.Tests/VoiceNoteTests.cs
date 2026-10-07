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

    private static OpenAiAudioTranscriber Transcriber(HttpMessageHandler handler, string apiKey = "k", string model = "whisper-1") =>
        new(new HttpClient(handler), Options.Create(new OpenAiOptions { ApiKey = apiKey, TranscriptionModel = model }), NullLogger<OpenAiAudioTranscriber>.Instance);

    [Fact]
    public async Task Transcriber_Whisper_SendsModelPromptAndOggFile_AndReturnsTrimmedText()
    {
        var api = new FakeTranscriptionApi(HttpStatusCode.OK, "{\"text\":\"  Ayesha 2 lawn suit 03001234567  \"}");

        var text = await Transcriber(api).TranscribeAsync(new byte[] { 1, 2, 3 }, "audio/ogg; codecs=opus", new[] { "Hassan Ali", "Khaddar Chadar" });

        Assert.Equal("Ayesha 2 lawn suit 03001234567", text);
        Assert.EndsWith("/audio/transcriptions", api.Url);
        Assert.Contains("whisper-1", api.RequestBody);
        Assert.Contains("voice.ogg", api.RequestBody);
        Assert.Contains("Roman Urdu", api.RequestBody); // the style prompt
        Assert.Contains("Names: Hassan Ali, Khaddar Chadar.", api.RequestBody); // the seller's own names as a hint
        Assert.DoesNotContain("keywords[]", api.RequestBody);
    }

    private static int CountOf(string text, string needle) => text.Split(needle).Length - 1;

    [Fact]
    public async Task Transcriber_GptTranscribe_SendsNamesAsKeywords_AndUrduEnglishLanguageHints()
    {
        var api = new FakeTranscriptionApi(HttpStatusCode.OK, "{\"text\":\"Boski 5000\"}");

        var text = await Transcriber(api, model: "gpt-transcribe").TranscribeAsync(new byte[] { 1 }, "audio/ogg", new[] { "Boski", "Hassan Ali", "boski" });

        Assert.Equal("Boski 5000", text);
        Assert.Contains("gpt-transcribe", api.RequestBody);
        Assert.Equal(2, CountOf(api.RequestBody!, "keywords[]")); // "Boski" and "boski" are one keyword
        Assert.Equal(2, CountOf(api.RequestBody!, "languages[]")); // ur, en
        Assert.DoesNotContain("Names:", api.RequestBody); // names are keywords here, not prompt text
    }

    private sealed class RejectHintsThenAccept : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            return body.Contains("keywords[]")
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("unknown parameter") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"text\":\"theek hai\"}", System.Text.Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Transcriber_GptTranscribe_RetriesWithoutHints_WhenTheApiRejectsThem()
    {
        var api = new RejectHintsThenAccept();

        var text = await Transcriber(api, model: "gpt-transcribe").TranscribeAsync(new byte[] { 1 }, "audio/ogg", new[] { "Boski" });

        Assert.Equal("theek hai", text);
        Assert.Equal(2, api.Bodies.Count);
        Assert.DoesNotContain("keywords[]", api.Bodies[1]);
        Assert.Contains("Names: Boski.", api.Bodies[1]); // the names still reach the model, through the prompt
    }

    [Fact]
    public async Task Transcriber_WithoutKeyOrOnError_ReturnsNull()
    {
        Assert.False(Transcriber(new FakeTranscriptionApi(HttpStatusCode.OK, "{}"), apiKey: "").IsConfigured);
        Assert.Null(await Transcriber(new FakeTranscriptionApi(HttpStatusCode.InternalServerError, "oops")).TranscribeAsync(new byte[] { 1 }, "audio/ogg", null));
    }
}
