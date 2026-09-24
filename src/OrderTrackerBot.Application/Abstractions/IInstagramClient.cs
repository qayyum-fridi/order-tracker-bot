using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Abstractions;

/// <summary>Instagram comment access (manage-comments scope): the OAuth connect link and posting public comment replies.</summary>
public interface IInstagramClient
{
    /// <summary>False when the Instagram app credentials aren't configured — "connect instagram" then explains it isn't available yet.</summary>
    bool IsConfigured { get; }

    /// <summary>Short link the seller opens to start OAuth; it carries a signed, expiring token identifying the seller.</summary>
    string BuildConnectLink(int sellerId);

    /// <summary>Posts a public reply under a comment. Returns false when Meta rejected it or the token is invalid.</summary>
    Task<bool> ReplyToCommentAsync(InstagramConnection connection, string commentId, string message, CancellationToken cancellationToken = default);
}

/// <summary>Used when no Instagram app is configured (and in tests).</summary>
public sealed class NullInstagramClient : IInstagramClient
{
    public bool IsConfigured => false;
    public string BuildConnectLink(int sellerId) => "";
    public Task<bool> ReplyToCommentAsync(InstagramConnection connection, string commentId, string message, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
