using System.Security.Cryptography;
using System.Text;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>The keyed hash that labels a seller's captured messages. Null when no salt is set: nothing is captured then.</summary>
public static class UnplacedSellerHash
{
    public static string? For(string salt, int sellerId)
    {
        if (string.IsNullOrEmpty(salt)) return null;
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(salt), Encoding.UTF8.GetBytes(sellerId.ToString()));
        return Convert.ToHexString(mac)[..16];
    }
}
