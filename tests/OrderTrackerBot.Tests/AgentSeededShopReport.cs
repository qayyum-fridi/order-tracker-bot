using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.Ai;
using Xunit;
using Xunit.Abstractions;

namespace OrderTrackerBot.Tests;

/// <summary>
/// The real-sentences report (<see cref="AgentRealReport"/>) on the SEEDED shop of <see cref="AgentPlanEval"/>, with a stress set of MADE-UP messy sentences
/// instead of a copy of the app database: typos, Urdu script, filler words, self-corrections, ambiguity, details that are not orders, and requests the planner has
/// no tool for. They are not real sellers' words and have no expected answers; the printed plans are for a person to judge. Local only (needs OPENAI_API_KEY):
/// <c>OTB_AGENT_SEEDED=1 OPENAI_API_KEY=... dotnet test --filter AgentSeededShopReport --logger "console;verbosity=detailed"</c>
/// </summary>
public class AgentSeededShopReport(ITestOutputHelper output)
{
    internal static readonly string[] Sentences =
    {
        // typos, filler, voice-like wording
        "Shirt ka pirce 2000 kar do",
        "oye bhai Cap add kar de 250 ka",
        "bhai Dupatta ka stock bees kar dena",
        "Polo shirt white 600 ka add karna hai",
        "Lawn Suit ki cost 2400 hai wese",
        "Hat 100 nahi 120 ka add karo",
        // orders in different shapes
        "Bilal bhai ko 3 Shirt bhejni hain, delivery 200",
        "Ayesha Raza ko ek Lawn Suit aur do Dupatta bhej dena",
        "Zainab ko 2 Shirt dena advance 500 aya hai",
        "Bilal ko 2 Shirt, address Model Town Multan",
        "Hamza ko 1 Polo Shirt bhejo phone 03001234567",
        "Sara Khan ko 1 Lawn Suit bhejna",
        "Sara ko 2 Shirt bhej do",
        "Bilal ko Shirt aur Dupatta dono bhej do 2 2",
        // Urdu script
        "بلال کو دو کرتی بھیج دیں",
        "ہیٹ سو روپے کا شامل کر دیں",
        "کرتی کی قیمت دو ہزار کر دیں",
        "مجھے تین پروڈکٹ شامل کرنے ہیں ایک ایک کر کے بھیجوں گا",
        // several tasks
        "pehle Cap 250 ka add karo phir Shirt ka price 1900 kar do phir Bilal ko 1 Cap bhejo",
        "Dupatta ka stock 10 kar do aur Lawn Suit ki cost 2300 kar do",
        "Hat 100 aur Cap 250 aur Belt 300 teeno add karo",
        // missing or unclear details
        "Belt add karo",
        "Shirt ka rate badal do",
        "ek order dena hai",
        "Jhumka ka price 300 kar do",
        "Bilal ko Jhumka bhej do",
        // not for the planner: reports, questions, cancel, delete, small talk
        "Shirt ka price kya hai",
        "aaj kitni sale hui hai bhai",
        "Bilal ka order cancel kar do",
        "Dupatta delete kar do catalog se",
        "shukriya bohat acha bot hai",
        "mera business bohat acha chal raha hai",
        "kal Sara ne 500 ka kharcha kiya packaging pe",
        // numbers and details that must not become something else
        "Cap 250 add karo uska phone number 03001234567 hai",
        "Shirt 1800 ki hai magar Bilal ko 1500 mein dena",
        "Bilal ko 2 Shirt bhej do 5 baje tak",
    };

    /// <summary>
    /// A beginner's day, as a non-technical seller writes it: vague, chatty, half a sentence, "that one from yesterday", relative price changes, money talk, the rider,
    /// shorthand, and plain confusion. Most of it is NOT for the planner (it has only add / edit / order tools): the question for each is whether it does something
    /// WRONG, not whether it does something. Made up, judged by hand.
    /// </summary>
    internal static readonly string[] BeginnerDay =
    {
        // taking orders the way people talk
        "Ali ne 2 suit mangwaye hain lahore bhejna hai",
        "wo Sara wali jo kal aayi thi usko 3 dupatte bhej do",
        "wohi pehle wala order dobara bhej do",
        "ek aur suit bhi daal do us order mein",
        "madam ne kaha 2 shirt aur chahiye",
        "bilal ko wohi dena jo pichli dafa liya tha",
        "3 shirt 1 dupatta Hira ke liye",
        "Hina bibi ka order likh lo 2 lawn suit 6400 total",
        "Bilal 3 shirt",
        "ek dozen shirt Bilal ko bhej do",
        "Hamza ko 12 shirt chahiye aaj hi",
        // looking after the catalog
        "naya maal aaya hai chappal 800 wali",
        "shirt khatam ho gayi hai",
        "shirt ka maal aur mangwana hai",
        "dupatta sasta kar do thora",
        "lawn suit pe 200 kam kar do",
        "shirt ka rate 100 barha do",
        "polo shirt ab 700 ki hai",
        "Shirt band kar do ab nahi bechni",
        "sab products ka price 10% barha do",
        "Shirt 1800 Dupatta 600 Lawn Suit 3200",
        "naya product Chappal",
        // money, rider, status: nothing for the planner to do
        "Ali ne paisay bhej diye",
        "Sara ka order bhej diya aaj TCS se",
        "Bilal ka order wapas aa gaya",
        "kal ka hisaab batao",
        "kitne order pending hain",
        "Hina ne 2000 advance diya hai",
        "mujhe udhar likhna hai Ali ka 1500",
        "rider 300 le gaya delivery ka",
        "pichle hafte kya bika sab se zyada",
        // chat, confusion, a customer's question reaching the seller's number
        "assalam o alaikum bhai ek kaam tha",
        "help karo mujhe samajh nahi aa raha kya karun",
        "yeh kaise chalta hai",
        "ok theek hai",
        "mera order kahan hai",
        "aap kon ho",
        "Ayesha ko bol do ke order kal milega",
        // women sellers, several things, half-said
        "3 jhumke add karne hain ek ek karke bhejti hun",
        "Cap 250 add karo aur wo Sara ko bhi bhej dena 2",
        "kuch bhi add karna ho to kaise karun",
        "Sara Khan ko Shirt",
        "shirt ki cost 900 hai",
        "shirt ka price 900 kar do",
    };

    [Fact]
    public async Task Report_OnTheSeededShop_WithABeginnersDay()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (Environment.GetEnvironmentVariable("OTB_AGENT_BEGINNER") != "1" || string.IsNullOrWhiteSpace(key)) return;
        await RunAsync(key!, BeginnerDay);
    }

    private async Task RunAsync(string key, string[] sentences)
    {
        using var factory = new TestDbContextFactory();
        using var db = factory.CreateContext();
        var (seller, _) = await AgentPlanEval.SeedAsync(db);
        // oldest first, so the report's "newest first" pick returns them in the order written here
        foreach (var s in sentences.Reverse()) db.MessageLogs.Add(new MessageLog { Phone = seller.WhatsAppPhoneNumber, Direction = "inbound", RawText = s });
        await db.SaveChangesAsync();

        var assistant = new OpenAiOrderAssistant(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, Options.Create(new OpenAiOptions { ApiKey = key }),
            NullLogger<OpenAiOrderAssistant>.Instance, Mock.Of<IIssueReporter>());
        var rows = await AgentRealReport.RunAsync(db, assistant, 100);
        foreach (var r in rows)
            output.WriteLine($"{r.Sentence}\n          {r.Plan}  {r.Verdicts}{(r.Question is null ? "" : "  ASKS: " + r.Question)}  ({r.Ms} ms)");
        output.WriteLine(AgentRealReport.Summary(rows));
        output.WriteLine($"(sentences the parser claims are not sent to the planner: {sentences.Length - rows.Count} of {sentences.Length})");
        if (Environment.GetEnvironmentVariable("OTB_AGENT_REAL_REPORT") is { Length: > 0 } outFile)
            File.WriteAllText(outFile, "\uFEFF" + AgentRealReport.ToCsv(rows), new UTF8Encoding(false));
    }

    [Fact]
    public async Task Report_OnTheSeededShop_WithAStressSetOfMadeUpSentences()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (Environment.GetEnvironmentVariable("OTB_AGENT_SEEDED") != "1" || string.IsNullOrWhiteSpace(key)) return;

        await RunAsync(key!, Sentences);
    }
}
