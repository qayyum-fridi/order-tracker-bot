namespace OrderTrackerBot.Api.Admin;

/// <summary>Daily numbers for a date range. Days follow Asia/Karachi; empty days are included so charts have no gaps.</summary>
public sealed record DailyReportDto(string From, string To, DailyTotalsDto Totals, IReadOnlyList<DailyRowDto> Days);

public sealed record DailyTotalsDto(
    int NewSellers,
    int ActiveSellers,
    int InboundMessages,
    int Orders,
    decimal Revenue,
    int Errors,
    int Issues);

public sealed record DailyRowDto(
    string Date,
    int NewSellers,
    int ActiveSellers,
    int InboundMessages,
    int Orders,
    decimal Revenue,
    int Errors,
    int Issues);

/// <summary>How far a seller got in setup. Each flag is one step of the checklist.</summary>
public sealed record SetupStepsDto(
    bool BusinessName,
    bool City,
    bool BusinessType,
    bool OnboardingComplete,
    bool Product,
    bool PaymentMethod,
    bool Order);

public sealed record SellerSetupDto(
    int Id,
    string WhatsAppPhoneNumber,
    string? BusinessName,
    int StepsDone,
    int StepsTotal,
    SetupStepsDto Steps,
    /// <summary>Where the seller is in onboarding, when it is unfinished (e.g. "business name step").</summary>
    string? CurrentStep,
    /// <summary>Why the seller needs attention, or null. Set when onboarding is unfinished after 24 hours, or no order after 7 days of setup.</summary>
    string? StuckReason,
    DateTime? LastMessageAt,
    DateTime CreatedAt);

public sealed record IssueCountDto(string Code, string Title, string Severity, int Count);

public sealed record IssueSummaryDto(
    string From,
    string To,
    IReadOnlyList<IssueCountDto> Errors,
    IReadOnlyList<IssueCountDto> Issues);

public sealed record IssueRecordDto(
    int Id,
    DateTime CreatedAt,
    string Code,
    string Title,
    string Severity,
    string? SellerPhone,
    string? BusinessName,
    string? Detail,
    string? Error);
