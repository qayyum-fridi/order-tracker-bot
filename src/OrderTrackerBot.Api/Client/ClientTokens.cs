using System.Security.Cryptography;
using System.Text;

namespace OrderTrackerBot.Api.Client;

/// <summary>
/// Bearer tokens for the client dashboard, one per seller. Only the SHA-256 hash is stored, so a database leak
/// doesn't expose working tokens. The token is shown once, when an admin issues it.
/// </summary>
public static class ClientTokens
{
    public const string Prefix = "otc_";

    public static string Create() => Prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
