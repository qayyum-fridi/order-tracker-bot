namespace OrderTrackerBot.Application.Abstractions;

/// <summary>
/// Large-catalog onboarding path: the seller fills a Google Sheet template (one product per row,
/// "Name" then "Price") and sends the share link back instead of typing 50+ products one by one.
/// </summary>
public interface ICatalogSheetImporter
{
    /// <summary>
    /// Fetches the sheet and returns each row as a "Name - Price" line ready for
    /// <see cref="OrderTrackerBot.Application.Conversation.CommandParser.TryParseProductLine"/>.
    /// Returns null if the sheet couldn't be fetched (not shared publicly, deleted, network error).
    /// </summary>
    Task<IReadOnlyList<string>?> FetchProductLinesAsync(string sheetUrl, CancellationToken cancellationToken = default);
}
