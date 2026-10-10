namespace OrderTrackerBot.Domain.Entities;

/// <summary>
/// One occurrence of a reported problem (see IssueCodes). Severity "Error" = a system failure; "Warning" = something
/// a seller ran into. The admin panel lists the two separately.
/// </summary>
public class IssueRecord
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public required string Code { get; set; }
    public required string Title { get; set; }
    public required string Severity { get; set; }
    public string? SellerPhone { get; set; }
    public string? BusinessName { get; set; }
    public string? Detail { get; set; }
    public string? Error { get; set; }
}
