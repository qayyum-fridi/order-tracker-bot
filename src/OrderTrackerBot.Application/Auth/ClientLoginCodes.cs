using System.Security.Cryptography;
using System.Text;

namespace OrderTrackerBot.Application.Auth;

/// <summary>
/// One-time codes for the client dashboard's phone login. A code is only sent to the WhatsApp chat of the seller who
/// asked for it, so typing the code proves the seller owns the number. Only a salted hash is stored.
/// </summary>
public static class ClientLoginCodes
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ResendWait = TimeSpan.FromSeconds(60);

    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public static string Hash(int sellerId, string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"client-login:{sellerId}:{code}"))).ToLowerInvariant();

    /// <summary>Last 10 digits of a mobile number however it was typed ("0312…", "+92 312…", "92312…"); null when fewer than 10 digits.</summary>
    public static string? SubscriberDigits(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var digits = new string(raw.Where(char.IsAsciiDigit).ToArray());
        return digits.Length >= 10 ? digits[^10..] : null;
    }
}
