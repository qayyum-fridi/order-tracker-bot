using System.Security.Cryptography;
using System.Text;

namespace OrderTrackerBot.Application.Intents;

/// <summary>What a sync does with a seeded row when the shipped text may have changed.</summary>
public enum SeedAction
{
    /// <summary>Nothing: the row already carries the shipped text's fingerprint.</summary>
    None,
    /// <summary>The row text equals the shipped text but has no fingerprint yet: record it.</summary>
    Stamp,
    /// <summary>The shipped text changed and nobody edited the row (its text still matches the old fingerprint): take the new text.</summary>
    Update,
    /// <summary>The row was edited (or its origin is unknown): leave it alone.</summary>
    Leave,
}

/// <summary>
/// Fingerprints of seeded text, so a change in the shipped JSON can reach rows nobody edited without overwriting an operator's edit.
/// A row keeps the fingerprint of the text it was seeded with; if its current text still matches that, it was never edited.
/// </summary>
public static class SeedHash
{
    public static string Of(params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u0001', parts))))[..32];

    public static SeedAction Decide(string? storedSeedHash, string currentHash, string shippedHash)
    {
        if (storedSeedHash == shippedHash) return SeedAction.None;
        if (storedSeedHash is null) return currentHash == shippedHash ? SeedAction.Stamp : SeedAction.Leave;
        return currentHash == storedSeedHash ? SeedAction.Update : SeedAction.Leave;
    }
}
