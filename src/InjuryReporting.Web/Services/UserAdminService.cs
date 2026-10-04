using InjuryReporting.Web.Data;
using InjuryReporting.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace InjuryReporting.Web.Services;

public record UserRow(Guid Id, string Email, string DisplayName, string Role, bool IsSuspended, bool EmailConfirmed, bool TwoFactor, DateTime CreatedUtc);

public interface IUserAdminService
{
    Task<(List<UserRow> Rows, int Total)> ListAsync(string? search, int page, int pageSize, CancellationToken ct = default);
    Task<UserRow?> GetAsync(Guid id, CancellationToken ct = default);
    Task<OperationResult> SuspendAsync(Guid actorId, Guid targetId, string reason);
    Task<OperationResult> UnsuspendAsync(Guid actorId, Guid targetId);
    Task<OperationResult> SetRoleAsync(Guid actorId, Guid targetId, string role);
}

/// <summary>
/// User moderation and role administration with the guard rails:
/// <list type="bullet">
/// <item>Nobody can act on their own account (no self-demotion / self-suspension lockouts).</item>
/// <item>Admins may only moderate plain users. Admins cannot touch other staff or change roles.</item>
/// <item>Only Super Admins grant/revoke roles; the last active Super Admin cannot be removed or suspended.</item>
/// <item>Every change bumps the security stamp, which invalidates the target's sessions within a minute.</item>
/// </list>
/// </summary>
public class UserAdminService : IUserAdminService
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly AppDbContext _db;
    private readonly IAuditService _audit;

    public UserAdminService(UserManager<ApplicationUser> users, AppDbContext db, IAuditService audit)
    {
        _users = users; _db = db; _audit = audit;
    }

    public async Task<(List<UserRow>, int)> ListAsync(string? search, int page, int pageSize, CancellationToken ct = default)
    {
        var q = _db.Users.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToUpperInvariant();
            q = q.Where(u => u.NormalizedEmail!.Contains(s) || u.DisplayName.ToUpper().Contains(s));
        }
        var total = await q.CountAsync(ct);
        var users = await q.OrderBy(u => u.Email).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var ids = users.Select(u => u.Id).ToList();
        var roles = await (from ur in _db.UserRoles join r in _db.Roles on ur.RoleId equals r.Id where ids.Contains(ur.UserId) select new { ur.UserId, r.Name }).ToListAsync(ct);
        var rows = users.Select(u => ToRow(u, HighestRole(roles.Where(r => r.UserId == u.Id).Select(r => r.Name!)))).ToList();
        return (rows, total);
    }

    public async Task<UserRow?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var u = await _users.FindByIdAsync(id.ToString());
        return u == null ? null : ToRow(u, HighestRole(await _users.GetRolesAsync(u)));
    }

    public async Task<OperationResult> SuspendAsync(Guid actorId, Guid targetId, string reason)
    {
        var (actor, target, err) = await Load(actorId, targetId);
        if (err != null) return err;
        if (string.IsNullOrWhiteSpace(reason)) return OperationResult.Fail("A reason is required.");
        var actorRole = HighestRole(await _users.GetRolesAsync(actor!));
        var targetRole = HighestRole(await _users.GetRolesAsync(target!));
        if (AppRoles.Rank(targetRole) >= AppRoles.Rank(actorRole))
            return OperationResult.Fail("You can only moderate users with a lower role than your own.");

        target!.IsSuspended = true;
        target.SuspendedUtc = DateTime.UtcNow;
        target.SuspensionReason = reason.Trim();
        target.LockoutEnabled = true;
        target.LockoutEnd = DateTimeOffset.MaxValue;
        var result = await _users.UpdateAsync(target);
        if (!result.Succeeded) return OperationResult.Fail(string.Join(" ", result.Errors.Select(e => e.Description)));
        await _users.UpdateSecurityStampAsync(target);       // invalidates their sessions at the next stamp check (<= 1 min)
        await _audit.LogAsync("user.suspended", "User", target.Id.ToString(), "reason recorded on user");
        return OperationResult.Ok();
    }

    public async Task<OperationResult> UnsuspendAsync(Guid actorId, Guid targetId)
    {
        var (actor, target, err) = await Load(actorId, targetId);
        if (err != null) return err;
        var actorRole = HighestRole(await _users.GetRolesAsync(actor!));
        var targetRole = HighestRole(await _users.GetRolesAsync(target!));
        if (AppRoles.Rank(targetRole) >= AppRoles.Rank(actorRole))
            return OperationResult.Fail("You can only moderate users with a lower role than your own.");

        target!.IsSuspended = false;
        target.SuspendedUtc = null;
        target.SuspensionReason = null;
        target.LockoutEnd = null;
        await _users.ResetAccessFailedCountAsync(target);
        var result = await _users.UpdateAsync(target);
        if (!result.Succeeded) return OperationResult.Fail(string.Join(" ", result.Errors.Select(e => e.Description)));
        await _audit.LogAsync("user.unsuspended", "User", target.Id.ToString());
        return OperationResult.Ok();
    }

    public async Task<OperationResult> SetRoleAsync(Guid actorId, Guid targetId, string role)
    {
        if (!AppRoles.All.Contains(role)) return OperationResult.Fail("Unknown role.");
        var (actor, target, err) = await Load(actorId, targetId);
        if (err != null) return err;
        if (!await _users.IsInRoleAsync(actor!, AppRoles.SuperAdmin))
            return OperationResult.Fail("Only Super Admins can change roles.");

        var current = HighestRole(await _users.GetRolesAsync(target!));
        if (current == role) return OperationResult.Ok();
        if (current == AppRoles.SuperAdmin && await ActiveSuperAdminCount(excluding: target!.Id) == 0)
            return OperationResult.Fail("You cannot remove the last active Super Admin.");

        var existing = await _users.GetRolesAsync(target!);
        var removed = await _users.RemoveFromRolesAsync(target!, existing);
        if (!removed.Succeeded) return OperationResult.Fail(string.Join(" ", removed.Errors.Select(e => e.Description)));
        var added = await _users.AddToRoleAsync(target!, role);
        if (!added.Succeeded) return OperationResult.Fail(string.Join(" ", added.Errors.Select(e => e.Description)));
        await _users.UpdateSecurityStampAsync(target!);
        await _audit.LogAsync("user.role_changed", "User", target!.Id.ToString(), $"{current} -> {role}");
        return OperationResult.Ok();
    }

    private async Task<(ApplicationUser?, ApplicationUser?, OperationResult?)> Load(Guid actorId, Guid targetId)
    {
        if (actorId == targetId) return (null, null, OperationResult.Fail("You cannot change your own account here."));
        var actor = await _users.FindByIdAsync(actorId.ToString());
        var target = await _users.FindByIdAsync(targetId.ToString());
        if (actor == null || target == null) return (null, null, OperationResult.Fail("User not found."));
        return (actor, target, null);
    }

    private async Task<int> ActiveSuperAdminCount(Guid excluding)
    {
        var supers = await _users.GetUsersInRoleAsync(AppRoles.SuperAdmin);
        return supers.Count(u => !u.IsSuspended && u.Id != excluding);
    }

    public static string HighestRole(IEnumerable<string> roles) =>
        roles.OrderByDescending(AppRoles.Rank).FirstOrDefault() ?? AppRoles.User;

    private static UserRow ToRow(ApplicationUser u, string role) =>
        new(u.Id, u.Email ?? "", u.DisplayName, role, u.IsSuspended, u.EmailConfirmed, u.TwoFactorEnabled, u.CreatedUtc);
}
