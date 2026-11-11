using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Agent;
using OrderTrackerBot.Infrastructure.Ai;
using Xunit;

namespace OrderTrackerBot.Tests;

public class AgentPlannerTests
{
    private const string ThreeTaskPlan = """
    {"steps":[
      {"tool":"add_product","args":{"name":"Shirt","price":500}},
      {"tool":"edit_product","args":{"product_id":7,"price":600}},
      {"tool":"draft_order","args":{"customer_id":14},"items":[{"name":"Shirt","qty":2}]}
    ],"question":null,"awaiting":null,"confidence":0.92}
    """;

    [Fact]
    public void Parse_ReadsAThreeTaskPlan_InOrder()
    {
        var plan = AgentPlanParser.Parse(ThreeTaskPlan)!;

        Assert.Equal(new[] { "add_product", "edit_product", "draft_order" }, plan.Steps.Select(s => s.Tool));
        Assert.Equal("500", plan.Steps[0].Args["price"]);
        Assert.Equal("7", plan.Steps[1].Args["product_id"]);
        Assert.Equal(new PlanItem(null, "Shirt", 2), plan.Steps[2].Items.Single());
        Assert.Equal(0.92, plan.Confidence);
    }

    [Fact]
    public void Parse_ReadsAnAwaitingPromise_WithNoSteps()
    {
        var plan = AgentPlanParser.Parse("""{"steps":[],"question":"Pehla product likhein: Name - price","awaiting":{"tool":"add_product","count":3},"confidence":0.9}""")!;

        Assert.Empty(plan.Steps);
        Assert.Equal(new PlanAwaiting("add_product", 3), plan.Awaiting);
        Assert.Contains("Pehla", plan.Question);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"steps":[],"question":null,"awaiting":null,"confidence":0}""")]                        // nothing to do
    [InlineData("""{"steps":[{"tool":"delete_everything","args":{}}]}""")]                                 // tool not in the list
    [InlineData("""{"steps":[{"tool":"add_product","args":{"name":"A","price":1,"colour":"red"}}]}""")]   // argument the tool does not have
    [InlineData("""{"steps":[{"tool":"add_product","args":{"name":"A","price":1},"items":[{"name":"A"}]}]}""")] // items on a tool without items
    [InlineData("""{"steps":[{"tool":"draft_order","args":{},"items":[{"qty":2}]}]}""")]                    // item with neither id nor name
    [InlineData("""{"steps":[{"tool":"draft_order","args":{},"items":[{"name":"A","qty":0}]}]}""")]        // quantity out of range
    [InlineData("""{"steps":[],"awaiting":{"tool":"nope","count":2}}""")]                                   // awaiting an unknown tool
    public void Parse_RefusesAnythingUnusable_WholePlanIsNull(string json) => Assert.Null(AgentPlanParser.Parse(json));

    [Fact]
    public void Parse_RefusesMoreStepsThanTheLimit()
    {
        var steps = string.Join(",", Enumerable.Repeat("""{"tool":"add_product","args":{"name":"A","price":1}}""", AgentTools.MaxSteps + 1));
        Assert.Null(AgentPlanParser.Parse("{\"steps\":[" + steps + "]}"));
    }

    [Fact]
    public void Parse_AcceptsTheShapeTheRealModelReturns_EmptyItemsAndEmptyStrings_AndDropsARepeatedStep()
    {
        // Seen live: every step carries "items": [], unsaid arguments are "" or null, and a step is sometimes repeated.
        var plan = AgentPlanParser.Parse("""
        {"steps":[
          {"tool":"add_product","args":{"name":"Hat","price":100,"cost":null,"phone":""},"items":[]},
          {"tool":"add_product","args":{"name":"Hat","price":100},"items":[]},
          {"tool":"draft_order","args":{"customer_name":"Bilal","phone":""},"items":[{"name":"Hat","qty":2}]},
          {"tool":"draft_order","args":{"customer_name":"Bilal","phone":""},"items":[{"name":"Hat","qty":2}]}
        ],"question":null,"awaiting":null,"confidence":0.9}
        """)!;

        Assert.Equal(new[] { "add_product", "draft_order", "draft_order" }, plan.Steps.Select(s => s.Tool));   // two identical orders are never merged
        Assert.Equal(new[] { "name", "price" }, plan.Steps[0].Args.Keys.OrderBy(k => k));
        Assert.False(plan.Steps[1].Args.ContainsKey("phone"));
    }

    [Fact]
    public void Parse_ReadsWhichTaskAQuestionIsFor()
    {
        Assert.Equal("add_product", AgentPlanParser.Parse("""{"steps":[],"question":"Belt ka price?","awaiting":null,"confidence":0.9,"missing_for":"add_product"}""")!.MissingFor);
        Assert.Equal("none", AgentPlanParser.Parse("""{"steps":[],"question":"Kaun si Ali?","awaiting":null,"confidence":0.9,"missing_for":"none"}""")!.MissingFor);
        Assert.Null(AgentPlanParser.Parse("""{"steps":[],"question":"q","awaiting":null,"confidence":0.9,"missing_for":"delete_everything"}""")!.MissingFor);
    }

    [Fact]
    public void Parse_ClampsConfidence_AndAwaitingCount()
    {
        var plan = AgentPlanParser.Parse("""{"steps":[],"question":"q","awaiting":{"tool":"add_product","count":9999},"confidence":7}""")!;
        Assert.Equal(1, plan.Confidence);
        Assert.Equal(100, plan.Awaiting!.Count);
    }

    [Fact]
    public void EveryTool_DeclaresKnownArgKinds_AndRiskyOnesAreFlagged()
    {
        var kinds = new[] { "text", "number", "product_id", "customer_id" };
        Assert.All(AgentTools.All, t => Assert.All(t.Args, a => Assert.Contains(a.Kind, kinds)));
        Assert.True(AgentTools.Find("edit_product")!.Risky);
        Assert.False(AgentTools.Find("add_product")!.Risky);
    }

    // --- the OpenAI call: the context and the tool list reach the model, and its JSON becomes a plan ---

    private sealed class CapturingOpenAi(string content) : HttpMessageHandler
    {
        public string? RequestBody;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            var body = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static OpenAiOrderAssistant Assistant(HttpMessageHandler handler, string apiKey = "k") =>
        new(new HttpClient(handler), Options.Create(new OpenAiOptions { ApiKey = apiKey }), NullLogger<OpenAiOrderAssistant>.Instance, new Mock<IIssueReporter>().Object);

    [Fact]
    public async Task PlanAsync_SendsTheSellerContextAndEveryTool_AndReturnsThePlan()
    {
        var handler = new CapturingOpenAi(ThreeTaskPlan);

        var plan = await Assistant(handler).PlanAsync("Shirt 500 add karo, Polo 600 kar do, Sara ko 2 Shirt",
            "Catalog: #7 Polo Shirt Rs.500\nCustomers: #14 Sara Khan", AgentTools.All);

        Assert.Equal(3, plan!.Steps.Count);
        Assert.Contains("#14 Sara Khan", handler.RequestBody);
        Assert.Contains("Sara ko 2 Shirt", handler.RequestBody);
        foreach (var tool in AgentTools.All) Assert.Contains(tool.Name, handler.RequestBody);
        Assert.Contains("never invent an id", handler.RequestBody);
    }

    private sealed class SequenceOpenAi(params string[] contents) : HttpMessageHandler
    {
        public readonly List<string> Requests = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var content = contents[Math.Min(Requests.Count, contents.Length) - 1];
            var body = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task PlanAsync_CapsTheAnswer_AndAsksTheStrongerModelOnceWhenTheFirstAnswerIsNotValidJson()
    {
        // Seen live with the small model: a stray quote, then "}  }  }" until the token limit. The order sentence got a 502 from the proxy every time.
        const string derailed = "{\"steps\":[{\"tool\":\"draft_order\",\"args\":{\"customer_name\":\"Bilal\"}},\"question”:null,\"   , \"confidence”:0.95}  }  }  }";
        var handler = new SequenceOpenAi(derailed, ThreeTaskPlan);

        var plan = await Assistant(handler).PlanAsync("Bilal ko 2 Shirt bhej do", "Customers: #3 Bilal", AgentTools.All);

        Assert.Equal(3, plan!.Steps.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("\"max_completion_tokens\":800", handler.Requests[0]);
        Assert.Contains("\"type\":\"json_schema\"", handler.Requests[0]);
        Assert.Contains("\"strict\":true", handler.Requests[0]);
        Assert.Contains("\"type\":\"json_schema\"", handler.Requests[1]);
        Assert.Contains("\"model\":\"gpt-4o-mini\"", handler.Requests[0]);
        Assert.Contains("\"model\":\"gpt-4o\"", handler.Requests[1]);
    }

    // ---- the schema the answer must follow (structured outputs, strict) ----

    /// <summary>Strict mode accepts only closed objects whose every property is required; this walks the schema and checks both.</summary>
    private static void AssertStrict(System.Text.Json.Nodes.JsonNode? node, string path)
    {
        switch (node)
        {
            case System.Text.Json.Nodes.JsonObject obj:
                if (obj["type"]?.ToString() == "object")
                {
                    Assert.False(obj["additionalProperties"]?.GetValue<bool>() ?? true, path + " must be closed");
                    var props = obj["properties"]!.AsObject().Select(p => p.Key).OrderBy(k => k).ToArray();
                    var required = obj["required"]!.AsArray().Select(r => r!.GetValue<string>()).OrderBy(k => k).ToArray();
                    Assert.Equal(props, required);
                }
                foreach (var (key, child) in obj) AssertStrict(child, path + "/" + key);
                break;
            case System.Text.Json.Nodes.JsonArray arr:
                for (var i = 0; i < arr.Count; i++) AssertStrict(arr[i], path + "[" + i + "]");
                break;
        }
    }

    [Fact]
    public void Schema_IsStrictEverywhere_WithOneClosedStepPerTool()
    {
        var schema = AgentPlanSchema.Build(AgentTools.All);

        AssertStrict(schema, "$");
        var steps = schema["properties"]!["steps"]!["items"]!["anyOf"]!.AsArray();
        Assert.Equal(AgentTools.All.Select(t => t.Name), steps.Select(st => st!["properties"]!["tool"]!["enum"]![0]!.GetValue<string>()));
        foreach (var tool in AgentTools.All)
        {
            var step = steps.Single(st => st!["properties"]!["tool"]!["enum"]![0]!.GetValue<string>() == tool.Name)!;
            // exactly this tool's arguments: a name cannot appear in an edit, a price cannot appear in an order
            Assert.Equal(tool.Args.Select(a => a.Name).OrderBy(n => n), step["properties"]!["args"]!["properties"]!.AsObject().Select(p => p.Key).OrderBy(n => n));
            Assert.Equal(tool.TakesItems, step["properties"]!.AsObject().ContainsKey("items"));
        }
        var edit = steps.Single(st => st!["properties"]!["tool"]!["enum"]![0]!.GetValue<string>() == "edit_product")!;
        Assert.Equal(new[] { "integer", "null" }, edit["properties"]!["args"]!["properties"]!["product_id"]!["type"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(new[] { "number", "null" }, edit["properties"]!["args"]!["properties"]!["price"]!["type"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void Parse_DropsAStepWithNothingInIt_AndReadsPerToolArgs()
    {
        // Seen live under the schema: an extra step with every argument null.
        var plan = AgentPlanParser.Parse("""
        {"steps":[{"tool":"edit_product","args":{"product_id":3,"price":null,"cost":null,"stock":40}},
                  {"tool":"edit_product","args":{"product_id":null,"price":null,"cost":null,"stock":null}}],
         "question":null,"awaiting":null,"confidence":0.95}
        """)!;

        var step = Assert.Single(plan.Steps);
        Assert.Equal(new[] { "product_id", "stock" }, step.Args.Keys.OrderBy(k => k));
    }

    [Fact]
    public void Parse_ReadsAnAnswerInTheStrictSchemaShape_NullsAreOmittedArguments()
    {
        var plan = AgentPlanParser.Parse("""
        {"steps":[{"tool":"draft_order","args":{"customer_id":3,"customer_name":"Bilal","phone":null},
                   "items":[{"product_id":null,"name":"Shirt","qty":2}]}],
         "question":null,"awaiting":null,"confidence":0.95}
        """)!;

        var step = Assert.Single(plan.Steps);
        Assert.Equal(new[] { "customer_id", "customer_name" }, step.Args.Keys.OrderBy(k => k));
        Assert.Equal(new PlanItem(null, "Shirt", 2), step.Items.Single());
    }

    [Fact]
    public async Task PlanAsync_AsksTheStrongerModelOnce_WhenTheFirstCallGivesNoAnswerAtAll()
    {
        var handler = new SequenceOpenAi("", ThreeTaskPlan);   // an empty answer is what a refusal or an API error looks like to the caller

        var plan = await Assistant(handler).PlanAsync("x", "", AgentTools.All);

        Assert.NotNull(plan);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task PlanAsync_DoesNotRetry_WhenTheAnswerIsValidJson_EvenIfThereIsNothingToDo()
    {
        var handler = new SequenceOpenAi("""{"steps":[],"question":null,"awaiting":null,"confidence":0}""");

        Assert.Null(await Assistant(handler).PlanAsync("aaj kitna bika", "", AgentTools.All));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PlanAsync_IsNull_WithoutAnApiKey_OrWhenTheAnswerIsUnusable()
    {
        Assert.Null(await Assistant(new CapturingOpenAi(ThreeTaskPlan), apiKey: "").PlanAsync("x", "", AgentTools.All));
        Assert.Null(await Assistant(new CapturingOpenAi("""{"steps":[{"tool":"drop_table"}]}""")).PlanAsync("x", "", AgentTools.All));
    }
}
