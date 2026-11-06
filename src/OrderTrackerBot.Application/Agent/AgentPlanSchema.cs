using System.Text.Json.Nodes;

namespace OrderTrackerBot.Application.Agent;

/// <summary>
/// The JSON Schema the planner's answer must follow (OpenAI structured outputs, <c>strict: true</c>), built from the tool list so a new tool needs no schema edit.
/// Strict mode wants every object closed (<c>additionalProperties: false</c>) with every property required, so "not said" is a <c>null</c>, which
/// <see cref="AgentPlanParser"/> already reads as an omitted argument. With the shape enforced, the small model can no longer derail into a runaway answer.
/// </summary>
public static class AgentPlanSchema
{
    public const string Name = "agent_plan";

    public static JsonObject Build(IReadOnlyList<AgentTool> tools)
    {
        var toolNames = new JsonArray(tools.Select(t => (JsonNode?)JsonValue.Create(t.Name)).ToArray());

        var item = Closed(new JsonObject
        {
            ["product_id"] = Nullable("integer"),
            ["name"] = new JsonObject { ["type"] = "string" },
            ["qty"] = new JsonObject { ["type"] = "integer" },
        });

        // One closed object per tool, holding only that tool's arguments: a shared "args" object made the model fill keys that do not belong (a "name" on an edit,
        // a catalog price copied into an edit that only changes stock).
        var steps = new JsonArray();
        foreach (var tool in tools)
        {
            var argProps = new JsonObject();
            foreach (var arg in tool.Args)
                argProps[arg.Name] = Nullable(arg.Kind switch { "number" => "number", "product_id" or "customer_id" => "integer", _ => "string" });
            var props = new JsonObject
            {
                ["tool"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(tool.Name) },
                ["args"] = Closed(argProps),
            };
            if (tool.TakesItems) props["items"] = new JsonObject { ["type"] = "array", ["items"] = item.DeepClone() };
            steps.Add(Closed(props));
        }

        var awaiting = Closed(new JsonObject
        {
            ["tool"] = new JsonObject { ["type"] = "string", ["enum"] = toolNames.DeepClone() },
            ["count"] = new JsonObject { ["type"] = "integer" },
        });

        return Closed(new JsonObject
        {
            ["steps"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["anyOf"] = steps } },
            ["question"] = Nullable("string"),
            ["awaiting"] = new JsonObject { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "null" }, awaiting) },
            ["confidence"] = new JsonObject { ["type"] = "number" },
        });
    }

    private static JsonObject Nullable(string type) => new() { ["type"] = new JsonArray(type, "null") };

    /// <summary>An object schema with the given properties, all required, nothing else allowed.</summary>
    private static JsonObject Closed(JsonObject properties) => new()
    {
        ["type"] = "object",
        ["properties"] = properties,
        ["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray()),
        ["additionalProperties"] = false,
    };
}
