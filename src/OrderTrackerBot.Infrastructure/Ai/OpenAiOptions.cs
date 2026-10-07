namespace OrderTrackerBot.Infrastructure.Ai;

public class OpenAiOptions
{
    public const string SectionName = "OpenAi";

    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    /// <summary>Cheap, fast model is enough — this is the whole per-message cost driver (see spec's ~$1-2/month target).</summary>
    public string Model { get; set; } = "gpt-4o-mini";
    /// <summary>Stronger model for understanding a voice transcript in context (Urdu meaning, not sounds); one call per voice note.</summary>
    public string VoiceModel { get; set; } = "gpt-4o";
    /// <summary>Speech-to-text model for voice notes.</summary>
    public string TranscriptionModel { get; set; } = "whisper-1";
    /// <summary>Style hint for transcription: the language mix and formats sellers use (names, quantities, phone digits).</summary>
    public string TranscriptionPrompt { get; set; } =
        "Pakistani seller order bot. Roman Urdu aur English. Ayesha, 2 lawn suit, 03001234567, Gulberg Lahore, delivery 250, advance 500. mark 3 shipped. orders today.";
}
