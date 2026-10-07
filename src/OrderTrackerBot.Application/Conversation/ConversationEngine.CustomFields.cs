using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

// Seller-defined attributes (WordPress-style custom fields) on products, customers and orders. Values are free text.
//   add field product Fabric · remove field customer Birthday · fields · fields product Kurti · set product Kurti Fabric = Cotton
public partial class ConversationEngine
{
    private const int MaxCustomFieldsPerEntity = 10;
    private const int MaxCustomFieldNameLength = 30;
    private const int MaxCustomFieldValueLength = 200;

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

    private async Task HandleCustomFieldAddAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var entity = FieldEntityFrom(cmd.Text);
        var name = Regex.Replace(cmd.Text2!.Trim(), @"\s+", " ");
        if (name.Length > MaxCustomFieldNameLength || name.IndexOfAny(new[] { '=', ':' }) >= 0)
        {
            await ReplyAsync(seller, $"Field ka naam chhota rakhein ({MaxCustomFieldNameLength} huroof tak, \"=\" ya \":\" ke baghair). Jaise: add field {FieldEntityLabel(entity)} Fabric", ct);
            return;
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

        _db.CustomFields.Add(new CustomField { SellerId = seller.Id, Entity = entity, Name = name });
        await ReplyAsync(seller, $"✅ {FieldEntityLabel(entity)} field \"{name}\" ban gayi.\nValue likhein: {FieldExample(entity, name)}\nSab fields: \"fields\"", ct);
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
            var names = fields.Where(f => f.Entity == entity).Select(f => f.Name).ToList();
            rows.Add($"{char.ToUpperInvariant(FieldEntityLabel(entity)[0])}{FieldEntityLabel(entity)[1..]}: {(names.Count == 0 ? "—" : string.Join(", ", names))}");
        }
        await ReplyAsync(seller,
            "🏷️ Custom fields:\n\n" + string.Join("\n", rows) +
            "\n\nNayi: \"add field product Fabric\"\nValue: \"set product Kurti Fabric = Cotton\"\nDekhein: \"fields product Kurti\"\nHatayein: \"remove field product Fabric\"", ct);
    }

    private async Task HandleCustomFieldSetAsync(Seller seller, SessionContextData ctx, ParsedCommand cmd, CancellationToken ct)
    {
        var entity = FieldEntityFrom(cmd.Text);
        var label = FieldEntityLabel(entity);
        var fields = await _db.CustomFields.Where(f => f.SellerId == seller.Id && f.Entity == entity).ToListAsync(ct);
        var lhs = cmd.Text2!.Trim();

        // The field name is the longest defined name that ends the text before "=": "Kurti Fabric" -> record "Kurti", field "Fabric".
        var field = fields
            .Where(f => lhs.EndsWith(f.Name, StringComparison.OrdinalIgnoreCase)
                        && (lhs.Length == f.Name.Length || char.IsWhiteSpace(lhs[lhs.Length - f.Name.Length - 1])))
            .OrderByDescending(f => f.Name.Length).FirstOrDefault();
        if (field is null)
        {
            await ReplyAsync(seller, fields.Count == 0
                ? $"Pehle {label} field banayein: \"add field {label} Fabric\""
                : $"{label} ki yeh field nahi mili. Maujood: {string.Join(", ", fields.Select(f => f.Name))}\nNayi: \"add field {label} <naam>\"", ct);
            return;
        }

        var recordText = lhs[..^field.Name.Length].Trim();
        if (recordText.Length == 0)
        {
            await ReplyAsync(seller, $"Kis {label} ke liye? Jaise: {FieldExample(entity, field.Name)}", ct);
            return;
        }

        var value = cmd.Text3!.Trim();
        var clear = value is "-" || value.Equals("clear", StringComparison.OrdinalIgnoreCase) || value.Equals("hatao", StringComparison.OrdinalIgnoreCase);
        if (!clear && value.Length > MaxCustomFieldValueLength)
        {
            await ReplyAsync(seller, $"Value bohat lambi hai ({MaxCustomFieldValueLength} huroof tak).", ct);
            return;
        }

        if (await ResolveFieldRecordAsync(seller, ctx, entity, recordText, ct) is not { } record) return;

        var current = await _db.CustomFieldValues.FirstOrDefaultAsync(v => v.CustomFieldId == field.Id && v.EntityId == record.Id, ct);
        if (clear)
        {
            if (current is not null) _db.CustomFieldValues.Remove(current);
            await ReplyAsync(seller, $"🗑️ {record.Name} ki {field.Name} hata di.", ct);
            return;
        }

        var previous = current?.Value;
        if (current is null) _db.CustomFieldValues.Add(new CustomFieldValue { SellerId = seller.Id, CustomFieldId = field.Id, EntityId = record.Id, Value = value });
        else current.Value = value;
        await ReplyAsync(seller, $"✅ {record.Name} — {field.Name}: {value}" + (previous is null ? "" : $" (pehle: {previous})"), ct);
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
