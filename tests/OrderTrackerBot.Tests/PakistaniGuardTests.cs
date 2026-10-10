using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

// The voice rewrite guard, on realistic lines: a rewrite that keeps what the seller meant is accepted, one that changes it is refused.
public class PakistaniGuardTests
{
    [Theory]
    // Counts below 100 must match what was said
    [InlineData("Teen piece chahiye", "quantity 3")]
    [InlineData("Do piece chahiye", "2 pieces")]
    [InlineData("Pehla wala", "1")]                                  // an ordinal choice
    [InlineData("pehle wala ka quantity teen kar do", "1 = 3")]      // the index "1" is not a count
    // Negations keep their scope
    [InlineData("Nahi bhai, cancel karo", "cancel")]                  // a refusal followed by an instruction to cancel
    [InlineData("Kurti nahi, Lawn chahiye", "Lawn")]
    [InlineData("Cancel nahi karna, sirf address change karna hai", "address change")]
    [InlineData("cancel mat karna... sorry order id 105 cancel kar do", "cancel order 105")]
    // Comparators and modifiers survive
    [InlineData("Paanch hazaar se kam", "price under 5000")]
    [InlineData("Panj hazaar ton ghatt rakhna", "price under 5000")]
    [InlineData("Teen hazaar tak kuch dikhao", "catalog under 3000")]
    [InlineData("Paanch hazaar including delivery", "price 5000 including delivery")]
    [InlineData("Bhai teen hazaar paanch sau ka order bana do, delivery alag hai", "price 3500 delivery separate")]
    // Approval, refusal and corrections that are faithful
    [InlineData("Haan bhai order kar do", "yes")]
    [InlineData("Haan, theek hai", "yes")]
    [InlineData("Paanch hazaar nahi, chhe hazaar likho", "price 6000")]
    [InlineData("Nahi nahi, paanch hazaar mat likhna, teen hazaar paanch sau likho", "price 3500")]
    [InlineData("Teen hazaar ka item hai aur paanch sau delivery, total kitna hua?", "price 3000 delivery 500")]
    public void FaithfulRewrite_IsAccepted(string said, string rewrite)
    {
        Assert.Null(ConversationEngine.WhyUnfaithful(said, rewrite));
    }

    [Theory]
    // Counts
    [InlineData("Teen piece chahiye", "quantity 5")]                 // a count nobody said
    [InlineData("Do piece", "quantity 3")]
    [InlineData("Teen piece nahi, do chahiye", "quantity 3")]         // taken back
    [InlineData("Maine teen nahi do bole thay", "quantity 3")]        // taken back, Roman Urdu word order
    // Negated amounts and items
    [InlineData("Paanch hazaar mat karna", "price 5000")]             // refused amount
    [InlineData("Kurti nahi, Lawn chahiye", "Kurti - 1800")]          // refused item comes back
    [InlineData("cancel mat karna, sirf address change", "cancel")]    // refused action set as the rewrite
    // Unconditional approval
    [InlineData("Haan lekin quantity do kar do", "yes")]              // 86
    [InlineData("Haan, theek hai... nahi ruko bhai, pehle mujhse confirm kar lena", "yes")] // 100
    [InlineData("Rate 3500 hai ya 350?", "yes")]                      // a question is not an approval
    [InlineData("Haan bhai order kar do, delivery alag", "yes")]      // the delivery part is dropped
    // Comparators and modifiers dropped
    [InlineData("Paanch hazaar se kam", "price 5000")]
    [InlineData("Panj hazaar ton ghatt rakhna", "price 5000")]
    [InlineData("Panj hazaar na rakhna", "price 5000")]
    [InlineData("Paanch hazaar aur delivery", "price 5000")]
    [InlineData("Paanch hazaar including delivery", "price 5000 delivery 0")] // "including" is not "free"
    // Amounts
    [InlineData("Teen hazaar ka item hai aur paanch sau delivery", "price 3000")]
    [InlineData("Price 3500 nahi, 5300 hai", "price 3500")]          // digit runs compared whole
    [InlineData("Price 3500 nahi, 5300 hai", "price 1500")]
    public void UnfaithfulRewrite_IsRefused(string said, string rewrite)
    {
        Assert.NotNull(ConversationEngine.WhyUnfaithful(said, rewrite));
    }
}
