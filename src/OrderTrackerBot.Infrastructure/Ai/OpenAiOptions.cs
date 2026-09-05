namespace OrderTrackerBot.Infrastructure.Ai;

public class OpenAiOptions
{
    public const string SectionName = "OpenAi";

    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    /// <summary>Cheap, fast model is enough — this is the whole per-message cost driver (see spec's ~$1-2/month target).</summary>
    public string Model { get; set; } = "gpt-4o-mini";
}
