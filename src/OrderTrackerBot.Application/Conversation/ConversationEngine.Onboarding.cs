using System.Text.RegularExpressions;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

public partial class ConversationEngine
{
    private static readonly Regex ProductLine = new(@"^(.+?)\s*-\s*(\d+(?:\.\d+)?)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DoneOrSkip = new(@"^(done|skip)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private async Task HandleOnboardingAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        switch (session.State)
        {
            case ConversationState.OnboardingBusinessName:
                seller.BusinessName = message;
                SetState(session, ConversationState.OnboardingCatalogSize);
                await ReplyAsync(seller, "Great! Ab products add karte hain. Kitne products hain — 20 se kam ya zyada?", ct);
                return;

            case ConversationState.OnboardingCatalogSize:
                SetState(session, ConversationState.OnboardingAddProduct);
                await ReplyAsync(seller, "Theek hai, ek ek karke bataiye — naam aur price (e.g. 'Lawn Suit - 3500')", ct);
                return;

            case ConversationState.OnboardingAddProduct:
                if (DoneOrSkip.IsMatch(message))
                {
                    seller.OnboardingComplete = true;
                    SetState(session, ConversationState.Idle);
                    var reply = message.Equals("skip", StringComparison.OrdinalIgnoreCase)
                        ? "Theek hai — jab chahein \"catalog\" likh kar wapas add kar saktay hain."
                        : "🎉 Setup complete — catalog saved. Ab jab bhi order aaye, forward kar dein ya likh dein 'new order: ...'";
                    await ReplyAsync(seller, reply, ct);
                    return;
                }

                var match = ProductLine.Match(message);
                if (match.Success)
                {
                    _db.Products.Add(new Product
                    {
                        SellerId = seller.Id,
                        Name = match.Groups[1].Value.Trim(),
                        Price = decimal.Parse(match.Groups[2].Value)
                    });
                    await ReplyAsync(seller, "✅ Added. Agla? (ya 'done' likhein jab khatam ho)", ct);
                    return;
                }

                // Mid-onboarding interruption (spec screen 17): a recognised command doesn't
                // execute yet — the catalog step must finish (or be explicitly skipped) first.
                if (CommandParser.TryParse(message) is not null)
                {
                    await ReplyAsync(seller,
                        "Abhi koi order nahi hai — pehle catalog complete karein.\n" +
                        "Product name/price bhejein, ya \"skip\" likh kar baad mein karein.", ct);
                    return;
                }

                await ReplyAsync(seller, "Samajh nahi aaya — format: 'naam - price' (e.g. 'Kurti - 1800'), ya 'done'/'skip'.", ct);
                return;

            default:
                // New seller, not yet in an onboarding step.
                if (CommandParser.TryParse(message)?.Kind == CommandKind.Start)
                {
                    SetState(session, ConversationState.OnboardingBusinessName);
                    await ReplyAsync(seller, "Salam! 👋 Main aapka order assistant hoon. Pehle business ka naam bataiye?", ct);
                }
                else
                {
                    await ReplyAsync(seller, "Salam! 👋 Main aapka order assistant hoon. Shuru karne ke liye 'start' likhein.", ct);
                }
                return;
        }
    }
}
