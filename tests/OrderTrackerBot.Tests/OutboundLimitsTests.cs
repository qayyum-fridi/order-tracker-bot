using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.WhatsApp;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>WhatsApp refuses an interactive body over 1024 characters (OTB-2001 "Body text length invalid"); the sender and the translator must never produce one.</summary>
public class OutboundLimitsTests
{
    private sealed class Recorder : HttpMessageHandler
    {
        public List<JsonDocument> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    private readonly Recorder _http = new();
    private readonly Mock<IIssueReporter> _issues = new();

    private WhatsAppSender Sender() => new(new HttpClient(_http), Options.Create(new WhatsAppOptions { AccessToken = "t", PhoneNumberId = "1" }),
        Mock.Of<IOptionsMonitor<WhatsAppTemplatesOptions>>(), NullLogger<WhatsAppSender>.Instance, _issues.Object);

    private static string Type(JsonDocument d) => d.RootElement.GetProperty("type").GetString()!;
    private static string TextBody(JsonDocument d) => d.RootElement.GetProperty("text").GetProperty("body").GetString()!;

    [Fact]
    public async Task ButtonsBodyOver1024_GoesOutAsPlainText_WithTheOptions_InOneCall()
    {
        await Sender().SendButtonsMessageAsync("923001234567", new string('a', 1100), new[] { "✅ Save", "❌ Cancel" });

        var sent = Assert.Single(_http.Requests);
        Assert.Equal("text", Type(sent));
        Assert.Contains(new string('a', 1100), TextBody(sent));
        Assert.EndsWith("✅ Save / ❌ Cancel", TextBody(sent));
        _issues.Verify(i => i.ReportAsync(It.IsAny<IssueCode>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Exception?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ButtonsBodyOfExactly1024_StaysInteractive()
    {
        await Sender().SendButtonsMessageAsync("923001234567", new string('a', 1024), new[] { "Menu" });

        Assert.Equal("interactive", Type(Assert.Single(_http.Requests)));
    }

    [Fact]
    public async Task EmptyButtonsBody_SendsTheLabelsAsText()
    {
        await Sender().SendButtonsMessageAsync("923001234567", "  ", new[] { "Menu", "Guide" });

        Assert.Equal("Menu / Guide", TextBody(Assert.Single(_http.Requests)));
    }

    [Fact]
    public async Task ListBodyOver1024_GoesOutAsPlainText()
    {
        await Sender().SendListMessageAsync("923001234567", new string('b', 1500), "Menu", new[] { new MenuSection("S", new[] { new MenuRow("menu", "Menu") }) });

        var sent = Assert.Single(_http.Requests);
        Assert.Equal("text", Type(sent));
        Assert.Equal(new string('b', 1500), TextBody(sent));
    }

    [Fact]
    public async Task EmptyText_IsNotSent()
    {
        await Sender().SendTextMessageAsync("923001234567", "   ");

        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task VeryLongText_IsSplitAt4096_AndNothingIsLost()
    {
        var lines = Enumerable.Range(1, 900).Select(i => $"line {i:0000} " + new string('x', 5)).ToList();
        var text = string.Join("\n", lines);
        Assert.True(text.Length > 8192);

        await Sender().SendTextMessageAsync("923001234567", text);

        Assert.True(_http.Requests.Count >= 3);
        Assert.All(_http.Requests, r => Assert.True(TextBody(r).Length <= 4096));
        var joined = string.Join("\n", _http.Requests.Select(TextBody));
        Assert.Equal(lines.Count, joined.Split('\n').Length);
        Assert.Equal(text, joined);
    }

    [Fact]
    public async Task TranslatedBodyOver1024_FallsBackToTheOriginalText()
    {
        var inner = new Mock<IWhatsAppSender>();
        string? sentBody = null;
        inner.Setup(s => s.SendButtonsMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, b, _, _) => sentBody = b).Returns(Task.CompletedTask);
        var ai = new Mock<IAiOrderAssistant>();
        ai.Setup(a => a.TranslateAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> texts, string _, CancellationToken _) => texts.Select(t => t.Length > 100 ? new string('t', 1200) : t).ToList());
        var sender = new TranslatingSender(inner.Object, ai.Object) { Seller = new Seller { WhatsAppPhoneNumber = "923001234567", PreferredLanguage = Lang.English } };
        var original = new string('o', 500);

        await sender.SendButtonsMessageAsync("923001234567", original, new[] { "Menu" });

        Assert.Equal(original, sentBody);
    }

    [Fact]
    public async Task TranslatedBodyWithin1024_IsUsed()
    {
        var inner = new Mock<IWhatsAppSender>();
        string? sentBody = null;
        inner.Setup(s => s.SendButtonsMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, b, _, _) => sentBody = b).Returns(Task.CompletedTask);
        var ai = new Mock<IAiOrderAssistant>();
        ai.Setup(a => a.TranslateAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> texts, string _, CancellationToken _) => texts.Select(t => t == "Aap kaise hain" ? "How are you" : t).ToList());
        var sender = new TranslatingSender(inner.Object, ai.Object) { Seller = new Seller { WhatsAppPhoneNumber = "923001234567", PreferredLanguage = Lang.English } };

        await sender.SendButtonsMessageAsync("923001234567", "Aap kaise hain", new[] { "Menu" });

        Assert.Equal("How are you", sentBody);
    }
}
