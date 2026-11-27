using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

// Stage 6: a reply is bound to one seller, one user and one pending field; an invalid reply asks the same field again; expiry closes the record.
public class ClarificationManagerTests
{
    private static readonly DateTime T0 = new(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc);

    private static PendingAction Pending(params string[] missing) =>
        ClarificationManager.Create("p1", sellerId: 1, userId: "923001111111", action: "create_order",
            missingFields: missing, firstQuestion: ClarificationManager.QuestionFor(missing[0]), now: T0);

    private static string? Accept(string field, string reply) => reply;

    [Fact]
    public void Create_SetsExpiryAndWaitingStatus()
    {
        var p = Pending("quantity");
        Assert.Equal(PendingStatus.Waiting, p.Status);
        Assert.Equal(T0 + ClarificationManager.DefaultTtl, p.ExpiresAt);
    }

    [Fact]
    public void Reply_FromAnotherSellerOrUser_IsNotForThisPending()
    {
        var p = Pending("quantity");
        Assert.Equal(ClarificationOutcome.NotForThisPending, ClarificationManager.Answer(p, 2, "923001111111", "2", T0, Accept).Outcome);
        Assert.Equal(ClarificationOutcome.NotForThisPending, ClarificationManager.Answer(p, 1, "923009999999", "2", T0, Accept).Outcome);
        Assert.Equal(PendingStatus.Waiting, p.Status);
        Assert.Empty(p.Answers);
    }

    [Fact]
    public void InvalidReply_AsksTheSameFieldAgain_AndKeepsWaiting()
    {
        var p = Pending("quantity", "status");
        var result = ClarificationManager.Answer(p, 1, "923001111111", "many", T0, (field, reply) => reply == "2" ? reply : null);

        Assert.Equal(ClarificationOutcome.Invalid, result.Outcome);
        Assert.Equal("quantity", p.MissingFields[0]);
        Assert.Equal(ClarificationManager.QuestionFor("quantity"), result.Question);
        Assert.Empty(p.Answers);
    }

    [Fact]
    public void ValidReply_KeepsValue_AndAsksOnlyTheNextMissingField()
    {
        var p = Pending("quantity", "status");
        var result = ClarificationManager.Answer(p, 1, "923001111111", "2", T0, Accept);

        Assert.Equal(ClarificationOutcome.Ask, result.Outcome);
        Assert.Equal("2", p.Answers["quantity"]);
        Assert.Equal(new[] { "status" }, p.MissingFields);
        Assert.Equal(ClarificationManager.QuestionFor("status"), result.Question);
    }

    [Fact]
    public void AllFieldsAnswered_IsReady_ForReferenceAndSafetyRerun()
    {
        var p = Pending("quantity");
        var result = ClarificationManager.Answer(p, 1, "923001111111", "2", T0, Accept);

        Assert.Equal(ClarificationOutcome.Ready, result.Outcome);
        Assert.Empty(p.MissingFields);
        Assert.Equal(PendingStatus.Waiting, p.Status);   // completed only after the action actually runs
    }

    [Fact]
    public void ReplyAfterExpiry_Expires_AndIsNotApplied()
    {
        var p = Pending("quantity");
        var late = T0 + ClarificationManager.DefaultTtl + TimeSpan.FromSeconds(1);

        var result = ClarificationManager.Answer(p, 1, "923001111111", "2", late, Accept);

        Assert.Equal(ClarificationOutcome.Expired, result.Outcome);
        Assert.Equal(PendingStatus.Expired, p.Status);
        Assert.Empty(p.Answers);
        Assert.Equal(ClarificationOutcome.Closed, ClarificationManager.Answer(p, 1, "923001111111", "2", T0, Accept).Outcome);
    }

    [Fact]
    public void Completed_OrCancelled_RecordsAcceptNoFurtherReplies()
    {
        var done = Pending("quantity");
        ClarificationManager.Complete(done);
        Assert.Equal(ClarificationOutcome.Closed, ClarificationManager.Answer(done, 1, "923001111111", "2", T0, Accept).Outcome);

        var cancelled = Pending("quantity");
        ClarificationManager.Cancel(cancelled);
        Assert.Equal(ClarificationOutcome.Closed, ClarificationManager.Answer(cancelled, 1, "923001111111", "2", T0, Accept).Outcome);
    }

    [Fact]
    public void Create_WithoutMissingFields_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            ClarificationManager.Create("p2", 1, "u", "create_order", Array.Empty<string>(), "q", T0));
    }
}
