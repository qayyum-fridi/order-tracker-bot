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
        "Kurti ka pirce 2000 kar do",
        "oye bhai Cap add kar de 250 ka",
        "bhai Dupatta ka stock bees kar dena",
        "Polo shirt white 600 ka add karna hai",
        "Lawn Suit ki cost 2400 hai wese",
        "Hat 100 nahi 120 ka add karo",
        // orders in different shapes
        "Bilal bhai ko 3 Kurti bhejni hain, delivery 200",
        "Ayesha Raza ko ek Lawn Suit aur do Dupatta bhej dena",
        "Zainab ko 2 Kurti dena advance 500 aya hai",
        "Bilal ko 2 Kurti, address Model Town Multan",
        "Hamza ko 1 Polo Shirt bhejo phone 03001234567",
        "Sara Khan ko 1 Lawn Suit bhejna",
        "Sara ko 2 Kurti bhej do",
        "Bilal ko Kurti aur Dupatta dono bhej do 2 2",
        // Urdu script
        "بلال کو دو کرتی بھیج دیں",
        "ہیٹ سو روپے کا شامل کر دیں",
        "کرتی کی قیمت دو ہزار کر دیں",
        "مجھے تین پروڈکٹ شامل کرنے ہیں ایک ایک کر کے بھیجوں گا",
        // several tasks
        "pehle Cap 250 ka add karo phir Kurti ka price 1900 kar do phir Bilal ko 1 Cap bhejo",
        "Dupatta ka stock 10 kar do aur Lawn Suit ki cost 2300 kar do",
        "Hat 100 aur Cap 250 aur Belt 300 teeno add karo",
        // missing or unclear details
        "Belt add karo",
        "Kurti ka rate badal do",
        "ek order dena hai",
        "Jhumka ka price 300 kar do",
        "Bilal ko Jhumka bhej do",
        // not for the planner: reports, questions, cancel, delete, small talk
        "Kurti ka price kya hai",
        "aaj kitni sale hui hai bhai",
        "Bilal ka order cancel kar do",
        "Dupatta delete kar do catalog se",
        "shukriya bohat acha bot hai",
        "mera business bohat acha chal raha hai",
        "kal Sara ne 500 ka kharcha kiya packaging pe",
        // numbers and details that must not become something else
        "Cap 250 add karo uska phone number 03001234567 hai",
        "Kurti 1800 ki hai magar Bilal ko 1500 mein dena",
        "Bilal ko 2 Kurti bhej do 5 baje tak",
    };

    [Fact]
    public async Task Report_OnTheSeededShop_WithAStressSetOfMadeUpSentences()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (Environment.GetEnvironmentVariable("OTB_AGENT_SEEDED") != "1" || string.IsNullOrWhiteSpace(key)) return;

        using var factory = new TestDbContextFactory();
        using var db = factory.CreateContext();
        var (seller, _) = await AgentPlanEval.SeedAsync(db);
        // oldest first, so the report's "newest first" pick returns them in the order written here
        foreach (var s in Sentences.Reverse()) db.MessageLogs.Add(new MessageLog { Phone = seller.WhatsAppPhoneNumber, Direction = "inbound", RawText = s });
        await db.SaveChangesAsync();

        var assistant = new OpenAiOrderAssistant(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, Options.Create(new OpenAiOptions { ApiKey = key }),
            NullLogger<OpenAiOrderAssistant>.Instance, Mock.Of<IIssueReporter>());
        var rows = await AgentRealReport.RunAsync(db, assistant, 100);
        foreach (var r in rows)
            output.WriteLine($"{r.Sentence}\n          {r.Plan}  {r.Verdicts}{(r.Question is null ? "" : "  ASKS: " + r.Question)}  ({r.Ms} ms)");
        output.WriteLine(AgentRealReport.Summary(rows));
        output.WriteLine($"(sentences the parser claims are not sent to the planner: {Sentences.Length - rows.Count} of {Sentences.Length})");
        if (Environment.GetEnvironmentVariable("OTB_AGENT_REAL_REPORT") is { Length: > 0 } outFile)
            File.WriteAllText(outFile, "﻿" + AgentRealReport.ToCsv(rows), new UTF8Encoding(false));
    }
}
