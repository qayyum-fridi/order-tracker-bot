using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using Xunit;

namespace OrderTrackerBot.Tests;

public class BuildInfoTests
{
    [Theory]
    [InlineData("f153b83145abc0e3e4b2e167ed4072367f2034ee", "f153b83")]
    [InlineData("F153B83145ABC0E3E4B2E167ED4072367F2034EE", "f153b83")]
    [InlineData("f153b83", "f153b83")]
    [InlineData("v1.4.2", "v1.4.2")]
    [InlineData("unknown", "unknown")]
    [InlineData("", "unknown")]
    [InlineData(null, "unknown")]
    [InlineData("  ", "unknown")]
    [InlineData("a b; rm -rf /", "unknown")]
    [InlineData("x\nInjected: line", "unknown")]
    public void Version_IsShortened_AndUnsafeOrMissingIsUnknown(string? raw, string expected) =>
        Assert.Equal(expected, new BuildInfo { Version = raw }.Version);

    [Theory]
    [InlineData("version")]
    [InlineData("Version")]
    [InlineData("build")]
    [InlineData("bot version")]
    public void Parser_RecognisesVersion(string text) => Assert.Equal(CommandKind.Version, CommandParser.TryParse(text)?.Kind);

    [Theory]
    [InlineData("version 2 of my order")]
    [InlineData("build a catalog")]
    public void Parser_IgnoresSentences(string text) => Assert.NotEqual(CommandKind.Version, CommandParser.TryParse(text)?.Kind);
}

public class BuildInfoEngineTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly List<string> _sent = new();

    public BuildInfoEngineTests() =>
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _sent.Add(t)).Returns(Task.CompletedTask);

    private async Task<ConversationEngine> EngineAsync(Infrastructure.Persistence.AppDbContext db, BuildInfo? build)
    {
        db.Sellers.Add(new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } });
        await db.SaveChangesAsync();
        return new ConversationEngine(db, Mock.Of<IAiOrderAssistant>(), _sender.Object, Mock.Of<IFounderAlertNotifier>(), build: build);
    }

    [Fact]
    public async Task Version_ShowsTheDeployedBuild()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await EngineAsync(db, new BuildInfo { Version = "f153b83145abc0e3e4b2e167ed4072367f2034ee" });

        await engine.HandleIncomingMessageAsync(Phone, "version", default);

        Assert.Contains(_sent, m => m.Contains("Version: f153b83") && !m.Contains("set nahi hua"));
    }

    [Fact]
    public async Task Version_WhenNotSet_SaysUnknown()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await EngineAsync(db, null);

        await engine.HandleIncomingMessageAsync(Phone, "version", default);

        Assert.Contains(_sent, m => m.Contains("Version: unknown") && m.Contains("set nahi hua"));
    }

    public void Dispose() => _dbFactory.Dispose();
}
