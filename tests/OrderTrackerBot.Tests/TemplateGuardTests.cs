using System.Text.RegularExpressions;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>
/// Seller-facing wording lives in Templates/Messages/*.json. A send call must take MessageTemplates.Get(...), not a prose literal.
/// Checks the usual form (the literal is the message argument); a literal inside a ternary or a variable is not seen, so review those by hand.
/// </summary>
public class TemplateGuardTests
{
    // A send call whose message argument is a string literal with at least two words in it.
    private static readonly Regex InlineProseSend = new(
        @"(?:ReplyAsync\(seller,|SendTextMessageAsync\([^,()\n]+,|SendButtonsMessageAsync\([^,()\n]+,|SendListMessageAsync\([^,()\n]+,|SendChoicesAsync\([^,()\n]+,|SendFlowMessageAsync\([^,()\n]+,[^,()\n]+,)\s*\$?@?""[^""\n]*?[\p{L}]{2,}\s+[\p{L}]{2,}",
        RegexOptions.Compiled);

    private static string ApplicationSourceDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "OrderTrackerBot.Application")))
            dir = dir.Parent;
        return dir is null
            ? throw new DirectoryNotFoundException("src/OrderTrackerBot.Application was not found above the test binaries.")
            : Path.Combine(dir.FullName, "src", "OrderTrackerBot.Application");
    }

    [Fact]
    public void SendCalls_DoNotCarryProseLiterals_UseMessageTemplates()
    {
        var root = ApplicationSourceDirectory();
        var files = Directory.EnumerateFiles(Path.Combine(root, "Conversation"), "*.cs")
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "Formatting"), "*.cs"));

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (Match match in InlineProseSend.Matches(text))
            {
                var line = text.Take(match.Index).Count(c => c == '\n') + 1;
                offenders.Add($"{Path.GetFileName(file)}:{line}: {match.Value.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Move these messages into Templates/Messages and send MessageTemplates.Get(key):\n" + string.Join("\n", offenders));
    }
}
