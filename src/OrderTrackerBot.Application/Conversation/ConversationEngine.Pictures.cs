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
// Several pictures at once: each one arrives as its own message. Unlabelled ones are remembered (up to MaxPendingPictures) and one label covers
// them all; labelled ones add their command to the steps already waiting for the same YES.
public partial class ConversationEngine
{
    private static readonly TimeSpan PictureLabelWindow = TimeSpan.FromMinutes(10);

    /// <summary>Pictures a single label covers, and steps one YES covers.</summary>
    internal const int MaxPendingPictures = 5;
    private const int MaxPictureSteps = 10;

    /// <summary>Remembers an unlabelled picture for the next type word; pictures older than the window are forgotten.</summary>
    private void RememberPicture(SessionContextData ctx, string mediaId)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var ids = ctx.PendingPictureAt is { } at && now - at <= PictureLabelWindow ? ctx.PendingPictureMediaIds ?? new List<string>() : new List<string>();
        ids.Remove(mediaId);
        ids.Add(mediaId);
        ctx.PendingPictureMediaIds = ids.TakeLast(MaxPendingPictures).ToList();
        ctx.PendingPictureAt = now;
    }

    /// <summary>True when fresh remembered pictures exist and <paramref name="message"/> is only a type word: they are then read as that type.</summary>
    private async Task<bool> TryHandlePictureLabelAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        if (ctx.PendingPictureMediaIds is not { Count: > 0 } mediaIds) return false;
        if (ctx.PendingPictureAt is not { } at || _clock.GetUtcNow().UtcDateTime - at > PictureLabelWindow)
        {
            ctx.PendingPictureMediaIds = null;
            ctx.PendingPictureAt = null;
            return false;
        }
        if (!PictureLabels.TryParseReply(message, out var kind)) return false;

        ctx.PendingPictureMediaIds = null;
        ctx.PendingPictureAt = null;
        if (kind == PictureLabels.Other)
        {
            await ReplyAsync(seller, MessageTemplates.Get("voice-confirm.023"), ct);
            return true;
        }
        if (kind != PictureLabels.Order && !CanParkPictureSteps(session, ctx))
        {
            await ReplyAsync(seller, MessageTemplates.Get("voice-confirm.025"), ct);
            return true;
        }

        // An order is read from the newest picture only; the other types read every remembered picture.
        var ids = kind == PictureLabels.Order ? mediaIds.TakeLast(1).ToList() : mediaIds;
        var steps = new List<string>();
        var failed = 0;
        foreach (var id in ids)
        {
            if (!TryTakeUsage(seller, seller.WhatsAppPhoneNumber, MediaKind.Image, out var decision, out var wait))
            {
                await ReplyAsync(seller, UsageLimitText(seller, MediaKind.Image, decision, wait), ct);
                break;
            }
            var media = _media is null ? null : await _media.DownloadAsync(id, ct);
            if (media is null) { failed++; continue; }
            if (kind == PictureLabels.Order)
            {
                await ApplyPictureLabelAsync(seller, session, ctx, kind, "", media.Value, ct);
                return true;
            }
            var step = await ReadPictureStepAsync(kind, "", media.Value, ct);
            if (step is null) failed++; else steps.Add(step);
        }
        await ParkPictureStepsAsync(seller, session, ctx, kind, steps, failed, ct);
        return true;
    }

    /// <summary>One labelled picture (its caption named the type): read it and add the result to the steps waiting for YES.</summary>
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

        // Parking replaces the state: only from idle (or from a wait that earlier pictures made), so a half-finished order draft is never dropped by a photo.
        if (!CanParkPictureSteps(session, ctx))
        {
            await ReplyAsync(seller, MessageTemplates.Get("voice-confirm.025"), ct);
            return;
        }

        var step = await ReadPictureStepAsync(kind, note, media, ct);
        await ParkPictureStepsAsync(seller, session, ctx, kind, step is null ? new List<string>() : new List<string> { step }, step is null ? 1 : 0, ct);
    }

    private static bool CanParkPictureSteps(ConversationSession session, SessionContextData ctx) =>
        session.State == ConversationState.Idle
        || (session.State == ConversationState.AwaitingVoiceConfirmation && ctx.PendingVoiceFromPicture && ctx.PendingVoiceSteps is { Count: > 0 });

    private async Task<string?> ReadPictureStepAsync(string kind, string note, (byte[] Bytes, string MimeType) media, CancellationToken ct)
    {
        var reading = await _ai.ReadPictureAsync(kind, new AiImageInput(media.Bytes, media.MimeType, string.IsNullOrWhiteSpace(note) ? null : note), ct);
        return BuildPictureStep(kind, reading, note);
    }

    /// <summary>Parks the commands read from pictures behind one YES (joining those already waiting from earlier pictures), or says what could not be read.</summary>
    private async Task ParkPictureStepsAsync(Seller seller, ConversationSession session, SessionContextData ctx, string kind, List<string> steps, int failed, CancellationToken ct)
    {
        if (steps.Count == 0)
        {
            if (failed > 0) await ReplyAsync(seller, MessageTemplates.Get("voice-confirm.022", PictureExample(kind)), ct);
            return;
        }

        // A small expense from a seller who has confirmed these every time is saved without the tap (see ExpenseConfirmTrustedAsync); the caller runs it after the turn.
        if (kind == PictureLabels.Expense && steps.Count == 1 && failed == 0 && session.State == ConversationState.Idle
            && CommandParser.TryParse(steps[0]) is { Kind: CommandKind.Expense, Amount: > 0 } expense && expense.Amount <= AutoExpenseMax
            && await ExpenseConfirmTrustedAsync(seller, ct))
        {
            _autoRunStep = steps[0];
            return;
        }

        var waiting = session.State == ConversationState.AwaitingVoiceConfirmation && ctx.PendingVoiceFromPicture ? ctx.PendingVoiceSteps ?? new List<string>() : new List<string>();
        var all = waiting.Concat(steps.Where(s => !waiting.Contains(s))).Take(MaxPictureSteps).ToList();

        ctx.PendingVoiceSteps = all;
        ctx.PendingVoiceFromPicture = true;
        ctx.PendingVoiceYesLabel = YesButtonLabel(all);
        ctx.PendingVoiceParkedAt = DateTime.UtcNow;
        ctx.PendingVoiceSentAt = _sentAtUtc;
        ctx.PendingVoiceLogId = await NewestActionLogIdAsync(seller.Id, ct);
        SetState(session, ConversationState.AwaitingVoiceConfirmation);
        var shown = all.Count == 1 ? all[0] : "\n" + string.Join("\n", all.Select(s => $"• {s}"));
        var text = MessageTemplates.Get("media.002", MessageTemplates.Get("voice-confirm.021", kind, shown));
        if (failed > 0) text = MessageTemplates.Get("voice-confirm.027", failed) + "\n" + text;
        // What the reports need for an exact result, said where the seller is already looking at the products.
        if (kind == PictureLabels.Products) text += "\n\n" + MessageTemplates.Get("voice-confirm.035");
        // Every picture read is a machine reading: names, phones and prices can be wrong, so the list is checked before YES.
        text += "\n\n" + MessageTemplates.Get("photo.006");
        await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, text, new[] { ctx.PendingVoiceYesLabel, NoButtonLabel }, ct);
    }

    private static string PictureExample(string kind) => kind switch
    {
        PictureLabels.Expense => "expense 500 packaging",
        PictureLabels.Wages => "wages Ali 5000",
        PictureLabels.Products => "Lawn Suit - 3500, cost 2400",
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
            case PictureLabels.Products:
                // One product per line ("Lawn Suit - 3500, cost 2400"); a line the product parser would not accept is left out, so the rest still go in together.
                var lines = reading.Products
                    .Where(p => p.Price is not null && CleanPictureText(p.Name, 60).Length >= 2)
                    .Select(p => $"{CleanPictureText(p.Name, 60)} - {p.Price!.Value.ToString("0.##", CultureInfo.InvariantCulture)}"
                                 + (p.Cost is { } productCost ? $", cost {productCost.ToString("0.##", CultureInfo.InvariantCulture)}" : ""))
                    .Where(l => CommandParser.TryParse(l)?.Kind == CommandKind.AddProduct)
                    .Take(30).ToList();
                if (lines.Count == 0) return null;
                step = string.Join("\n", lines);
                expected = lines.Count == 1 ? CommandKind.AddProduct : CommandKind.AddProductsBulk;
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
