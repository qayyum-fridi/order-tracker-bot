using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Infrastructure.Ai;
using Xunit;
using Xunit.Abstractions;

namespace OrderTrackerBot.Tests;

/// <summary>
/// Measures the yes / no meaning step against the REAL model. Skipped unless OTB_VOICE_EVAL=1 and OPENAI_API_KEY are set, so the normal run stays offline:
/// <c>OTB_VOICE_EVAL=1 OPENAI_API_KEY=... dotnet test --filter VoiceYesNoEval --logger "console;verbosity=detailed"</c>
/// Add real transcripts to <see cref="Cases"/> (expected: yes / no / other) to grow the set.
/// </summary>
public class VoiceYesNoEval
{
    private const string ResetQ = "⚠️ Yeh aapka poora data (orders, products, customers) delete kar dega aur setup dobara shuru hoga. Pakka? YES ya NO likhein.";
    private const string DraftQ = "📝 Confirm order: Customer: Sara Khan | 2x Kurti - Rs.4,200 | Total: Rs.4,200. Save ya Cancel?";

    // (question, spoken answer, expected). "other" = a different request, a question, both, or unclear.
    private static readonly (string Q, string A, string Expected)[] Cases =
    {
        (ResetQ, "ہاں", "yes"),
        (ResetQ, "haan", "yes"),
        (ResetQ, "او بھائی میں نے ہاں بولا تھا", "yes"),
        (ResetQ, "oye haanji bilkul kar do", "yes"),
        (ResetQ, "ji ji kar do", "yes"),
        (ResetQ, "pakka kar de yaar", "yes"),
        (ResetQ, "chal theek hai karo", "yes"),
        (ResetQ, "haan haan bhai bol to diya", "yes"),
        (ResetQ, "yes please", "yes"),
        (ResetQ, "ਹਾਂ ਜੀ", "yes"),
        (ResetQ, "haanji kar deyo", "yes"),
        (ResetQ, "ho ja bhai", "yes"),
        (ResetQ, "ہاں جی کر دیں", "yes"),
        (ResetQ, "kal se bhaiya bolunga par abhi haan", "yes"),
        (DraftQ, "ok done save kar do", "yes"),
        (DraftQ, "ہاں بالکل", "yes"),
        (DraftQ, "bilkul sahi hai", "yes"),
        (DraftQ, "bhai karo karo", "yes"),
        (DraftQ, "oye beta haan", "yes"),
        (DraftQ, "ਠੀਕ ਹੈ ਕਰ ਦਿਓ", "yes"),
        (ResetQ, "نہیں", "no"),
        (ResetQ, "nahi", "no"),
        (ResetQ, "nahin yaar rehne do", "no"),
        (ResetQ, "ruko mat karo", "no"),
        (ResetQ, "oye nai nai", "no"),
        (ResetQ, "na ji na", "no"),
        (ResetQ, "nahi bhai abhi nahi", "no"),
        (ResetQ, "ਨਹੀਂ ਜੀ", "no"),
        (ResetQ, "chhodo isko", "no"),
        (DraftQ, "cancel kar do", "no"),
        (DraftQ, "mat karo bhai", "no"),
        (DraftQ, "نہیں نہیں رہنے دیں", "no"),
        (DraftQ, "haan par price badlo", "other"),
        (DraftQ, "pehle yeh batao delivery kab hogi", "other"),
        (DraftQ, "mujhe samajh nahi aa raha kya bolun", "other"),
        (DraftQ, "orders today dikhao", "other"),
        (DraftQ, "haan nahi pata nahi", "other"),
        (ResetQ, "kitna hua", "other"),
        (ResetQ, "oye bhai", "other"),
        (ResetQ, "ہاں مگر پہلے ایڈریس بدلو", "other"),
    };

    private readonly ITestOutputHelper _output;
    public VoiceYesNoEval(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task MeasureAccuracyAgainstTheRealModel()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (Environment.GetEnvironmentVariable("OTB_VOICE_EVAL") != "1" || string.IsNullOrWhiteSpace(key)) return;

        var assistant = new OpenAiOrderAssistant(new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
            Options.Create(new OpenAiOptions { ApiKey = key }), NullLogger<OpenAiOrderAssistant>.Instance, Mock.Of<IIssueReporter>());

        var right = 0;
        var wrongYesNo = 0;   // yes<->no swaps are the dangerous ones: a wrong NO drops a draft, a wrong YES confirms a reset
        foreach (var (q, a, expected) in Cases)
        {
            var got = await assistant.ClassifyYesNoAsync(q, a) ?? "unavailable";
            var ok = got == expected;
            if (ok) right++;
            else if (got is "yes" or "no" && expected is "yes" or "no") wrongYesNo++;
            _output.WriteLine($"{(ok ? "OK  " : "MISS")} expected={expected,-5} got={got,-11} | {a}");
        }
        _output.WriteLine($"ACCURACY {right}/{Cases.Length} ({100.0 * right / Cases.Length:F0}%), yes<->no swaps: {wrongYesNo}");
        Assert.Equal(0, wrongYesNo);
    }
}
