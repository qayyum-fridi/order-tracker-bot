namespace OrderTrackerBot.Application.Abstractions;

public enum IssueSeverity { Warning, Error }

/// <summary>A stable, searchable code per kind of failure. Never renumber a shipped code — alerts and seller messages quote it.</summary>
public sealed record IssueCode(string Code, string Title, IssueSeverity Severity);

public static class IssueCodes
{
    // 1xxx — handling an inbound WhatsApp message
    public static readonly IssueCode InboundMessageFailed = new("OTB-1001", "Inbound message processing failed", IssueSeverity.Error);
    public static readonly IssueCode ScreenshotFailed = new("OTB-1002", "Screenshot processing failed", IssueSeverity.Error);
    public static readonly IssueCode FlowSubmissionFailed = new("OTB-1003", "WhatsApp Flow submission failed", IssueSeverity.Error);
    public static readonly IssueCode UnsupportedMediaReplyFailed = new("OTB-1004", "Reply to unsupported media failed", IssueSeverity.Warning);

    // 2xxx — WhatsApp Cloud API (outbound)
    public static readonly IssueCode WhatsAppSendRejected = new("OTB-2001", "WhatsApp rejected an outbound message", IssueSeverity.Warning);
    public static readonly IssueCode WhatsAppSendThrew = new("OTB-2002", "WhatsApp send failed (network/exception)", IssueSeverity.Error);
    public static readonly IssueCode WhatsAppTokenRejected = new("OTB-2003", "WhatsApp access token rejected (401/403)", IssueSeverity.Error);

    // 3xxx — OpenAI, 4xxx — Instagram, 5xxx — background jobs
    public static readonly IssueCode OpenAiAnalysisFailed = new("OTB-3001", "OpenAI order analysis failed", IssueSeverity.Error);
    public static readonly IssueCode InstagramCommentFailed = new("OTB-4001", "Instagram comment processing failed", IssueSeverity.Error);
    public static readonly IssueCode ScheduledJobFailed = new("OTB-5001", "Scheduled job run failed", IssueSeverity.Error);
}

/// <summary>Tells the founder which seller hit which problem. Implementations must never throw.</summary>
public interface IIssueReporter
{
    Task ReportAsync(IssueCode code, string? sellerPhone, string? detail = null, Exception? exception = null, CancellationToken cancellationToken = default);
}
