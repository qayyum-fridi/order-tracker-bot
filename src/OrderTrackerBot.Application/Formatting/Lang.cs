namespace OrderTrackerBot.Application.Formatting;

public static class Lang
{
    public const string RomanUrdu = "roman_urdu";
    public const string English = "english";
    public const string UrduScript = "urdu_script";

    public static readonly string[] ButtonLabels = { "Roman Urdu", "English", "اردو" };

    public static string Resolve(string raw)
    {
        var text = raw.Trim().ToLowerInvariant();
        if (text.Contains("اردو") || text.Contains("urdu script")) return UrduScript;
        if (text.Contains("english")) return English;
        return RomanUrdu;
    }

    public static string Normalize(string? stored) => stored switch
    {
        UrduScript => UrduScript,
        English => English,
        _ => RomanUrdu
    };
}
