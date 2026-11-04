using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

// Staff records (MVP): a list of the people who work in the shop, for the owner's reference. No login, no permissions.
// Removing a person switches the record off; past wages are kept as they were.
public partial class ConversationEngine
{
    private async Task HandleStaffAddAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var name = cmd.Text?.Trim() ?? "";
        if (name.Length == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("staff.007"), ct);
            return;
        }

        var lower = name.ToLowerInvariant();
        var existing = await _db.StaffMembers.Where(s => s.SellerId == seller.Id && s.Name.ToLower() == lower).ToListAsync(ct);
        if (existing.Any(s => s.IsActive))
        {
            await ReplyAsync(seller, MessageTemplates.Get("staff.002", name), ct);
            return;
        }

        var member = existing.FirstOrDefault();
        if (member is null)
        {
            member = new StaffMember { SellerId = seller.Id, Name = name };
            _db.StaffMembers.Add(member);
        }
        member.IsActive = true;
        member.Phone = cmd.Text2;
        member.Role = cmd.Text3;
        await _db.SaveChangesAsync(ct);

        var detail = (string.IsNullOrWhiteSpace(member.Phone) ? "" : $" ({member.Phone})") + (string.IsNullOrWhiteSpace(member.Role) ? "" : $" — {member.Role}");
        await ReplyAsync(seller, MessageTemplates.Get("staff.001", name, detail), ct);
    }

    /// <summary>
    /// A wage is paid to a person, so a worker name not on the active staff list is added to it (no question asked).
    /// Returns true when a record was added or switched back on.
    /// </summary>
    private async Task<bool> EnsureStaffForWageAsync(Seller seller, string worker, CancellationToken ct)
    {
        var lower = worker.ToLowerInvariant();
        var rows = await _db.StaffMembers.Where(s => s.SellerId == seller.Id && s.Name.ToLower() == lower).ToListAsync(ct);
        if (rows.Any(s => s.IsActive)) return false;

        var member = rows.FirstOrDefault();
        if (member is null) _db.StaffMembers.Add(new StaffMember { SellerId = seller.Id, Name = worker });
        else member.IsActive = true;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private async Task HandleStaffListAsync(Seller seller, CancellationToken ct)
    {
        var staff = await _db.StaffMembers.AsNoTracking()
            .Where(s => s.SellerId == seller.Id && s.IsActive)
            .OrderBy(s => s.Name).ToListAsync(ct);
        if (staff.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("staff.004"), ct);
            return;
        }

        var lines = string.Join("\n", staff.Select(s =>
            $"• {s.Name}{(string.IsNullOrWhiteSpace(s.Phone) ? "" : $" ({s.Phone})")}{(string.IsNullOrWhiteSpace(s.Role) ? "" : $" — {s.Role}")}"));
        await ReplyAsync(seller, MessageTemplates.Get("staff.003", staff.Count, lines), ct);
    }

    private async Task HandleStaffRemoveAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var name = cmd.Text?.Trim() ?? "";
        var lower = name.ToLowerInvariant();
        var member = await _db.StaffMembers.FirstOrDefaultAsync(s => s.SellerId == seller.Id && s.IsActive && s.Name.ToLower() == lower, ct);
        if (member is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("staff.006", name), ct);
            return;
        }

        member.IsActive = false;
        await _db.SaveChangesAsync(ct);
        await ReplyAsync(seller, MessageTemplates.Get("staff.005", member.Name), ct);
    }
}
