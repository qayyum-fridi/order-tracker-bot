using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

// Photo import (MVP): "photo import" offers taps for what to photograph. Choosing a kind (customers, products, wages) makes the next photo
// read as that kind; the result is shown back and saved only after YES (the existing picture flow). Orders keep the normal screenshot
// path, where the draft is saved only after Save. Nothing is read or saved without a photo.
public partial class ConversationEngine
{
    /// <summary>How long a chosen kind waits for its photo.</summary>
    private static readonly TimeSpan PendingPhotoWindow = TimeSpan.FromMinutes(10);

    private static readonly ChoiceOption[] PhotoMenuOptions =
    {
        new("📷 Customers photo", "photo customers"),
        new("📦 Products photo", "photo products"),
        new("👷 Wages photo", "photo wages"),
        new("🧾 Order screenshot", "photo orders"),
        new("👥 Customers list", "customers"),
        new("📦 Products list", "catalog"),
        new("🧾 Today's orders", "orders today"),
    };

    private async Task HandlePhotoImportMenuAsync(Seller seller, CancellationToken ct) =>
        await SendChoicesAsync(seller.WhatsAppPhoneNumber, MessageTemplates.Get("photo.001"), PhotoMenuOptions, ct);

    private async Task HandlePhotoKindAsync(Seller seller, SessionContextData ctx, string kind, CancellationToken ct)
    {
        if (kind is "order" or "orders")
        {
            ClearPendingPhoto(ctx);
            await ReplyAsync(seller, MessageTemplates.Get("photo.005"), ct);
            return;
        }

        var label = kind.StartsWith("cust", StringComparison.Ordinal) ? PictureLabels.Customer
            : kind is "catalog" || kind.StartsWith("prod", StringComparison.Ordinal) ? PictureLabels.Products
            : PictureLabels.Wages;
        ctx.PendingPhotoKind = label;
        ctx.PendingPhotoAt = _clock.GetUtcNow().UtcDateTime;
        var key = label switch { PictureLabels.Customer => "photo.002", PictureLabels.Products => "photo.003", _ => "photo.004" };
        await ReplyAsync(seller, MessageTemplates.Get(key), ct);
    }

    /// <summary>
    /// The caption to read a photo with: its own caption when it has one, otherwise the kind chosen just before (if still fresh).
    /// Every photo clears the chosen kind, so one choice reads one photo.
    /// </summary>
    private string? PhotoCaptionFor(SessionContextData ctx, string? caption)
    {
        var chosen = ctx.PendingPhotoKind;
        var at = ctx.PendingPhotoAt;
        var pages = ctx.PendingPhotoPages;
        if (!string.IsNullOrWhiteSpace(caption) || chosen is null || at is not { } chosenAt
            || _clock.GetUtcNow().UtcDateTime - chosenAt > PendingPhotoWindow || pages >= MaxPhotoPagesPerRound)
        {
            ClearPendingPhoto(ctx);
            return caption;
        }
        // Several pages of one book: the kind stays for the next photos, up to the round limit; the seller then taps the kind again.
        ctx.PendingPhotoPages = pages + 1;
        return chosen;
    }

    /// <summary>Pages read for one chosen kind before the seller must choose the kind again (the YES list holds them together).</summary>
    private const int MaxPhotoPagesPerRound = 5;

    private static void ClearPendingPhoto(SessionContextData ctx)
    {
        ctx.PendingPhotoKind = null;
        ctx.PendingPhotoAt = null;
        ctx.PendingPhotoPages = 0;
    }
}
