using System.Globalization;
using System.Text.RegularExpressions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// A picture the seller labels as an expense, wages or a customer: the caption, or a short text / voice message sent after it, names the type;
// the AI then reads the picture for that type's fields only, and the typed command it builds waits for YES (the voice-confirm mechanism)
// before anything is saved. "order" reads the picture as an order screenshot, "other" saves nothing.
public partial class ConversationEngine
{
    private static readonly TimeSpan PictureLabelWindow = TimeSpan.FromMinutes(10);

    /// <summary>True when a fresh remembered picture exists and <paramref name="message"/> is only a type word: the picture is then read as that type.</summary>
    private async Task<bool> TryHandlePictureLabelAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        if (ctx.PendingPictureMediaId is not { } mediaId) return false;
        if (ctx.PendingPictureAt is not { } at || _clock.GetUtcNow().UtcDateTime - at > PictureLabelWindow)
        {
            ctx.PendingPictureMediaId = null;
            ctx.PendingPictureAt = null;
            return false;
        }
        if (!PictureLabels.TryParseReply(message, out var kind)) return false;

        ctx.PendingPictureMediaId = null;
        ctx.PendingPictureAt = null;
        if (kind != PictureLabels.Other)
        {
            if (!TryTakeUsage(seller, seller.WhatsAppPhoneNumber, MediaKind.Image, out var decision, out var wait))
            {
                await ReplyAsync(seller, UsageLimitText(seller, MediaKind.Image, decision, wait), ct);
                return true;
            }
            var media = _media is null ? null : await _media.DownloadAsync(mediaId, ct);
            if (media is null)
            {
                await ReplyAsync(seller, MessageTemplates.Get("voice-confirm.026"), ct);
                return true;
            }
            await ApplyPictureLabelAsync(seller, session, ctx, kind, "", media.Value, ct);
            return true;
        }
        await ReplyAsync(seller, MessageTemplates.Get("voice-confirm.023"), ct);
        return true;
    }

    /// <summary>Reads <paramref name="media"/> as <paramref name="kind"/> and, when it gave usable fields, parks the typed command for YES.</summary>
    private async Task ApplyPictureLabelAsync(Seller seller, ConversationSession session, SessionContextData ctx, string kind, string note,
        (byte[] Bytes, string MimeType) media, CancellationToken ct)
    {
        if (kind == PictureLabels.Other)
        {
            await ReplyAsync(seller, MessageTemplates.Get("voice-confirm.023"), ct);
            return;
        }

        if (kind == PictureLabels.Order)
        {
            ResetFlowContext(ctx);
            SetState(session, ConversationState.Idle);
            var catalog = await LoadCatalogAsync(seller, ct);
            var analysis = await _ai.AnalyzeImageAsync(AiContext(seller, catalog), new AiImageInput(media.Bytes, media.MimeType, string.IsNullOrWhiteSpace(note) ? "order" : note), ct);
            if (analysis.Receipt is { Amount: > 0 } receipt) await StartReceiptMatchAsync(seller, session, ctx, receipt, ct);
            else await HandleAnalysisAsync(seller, session, ctx, analysis, catalog, fromScreenshot: true, ct);
            return;
        }

        // Parking replaces the state: only from idle, so a half-finished order draft is never dropped by a photo.
        if (session.State != ConversationState.Idle)
        {
            await ReplyAsync(seller, MessageTemplates.Get("voice-confirm.025"), ct);
            return;
        }

        var reading = await _ai.ReadPictureAsync(kind, new AiImageInput(media.Bytes, media.MimeType, string.IsNullOrWhiteSpace(note) ? null : note), ct);
        var step = BuildPictureStep(kind, reading, note);
        if (step is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("voice-confirm.022", PictureExample(kind)), ct);
            return;
        }

        ctx.PendingVoiceSteps = new List<string> { step };
        ctx.PendingVoiceParkedAt = DateTime.UtcNow;
        ctx.PendingVoiceSentAt = _sentAtUtc;
        ctx.PendingVoiceLogId = await NewestActionLogIdAsync(seller.Id, ct);
        SetState(session, ConversationState.AwaitingVoiceConfirmation);
        await _sender.SendTextMessageAsync(seller.WhatsAppPhoneNumber,
            MessageTemplates.Get("media.002", MessageTemplates.Get("voice-confirm.021", kind, step)), ct);
    }

    private static string PictureExample(string kind) => kind switch
    {
        PictureLabels.Expense => "expense 500 packaging",
        PictureLabels.Wages => "wages Ali 5000",
        _ => "customer add Sara, Model Town Lahore, 03001234567",
    };

    private static readonly Regex NotWordChars = new(@"[^\p{L}\p{N} .'-]+", RegexOptions.Compiled);

    private static string CleanPictureText(string? text, int max)
    {
        var cleaned = NotWordChars.Replace(text ?? "", " ").Trim();
        cleaned = Regex.Replace(cleaned, @"\s+", " ");
        return cleaned.Length > max ? cleaned[..max].Trim() : cleaned;
    }

    /// <summary>
    /// The typed command for what was read, or null when a needed field is missing or the command does not parse as the expected kind. Plain code: the model
    /// only supplied the fields. The seller sees the command and says YES before it runs.
    /// </summary>
    internal static string? BuildPictureStep(string kind, AiPictureReading? reading, string note)
    {
        if (reading is null) return null;
        string? step;
        CommandKind expected;
        switch (kind)
        {
            case PictureLabels.Expense:
                if (reading.Amount is not { } amount || amount <= 0) return null;
                step = $"expense {amount.ToString("0.##", CultureInfo.InvariantCulture)} {CleanPictureText(reading.Note ?? note, 40)}".Trim();
                expected = CommandKind.Expense;
                break;
            case PictureLabels.Wages:
                var worker = CleanPictureText(reading.Name, 40);
                if (worker.Length < 2 || reading.Amount is not { } pay || pay <= 0) return null;
                step = $"wages {worker} {pay.ToString("0.##", CultureInfo.InvariantCulture)}";
                expected = CommandKind.Wage;
                break;
            case PictureLabels.Customer:
                var name = CleanPictureText(reading.Name, 40);
                if (name.Length < 2) return null;
                var phone = Regex.Replace(reading.Phone ?? "", @"\D", "");
                var parts = new List<string> { name };
                var place = string.Join(" ", new[] { reading.Address, reading.City }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => CleanPictureText(p, 60)));
                if (place.Length > 0) parts.Add(place);
                if (IsPlausiblePhone(phone)) parts.Add(phone);
                step = "customer add " + string.Join(", ", parts);
                expected = CommandKind.CustomerAdd;
                break;
            default:
                return null;
        }
        return CommandParser.TryParse(step)?.Kind == expected ? step : null;
    }
}
