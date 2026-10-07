using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Seller-defined attributes (WordPress-style custom fields) on products, customers and orders.
//   add field product Fabric            free text          add field product Fabric: Cotton, Lawn   choice (tap to select)
//   add field product Weight: number    add field customer Birthday: date    add field product Cost private   (never on receipts / shared catalog)
//   set product Kurti Fabric = Cotton   set product Kurti Fabric   set product Kurti   fields   fields product Kurti
public partial class ConversationEngine
{
    private const int MaxCustomFieldsPerEntity = 10;
    private const int MaxCustomFieldNameLength = 30;
    private const int MaxCustomFieldValueLength = 200;
    private const int MaxCustomFieldOptions = 10;      // one WhatsApp list message
    private const int MaxCustomFieldOptionLength = 20; // WhatsApp button title limit
    private const int MaxCustomFieldRecordRows = 10;

    private static readonly string[] FieldDateWithYear = { "d MMM yyyy", "d MMMM yyyy", "d/M/yyyy", "d-M-yyyy", "yyyy-MM-dd", "MMM d yyyy", "MMMM d yyyy" };
    private static readonly string[] FieldDateNoYear = { "d MMM yyyy", "d MMMM yyyy", "d/M yyyy", "MMM d yyyy", "MMMM d yyyy" };
    private static readonly Regex PrivateMarker = new(@"\s+(?:private|internal|نجی)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static CustomFieldEntity FieldEntityFrom(string? text) => text switch
    {
        "product" => CustomFieldEntity.Product,
        "customer" => CustomFieldEntity.Customer,
        _ => CustomFieldEntity.Order
    };

    private static string FieldEntityLabel(CustomFieldEntity entity) => entity switch
    {
        CustomFieldEntity.Product => "product",
        CustomFieldEntity.Customer => "customer",
        _ => "order"
    };

    private static string Capitalise(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    private static string FieldExample(CustomFieldEntity entity, string field) => entity switch
    {
        CustomFieldEntity.Product => $"set product Kurti {field} = ...",
        CustomFieldEntity.Customer => $"set customer Sara {field} = ...",
        _ => $"set order 12 {field} = ..."
    };

    private static string FieldRecordExample(CustomFieldEntity entity) => entity switch
    {
        CustomFieldEntity.Product => "Kurti",
        CustomFieldEntity.Customer => "Sara",
        _ => "12"
    };

    private static string DescribeField(CustomField f) =>
        (f.Options is not null ? $"{f.Name} ({string.Join("/", f.OptionList)})"
            : f.Type == CustomFieldType.Number ? $"{f.Name} (number)"
            : f.Type == CustomFieldType.Date ? $"{f.Name} (date)"
            : f.Name) + (f.IsPrivate ? " 🔒" : "");

    private Task<List<CustomField>> FieldsForAsync(Seller seller, CustomFieldEntity entity, CancellationToken ct) =>
        _db.CustomFields.Where(f => f.SellerId == seller.Id && f.Entity == entity).OrderBy(f => f.Id).ToListAsync(ct);

    private async Task<CustomField?> FindFieldAsync(Seller seller, CustomFieldEntity entity, string name, CancellationToken ct)
    {
        var wanted = name.Trim().ToLower();
        return await _db.CustomFields.FirstOrDefaultAsync(f => f.SellerId == seller.Id && f.Entity == entity && f.Name.ToLower() == wanted, ct);
    }

    // ---- define / change / remove -----------------------------------------------------------------------------------------------

    // "add field product Fabric" = free text · "…Fabric: Cotton, Lawn, Silk" = choice · "…Weight: number" / "…Birthday: date" · trailing "private" hides it from receipts.
    private async Task HandleCustomFieldAddAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var entity = FieldEntityFrom(cmd.Text);
        var raw = cmd.Text2!.Trim();
        var isPrivate = PrivateMarker.IsMatch(raw);
        if (isPrivate) raw = PrivateMarker.Replace(raw, "");

        var cut = raw.IndexOfAny(new[] { ':', '=' });
        var name = Regex.Replace((cut < 0 ? raw : raw[..cut]).Trim(), @"\s+", " ");
        if (name.Length == 0 || name.Length > MaxCustomFieldNameLength || name.IndexOfAny(new[] { '=', ':' }) >= 0)
        {
            await ReplyAsync(seller, $"Field ka naam chhota rakhein ({MaxCustomFieldNameLength} huroof tak). Jaise: add field {FieldEntityLabel(entity)} Fabric", ct);
            return;
        }

        var type = CustomFieldType.Text;
        List<string>? options = null;
        if (cut >= 0)
        {
            var after = raw[(cut + 1)..].Trim();
            switch (after.ToLowerInvariant())
            {
                case "number" or "numeric" or "نمبر": type = CustomFieldType.Number; break;
                case "date" or "تاریخ": type = CustomFieldType.Date; break;
                case "text" or "ٹیکسٹ": break;
                default:
                    var (parsed, error) = ParseFieldOptions(after);
                    if (error is not null)
                    {
                        await ReplyAsync(seller, error + $"\nJaise: add field {FieldEntityLabel(entity)} {name}: Cotton, Lawn, Silk", ct);
                        return;
                    }
                    options = parsed;
                    break;
            }
        }

        var existing = await FieldsForAsync(seller, entity, ct);
        if (existing.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            await ReplyAsync(seller, $"\"{name}\" field {FieldEntityLabel(entity)} ke liye pehle se maujood hai.", ct);
            return;
        }
        if (existing.Count >= MaxCustomFieldsPerEntity)
        {
            await ReplyAsync(seller, $"{FieldEntityLabel(entity)} ke liye {MaxCustomFieldsPerEntity} fields ho chuki hain. Kisi ko \"remove field {FieldEntityLabel(entity)} <naam>\" se hata dein.", ct);
            return;
        }

        var field = new CustomField
        {
            SellerId = seller.Id, Entity = entity, Name = name, Type = type, IsPrivate = isPrivate,
            Options = options is null ? null : string.Join('|', options)
        };
        _db.CustomFields.Add(field);
        await ReplyAsync(seller,
            $"✅ {FieldEntityLabel(entity)} field \"{DescribeField(field)}\" ban gayi." +
            (isPrivate ? "\n🔒 Private: receipt aur share catalog mein nahi dikhegi." : "") +
            (options is not null ? $"\nChunne ke liye: set {FieldEntityLabel(entity)} {FieldRecordExample(entity)} {name}" : $"\nValue likhein: {FieldExample(entity, name)}") +
            "\nSab fields: \"fields\"", ct);
    }

    /// <summary>"Cotton, Lawn, Silk" -> options (comma / Urdu comma / pipe), 2-10 of them, each short enough for a WhatsApp button.</summary>
    private static (List<string> Options, string? Error) ParseFieldOptions(string text)
    {
        var options = new List<string>();
        foreach (var part in text.Split(new[] { ',', '،', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var option = Regex.Replace(part, @"\s+", " ");
            if (option.Length > MaxCustomFieldOptionLength) return (options, $"Option \"{option}\" bohat lambi hai ({MaxCustomFieldOptionLength} huroof tak).");
            if (!options.Any(o => string.Equals(o, option, StringComparison.OrdinalIgnoreCase))) options.Add(option);
        }
        if (options.Count < 2) return (options, "Kam az kam 2 options likhein, comma se alag karke.");
        if (options.Count > MaxCustomFieldOptions) return (options, $"Zyada se zyada {MaxCustomFieldOptions} options ho saktay hain.");
        return (options, null);
    }

    private static List<string> SplitOptionList(string text) =>
        text.Split(new[] { ',', '،', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => Regex.Replace(p, @"\s+", " ")).ToList();

    // "add option product Fabric: Silk, Chiffon" extends a choice field.
    private async Task HandleCustomFieldOptionAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var entity = FieldEntityFrom(cmd.Text);
        var field = await FindFieldAsync(seller, entity, cmd.Text2!, ct);
        if (field is null)
        {
            await ReplyAsync(seller, $"{FieldEntityLabel(entity)} ki \"{cmd.Text2}\" field nahi mili. \"fields\" se dekhein.", ct);
            return;
        }
        if (field.Options is null)
        {
            await ReplyAsync(seller, $"\"{field.Name}\" choice field nahi hai. Choice banane ke liye pehle \"remove field {FieldEntityLabel(entity)} {field.Name}\", phir \"add field {FieldEntityLabel(entity)} {field.Name}: A, B, C\".", ct);
            return;
        }

        var current = field.OptionList;
        var added = new List<string>();
        foreach (var option in SplitOptionList(cmd.Text3!))
        {
            if (option.Length > MaxCustomFieldOptionLength)
            {
                await ReplyAsync(seller, $"Option \"{option}\" bohat lambi hai ({MaxCustomFieldOptionLength} huroof tak).", ct);
                return;
            }
            if (!current.Concat(added).Any(o => string.Equals(o, option, StringComparison.OrdinalIgnoreCase))) added.Add(option);
        }
        if (added.Count == 0)
        {
            await ReplyAsync(seller, "Yeh option(s) pehle se maujood hain.", ct);
            return;
        }
        if (current.Count + added.Count > MaxCustomFieldOptions)
        {
            await ReplyAsync(seller, $"Zyada se zyada {MaxCustomFieldOptions} options ho saktay hain (abhi {current.Count}).", ct);
            return;
        }

        field.Options = string.Join('|', current.Concat(added));
        await ReplyAsync(seller, $"✅ {field.Name} mein add: {string.Join(", ", added)}\nAb options: {string.Join(", ", field.OptionList)}", ct);
    }

    // "remove option product Fabric: Silk": values already saved with that option are kept; it just stops being offered.
    private async Task HandleCustomFieldOptionRemoveAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var entity = FieldEntityFrom(cmd.Text);
        var field = await FindFieldAsync(seller, entity, cmd.Text2!, ct);
        if (field is null)
        {
            await ReplyAsync(seller, $"{FieldEntityLabel(entity)} ki \"{cmd.Text2}\" field nahi mili. \"fields\" se dekhein.", ct);
            return;
        }
        if (field.Options is null)
        {
            await ReplyAsync(seller, $"\"{field.Name}\" choice field nahi hai — is mein options nahi hain.", ct);
            return;
        }

        var current = field.OptionList;
        var wanted = SplitOptionList(cmd.Text3!);
        var removed = current.Where(o => wanted.Any(w => string.Equals(w, o, StringComparison.OrdinalIgnoreCase))).ToList();
        if (removed.Count == 0)
        {
            await ReplyAsync(seller, $"Yeh option nahi mila. {field.Name} ke options: {string.Join(", ", current)}", ct);
            return;
        }
        var remaining = current.Except(removed).ToList();
        if (remaining.Count < 2)
        {
            await ReplyAsync(seller, $"Kam az kam 2 options rehne chahiye. Poori field hatani ho to \"remove field {FieldEntityLabel(entity)} {field.Name}\".", ct);
            return;
        }

        var inUse = await _db.CustomFieldValues.CountAsync(v => v.CustomFieldId == field.Id && removed.Contains(v.Value), ct);
        field.Options = string.Join('|', remaining);
        await ReplyAsync(seller,
            $"🗑️ {field.Name} se hata diya: {string.Join(", ", removed)}\nAb options: {string.Join(", ", remaining)}" +
            (inUse > 0 ? $"\nℹ️ {inUse} record(s) par purani value rahegi." : ""), ct);
    }

    // "hide field product Cost" / "show field product Cost": private fields stay off receipts and the shareable catalog.
    private async Task HandleCustomFieldVisibilityAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var entity = FieldEntityFrom(cmd.Text);
        var field = await FindFieldAsync(seller, entity, cmd.Text2!, ct);
        if (field is null)
        {
            await ReplyAsync(seller, $"{FieldEntityLabel(entity)} ki \"{cmd.Text2}\" field nahi mili. \"fields\" se dekhein.", ct);
            return;
        }

        field.IsPrivate = cmd.Text3 == "private";
        await ReplyAsync(seller, field.IsPrivate
            ? $"🔒 \"{field.Name}\" ab private hai — receipt aur share catalog mein nahi dikhegi."
            : $"👁️ \"{field.Name}\" ab receipt aur share catalog mein dikhegi.", ct);
    }

    private async Task HandleCustomFieldRemoveAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var entity = FieldEntityFrom(cmd.Text);
        var field = await FindFieldAsync(seller, entity, cmd.Text2!, ct);
        if (field is null)
        {
            await ReplyAsync(seller, $"{FieldEntityLabel(entity)} ki \"{cmd.Text2!.Trim()}\" field nahi mili. \"fields\" se dekhein.", ct);
            return;
        }

        var values = await _db.CustomFieldValues.Where(v => v.CustomFieldId == field.Id).ToListAsync(ct);
        _db.CustomFieldValues.RemoveRange(values);
        _db.CustomFields.Remove(field);
        await ReplyAsync(seller, $"🗑️ {FieldEntityLabel(entity)} field \"{field.Name}\" hata di" + (values.Count > 0 ? $" ({values.Count} values bhi delete)." : "."), ct);
    }

    // ---- list / show ------------------------------------------------------------------------------------------------------------

    private async Task HandleCustomFieldListAsync(Seller seller, ConversationSession session, SessionContextData ctx, ParsedCommand cmd, CancellationToken ct)
    {
        if (cmd.Text is not null)
        {
            var entity = FieldEntityFrom(cmd.Text);
            if (await ResolveFieldRecordAsync(seller, ctx, entity, cmd.Text2!, ct) is not { } record) return;
            var lines = await CustomFieldLinesAsync(seller, entity, record.Id, ct);
            await ReplyAsync(seller, lines.Count == 0
                ? $"{record.Name} ki koi custom value set nahi hai. \"fields\" se fields dekhein."
                : $"{record.Name}\n" + string.Join("\n", lines), ct);
            await OfferFieldsButtonAsync(seller, ctx, entity, record.Id, record.Name, ct);
            return;
        }

        var fields = await _db.CustomFields.AsNoTracking().Where(f => f.SellerId == seller.Id).OrderBy(f => f.Id).ToListAsync(ct);
        if (fields.Count == 0)
        {
            await ReplyAsync(seller,
                "🏷️ Abhi koi custom field nahi hai.\n\nApni fields banayein:\n" +
                "• add field product Fabric\n• add field product Fabric: Cotton, Lawn, Silk (chunne wali)\n• add field customer Birthday: date\n" +
                "• add field order Gift Note\n• add field product Cost: number private (receipt par nahi aayegi)\n\n" +
                "Phir value likhein: set product Kurti Fabric = Cotton, ya sirf \"set product Kurti\" likh kar chunein.", ct);
            return;
        }

        var rows = new List<string>();
        foreach (var entity in new[] { CustomFieldEntity.Product, CustomFieldEntity.Customer, CustomFieldEntity.Order })
        {
            var names = fields.Where(f => f.Entity == entity).Select(DescribeField).ToList();
            rows.Add($"{Capitalise(FieldEntityLabel(entity))}: {(names.Count == 0 ? "—" : string.Join(", ", names))}");
        }
        await ReplyAsync(seller,
            "🏷️ Custom fields:\n\n" + string.Join("\n", rows) +
            "\n\nNayi: \"add field product Fabric\" · chunne wali: \"add field product Fabric: Cotton, Lawn\" · \"…: number\" / \"…: date\" · \"…private\"" +
            "\nValue: \"set product Kurti Fabric = Cotton\" ya \"set product Kurti\"\nDekhein: \"fields product Kurti\"\nHatayein: \"remove field product Fabric\"", ct);

        // Tap-through: which kind of record, then which record, field and value.
        var entities = fields.Select(f => f.Entity).Distinct().OrderBy(e => (int)e).ToList();
        ctx.CustomFieldPick = new CustomFieldPickData { Stage = "entity" };
        SetState(session, ConversationState.AwaitingCustomFieldChoice);
        await SendPickerAsync(seller, "🏷️ Kis ki details set karni hain?", entities.Select(e => Capitalise(FieldEntityLabel(e))).ToList(), ct);
    }

    // ---- set --------------------------------------------------------------------------------------------------------------------

    private async Task HandleCustomFieldSetAsync(Seller seller, ConversationSession session, SessionContextData ctx, ParsedCommand cmd, CancellationToken ct)
    {
        var entity = FieldEntityFrom(cmd.Text);
        var label = FieldEntityLabel(entity);
        var fields = await FieldsForAsync(seller, entity, ct);
        var lhs = cmd.Text2!.Trim();
        var tapping = cmd.Text3 is null; // "set product Kurti [Fabric]" without "=" -> pick from buttons/list

        // The field name is the longest defined name that ends the text before "=": "Kurti Fabric" -> record "Kurti", field "Fabric".
        var field = fields
            .Where(f => lhs.EndsWith(f.Name, StringComparison.OrdinalIgnoreCase)
                        && (lhs.Length == f.Name.Length || char.IsWhiteSpace(lhs[lhs.Length - f.Name.Length - 1])))
            .OrderByDescending(f => f.Name.Length).FirstOrDefault();
        if (field is null && (!tapping || fields.Count == 0))
        {
            await ReplyAsync(seller, fields.Count == 0
                ? $"Pehle {label} field banayein: \"add field {label} Fabric\" (ya choices ke saath: \"add field {label} Fabric: Cotton, Lawn\")"
                : $"{label} ki yeh field nahi mili. Maujood: {string.Join(", ", fields.Select(f => f.Name))}\nNayi: \"add field {label} <naam>\"", ct);
            return;
        }

        var recordText = field is null ? lhs : lhs[..^field.Name.Length].Trim();
        if (recordText.Length == 0)
        {
            await ReplyAsync(seller, $"Kis {label} ke liye? Jaise: {FieldExample(entity, field!.Name)}", ct);
            return;
        }

        if (await ResolveFieldRecordAsync(seller, ctx, entity, recordText, ct) is not { } record) return;

        if (tapping)
        {
            if (field is null) await StartCustomFieldFieldStageAsync(seller, session, ctx, entity, record, fields, ct);
            else await StartCustomFieldValueAsync(seller, session, ctx, field, label, record, ct);
            return;
        }

        var value = cmd.Text3!.Trim();
        if (value is "-" || value.Equals("clear", StringComparison.OrdinalIgnoreCase) || value.Equals("hatao", StringComparison.OrdinalIgnoreCase) || value == "ہٹاؤ")
        {
            var current = await _db.CustomFieldValues.FirstOrDefaultAsync(v => v.CustomFieldId == field!.Id && v.EntityId == record.Id, ct);
            if (current is null)
            {
                await ReplyAsync(seller, $"{record.Name} ki {field!.Name} pehle se khali hai.", ct);
                return;
            }
            LogCustomFieldChange(seller, field!, record.Id, record.Name, current.Value);
            _db.CustomFieldValues.Remove(current);
            await ReplyAsync(seller, $"🗑️ {record.Name} ki {field!.Name} hata di.\nGhalti ho to \"undo\".", ct);
            return;
        }

        var (normalised, error) = NormalizeFieldValue(field!, value);
        if (normalised is null)
        {
            await ReplyAsync(seller, error!, ct);
            if (field!.Options is not null) await StartCustomFieldValueAsync(seller, session, ctx, field, label, record, ct); // show the choices
            return;
        }

        await ReplyAsync(seller, await SaveCustomFieldValueAsync(seller, field!, record.Id, record.Name, normalised, ct), ct);
    }

    /// <summary>Checks/normalises a typed or tapped value for the field's kind: choice, number, date or text.</summary>
    private static (string? Value, string? Error) NormalizeFieldValue(CustomField field, string raw)
    {
        var value = raw.Trim();
        if (value.Length > MaxCustomFieldValueLength) return (null, $"Value bohat lambi hai ({MaxCustomFieldValueLength} huroof tak).");

        if (field.Options is not null)
        {
            var option = field.OptionList.FirstOrDefault(o => string.Equals(o, value, StringComparison.OrdinalIgnoreCase));
            return option is null ? (null, $"\"{value}\" {field.Name} ka option nahi hai. Neeche se chunein:") : (option, null);
        }

        switch (field.Type)
        {
            case CustomFieldType.Number:
                var digits = Regex.Replace(value, @"\b(?:rs\.?|rupees?|pkr)|,|\s", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                return decimal.TryParse(digits, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                    ? (number.ToString("0.##########", CultureInfo.InvariantCulture), null)
                    : (null, $"{field.Name} ke liye number likhein, jaise 1500 ya 2.5");
            case CustomFieldType.Date:
                var style = DateTimeStyles.AllowWhiteSpaces;
                if (DateTime.TryParseExact(value, FieldDateWithYear, CultureInfo.InvariantCulture, style, out var withYear))
                    return (withYear.ToString("dd MMM yyyy", CultureInfo.InvariantCulture), null);
                // No year ("12 May", a birthday): keep just day + month (parsed against a leap year so 29 Feb works).
                if (DateTime.TryParseExact(value + " 2000", FieldDateNoYear, CultureInfo.InvariantCulture, style, out var noYear))
                    return (noYear.ToString("dd MMM", CultureInfo.InvariantCulture), null);
                return (null, $"{field.Name} ke liye date likhein, jaise 12 May ya 12/05/1995");
            default:
                return (value, null);
        }
    }

    private async Task<string> SaveCustomFieldValueAsync(Seller seller, CustomField field, int entityId, string recordName, string value, CancellationToken ct)
    {
        var current = await _db.CustomFieldValues.FirstOrDefaultAsync(v => v.CustomFieldId == field.Id && v.EntityId == entityId, ct);
        var previous = current?.Value;
        if (previous != value) LogCustomFieldChange(seller, field, entityId, recordName, previous);
        if (current is null) _db.CustomFieldValues.Add(new CustomFieldValue { SellerId = seller.Id, CustomFieldId = field.Id, EntityId = entityId, Value = value });
        else current.Value = value;
        return $"✅ {recordName} — {field.Name}: {value}" + (previous is null || previous == value ? "" : $" (pehle: {previous})") +
               (previous == value ? "" : "\nGhalti ho to \"undo\".");
    }

    private void LogCustomFieldChange(Seller seller, CustomField field, int entityId, string recordName, string? previous) =>
        _db.ActionLogs.Add(new ActionLog
        {
            SellerId = seller.Id, ActionType = ActionType.CustomFieldChanged,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { FieldId = field.Id, FieldName = field.Name, EntityId = entityId, RecordName = recordName, Previous = previous })
        });

    // "undo" after a field change: put the previous value back (or remove the value if there was none).
    private async Task UndoCustomFieldChangeAsync(Seller seller, ActionLog log, CancellationToken ct)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(log.PayloadJson);
        var fieldId = doc.RootElement.GetProperty("FieldId").GetInt32();
        var entityId = doc.RootElement.GetProperty("EntityId").GetInt32();
        var recordName = doc.RootElement.GetProperty("RecordName").GetString();
        var previous = doc.RootElement.GetProperty("Previous").GetString();

        var field = await _db.CustomFields.FirstOrDefaultAsync(f => f.Id == fieldId && f.SellerId == seller.Id, ct);
        if (field is null)
        {
            await ReplyAsync(seller, "↩️ Yeh field ab maujood nahi, isliye undo nahi ho saka.", ct);
            return;
        }

        var current = await _db.CustomFieldValues.FirstOrDefaultAsync(v => v.CustomFieldId == fieldId && v.EntityId == entityId, ct);
        if (previous is null)
        {
            if (current is not null) _db.CustomFieldValues.Remove(current);
            await ReplyAsync(seller, $"↩️ Reverted — {recordName} ki {field.Name} hata di.", ct);
            return;
        }

        if (current is null) _db.CustomFieldValues.Add(new CustomFieldValue { SellerId = seller.Id, CustomFieldId = fieldId, EntityId = entityId, Value = previous });
        else current.Value = previous;
        await ReplyAsync(seller, $"↩️ Reverted — {recordName} ki {field.Name} wapas: {previous}", ct);
    }

    // ---- tap-to-select flow: entity -> record -> field -> value ----------------------------------------------------------------------

    private async Task StartCustomFieldFieldStageAsync(Seller seller, ConversationSession session, SessionContextData ctx, CustomFieldEntity entity, (int Id, string Name) record, List<CustomField> fields, CancellationToken ct)
    {
        var label = FieldEntityLabel(entity);
        if (fields.Count == 0)
        {
            await ReplyAsync(seller, $"Pehle {label} field banayein: \"add field {label} Fabric\"", ct);
            return;
        }
        if (fields.Count == 1)
        {
            await StartCustomFieldValueAsync(seller, session, ctx, fields[0], label, record, ct);
            return;
        }

        ctx.CustomFieldPick = new CustomFieldPickData { Stage = "field", Entity = label, EntityId = record.Id, RecordName = record.Name };
        SetState(session, ConversationState.AwaitingCustomFieldChoice);
        await SendPickerAsync(seller, $"🏷️ {record.Name} — kaunsi field set karni hai?", fields.Select(f => f.Name).ToList(), ct);
    }

    private async Task StartCustomFieldValueAsync(Seller seller, ConversationSession session, SessionContextData ctx, CustomField field, string entityLabel, (int Id, string Name) record, CancellationToken ct)
    {
        var pick = new CustomFieldPickData { Entity = entityLabel, EntityId = record.Id, RecordName = record.Name, FieldId = field.Id };
        ctx.CustomFieldPick = pick;
        SetState(session, ConversationState.AwaitingCustomFieldChoice);
        if (field.Options is null)
        {
            pick.Stage = "text";
            var hint = field.Type switch { CustomFieldType.Number => " (number, jaise 1500)", CustomFieldType.Date => " (date, jaise 12 May)", _ => "" };
            await ReplyAsync(seller, $"🏷️ {record.Name} — {field.Name} likhein{hint} (ya \"cancel\"):", ct);
            return;
        }

        pick.Stage = "value";
        await SendPickerAsync(seller, $"🏷️ {record.Name} — {field.Name} chunein:", field.OptionList, ct);
    }

    private async Task StartCustomFieldRecordStageAsync(Seller seller, ConversationSession session, SessionContextData ctx, CustomFieldEntity entity, CancellationToken ct)
    {
        var label = FieldEntityLabel(entity);
        List<(string Id, string Title)> rows;
        int total;
        switch (entity)
        {
            case CustomFieldEntity.Product:
            {
                var q = _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && p.IsActive);
                total = await q.CountAsync(ct);
                rows = (await q.OrderBy(p => p.Id).Take(MaxCustomFieldRecordRows).ToListAsync(ct)).Select(p => ($"r{p.Id}", p.Name)).ToList();
                break;
            }
            case CustomFieldEntity.Customer:
            {
                var q = SellerCustomers(seller).AsNoTracking();
                total = await q.CountAsync(ct);
                rows = (await q.OrderByDescending(c => c.Id).Take(MaxCustomFieldRecordRows).ToListAsync(ct)).Select(c => ($"r{c.Id}", c.Name)).ToList();
                break;
            }
            default:
            {
                var q = _db.Orders.AsNoTracking().Where(o => o.SellerId == seller.Id);
                total = await q.CountAsync(ct);
                rows = (await q.Include(o => o.Customer).OrderByDescending(o => o.Id).Take(MaxCustomFieldRecordRows).ToListAsync(ct))
                    .Select(o => ($"r{o.Id}", $"#{o.Id} {o.Customer?.Name}")).ToList();
                break;
            }
        }

        if (rows.Count == 0)
        {
            EndCustomFieldPick(session, ctx);
            await ReplyAsync(seller, $"Abhi koi {label} nahi hai.", ct);
            return;
        }

        ctx.CustomFieldPick = new CustomFieldPickData { Stage = "record", Entity = label };
        SetState(session, ConversationState.AwaitingCustomFieldChoice);
        await SendPickerRowsAsync(seller,
            $"🏷️ Kaunsa {label}?" + (total > rows.Count ? $" (aakhri {rows.Count} — doosray ke liye {(entity == CustomFieldEntity.Order ? "number" : "naam")} likhein)" : ""),
            rows, ct);
    }

    /// <summary>Up to 3 short items become buttons; otherwise a list (max 10). The tapped item arrives as its own text.</summary>
    private Task SendPickerAsync(Seller seller, string body, IReadOnlyList<string> items, CancellationToken ct) =>
        items.Count <= 3 && items.All(i => i.Length <= MaxCustomFieldOptionLength)
            ? _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, body, items, ct)
            : SendPickerRowsAsync(seller, body, items.Select(i => (i, i)).ToList(), ct);

    private Task SendPickerRowsAsync(Seller seller, string body, IReadOnlyList<(string Id, string Title)> items, CancellationToken ct)
    {
        var rows = items.Take(MaxCustomFieldOptions).Select(i => new MenuRow(i.Id, i.Title.Length <= 24 ? i.Title : i.Title[..23] + "…")).ToList();
        return _sender.SendListMessageAsync(seller.WhatsAppPhoneNumber, body, "Chunein", new[] { new MenuSection("Options", rows) }, ct);
    }

    private static void EndCustomFieldPick(ConversationSession session, SessionContextData ctx)
    {
        ctx.CustomFieldPick = null;
        SetState(session, ConversationState.Idle);
    }

    private async Task HandleCustomFieldChoiceAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var pick = ctx.CustomFieldPick;
        if (pick is null)
        {
            EndCustomFieldPick(session, ctx);
            await HandleIdleAsync(seller, session, ctx, message, ct);
            return;
        }

        var text = message.Trim();
        static string? Match(IReadOnlyList<string> items, string text) =>
            items.FirstOrDefault(i => string.Equals(i, text, StringComparison.OrdinalIgnoreCase))
            ?? (int.TryParse(text, out var n) && n >= 1 && n <= items.Count ? items[n - 1] : null);

        switch (pick.Stage)
        {
            case "entity":
            {
                var fields = await _db.CustomFields.AsNoTracking().Where(f => f.SellerId == seller.Id).ToListAsync(ct);
                var labels = fields.Select(f => f.Entity).Distinct().OrderBy(e => (int)e).Select(e => Capitalise(FieldEntityLabel(e))).ToList();
                var chosen = Match(labels, text);
                if (chosen is not null)
                {
                    await StartCustomFieldRecordStageAsync(seller, session, ctx, FieldEntityFrom(chosen.ToLowerInvariant()), ct);
                    return;
                }
                if (await TryLeaveCustomFieldPickAsync(seller, session, ctx, text, ct)) return;
                await ReplyAsync(seller, "Neeche se chunein (ya \"cancel\").", ct);
                await SendPickerAsync(seller, "🏷️ Kis ki details set karni hain?", labels, ct);
                return;
            }
            case "record":
            {
                var entity = FieldEntityFrom(pick.Entity);
                (int Id, string Name)? record = null;
                var rowId = Regex.Match(text, @"^r(\d+)$");
                if (rowId.Success)
                {
                    record = await LookupFieldRecordAsync(seller, entity, int.Parse(rowId.Groups[1].Value), ct);
                    if (record is null)
                    {
                        await ReplyAsync(seller, $"Yeh {pick.Entity} nahi mila.", ct);
                        return;
                    }
                }
                else
                {
                    // A bare number is an order number; anything else that is a real command leaves the flow.
                    if (!(entity == CustomFieldEntity.Order && int.TryParse(text.TrimStart('#'), out _)) && await TryLeaveCustomFieldPickAsync(seller, session, ctx, text, ct)) return;
                    record = await ResolveFieldRecordAsync(seller, ctx, entity, text, ct);
                    if (record is null) return; // the reply says why; the list above is still the current question
                }

                await StartCustomFieldFieldStageAsync(seller, session, ctx, entity, record.Value, await FieldsForAsync(seller, entity, ct), ct);
                return;
            }
            case "field":
            {
                var entity = FieldEntityFrom(pick.Entity);
                var fields = await FieldsForAsync(seller, entity, ct);
                var chosen = Match(fields.Select(f => f.Name).ToList(), text);
                if (chosen is not null)
                {
                    await StartCustomFieldValueAsync(seller, session, ctx, fields.First(f => f.Name == chosen), pick.Entity, (pick.EntityId, pick.RecordName), ct);
                    return;
                }
                if (await TryLeaveCustomFieldPickAsync(seller, session, ctx, text, ct)) return;
                await ReplyAsync(seller, "Neeche se field chunein (ya \"cancel\").", ct);
                await SendPickerAsync(seller, $"🏷️ {pick.RecordName} — kaunsi field set karni hai?", fields.Select(f => f.Name).ToList(), ct);
                return;
            }
        }

        var current = await _db.CustomFields.FirstOrDefaultAsync(f => f.Id == pick.FieldId && f.SellerId == seller.Id, ct);
        if (current is null)
        {
            EndCustomFieldPick(session, ctx);
            await ReplyAsync(seller, "Yeh field ab maujood nahi hai.", ct);
            return;
        }

        if (pick.Stage == "text")
        {
            if (await TryLeaveCustomFieldPickAsync(seller, session, ctx, text, ct)) return;
            var (value, error) = NormalizeFieldValue(current, text);
            if (value is null)
            {
                await ReplyAsync(seller, error!, ct);
                return;
            }
            EndCustomFieldPick(session, ctx);
            await ReplyAsync(seller, await SaveCustomFieldValueAsync(seller, current, pick.EntityId, pick.RecordName, value, ct), ct);
            return;
        }

        var options = current.OptionList;
        var option = Match(options, text);
        if (option is not null)
        {
            EndCustomFieldPick(session, ctx);
            await ReplyAsync(seller, await SaveCustomFieldValueAsync(seller, current, pick.EntityId, pick.RecordName, option, ct), ct);
            return;
        }
        if (await TryLeaveCustomFieldPickAsync(seller, session, ctx, text, ct)) return;
        await ReplyAsync(seller, "Neeche se option chunein (ya \"cancel\").", ct);
        await SendPickerAsync(seller, $"🏷️ {pick.RecordName} — {current.Name} chunein:", options, ct);
    }

    // "cancel" ends the pick; any other real command ("orders today", "menu"...) leaves it and runs. True = handled.
    private async Task<bool> TryLeaveCustomFieldPickAsync(Seller seller, ConversationSession session, SessionContextData ctx, string text, CancellationToken ct)
    {
        if (text.Equals("cancel", StringComparison.OrdinalIgnoreCase) || text.Equals("back", StringComparison.OrdinalIgnoreCase) || text.Equals("skip", StringComparison.OrdinalIgnoreCase) || text == "منسوخ")
        {
            EndCustomFieldPick(session, ctx);
            await ReplyAsync(seller, "Theek hai, kuch change nahi kiya.", ct);
            return true;
        }
        if (CommandParser.TryParse(text) is { } command)
        {
            EndCustomFieldPick(session, ctx);
            await ExecuteCommandAsync(seller, session, ctx, command, ct);
            return true;
        }
        return false;
    }

    // ---- one-tap entry points ("🏷️ Set fields" button after a record is shown) ------------------------------------------------------

    private async Task OfferFieldsButtonAsync(Seller seller, SessionContextData ctx, CustomFieldEntity entity, int entityId, string recordName, CancellationToken ct)
    {
        if (!await _db.CustomFields.AnyAsync(f => f.SellerId == seller.Id && f.Entity == entity, ct)) return;
        ctx.FieldsTarget = new FieldsTargetData { Entity = FieldEntityLabel(entity), EntityId = entityId, RecordName = recordName };
        var urdu = Lang.Normalize(seller.PreferredLanguage) == Lang.UrduScript;
        await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber,
            urdu ? $"🏷️ {recordName} کی اضافی تفصیل؟" : $"🏷️ {recordName} ki extra details set karein?",
            new[] { urdu ? "🏷️ فیلڈ سیٹ کریں" : "🏷️ Set fields" }, ct);
    }

    private async Task HandleCustomFieldPickLastAsync(Seller seller, ConversationSession session, SessionContextData ctx, CancellationToken ct)
    {
        if (ctx.FieldsTarget is not { } target)
        {
            await ReplyAsync(seller, "Pehle koi order ya customer kholein, ya \"fields\" likhein.", ct);
            return;
        }

        var entity = FieldEntityFrom(target.Entity);
        var record = await LookupFieldRecordAsync(seller, entity, target.EntityId, ct);
        if (record is null)
        {
            await ReplyAsync(seller, $"Yeh {target.Entity} ab maujood nahi hai.", ct);
            return;
        }
        await StartCustomFieldFieldStageAsync(seller, session, ctx, entity, record.Value, await FieldsForAsync(seller, entity, ct), ct);
    }

    // ---- showing values ---------------------------------------------------------------------------------------------------------

    /// <summary>"🏷️ Fabric: Cotton" lines for one record, in the order the fields were created.</summary>
    private async Task<List<string>> CustomFieldLinesAsync(Seller seller, CustomFieldEntity entity, int entityId, CancellationToken ct, bool publicOnly = false)
    {
        var rows = await (from v in _db.CustomFieldValues.AsNoTracking()
                          join f in _db.CustomFields.AsNoTracking() on v.CustomFieldId equals f.Id
                          where v.SellerId == seller.Id && f.Entity == entity && v.EntityId == entityId && (!publicOnly || !f.IsPrivate)
                          orderby f.Id
                          select new { f.Name, v.Value }).ToListAsync(ct);
        return rows.Select(r => $"🏷️ {r.Name}: {r.Value}").ToList();
    }

    /// <summary>Custom values per product id as "Fabric: Cotton" pairs (catalog list, shareable catalog, receipts).</summary>
    private async Task<Dictionary<int, List<string>>> ProductFieldTextAsync(Seller seller, IReadOnlyCollection<int> productIds, bool publicOnly, CancellationToken ct)
    {
        if (productIds.Count == 0) return new();
        var rows = await (from v in _db.CustomFieldValues.AsNoTracking()
                          join f in _db.CustomFields.AsNoTracking() on v.CustomFieldId equals f.Id
                          where v.SellerId == seller.Id && f.Entity == CustomFieldEntity.Product && productIds.Contains(v.EntityId) && (!publicOnly || !f.IsPrivate)
                          orderby f.Id
                          select new { v.EntityId, f.Name, v.Value }).ToListAsync(ct);
        return rows.GroupBy(r => r.EntityId).ToDictionary(g => g.Key, g => g.Select(r => $"{r.Name}: {r.Value}").ToList());
    }

    private async Task<(int Id, string Name)?> LookupFieldRecordAsync(Seller seller, CustomFieldEntity entity, int id, CancellationToken ct)
    {
        switch (entity)
        {
            case CustomFieldEntity.Product:
                var product = await _db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.SellerId == seller.Id && p.Id == id, ct);
                return product is null ? null : (product.Id, product.Name);
            case CustomFieldEntity.Customer:
                var customer = await SellerCustomers(seller).AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
                return customer is null ? null : (customer.Id, customer.Name);
            default:
                var order = await _db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.SellerId == seller.Id && o.Id == id, ct);
                return order is null ? null : (order.Id, $"Order #{order.Id}");
        }
    }

    // Exact name wins; otherwise a unique partial match. Ambiguous or unknown names are answered here (null = reply already sent).
    private async Task<(int Id, string Name)?> ResolveFieldRecordAsync(Seller seller, SessionContextData ctx, CustomFieldEntity entity, string text, CancellationToken ct)
    {
        text = text.Trim();
        switch (entity)
        {
            case CustomFieldEntity.Order:
            {
                if (!int.TryParse(text.TrimStart('#'), out var number))
                {
                    await ReplyAsync(seller, "Order ka number likhein, jaise: set order 12 Gift Note = yes", ct);
                    return null;
                }
                var (orderId, _) = ResolveListNumber(ctx, number);
                var order = await LoadOrderForEditAsync(seller, orderId, ct);
                if (order is null)
                {
                    await ReplyAsync(seller, $"Order #{number} nahi mila.", ct);
                    return null;
                }
                return (order.Id, $"Order #{order.Id}");
            }
            case CustomFieldEntity.Customer:
            {
                if (int.TryParse(text, out var number) && ctx.LastListCustomerIds is { } ids && number >= 1 && number <= ids.Count)
                {
                    var byNumber = await SellerCustomers(seller).FirstOrDefaultAsync(c => c.Id == ids[number - 1], ct);
                    if (byNumber is not null) return (byNumber.Id, byNumber.Name);
                }
                var customers = await SellerCustomers(seller).ToListAsync(ct);
                return await PickRecordAsync(seller, text, "customer", customers.Select(c => (c.Id, c.Name)).ToList(), ct);
            }
            default:
            {
                var products = await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && p.IsActive).ToListAsync(ct);
                return await PickRecordAsync(seller, text, "product", products.Select(p => (p.Id, p.Name)).ToList(), ct);
            }
        }
    }

    private async Task<(int Id, string Name)?> PickRecordAsync(Seller seller, string text, string label, List<(int Id, string Name)> all, CancellationToken ct)
    {
        var matches = all.Where(x => string.Equals(x.Name, text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) matches = all.Where(x => x.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1) return matches[0];

        await ReplyAsync(seller, matches.Count == 0
            ? $"\"{text}\" naam ka koi {label} nahi mila."
            : $"\"{text}\" naam ke {matches.Count} {label}s hain:\n" + string.Join("\n", matches.Take(8).Select(x => $"• {x.Name}")) + "\n\nPoora naam likhein.", ct);
        return null;
    }
}
