using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Infrastructure.Ai;
using Xunit;
using Xunit.Abstractions;

namespace OrderTrackerBot.Tests;

/// <summary>
/// The voice path end to end with real audio: each voice case is spoken with the bot's text-to-speech, transcribed with the bot's transcription
/// model (given the shop's names as a hint, as the bot does), and the transcript is read by the understanding step. This measures speech-to-text
/// mistakes too, which the typed cases cannot. The speaking voice is synthetic, so it is only an approximation of a real seller's speech.
/// Skipped unless OTB_AUDIO_EVAL=1 and OPENAI_API_KEY are set:
/// <c>OTB_AUDIO_EVAL=1 OPENAI_API_KEY=... dotnet test --filter TurnAudioEval --logger "console;verbosity=detailed"</c>
/// </summary>
public class TurnAudioEval
{
    private const double PassRate = 0.85;
    private readonly ITestOutputHelper _output;
    public TurnAudioEval(ITestOutputHelper output) => _output = output;

    private static readonly string[] ShopNames = { "Sara Khan", "Ali Raza", "Bilal Ahmed", "Zainab", "Fatima", "Usman", "shirt", "kurti", "shawl", "dupatta" };

    /// <summary>Every voice case from the mix and the hard-case sets: they are typed text today, spoken here.</summary>
    internal static IReadOnlyList<(string Message, string[] Actions)> VoiceCases() =>
        TurnMixEval.Mix.Where(r => r.Voice).Select(r => (r.Message, r.Actions))
            .Concat(TurnRealWorldEval.Cases.Where(c => c.Voice).Select(c => (c.Message, c.Actions)))
            .ToList();

    [Fact]
    public async Task Spoken_cases_are_transcribed_and_understood_by_the_real_models()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (Environment.GetEnvironmentVariable("OTB_AUDIO_EVAL") != "1" || string.IsNullOrWhiteSpace(key)) return;
        var options = new OpenAiOptions { ApiKey = key };
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        var speech = new OpenAiSpeechSynthesizer(http, Options.Create(options), NullLogger<OpenAiSpeechSynthesizer>.Instance);
        var transcriber = new OpenAiAudioTranscriber(http, Options.Create(options), NullLogger<OpenAiAudioTranscriber>.Instance);
        var understanding = new OpenAiTurnInterpreter(http, Options.Create(options), NullLogger<OpenAiTurnInterpreter>.Instance);

        var cases = VoiceCases();
        var gate = new SemaphoreSlim(4);
        var results = await Task.WhenAll(cases.Select(async c =>
        {
            await gate.WaitAsync();
            try
            {
                var audio = await speech.SynthesizeAsync(c.Message);
                if (audio is null) return (c.Message, Transcript: (string?)null, Right: false, Reason: "speech failed");
                var transcript = await transcriber.TranscribeAsync(audio, "audio/ogg", ShopNames);
                if (string.IsNullOrWhiteSpace(transcript)) return (c.Message, Transcript: transcript, Right: false, Reason: "no transcript");
                var context = new TurnContext
                {
                    BusinessName = "Ayesha Collection", ShopType = "Clothing", CatalogSize = 3, FromVoice = true,
                    Catalog = new List<AiCatalogItem>
                    {
                        new() { Name = "Shirt", Price = 1800 }, new() { Name = "Kurti", Price = 1200 }, new() { Name = "Shawl", Price = 2500 },
                    },
                    KnownCustomers = new[] { "Sara Khan (03004445566, Model Town Lahore)", "Ali Raza (03215557788, Saddar Karachi)" },
                };
                var action = await understanding.UnderstandAsync(context, transcript);
                var right = action is not null && c.Actions.Contains(action.Action);
                return (c.Message, Transcript: transcript, Right: right, Reason: action?.Action ?? "nothing");
            }
            finally { gate.Release(); }
        }));

        foreach (var r in results.Where(r => !r.Right))
            _output.WriteLine($"WRONG  said \"{r.Message}\"\n       heard \"{r.Transcript ?? "(none)"}\" -> {r.Reason}");
        var right = results.Count(r => r.Right);
        var heardExact = results.Count(r => r.Transcript is not null && string.Equals(r.Transcript.Trim(), r.Message.Trim(), StringComparison.OrdinalIgnoreCase));
        _output.WriteLine($"\nAudio: {right}/{results.Length} understood ({100.0 * right / results.Length:0}%); transcripts identical to the text: {heardExact}");
        Assert.True(right >= results.Length * PassRate, $"{right}/{results.Length} understood, below {PassRate:P0}");
    }

    // Offline: there is a usable set of spoken cases.
    [Fact]
    public void Voice_cases_are_available_for_the_audio_eval()
    {
        Assert.True(VoiceCases().Count >= 40);
    }
}
