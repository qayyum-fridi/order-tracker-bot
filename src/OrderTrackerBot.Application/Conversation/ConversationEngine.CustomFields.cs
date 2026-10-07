using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Seller-defined attributes (WordPress-style custom fields) on products, customers and orders. Values are free text.
//   add field product Fabric · remove field customer Birthday · fields · fields product Kurti · set product Kurti Fabric = Cotton
public partial class ConversationEngine
{
    private const int MaxCustomFieldsPerEntity = 10;
    private const int MaxCustomFieldNameLength = 30;
    private const int MaxCustomFieldValueLength = 200;
    private const int MaxCustomFieldOptions = 10;      // one WhatsApp list message
    private const int MaxCustomFieldOptionLength = 20; // WhatsApp button title limit

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

    private static string FieldExample(CustomFieldEntity entity, string field) => entity switch
    {
        CustomFieldEntity.Product => $"set product Kurti {field} = ...",
        CustomFieldEntity.Customer => $"set customer Sara {field} = ...",
        _ => $"set order 12 {field} = ..."
    };

    // "add field product Fabric" = free text; "add field product Fabric: Cotton, Lawn, Silk" = choice field (tap to select).
    private async Task HandleCustomFieldAddAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var entity = FieldEntityFrom(cmd.Text);
        var raw = cmd.Text2!.Trim();
        var cut = raw.IndexOfAny(new[] { ':', '=' });
        var name = Regex.Replace((cut < 0 ? raw : raw[..cut]).Trim(), @"\s+", " ");
        if (name.Length == 0 || name.Length > MaxCustomFieldNameLength || name.IndexOfAny(new[] { '=', ':' }) >= 0)
        {
            await ReplyAsync(seller, $"Field ka naam chhota rakhein ({MaxCustomFieldNameLength} huroof tak). Jaise: add field {FieldEntityLabel(entity)} Fabric", ct);
            return;
        }

        List<string>? options = null;
        if (cut >= 0)
        {
            var (parsed, error) = ParseFieldOptions(raw[(cut + 1)..]);
            if (error is not null)
            {
                await ReplyAsync(seller, error + $"\nJaise: add field {FieldEntityLabel(entity)} {name}: Cotton, Lawn, Silk", ct);
                return;
            }
            options = parsed;
        }

        var existing = await _db.CustomFields.Where(f => f.SellerId == seller.Id && f.Entity == entity).ToListAsync(ct);
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

        _db.CustomFields.Add(new CustomField { SellerId = seller.Id, Entity = entity, Name = name, Options = options is null ? null : string.Join('|', options) });
        await ReplyAsync(seller,
            $"✅ {FieldEntityLabel(entity)} field \"{name}\" ban gayi" + (options is null ? "." : $" — options: {string.Join(", ", options)}.") +
            (options is null ? $"\nValue likhein: {FieldExample(entity, name)}" : $"\nChunne ke liye: set {FieldEntityLabel(entity)} {FieldRecordExample(entity)} {name}") +
            "\nSab fields: \"fields\"", ct);
    }

    private static string FieldRecordExample(CustomFieldEntity entity) => entity switch
    {
        CustomFieldEntity.Product => "Kurti",
        CustomFieldEntity.Customer => "Sara",
        _ => "12"
    };

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

    // "add option product Fabric: Chiffon, Silk" extends a choice field.
    private async Task HandleCustomFieldOptionAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var entity = FieldEntityFrom(cmd.Text);
        var wanted = cmd.Text2!.Trim();
        var field = await _db.CustomFields.FirstOrDefaultAsync(f => f.SellerId == seller.Id && f.Entity == entity && f.Name.ToLower() == wanted.ToLower(), ct);
        if (field is null)
        {
            await ReplyAsync(seller, $"{FieldEntityLabel(entity)} ki \"{wanted}\" field nahi mili. \"fields\" se dekhein.", ct);
            return;
        }
        if (field.Options is null)
        {
            await ReplyAsync(seller, $"\"{field.Name}\" free-text field hai. Choice banane ke liye pehle \"remove field {FieldEntityLabel(entity)} {field.Name}\", phir \"add field {FieldEntityLabel(entity)} {field.Name}: A, B, C\".", ct);
            return;
        }

        var added = new List<string>();
        var current = field.OptionList;
        foreach (var part in cmd.Text3!.Split(new[] { ',', '،', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var option = Regex.Replace(part, @"\s+", " ");
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

    private async Task HandleCustomFieldRemoveAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var entity = FieldEntityFrom(cmd.Text);
        var wanted = cmd.Text2!.Trim();
        var field = await _db.CustomFields.FirstOrDefaultAsync(f => f.SellerId == seller.Id && f.Entity == entity && f.Name.ToLower() == wanted.ToLower(), ct);
        if (field is null)
        {
            await ReplyAsync(seller, $"{FieldEntityLabel(entity)} ki \"{wanted}\" field nahi mili. \"fields\" se dekhein.", ct);
            return;
        }

        var values = await _db.CustomFieldValues.Where(v => v.CustomFieldId == field.Id).ToListAsync(ct);
        _db.CustomFieldValues.RemoveRange(values);
        _db.CustomFields.Remove(field);
        await ReplyAsync(seller, $"🗑️ {FieldEntityLabel(entity)} field \"{field.Name}\" hata di" + (values.Count > 0 ? $" ({values.Count} values bhi delete)." : "."), ct);
    }

    private async Task HandleCustomFieldListAsync(Seller seller, SessionContextData ctx, ParsedCommand cmd, CancellationToken ct)
    {
        if (cmd.Text is not null)
        {
            var entity = FieldEntityFrom(cmd.Text);
            if (await ResolveFieldRecordAsync(seller, ctx, entity, cmd.Text2!, ct) is not { } record) return;
            var lines = await CustomFieldLinesAsync(seller, entity, record.Id, ct);
            await ReplyAsync(seller, lines.Count == 0
                ? $"{record.Name} ki koi custom value set nahi hai. \"fields\" se fields dekhein."
                : $"{record.Name}\n" + string.Join("\n", lines), ct);
            return;
        }

        var fields = await _db.CustomFields.AsNoTracking().Where(f => f.SellerId == seller.Id).OrderBy(f => f.Id).ToListAsync(ct);
        if (fields.Count == 0)
        {
            await ReplyAsync(seller,
                "🏷️ Abhi koi custom field nahi hai.\n\nApni fields banayein:\n" +
                "• add field product Fabric\n• add field customer Birthday\n• add field order Gift Note\n\n" +
                "Phir value likhein: set product Kurti Fabric = Cotton", ct);
            return;
        }

        var rows = new List<string>();
        foreach (var entity in new[] { CustomFieldEntity.Product, CustomFieldEntity.Customer, CustomFieldEntity.Order })
        {
            var names = fields.Where(f => f.Entity == entity).Select(f => f.Options is null ? f.Name : $"{f.Name} ({string.Join("/", f.OptionList)})").ToList();
            rows.Add($"{char.ToUpperInvariant(FieldEntityLabel(entity)[0])}{FieldEntityLabel(entity)[1..]}: {(names.Count == 0 ? "—" : string.Join(", ", names))}");
        }
        await ReplyAsync(seller,
            "🏷️ Custom fields:\n\n" + string.Join("\n", rows) +
            "\n\nNayi: \"add field product Fabric\" ya choices ke saath \"add field product Fabric: Cotton, Lawn\"\nValue: \"set product Kurti Fabric = Cotton\" ya sirf \"set product Kurti\" likh kar chunein\nDekhein: \"fields product Kurti\"\nHatayein: \"remove field product Fabric\"", ct);
    }

    private async Task HandleCustomFieldSetAsync(Seller seller, ConversationSession session, SessionContextData ctx, ParsedCommand cmd, CancellationToken ct)
    {
        var entity = FieldEntityFrom(cmd.Text);
        var label = FieldEntityLabel(entity);
        var fields = await _db.CustomFields.Where(f => f.SellerId == seller.Id && f.Entity == entity).OrderBy(f => f.Id).ToListAsync(ct);
        var lhs = cmd.Text2!.Trim();
        var tapping = cmd.Text3 is null; // "set product Kurti [Fabric]" without "=" -> pick from buttons/list

        // The field name is the longest defined name that ends the text before "=": "Kurti Fabric" -> record "Kurti", field "Fabric".
        var field = fields
            .Where(f => lhs.EndsWith(f.Name, StringComparison.OrdinalIgnoreCase)
                        && (lhs.Length == f.Name.Length || char.IsWhiteSpace(lhs[lhs.Length - f.Name.Length - 1])))
            .OrderByDescending(f => f.Name.Length).FirstOrDefault();
        if (field is null && !tapping)
        {
            await ReplyAsync(seller, fields.Count == 0
                ? $"Pehle {label} field banayein: \"add field {label} Fabric\""
                : $"{label} ki yeh field nahi mili. Maujood: {string.Join(", ", fields.Select(f => f.Name))}\nNayi: \"add field {label} <naam>\"", ct);
            return;
        }

        var recordText = field is null ? lhs : lhs[..^field.Name.Length].Trim();
        if (recordText.Length == 0)
        {
            await ReplyAsync(seller, $"Kis {label} ke liye? Jaise: {FieldExample(entity, field!.Name)}", ct);
            return;
        }

        if (tapping && field is null && fields.Count == 0)
        {
            await ReplyAsync(seller, $"Pehle {label} field banayein: \"add field {label} Fabric\" (ya choices ke saath: \"add field {label} Fabric: Cotton, Lawn\")", ct);
            return;
        }

        if (await ResolveFieldRecordAsync(seller, ctx, entity, recordText, ct) is not { } record) return;

        if (tapping)
        {
            if (field is null) await StartCustomFieldPickAsync(seller, session, ctx, new CustomFieldPickData { Stage = "field", Entity = label, EntityId = record.Id, RecordName = record.Name }, fields, ct);
            else await StartCustomFieldValueAsync(seller, session, ctx, field, label, record, ct);
            return;
        }

        var value = cmd.Text3!.Trim();
        var clear = value is "-" || value.Equals("clear", StringComparison.OrdinalIgnoreCase) || value.Equals("hatao", StringComparison.OrdinalIgnoreCase);
        if (clear)
        {
            var current = await _db.CustomFieldValues.FirstOrDefaultAsync(v => v.CustomFieldId == field!.Id && v.EntityId == record.Id, ct);
            if (current is not null) _db.CustomFieldValues.Remove(current);
            await ReplyAsync(seller, $"🗑️ {record.Name} ki {field!.Name} hata di.", ct);
            return;
        }
        if (value.Length > MaxCustomFieldValueLength)
        {
            await ReplyAsync(seller, $"Value bohat lambi hai ({MaxCustomFieldValueLength} huroof tak).", ct);
            return;
        }

        if (field!.Options is not null)
        {
            var canonical = field.OptionList.FirstOrDefault(o => string.Equals(o, value, StringComparison.OrdinalIgnoreCase));
            if (canonical is null)
            {
                await ReplyAsync(seller, $"\"{value}\" {field.Name} ka option nahi hai. Neeche se chunein:", ct);
                await StartCustomFieldValueAsync(seller, session, ctx, field, label, record, ct);
                return;
            }
            value = canonical;
        }

        await ReplyAsync(seller, await SaveCustomFieldValueAsync(seller, field, record.Id, record.Name, value, ct), ct);
    }

    private async Task<string> SaveCustomFieldValueAsync(Seller seller, CustomField field, int entityId, string recordName, string value, CancellationToken ct)
    {
        var current = await _db.CustomFieldValues.FirstOrDefaultAsync(v => v.CustomFieldId == field.Id && v.EntityId == entityId, ct);
        var previous = current?.Value;
        if (current is null) _db.CustomFieldValues.Add(new CustomFieldValue { SellerId = seller.Id, CustomFieldId = field.Id, EntityId = entityId, Value = value });
        else current.Value = value;
        return $"✅ {recordName} — {field.Name}: {value}" + (previous is null || previous == value ? "" : $" (pehle: {previous})");
    }

    // ---- tap-to-select flow: fields -> value (buttons/list for choice fields, free text otherwise) --------------------------------

    private Task StartCustomFieldPickAsync(Seller seller, ConversationSession session, SessionContextData ctx, CustomFieldPickData pick, List<CustomField> fields, CancellationToken ct)
    {
        ctx.CustomFieldPick = pick;
        SetState(session, ConversationState.AwaitingCustomFieldChoice);
        return SendPickerAsync(seller, $"🏷️ {pick.RecordName} — kaunsi field set karni hai?", fields.Select(f => f.Name).ToList(), ct);
    }

    private async Task StartCustomFieldValueAsync(Seller seller, ConversationSession session, SessionContextData ctx, CustomField field, string entityLabel, (int Id, string Name) record, CancellationToken ct)
    {
        var pick = new CustomFieldPickData { Entity = entityLabel, EntityId = record.Id, RecordName = record.Name, FieldId = field.Id };
        ctx.CustomFieldPick = pick;
        SetState(session, ConversationState.AwaitingCustomFieldChoice);
        if (field.Options is null)
        {
            pick.Stage = "text";
            await ReplyAsync(seller, $"🏷️ {record.Name} — {field.Name} likhein (ya \"cancel\"):", ct);
            return;
        }

        pick.Stage = "value";
        await SendPickerAsync(seller, $"🏷️ {record.Name} — {field.Name} chunein:", field.OptionList, ct);
    }

    /// <summary>Up to 3 short items become buttons; otherwise a list (max 10). The tapped item arrives as its own text.</summary>
    private Task SendPickerAsync(Seller seller, string body, IReadOnlyList<string> items, CancellationToken ct)
    {
        if (items.Count <= 3 && items.All(i => i.Length <= MaxCustomFieldOptionLength))
            return _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, body, items, ct);

        var rows = items.Take(MaxCustomFieldOptions).Select(i => new MenuRow(i, i.Length <= 24 ? i : i[..23] + "…")).ToList();
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

        var entity = FieldEntityFrom(pick.Entity);
        var text = message.Trim();
        static string? Match(IReadOnlyList<string> items, string text) =>
            items.FirstOrDefault(i => string.Equals(i, text, StringComparison.OrdinalIgnoreCase))
            ?? (int.TryParse(text, out var n) && n >= 1 && n <= items.Count ? items[n - 1] : null);

        if (pick.Stage == "field")
        {
            var fields = await _db.CustomFields.Where(f => f.SellerId == seller.Id && f.Entity == entity).OrderBy(f => f.Id).ToListAsync(ct);
            var chosen = Match(fields.Select(f => f.Name).ToList(), text);
            if (chosen is not null)
            {
                var field = fields.First(f => f.Name == chosen);
                await StartCustomFieldValueAsync(seller, session, ctx, field, pick.Entity, (pick.EntityId, pick.RecordName), ct);
                return;
            }
            if (await TryLeaveCustomFieldPickAsync(seller, session, ctx, text, ct)) return;
            await ReplyAsync(seller, "Neeche se field chunein (ya \"cancel\").", ct);
            await SendPickerAsync(seller, $"🏷️ {pick.RecordName} — kaunsi field set karni hai?", fields.Select(f => f.Name).ToList(), ct);
            return;
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
            if (text.Length > MaxCustomFieldValueLength)
            {
                await ReplyAsync(seller, $"Value bohat lambi hai ({MaxCustomFieldValueLength} huroof tak). Chhoti likhein.", ct);
                return;
            }
            EndCustomFieldPick(session, ctx);
            await ReplyAsync(seller, await SaveCustomFieldValueAsync(seller, current, pick.EntityId, pick.RecordName, text, ct), ct);
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
        if (text.Equals("cancel", StringComparison.OrdinalIgnoreCase) || text.Equals("back", StringComparison.OrdinalIgnoreCase) || text.Equals("skip", StringComparison.OrdinalIgnoreCase))
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

    /// <summary>"🏷️ Fabric: Cotton" lines for one record, in the order the fields were created.</summary>
    private async Task<List<string>> CustomFieldLinesAsync(Seller seller, CustomFieldEntity entity, int entityId, CancellationToken ct)
    {
        var rows = await (from v in _db.CustomFieldValues.AsNoTracking()
                          join f in _db.CustomFields.AsNoTracking() on v.CustomFieldId equals f.Id
                          where v.SellerId == seller.Id && f.Entity == entity && v.EntityId == entityId
                          orderby f.Id
                          select new { f.Name, v.Value }).ToListAsync(ct);
        return rows.Select(r => $"🏷️ {r.Name}: {r.Value}").ToList();
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
