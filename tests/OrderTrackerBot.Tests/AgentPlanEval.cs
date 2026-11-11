using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Agent;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Ai;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace OrderTrackerBot.Tests;

/// <summary>
/// Measures the PLANNER (message -> plan -> verifier) against the REAL model on a fixed shop, per kind of sentence, so a change to the planner prompt or the
/// tool list can be judged by a number before <c>Features:Agent</c> is switched on. Skipped unless OTB_AGENT_EVAL=1 and OPENAI_API_KEY are set:
/// <c>OTB_AGENT_EVAL=1 OPENAI_API_KEY=... dotnet test --filter AgentPlanEval --logger "console;verbosity=detailed"</c> (set OTB_AGENT_EVAL_ONLY=Bilal to run only the sentences containing that text)
/// It starts from typed text, so it measures the plan and the verifier, not the nested handlers (those have their own tests).
/// Add the seller's real sentences to <see cref="Cases"/>. Reported per case: PLAN (right tools, arguments, awaiting) and VERDICT (what the verifier lets run);
/// plus two safety counts that must stay 0: invented ids (the model used an id it was not shown) and unsaid numbers (an amount the seller never said).
/// </summary>
public class AgentPlanEval
{
    /// <summary>
    /// What a good answer looks like. <c>Tools</c> = the expected tool sequence; <c>Args[i]</c> = texts that must appear in step i
    /// (<c>name=Hat</c>, <c>price=100</c>, <c>customer_name=Bilal</c>, <c>a/b</c> = either text, an order line as <c>Jacket x2</c>, <c>id:Polo Shirt</c> = that product's id as <c>product_id</c>);
    /// <c>Not</c> = texts that must appear in no step. <c>Awaiting</c> = the tool and count of a "one by one" promise.
    /// <c>NoPlan</c> = the message is not for the planner at all. <c>NoSteps</c> = it may ask a question or return nothing, but no step may be planned
    /// (<c>OrAsk</c>: or a first step the verifier turns into a question).
    /// </summary>
    internal sealed record Case(string Kind, string Message, string[] Tools, string[][]? Args = null, string[]? Not = null,
        (string Tool, int Count)? Awaiting = null, bool NoPlan = false, bool NoSteps = false, bool OrAsk = false, StepVerdict[]? Verdicts = null);

    private static readonly string[] NoTools = Array.Empty<string>();
    private const string Add = "add_product";
    private const string Edit = "edit_product";
    private const string Order = "draft_order";
    private static readonly StepVerdict[] Run1 = { StepVerdict.Run };
    private static readonly StepVerdict[] Confirm1 = { StepVerdict.Confirm };

    internal static readonly Case[] Cases =
    {
        // one new product
        new("add", "Hat 100 ka add karo", new[] { Add }, new[] { new[] { "name=Hat", "price=100" } }, Verdicts: Run1),
        new("add", "naya product Cap 250 cost 150 stock 20", new[] { Add }, new[] { new[] { "name=Cap", "price=250", "cost=150", "stock=20" } }, Verdicts: Run1),
        new("add", "Shawl teen hazar ka add kar do", new[] { Add }, new[] { new[] { "name=Shawl", "price=3000" } }, Verdicts: Run1),
        new("add", "Hat 100 aur Cap 250 dono add karo", new[] { Add, Add }, new[] { new[] { "name=Hat", "price=100" }, new[] { "name=Cap", "price=250" } }),
        // changing an existing product (always needs YES)
        new("edit", "Polo Shirt ka price 600 kar do", new[] { Edit }, new[] { new[] { "id:Polo Shirt", "price=600" } }, Verdicts: Confirm1),
        new("edit", "Dupatta ka stock 40 kar do", new[] { Edit }, new[] { new[] { "id:Dupatta", "stock=40" } }, Verdicts: Confirm1),
        new("edit", "Jacket ki cost 900 hai", new[] { Edit }, new[] { new[] { "id:Jacket", "cost=900" } }, Verdicts: Confirm1),
        new("edit", "Jacket ka price 1800 se 2100 kar do", new[] { Edit }, new[] { new[] { "id:Jacket", "price=2100" } }, Not: new[] { "price=1800" }, Verdicts: Confirm1),
        new("self-correction", "Polo Shirt 500 nahi 650 kar do", new[] { Edit }, new[] { new[] { "id:Polo Shirt", "price=650" } }, Not: new[] { "price=500" }),
        // orders
        new("order", "Bilal ko 2 Jacket bhej do", new[] { Order }, new[] { new[] { "Bilal/customer_id=3", "Jacket x2" } }, Verdicts: Run1),
        new("order", "Zainab ko 1 Lawn Suit aur 2 Dupatta", new[] { Order }, new[] { new[] { "Zainab", "Lawn Suit x1", "Dupatta x2" } }, Verdicts: Run1),
        new("order", "Ayesha Raza ko 2 polo shirt bhej do", new[] { Order }, new[] { new[] { "Ayesha Raza/customer_id=4", "x2" } }, Verdicts: Run1),
        new("order", "Bilal ko Shawl bhej do", new[] { Order }, new[] { new[] { "Bilal/customer_id=3", "Shawl" } }, Verdicts: Run1),
        // found by the stress set: a name in another script, a question the planner must not answer, an expense it has no tool for, a self-correction in an add
        new("script", "بلال کو دو کرتی بھیج دیں", new[] { Order }, new[] { new[] { "Bilal/customer_id=3", "Jacket x2" } }, Not: new[] { "customer_name=بلال" }, Verdicts: Run1),
        new("not-a-plan", "Jacket ka price kya hai", NoTools, NoPlan: true),
        new("not-a-plan", "kal Sara ne 500 ka kharcha kiya packaging pe", NoTools, NoPlan: true),
        new("self-correction", "Hat 100 nahi 120 ka add karo", new[] { Add }, new[] { new[] { "name=Hat", "price=120" } }, Not: new[] { "price=100" }),
        // two customers called Sara: the plan may name her or ask, but must not pick one
        new("ambiguous", "Sara ko 2 Jacket bhej do", NoTools, NoSteps: true, OrAsk: true),
        // several tasks in one message, in order
        new("multi", "Hat 100 ka add karo, Polo Shirt ka price 600 kar do, phir Bilal ko 2 Hat bhej do", new[] { Add, Edit, Order },
            new[] { new[] { "name=Hat", "price=100" }, new[] { "id:Polo Shirt", "price=600" }, new[] { "Bilal/customer_id=3", "Hat x2" } },
            Verdicts: new[] { StepVerdict.Run, StepVerdict.Confirm, StepVerdict.Run }),
        new("multi", "Cap 250 add karo aur Bilal ko 1 Cap bhej do", new[] { Add, Order },
            new[] { new[] { "name=Cap", "price=250" }, new[] { "Bilal/customer_id=3", "Cap x1" } }, Verdicts: new[] { StepVerdict.Run, StepVerdict.Run }),
        new("multi", "Jacket ka price 2000 kar do aur Dupatta ka stock 10 kar do", new[] { Edit, Edit },
            new[] { new[] { "id:Jacket", "price=2000" }, new[] { "id:Dupatta", "stock=10" } }, Verdicts: new[] { StepVerdict.Confirm, StepVerdict.Confirm }),
        // "I will send them one by one"
        new("one-by-one", "mere 3 product add karne hain ek ek karke dunga", NoTools, Awaiting: (Add, 3)),
        new("one-by-one", "kuch products add karne hain, ek ek karke bhejta hun, jab khatam ho to done likhunga", NoTools, Awaiting: (Add, 0)),
        new("one-by-one", "مجھے چار پروڈکٹ شامل کرنے ہیں، ایک ایک کر کے بھیجتا ہوں", NoTools, Awaiting: (Add, 4)),
        new("one-by-one", "I want to add 2 products, I will send them one by one", NoTools, Awaiting: (Add, 2)),
        // not for the planner: nothing may be planned
        new("not-a-plan", "assalam o alaikum kya haal hai", NoTools, NoPlan: true),
        new("not-a-plan", "aaj kitna bika", NoTools, NoPlan: true),
        new("not-a-plan", "mausam bohat garam hai", NoTools, NoPlan: true),
        new("not-a-plan", "mujhe support se baat karni hai", NoTools, NoPlan: true),
        new("not-a-plan", "Sara ka order cancel kar do", NoTools, NoPlan: true),          // there is no cancel tool
        new("not-a-plan", "Polo Shirt delete kar do", NoTools, NoPlan: true),             // there is no delete tool
        // traps: a missing price, an unknown product, a number that belongs to something else
        new("trap", "Hat add karo", NoTools, NoSteps: true, OrAsk: true),
        new("trap", "naya product add karo Chappal", NoTools, NoSteps: true, OrAsk: true),
        new("trap", "Jhumka ka price 300 kar do", NoTools, NoSteps: true),
        new("trap", "Cap 250 add karo, uska phone 03001234567", new[] { Add }, new[] { new[] { "name=Cap", "price=250" } }, Not: new[] { "03001234567" }),
    };

    /// <summary>What one case scored: the plan, what the verifier let through, and the two safety counters.</summary>
    internal sealed record Result(bool PlanOk, bool? VerdictOk, int InventedIds, int UnsaidNumbers, string Shown);

    private const double MinPlanConfidence = 0.7;   // the engine's bar (ConversationEngine.MinPlanConfidence)

    internal static Result Score(Case c, AgentPlan? plan, VerifiedPlan? verified, IReadOnlyDictionary<string, int> productIds)
    {
        var usable = plan is not null && plan.Confidence >= MinPlanConfidence ? plan : null;
        var invented = verified?.Steps.Count(s => s.Verdict == StepVerdict.Reject && s.Reason is "id_not_in_context" or "unknown_product" or "unknown_customer") ?? 0;
        var unsaid = verified?.Steps.Count(s => s.Verdict == StepVerdict.Reject && s.Reason is { } r && r.EndsWith("_not_said")) ?? 0;
        var shown = Show(plan, verified);

        bool planOk;
        if (c.NoPlan) planOk = usable is null;
        else if (c.NoSteps) planOk = usable is null || (usable.Steps.Count == 0 && usable.Awaiting is null)
                                     || (c.OrAsk && verified is { Steps.Count: > 0 } && verified.Steps[0].Verdict == StepVerdict.Ask);
        else if (c.Awaiting is { } want) planOk = usable is { Steps.Count: 0 } && usable.Awaiting == new PlanAwaiting(want.Tool, want.Count);
        else planOk = usable is not null && StepsMatch(c, usable, productIds);

        bool? verdictOk = null;
        if (planOk && c.Verdicts is { } verdicts && verified is not null)
            verdictOk = verified.Steps.Select(s => s.Verdict).SequenceEqual(verdicts);
        return new Result(planOk, verdictOk, invented, unsaid, shown);
    }

    private static bool StepsMatch(Case c, AgentPlan plan, IReadOnlyDictionary<string, int> productIds)
    {
        if (!plan.Steps.Select(s => s.Tool).SequenceEqual(c.Tools)) return false;
        var texts = plan.Steps.Select(StepText).ToList();
        for (var i = 0; i < texts.Count; i++)
        {
            var wanted = c.Args is not null && i < c.Args.Length ? c.Args[i] : Array.Empty<string>();
            foreach (var w in wanted)
            {
                // "a/b" = either text (a saved customer may be named or given by id).
                var found = w.Split('/').Any(alt =>
                {
                    var token = alt.StartsWith("id:", StringComparison.Ordinal)
                        ? "product_id=" + (productIds.TryGetValue(alt[3..], out var id) ? id.ToString() : "?")
                        : alt;
                    return texts[i].Contains(token, StringComparison.OrdinalIgnoreCase);
                });
                if (!found) return false;
            }
        }
        return c.Not is null || c.Not.All(n => texts.All(t => !t.Contains(n, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>A step as searchable text: <c>name=Hat|price=100|Jacket x2</c>.</summary>
    internal static string StepText(PlanStep step) =>
        string.Join("|", step.Args.Select(kv => $"{kv.Key}={kv.Value}").Concat(step.Items.Select(i => $"{i.Name} x{i.Quantity}")));

    private static string Show(AgentPlan? plan, VerifiedPlan? verified)
    {
        if (plan is null) return "(no plan)";
        var steps = plan.Steps.Select((s, i) => $"{s.Tool}({StepText(s)}) -> {verified?.Steps.ElementAtOrDefault(i)?.Verdict.ToString() ?? "?"}{(verified?.Steps.ElementAtOrDefault(i)?.Reason is { } r ? ":" + r : "")}");
        var text = string.Join("  ;  ", steps);
        if (plan.Awaiting is { } a) text += $"  awaiting {a.Tool} x{a.Count}";
        if (plan.Question is { } q) text += $"  ASKS: {q}";
        return $"{(text.Length == 0 ? "(no steps)" : text)}  [conf {plan.Confidence:0.##}]";
    }

    // ---- the fixed shop the cases are judged against ----

    internal static async Task<(Seller Seller, Dictionary<string, int> ProductIds)> SeedAsync(AppDbContext db)
    {
        var seller = new Seller { WhatsAppPhoneNumber = "923001234567", BusinessName = "Ayesha Collections", BusinessType = "Clothing", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        var products = new[] { ("Polo Shirt", 500m), ("Jacket", 1800m), ("Dupatta", 600m), ("Lawn Suit", 3200m) };
        foreach (var (name, price) in products) db.Products.Add(new Product { SellerId = seller.Id, Name = name, Price = price });
        db.Customers.Add(new Customer { SellerId = seller.Id, Name = "Sara Khan", Phone = "03001112222", City = "Lahore" });
        db.Customers.Add(new Customer { SellerId = seller.Id, Name = "Sara Ali", Phone = "03003334444", City = "Karachi" });
        db.Customers.Add(new Customer { SellerId = seller.Id, Name = "Bilal", Phone = "03005556666", City = "Multan" });
        db.Customers.Add(new Customer { SellerId = seller.Id, Name = "Ayesha Raza", Phone = "03007778888", City = "Lahore" });
        await db.SaveChangesAsync();
        var ids = await db.Products.Where(p => p.SellerId == seller.Id).ToDictionaryAsync(p => p.Name, p => p.Id);
        return (seller, ids);
    }

    /// <summary>Writes the assistant's log lines (including the raw answer of an unusable plan) into the test output.</summary>
    private sealed class OutputLogger<T>(ITestOutputHelper output) : Microsoft.Extensions.Logging.ILogger<T>
    {
        /// <summary>How many calls went to the stronger model because the first answer was not valid JSON.</summary>
        public int Retries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Debug;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception);
            if (line.Contains("agent plan (retry)", StringComparison.Ordinal)) Retries++;
            output.WriteLine("          LOG " + line);
        }
    }

    /// <summary>Writes the request and the answer of every call OpenAI refuses (status 400 and up) into the test output, so an API error can be reproduced.</summary>
    private sealed class DumpFailures(ITestOutputHelper output, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var response = await base.SendAsync(request, cancellationToken);
            if ((int)response.StatusCode >= 400)
                output.WriteLine($"          HTTP {(int)response.StatusCode}\n          REQUEST {body}\n          RESPONSE {await response.Content.ReadAsStringAsync(cancellationToken)}");
            return response;
        }
    }

    private readonly ITestOutputHelper _output;
    public AgentPlanEval(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task MeasureThePlannerAgainstTheRealModel()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (Environment.GetEnvironmentVariable("OTB_AGENT_EVAL") != "1" || string.IsNullOrWhiteSpace(key)) return;

        var logger = new OutputLogger<OpenAiOrderAssistant>(_output);
        var assistant = new OpenAiOrderAssistant(new HttpClient(new DumpFailures(_output, new HttpClientHandler())) { Timeout = TimeSpan.FromSeconds(60) },
            Options.Create(new OpenAiOptions { ApiKey = key }), logger, Mock.Of<IIssueReporter>());
        using var factory = new TestDbContextFactory();
        using var db = factory.CreateContext();
        var (seller, productIds) = await SeedAsync(db);
        var builder = new SellerContextBuilder(db);
        var verifier = new PlanVerifier(db);

        var plans = new Dictionary<string, (int Right, int Total)>();
        var verdicts = new Dictionary<string, (int Right, int Total)>();
        int invented = 0, unsaid = 0;
        var timings = new List<(string Kind, string Message, long Ms, bool Retried, bool HadPlan)>();
        var only = Environment.GetEnvironmentVariable("OTB_AGENT_EVAL_ONLY");   // run just the sentences containing this text
        foreach (var c in Cases.Where(c => string.IsNullOrEmpty(only) || c.Message.Contains(only, StringComparison.OrdinalIgnoreCase)))
        {
            var context = await builder.BuildAsync(seller, c.Message, default);
            var retriesBefore = logger.Retries;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var plan = await assistant.PlanAsync(c.Message, context.Render(), AgentTools.All);
            clock.Stop();
            timings.Add((c.Kind, c.Message, clock.ElapsedMilliseconds, logger.Retries > retriesBefore, plan is not null));
            // A null plan for a sentence that should have one may be an OpenAI error (a 502), not a miss: ask again, up to twice.
            for (var retry = 0; plan is null && !c.NoPlan && !c.NoSteps && retry < 2; retry++)
                plan = await assistant.PlanAsync(c.Message, context.Render(), AgentTools.All);
            var verified = plan is null ? null : await verifier.VerifyAsync(seller, plan, context, c.Message, default);
            var r = Score(c, plan, verified, productIds);

            Tally(plans, c.Kind, r.PlanOk);
            if (r.VerdictOk is { } v) Tally(verdicts, c.Kind, v);
            invented += r.InventedIds;
            unsaid += r.UnsaidNumbers;
            _output.WriteLine($"plan {(r.PlanOk ? "OK  " : "MISS")} verdict {(r.VerdictOk is null ? "-   " : r.VerdictOk.Value ? "OK  " : "MISS")} [{c.Kind}] {c.Message}");
            _output.WriteLine($"          {r.Shown}");
        }

        _output.WriteLine("kind              plan        verdict");
        foreach (var kind in plans.Keys)
            _output.WriteLine($"{kind,-16}  {Pct(plans[kind]),-10}  {(verdicts.TryGetValue(kind, out var v) ? Pct(v) : "-")}");
        _output.WriteLine($"PLAN ACCURACY {Pct(Sum(plans))}   VERDICT ACCURACY {Pct(Sum(verdicts))}");
        WriteLatency(timings);
        _output.WriteLine($"SAFETY invented ids: {invented}   unsaid numbers: {unsaid}   (the verifier rejected them; both should be 0 before the flag goes on)");
    }

    /// <summary>
    /// Wall-clock time of the planner call as the engine would wait for it (the OpenAI round trip, plus the second call to the stronger model when the first answer was not valid JSON).
    /// A sentence that needs no plan answers fastest; each row prints its time, the summary splits them by whether the model was asked twice.
    /// </summary>
    private void WriteLatency(List<(string Kind, string Message, long Ms, bool Retried, bool HadPlan)> timings)
    {
        if (timings.Count == 0) return;
        static long Pct(IEnumerable<long> ms, double p) { var a = ms.OrderBy(x => x).ToArray(); return a.Length == 0 ? 0 : a[Math.Min(a.Length - 1, (int)Math.Ceiling(p * a.Length) - 1)]; }
        _output.WriteLine("LATENCY per sentence (ms)");
        foreach (var t in timings) _output.WriteLine($"  {t.Ms,6} ms{(t.Retried ? "  +retry" : "         ")}  [{t.Kind}] {t.Message}");
        void Line(string label, IEnumerable<long> ms)
        {
            var list = ms.ToList();
            if (list.Count > 0) _output.WriteLine($"  {label,-22} n={list.Count,2}  p50 {Pct(list, 0.5),6} ms  p95 {Pct(list, 0.95),6} ms  max {list.Max(),6} ms  mean {list.Average():0} ms");
        }
        _output.WriteLine("LATENCY summary");
        Line("all", timings.Select(t => t.Ms));
        Line("one call (no retry)", timings.Where(t => !t.Retried).Select(t => t.Ms));
        Line("with the retry", timings.Where(t => t.Retried).Select(t => t.Ms));
        Line("a plan was returned", timings.Where(t => t.HadPlan).Select(t => t.Ms));
        Line("nothing to plan", timings.Where(t => !t.HadPlan).Select(t => t.Ms));
    }

    // ---- the scoring itself is checked offline, so the eval cannot rot between runs ----

    private static readonly Dictionary<string, int> Ids = new() { ["Polo Shirt"] = 7, ["Jacket"] = 8 };

    private static PlanStep S(string tool, Dictionary<string, string> args, params PlanItem[] items) => new(tool, args, items);

    private static AgentPlan P(double conf, params PlanStep[] steps) => new(steps, null, null, conf);

    private static Case Find(string message) => Cases.Single(c => c.Message == message);

    [Fact]
    public void Score_AcceptsTheRightPlan_AndChecksArgumentsAndIds()
    {
        var c = Find("Polo Shirt ka price 600 kar do");
        var good = P(0.9, S(Edit, new() { ["product_id"] = "7", ["price"] = "600" }));
        Assert.True(Score(c, good, null, Ids).PlanOk);

        var wrongId = P(0.9, S(Edit, new() { ["product_id"] = "8", ["price"] = "600" }));
        Assert.False(Score(c, wrongId, null, Ids).PlanOk);
        var wrongPrice = P(0.9, S(Edit, new() { ["product_id"] = "7", ["price"] = "650" }));
        Assert.False(Score(c, wrongPrice, null, Ids).PlanOk);
        Assert.False(Score(c, P(0.9, S(Add, new() { ["name"] = "Polo", ["price"] = "600" })), null, Ids).PlanOk);   // wrong tool
    }

    [Fact]
    public void Score_ALowConfidencePlan_CountsAsNoPlan()
    {
        var steps = P(0.3, S(Add, new() { ["name"] = "Hat", ["price"] = "100" }));
        Assert.False(Score(Find("Hat 100 ka add karo"), steps, null, Ids).PlanOk);
        Assert.True(Score(Find("aaj kitna bika"), steps, null, Ids).PlanOk);   // the engine would ignore it, which is right for a non-plan
        Assert.True(Score(Find("aaj kitna bika"), null, null, Ids).PlanOk);
        Assert.False(Score(Find("aaj kitna bika"), P(0.9, S(Add, new() { ["name"] = "Bika", ["price"] = "1" })), null, Ids).PlanOk);
    }

    [Fact]
    public void Score_UnwantedArgumentsFail_AndStepOrderMatters()
    {
        var trap = Find("Cap 250 add karo, uska phone 03001234567");
        Assert.True(Score(trap, P(0.9, S(Add, new() { ["name"] = "Cap", ["price"] = "250" })), null, Ids).PlanOk);
        Assert.False(Score(trap, P(0.9, S(Add, new() { ["name"] = "Cap", ["price"] = "250", ["stock"] = "03001234567" })), null, Ids).PlanOk);

        var multi = Find("Cap 250 add karo aur Bilal ko 1 Cap bhej do");
        var order = S(Order, new() { ["customer_name"] = "Bilal" }, new PlanItem(null, "Cap", 1));
        var add = S(Add, new() { ["name"] = "Cap", ["price"] = "250" });
        Assert.True(Score(multi, P(0.9, add, order), null, Ids).PlanOk);
        Assert.False(Score(multi, P(0.9, order, add), null, Ids).PlanOk);
    }

    [Fact]
    public void Score_OneByOne_NeedsTheToolAndTheCount_AndNoSteps()
    {
        var c = Find("mere 3 product add karne hain ek ek karke dunga");
        Assert.True(Score(c, new AgentPlan(Array.Empty<PlanStep>(), "Pehla likhein", new PlanAwaiting(Add, 3), 0.9), null, Ids).PlanOk);
        Assert.False(Score(c, new AgentPlan(Array.Empty<PlanStep>(), "Pehla likhein", new PlanAwaiting(Add, 5), 0.9), null, Ids).PlanOk);
        Assert.False(Score(c, new AgentPlan(new[] { S(Add, new() { ["name"] = "A", ["price"] = "1" }) }, null, new PlanAwaiting(Add, 3), 0.9), null, Ids).PlanOk);
    }

    [Fact]
    public void Score_AmbiguousCustomer_PassesOnlyWhenTheVerifierAsks_OrNothingIsPlanned()
    {
        var c = Find("Sara ko 2 Jacket bhej do");
        var step = S(Order, new() { ["customer_name"] = "Sara" }, new PlanItem(null, "Jacket", 2));
        var plan = P(0.9, step);
        var asks = new VerifiedPlan(new[] { new VerifiedStep(step, StepVerdict.Ask, "ambiguous_customer") });
        var runs = new VerifiedPlan(new[] { new VerifiedStep(step, StepVerdict.Run) });

        Assert.True(Score(c, plan, asks, Ids).PlanOk);
        Assert.False(Score(c, plan, runs, Ids).PlanOk);   // picked a Sara by itself
        Assert.True(Score(c, new AgentPlan(Array.Empty<PlanStep>(), "Kaun si Sara?", null, 0.9), null, Ids).PlanOk);
    }

    [Fact]
    public void Score_CountsInventedIdsAndUnsaidNumbers_AndComparesVerdicts()
    {
        var c = Find("Polo Shirt ka price 600 kar do");
        var step = S(Edit, new() { ["product_id"] = "7", ["price"] = "600" });
        var plan = P(0.9, step);

        var confirm = Score(c, plan, new VerifiedPlan(new[] { new VerifiedStep(step, StepVerdict.Confirm) }), Ids);
        Assert.True(confirm.VerdictOk);

        var runs = Score(c, plan, new VerifiedPlan(new[] { new VerifiedStep(step, StepVerdict.Run) }), Ids);
        Assert.False(runs.VerdictOk);

        var invented = Score(c, plan, new VerifiedPlan(new[] { new VerifiedStep(step, StepVerdict.Reject, "id_not_in_context") }), Ids);
        Assert.Equal(1, invented.InventedIds);
        var unsaid = Score(c, plan, new VerifiedPlan(new[] { new VerifiedStep(step, StepVerdict.Reject, "price_not_said") }), Ids);
        Assert.Equal(1, unsaid.UnsaidNumbers);
    }

    [Fact]
    public async Task IdealPlans_ThroughTheRealVerifierAndTheSeededShop_GiveTheVerdictsTheCasesExpect()
    {
        using var factory = new TestDbContextFactory();
        using var db = factory.CreateContext();
        var (seller, ids) = await SeedAsync(db);
        string Id(string name) => ids[name].ToString();
        var ideal = new Dictionary<string, PlanStep[]>
        {
            ["Hat 100 ka add karo"] = new[] { S(Add, new() { ["name"] = "Hat", ["price"] = "100" }) },
            ["Shawl teen hazar ka add kar do"] = new[] { S(Add, new() { ["name"] = "Shawl", ["price"] = "3000" }) },
            ["Polo Shirt ka price 600 kar do"] = new[] { S(Edit, new() { ["product_id"] = Id("Polo Shirt"), ["price"] = "600" }) },
            ["Dupatta ka stock 40 kar do"] = new[] { S(Edit, new() { ["product_id"] = Id("Dupatta"), ["stock"] = "40" }) },
            ["Jacket ki cost 900 hai"] = new[] { S(Edit, new() { ["product_id"] = Id("Jacket"), ["cost"] = "900" }) },
            ["Jacket ka price 1800 se 2100 kar do"] = new[] { S(Edit, new() { ["product_id"] = Id("Jacket"), ["price"] = "2100" }) },
            ["Bilal ko 2 Jacket bhej do"] = new[] { S(Order, new() { ["customer_name"] = "Bilal" }, new PlanItem(null, "Jacket", 2)) },
            ["Zainab ko 1 Lawn Suit aur 2 Dupatta"] = new[] { S(Order, new() { ["customer_name"] = "Zainab" }, new PlanItem(null, "Lawn Suit", 1), new PlanItem(null, "Dupatta", 2)) },
            ["Ayesha Raza ko 2 polo shirt bhej do"] = new[] { S(Order, new() { ["customer_name"] = "Ayesha Raza" }, new PlanItem(null, "polo shirt", 2)) },
            ["Bilal ko Shawl bhej do"] = new[] { S(Order, new() { ["customer_name"] = "Bilal" }, new PlanItem(null, "Shawl", 1)) },
            ["Hat 100 ka add karo, Polo Shirt ka price 600 kar do, phir Bilal ko 2 Hat bhej do"] = new[]
            {
                S(Add, new() { ["name"] = "Hat", ["price"] = "100" }),
                S(Edit, new() { ["product_id"] = Id("Polo Shirt"), ["price"] = "600" }),
                S(Order, new() { ["customer_name"] = "Bilal" }, new PlanItem(null, "Hat", 2)),
            },
            ["Cap 250 add karo aur Bilal ko 1 Cap bhej do"] = new[]
            {
                S(Add, new() { ["name"] = "Cap", ["price"] = "250" }),
                S(Order, new() { ["customer_name"] = "Bilal" }, new PlanItem(null, "Cap", 1)),
            },
            ["Jacket ka price 2000 kar do aur Dupatta ka stock 10 kar do"] = new[]
            {
                S(Edit, new() { ["product_id"] = Id("Jacket"), ["price"] = "2000" }),
                S(Edit, new() { ["product_id"] = Id("Dupatta"), ["stock"] = "10" }),
            },
        };

        foreach (var (message, steps) in ideal)
        {
            var c = Find(message);
            var plan = P(0.9, steps);
            var context = await new SellerContextBuilder(db).BuildAsync(seller, message, default);
            var verified = await new PlanVerifier(db).VerifyAsync(seller, plan, context, message, default);
            var r = Score(c, plan, verified, ids);
            Assert.True(r.PlanOk, $"plan: {message}  {r.Shown}");
            Assert.True(r.VerdictOk ?? true, $"verdict: {message}  {r.Shown}");
            Assert.True(c.Verdicts is null || r.VerdictOk == true, $"has expected verdicts: {message}");
        }

        // two Saras: the plan names her and the verifier turns it into a question
        var sara = Find("Sara ko 2 Jacket bhej do");
        var saraPlan = P(0.9, S(Order, new() { ["customer_name"] = "Sara" }, new PlanItem(null, "Jacket", 2)));
        var saraVerified = await new PlanVerifier(db).VerifyAsync(seller, saraPlan,
            await new SellerContextBuilder(db).BuildAsync(seller, sara.Message, default), sara.Message, default);
        Assert.True(Score(sara, saraPlan, saraVerified, ids).PlanOk);
    }

    [Fact]
    public void Cases_AreWellFormed_EveryKindIsCovered_AndExpectedToolsExist()
    {
        Assert.Equal(Cases.Length, Cases.Select(c => c.Message).Distinct().Count());
        Assert.All(Cases, c => Assert.All(c.Tools, t => Assert.NotNull(AgentTools.Find(t))));
        Assert.All(Cases.Where(c => c.Args is not null), c => Assert.True(c.Args!.Length <= c.Tools.Length, c.Message));
        Assert.All(Cases.Where(c => c.Verdicts is not null), c => Assert.Equal(c.Tools.Length, c.Verdicts!.Length));
        foreach (var kind in new[] { "add", "edit", "order", "multi", "one-by-one", "not-a-plan", "trap", "ambiguous", "self-correction" })
            Assert.Contains(Cases, c => c.Kind == kind);
        Assert.True(Cases.Length >= 30);
    }

    private static void Tally(Dictionary<string, (int Right, int Total)> map, string kind, bool ok)
    {
        var (right, total) = map.GetValueOrDefault(kind);
        map[kind] = (right + (ok ? 1 : 0), total + 1);
    }

    private static (int Right, int Total) Sum(Dictionary<string, (int Right, int Total)> map) => (map.Values.Sum(v => v.Right), map.Values.Sum(v => v.Total));
    private static string Pct((int Right, int Total) v) => $"{v.Right}/{v.Total} ({100.0 * v.Right / Math.Max(1, v.Total):F0}%)";
}
