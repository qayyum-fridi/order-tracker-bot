using Microsoft.Extensions.Configuration;
using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

// Features:Agent (the planner) is for development only until it has run with real sellers' sentences: on in appsettings.Development.json, off everywhere else.
public class AgentFlagConfigTests
{
    private static string ApiFile(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "src", "OrderTrackerBot.Api", name);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(name);
    }

    private static FeatureOptions Features(params string[] files)
    {
        var builder = new ConfigurationBuilder();
        foreach (var file in files) builder.AddJsonFile(ApiFile(file), optional: false);
        return builder.Build().GetSection(FeatureOptions.SectionName).Get<FeatureOptions>() ?? new FeatureOptions();
    }

    [Fact]
    public void TheAgentIsOffByDefault_AndInProductionSettings() =>
        Assert.False(Features("appsettings.json").Agent);

    [Fact]
    public void TheAgentIsOnInDevelopment_WithoutSwitchingOffTheOtherFeatures()
    {
        var dev = Features("appsettings.json", "appsettings.Development.json");

        Assert.True(dev.Agent);
        Assert.True(dev.RouterContext);     // the base file's flags still apply
        Assert.True(dev.StrategyLearning);
    }
}
