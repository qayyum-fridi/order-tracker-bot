namespace OrderTrackerBot.Domain.Entities;

/// <summary>
/// One seller-facing message in one language, changeable at runtime. <see cref="Language"/> is <c>roman_urdu</c> (the source wording),
/// <c>english</c> or <c>urdu_script</c>. Placeholders stay <c>{0}</c>, <c>{1}</c>, ... A row that is inactive, or a key with no row,
/// shows the wording shipped in the build. Seeded from the embedded JSON files once; after that the rows are the source of truth.
/// </summary>
public class MessageTemplateRow
{
    public int Id { get; set; }
    public required string Language { get; set; }
    public required string Key { get; set; }
    public required string Text { get; set; }
    public bool Active { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
