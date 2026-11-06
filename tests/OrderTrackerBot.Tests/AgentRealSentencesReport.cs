using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Agent;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.Ai;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace OrderTrackerBot.Tests;

/// <summary>One real seller sentence, what the planner made of it against that seller's own data, and what the verifier let through.</summary>
internal sealed record RealRow(string Seller, string Sentence, string Plan, string Verdicts, string? Question, long Ms, int Steps, bool Awaiting, int InventedIds, int UnsaidNumbers);

/// <summary>
/// The planner on REAL sentences: messages sellers actually sent that the deterministic parser does not claim (the ones that reach the freeform path), run against
/// each seller's own catalog and customers from a copy of the app database, opened read-only. There is no "expected" answer for them, so nothing is scored here:
/// the report counts what the planner did and the safety counters (invented ids, unsaid numbers), and writes a CSV with empty <c>verdict (right/wrong)</c> and
/// <c>note</c> columns for a person to fill in; the accuracy percent is right / (right + wrong) by hand. Sentences with a clear right answer can then be moved
/// into <see cref="AgentPlanEval.Cases"/>.
/// </summary>
internal static class AgentRealReport
{
    private static readonly Regex LongDigits = new(@"\d{7,}", RegexOptions.Compiled);

    /// <summary>Phone-length digit runs are hidden so a report can be shared without customer numbers.</summary>
    internal static string Mask(string text) => LongDigits.Replace(text, "<phone>");

    /// <summary>
    /// The newest distinct inbound text messages of onboarded sellers that no command claims. Skipped: pictures / documents / voice markers ("[..."), one-word
    /// answers (yes, 1, done), outbound messages, and anything <see cref="CommandParser"/> already understands (the planner never sees those).
    /// </summary>
    internal static async Task<List<(Seller Seller, string Text)>> PickAsync(AppDbContext db, int limit, CancellationToken ct = default)
    {
        var sellers = await db.Sellers.AsNoTracking().Include(s => s.Session).Where(s => s.OnboardingComplete).ToDictionaryAsync(s => s.WhatsAppPhoneNumber, ct);
        var logs = await db.MessageLogs.AsNoTracking().Where(m => m.Direction == "inbound").OrderByDescending(m => m.Id)
            .Select(m => new { m.Phone, m.RawText }).Take(limit * 40 + 200).ToListAsync(ct);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var picked = new List<(Seller, string)>();
        foreach (var log in logs)
        {
            if (picked.Count >= limit) break;
            var text = (log.RawText ?? "").Trim();
            if (!sellers.TryGetValue(log.Phone, out var seller) || text.Length < 6 || text.StartsWith('[')) continue;
            if (text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 2) continue;
            if (CommandParser.TryParse(text) is not null) continue;
            if (!seen.Add(log.Phone + "|" + text)) continue;
            picked.Add((seller, text));
        }
        return picked;
    }

    internal static async Task<List<RealRow>> RunAsync(AppDbContext db, IAiOrderAssistant planner, int limit, CancellationToken ct = default)
    {
        var rows = new List<RealRow>();
        var builder = new SellerContextBuilder(db);
        var verifier = new PlanVerifier(db);
        foreach (var (seller, text) in await PickAsync(db, limit, ct))
        {
            var context = await builder.BuildAsync(seller, text, ct);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var plan = await planner.PlanAsync(text, context.Render(), AgentTools.All, ct);
            clock.Stop();
            var verified = plan is null ? null : await verifier.VerifyAsync(seller, plan, context, text, ct);

            var steps = plan is null ? "(no plan)" : plan.Steps.Count == 0 ? "(no steps)" : string.Join("  ;  ", plan.Steps.Select(s => $"{s.Tool}({AgentPlanEval.StepText(s)})"));
            var verdicts = verified is null ? "" : string.Join(" ; ", verified.Steps.Select(v => v.Verdict + (v.Reason is { } r ? ":" + r : "")));
            var invented = verified?.Steps.Count(v => v.Verdict == StepVerdict.Reject && v.Reason is "id_not_in_context" or "unknown_product" or "unknown_customer") ?? 0;
            var unsaid = verified?.Steps.Count(v => v.Verdict == StepVerdict.Reject && v.Reason is { } r && r.EndsWith("_not_said")) ?? 0;
            rows.Add(new RealRow(seller.BusinessName ?? "", Mask(text), Mask(steps), verdicts, plan?.Question is { } q ? Mask(q) : null, clock.ElapsedMilliseconds,
                plan?.Steps.Count ?? 0, plan?.Awaiting is not null, invented, unsaid));
        }
        return rows;
    }

    internal static string Summary(IReadOnlyList<RealRow> rows)
    {
        if (rows.Count == 0) return "No sentences reached the planner (nothing unclaimed, or no onboarded seller).";
        var ms = rows.Select(r => r.Ms).OrderBy(x => x).ToArray();
        var sb = new StringBuilder();
        sb.AppendLine($"REAL SENTENCES {rows.Count}");
        sb.AppendLine($"  planned steps     {rows.Count(r => r.Steps > 0),3}");
        sb.AppendLine($"  one-by-one        {rows.Count(r => r.Awaiting),3}");
        sb.AppendLine($"  question only     {rows.Count(r => r.Steps == 0 && !r.Awaiting && r.Question is not null),3}");
        sb.AppendLine($"  nothing to plan   {rows.Count(r => r.Steps == 0 && !r.Awaiting && r.Question is null),3}");
        sb.AppendLine($"  latency p50 {ms[ms.Length / 2]} ms  max {ms[^1]} ms");
        sb.AppendLine($"  SAFETY invented ids: {rows.Sum(r => r.InventedIds)}   unsaid numbers: {rows.Sum(r => r.UnsaidNumbers)}   (the verifier rejected them; both should be 0)");
        return sb.ToString();
    }

    internal const string CsvHeader = "\"seller\",\"sentence\",\"plan\",\"verifier\",\"question\",\"ms\",\"verdict (right/wrong)\",\"note\"";

    internal static string ToCsv(IEnumerable<RealRow> rows)
    {
        var sb = new StringBuilder().AppendLine(CsvHeader);
        foreach (var r in rows)
            sb.AppendLine(string.Join(",", Cell(r.Seller), Cell(r.Sentence), Cell(r.Plan), Cell(r.Verdicts), Cell(r.Question), Cell(r.Ms.ToString()), Cell(""), Cell("")));
        return sb.ToString();
    }

    /// <summary>Always quoted; line breaks flattened; a leading = + - @ is neutralised so a value cannot run as a spreadsheet formula.</summary>
    internal static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        var text = value.Replace("\r", " ").Replace("\n", " ");
        if (text[0] is '=' or '+' or '-' or '@' or '\t') text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}

public class AgentRealSentencesReport(ITestOutputHelper output)
{
    private const string EnvVar = "OTB_AGENT_REAL";

    /// <summary>
    /// Local only. OTB_AGENT_REAL = a COPY of the app's .db (opened read-only); OPENAI_API_KEY is needed. Optional: OTB_AGENT_REAL_LIMIT (default 40 sentences),
    /// OTB_AGENT_REAL_REPORT = a .csv path for the review sheet. The sentences and each seller's product / customer names go to OpenAI, exactly as they do in production.
    /// <c>OTB_AGENT_REAL=/path/ordertrackerbot.db OPENAI_API_KEY=... dotnet test --filter AgentRealSentencesReport --logger "console;verbosity=detailed"</c>
    /// </summary>
    [LocalOnlyFact(EnvVar)]
    public async Task Report_OnRealSentences_FromACopyOfTheAppDatabase()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(key), "OPENAI_API_KEY is needed.");
        var path = Environment.GetEnvironmentVariable(EnvVar)!;
        var limit = int.TryParse(Environment.GetEnvironmentVariable("OTB_AGENT_REAL_LIMIT"), out var n) ? Math.Clamp(n, 1, 500) : 40;

        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path};Mode=ReadOnly").Options);
        var assistant = new OpenAiOrderAssistant(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, Options.Create(new OpenAiOptions { ApiKey = key }),
            NullLogger<OpenAiOrderAssistant>.Instance, Mock.Of<IIssueReporter>());

        var rows = await AgentRealReport.RunAsync(db, assistant, limit);
        foreach (var r in rows)
            output.WriteLine($"[{r.Seller}] {r.Sentence}\n          {r.Plan}  {r.Verdicts}{(r.Question is null ? "" : "  ASKS: " + r.Question)}  ({r.Ms} ms)");
        output.WriteLine(AgentRealReport.Summary(rows));
        if (Environment.GetEnvironmentVariable("OTB_AGENT_REAL_REPORT") is { Length: > 0 } outFile)
            File.WriteAllText(outFile, "﻿" + AgentRealReport.ToCsv(rows), new UTF8Encoding(false));
    }

    // ---- the harness itself, on made-up rows (these say nothing about real sellers) ----

    private readonly TestDbContextFactory _factory = new();

    private static async Task<Seller> SeedAsync(AppDbContext db, string phone, string name, bool onboarded = true)
    {
        var seller = new Seller { WhatsAppPhoneNumber = phone, BusinessName = name, OnboardingComplete = onboarded, Session = new ConversationSession { State = Domain.Enums.ConversationState.Idle } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        return seller;
    }

    private static void Inbound(AppDbContext db, string phone, string text) => db.MessageLogs.Add(new MessageLog { Phone = phone, Direction = "inbound", RawText = text });

    [Fact]
    public async Task Pick_TakesTheNewestDistinctUnclaimedTextsOfOnboardedSellers_AndSkipsTheRest()
    {
        using var db = _factory.CreateContext();
        await SeedAsync(db, "923001110000", "Shop A");
        await SeedAsync(db, "923002220000", "Shop B", onboarded: false);
        Inbound(db, "923001110000", "pehle Hat 100 ka add karo phir Cap 200 ka");
        Inbound(db, "923001110000", "orders today");                                   // a command: never reaches the planner
        Inbound(db, "923001110000", "[image]");                                        // not text
        Inbound(db, "923001110000", "yes");                                            // one word
        Inbound(db, "923002220000", "pehle Hat 100 ka add karo phir Cap 200 ka");        // not onboarded
        Inbound(db, "923001110000", "pehle Hat 100 ka add karo phir Cap 200 ka");        // the same text again
        Inbound(db, "923999990000", "kisi ajnabi ka paigham yahan hai");                 // unknown sender
        db.MessageLogs.Add(new MessageLog { Phone = "923001110000", Direction = "outbound", RawText = "bot ka jawab yahan likha hai" });
        Inbound(db, "923001110000", "Sara ko do Kurti bhej dena aaj hi");
        await db.SaveChangesAsync();

        var picked = await AgentRealReport.PickAsync(db, 10);

        Assert.Equal(new[] { "Sara ko do Kurti bhej dena aaj hi", "pehle Hat 100 ka add karo phir Cap 200 ka" }, picked.Select(p => p.Text));
        Assert.All(picked, p => Assert.Equal("Shop A", p.Seller.BusinessName));
    }

    [Fact]
    public async Task Run_PlansAgainstTheSellersOwnData_MasksPhones_CountsSafety_AndWritesAReviewSheet()
    {
        using var db = _factory.CreateContext();
        var seller = await SeedAsync(db, "923001110000", "Shop A");
        db.Products.Add(new Product { SellerId = seller.Id, Name = "Polo Shirt", Price = 500 });
        db.Customers.Add(new Customer { SellerId = seller.Id, Name = "Bilal", Phone = "03001234567" });
        Inbound(db, "923001110000", "Polo shirt ka price 600 kar do bhai");
        Inbound(db, "923001110000", "=HYPERLINK bana ke Bilal 03001234567 ko bhejo");
        await db.SaveChangesAsync();
        string? told = null;
        var planner = new Mock<IAiOrderAssistant>();
        planner.Setup(p => p.PlanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<AgentTool>>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, IReadOnlyList<AgentTool>, CancellationToken>((message, context, _, _) =>
            {
                told ??= context;
                return Task.FromResult<AgentPlan?>(message.StartsWith("Polo")
                    ? new AgentPlan(new[] { new PlanStep("edit_product", new Dictionary<string, string> { ["product_id"] = "1", ["price"] = "700" }, Array.Empty<PlanItem>()) }, null, null, 0.9)
                    : null);
            });

        var rows = await AgentRealReport.RunAsync(db, planner.Object, 10);

        Assert.Equal(2, rows.Count);
        var polo = rows.Single(r => r.Sentence.StartsWith("Polo"));
        Assert.Equal("Reject:price_not_said", polo.Verdicts);          // 700 was never said
        Assert.Equal(1, polo.UnsaidNumbers);
        var other = rows.Single(r => !r.Sentence.StartsWith("Polo"));
        Assert.DoesNotContain("03001234567", other.Sentence);          // masked
        Assert.Contains("<phone>", other.Sentence);
        Assert.Equal("(no plan)", other.Plan);
        Assert.Contains("Polo Shirt", told);                           // the planner saw the seller's own catalog

        var csv = AgentRealReport.ToCsv(rows);
        Assert.StartsWith(AgentRealReport.CsvHeader, csv);
        Assert.Contains("\"'=HYPERLINK", csv);                          // a leading = is neutralised
        Assert.All(csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Skip(1), line => Assert.EndsWith(",\"\",\"\"", line));   // verdict and note left empty for a person
        Assert.Contains("SAFETY invented ids: 0   unsaid numbers: 1", AgentRealReport.Summary(rows));
    }

    [Fact]
    public void Summary_OfNothing_SaysSo() => Assert.Contains("No sentences", AgentRealReport.Summary(Array.Empty<RealRow>()));
}
