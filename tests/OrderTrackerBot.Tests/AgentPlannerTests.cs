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
      {"tool":"add_product","args":{"name":"Kurti","price":500}},
      {"tool":"edit_product","args":{"product_id":7,"price":600}},
      {"tool":"draft_order","args":{"customer_id":14},"items":[{"name":"Kurti","qty":2}]}
    ],"question":null,"awaiting":null,"confidence":0.92}
    """;

    [Fact]
    public void Parse_ReadsAThreeTaskPlan_InOrder()
    {
        var plan = AgentPlanParser.Parse(ThreeTaskPlan)!;

        Assert.Equal(new[] { "add_product", "edit_product", "draft_order" }, plan.Steps.Select(s => s.Tool));
        Assert.Equal("500", plan.Steps[0].Args["price"]);
        Assert.Equal("7", plan.Steps[1].Args["product_id"]);
        Assert.Equal(new PlanItem(null, "Kurti", 2), plan.Steps[2].Items.Single());
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

        var plan = await Assistant(handler).PlanAsync("Kurti 500 add karo, Polo 600 kar do, Sara ko 2 Kurti",
            "Catalog: #7 Polo Shirt Rs.500\nCustomers: #14 Sara Khan", AgentTools.All);

        Assert.Equal(3, plan!.Steps.Count);
        Assert.Contains("#14 Sara Khan", handler.RequestBody);
        Assert.Contains("Sara ko 2 Kurti", handler.RequestBody);
        foreach (var tool in AgentTools.All) Assert.Contains(tool.Name, handler.RequestBody);
        Assert.Contains("never invent an id", handler.RequestBody);
    }

    [Fact]
    public async Task PlanAsync_IsNull_WithoutAnApiKey_OrWhenTheAnswerIsUnusable()
    {
        Assert.Null(await Assistant(new CapturingOpenAi(ThreeTaskPlan), apiKey: "").PlanAsync("x", "", AgentTools.All));
        Assert.Null(await Assistant(new CapturingOpenAi("""{"steps":[{"tool":"drop_table"}]}""")).PlanAsync("x", "", AgentTools.All));
    }
}
