using System.Text.Json;
using InjuryReporting.Web.Data;
using InjuryReporting.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace InjuryReporting.Web.Services;

public record ErasureResult(bool Succeeded, string? Error = null, int ReportsDeleted = 0, int IncidentsRemoved = 0, int AuditRowsScrubbed = 0)
{
    public static ErasureResult Fail(string error) => new(false, error);
}

public interface IUserDataService
{
    /// <summary>Everything the system holds about this account, as indented JSON (subject access / data portability).</summary>
    Task<byte[]> ExportAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Permanently deletes the account and (optionally) every report filed under it. <paramref name="actorId"/> is the
    /// person performing the erasure: the user themselves, or a Super Admin. Nobody else may erase an account.
    /// </summary>
    Task<ErasureResult> EraseAsync(Guid actorId, Guid userId, bool deleteReports, CancellationToken ct = default);
}

/// <summary>
/// Retrieval and erasure of an individual's data. Reports filed anonymously are not linked to any account, so they can
/// be neither retrieved nor erased by account (that is the point of filing anonymously).
/// </summary>
public class UserDataService : IUserDataService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly AppDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly IAuditService _audit;
    private readonly IAppEmailSender _email;

    public UserDataService(AppDbContext db, UserManager<ApplicationUser> users, IAuditService audit, IAppEmailSender email)
    {
        _db = db; _users = users; _audit = audit; _email = email;
    }

    public async Task<byte[]> ExportAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _db.Users.AsNoTracking().Include(u => u.Kingdom).FirstOrDefaultAsync(u => u.Id == userId, ct)
                   ?? throw new InvalidOperationException("User not found.");
        var roles = await _users.GetRolesAsync(user);
        var reports = await _db.Reports.AsNoTracking()
            .Where(r => r.ReporterUserId == userId)
            .OrderBy(r => r.SubmittedUtc)
            .Select(r => new
            {
                r.Id, r.IncidentId, InjuryDate = r.InjuryDate.ToString("yyyy-MM-dd"),
                Discipline = r.Discipline.Name, InjuryType = r.InjuryType.Name, Severity = r.Severity.ToString(),
                r.EventName, EventKingdom = r.EventKingdom.Name,
                InjuredPersonKingdom = r.InjuredKingdom == null ? "Non-member / unknown" : r.InjuredKingdom.Name,
                r.Narrative, r.SubmittedUtc, r.NarrativeEditedUtc
            })
            .ToListAsync(ct);
        var activity = await _db.AuditLog.AsNoTracking()
            .Where(a => a.ActorUserId == userId)
            .OrderBy(a => a.Id).Take(5000)
            .Select(a => new { a.TimestampUtc, a.Action, a.EntityType, a.EntityId, a.Detail, a.IpAddress })
            .ToListAsync(ct);

        var doc = new
        {
            exportedUtc = DateTime.UtcNow,
            account = new
            {
                user.Id, user.Email, user.DisplayName, user.EmailConfirmed, user.TwoFactorEnabled, user.CreatedUtc,
                MembershipKingdom = user.Kingdom?.Name, Roles = roles, user.IsSuspended, user.SuspendedUtc, user.SuspensionReason
            },
            reports,
            activity,
            notes = new[]
            {
                "Passwords, password hashes and security tokens are never exported.",
                "Reports submitted anonymously are not linked to any account, so they cannot appear here.",
                "Backups taken before an erasure expire on their own (daily 35 days, monthly 13 months)."
            }
        };
        return JsonSerializer.SerializeToUtf8Bytes(doc, Json);
    }

    public async Task<ErasureResult> EraseAsync(Guid actorId, Guid userId, bool deleteReports, CancellationToken ct = default)
    {
        var user = await _users.FindByIdAsync(userId.ToString());
        if (user == null) return ErasureResult.Fail("User not found.");

        var isSelf = actorId == userId;
        var actorEmail = user.Email;
        if (!isSelf)
        {
            var actor = await _users.FindByIdAsync(actorId.ToString());
            if (actor == null || !await _users.IsInRoleAsync(actor, AppRoles.SuperAdmin))
                return ErasureResult.Fail("Only a Super Admin can delete another person's account and data.");
            actorEmail = actor.Email;
        }

        if (await _users.IsInRoleAsync(user, AppRoles.SuperAdmin))
        {
            var others = (await _users.GetUsersInRoleAsync(AppRoles.SuperAdmin)).Count(u => u.Id != userId && !u.IsSuspended);
            if (others == 0) return ErasureResult.Fail("This is the only active Super Admin. Promote another Super Admin first.");
        }

        var email = user.Email ?? "";
        int reportsDeleted = 0, incidentsRemoved = 0, scrubbed;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        if (deleteReports)
        {
            var reports = await _db.Reports.Where(r => r.ReporterUserId == userId).ToListAsync(ct);
            var incidentIds = reports.Select(r => r.IncidentId).Distinct().ToList();
            reportsDeleted = reports.Count;
            _db.Reports.RemoveRange(reports);
            await _db.SaveChangesAsync(ct);

            foreach (var incidentId in incidentIds)
            {
                var incident = await _db.Incidents.Include(i => i.Reports).FirstOrDefaultAsync(i => i.Id == incidentId, ct);
                if (incident == null) continue;
                if (incident.Reports.Count == 0)
                {
                    // Nothing left: remove it, plus the retired tombstones that pointed at it (they hold no reports).
                    var tombstones = await _db.Incidents.Where(t => t.MergedIntoIncidentId == incident.Id).ToListAsync(ct);
                    _db.Incidents.RemoveRange(tombstones);
                    await _db.SaveChangesAsync(ct);
                    _db.Incidents.Remove(incident);
                    await _db.SaveChangesAsync(ct);
                    incidentsRemoved += 1 + tombstones.Count;
                }
                else
                {
                    incident.Severity = incident.Reports.Max(r => r.Severity);   // may drop now the user's report is gone
                    incident.UpdatedUtc = DateTime.UtcNow;
                    await _db.SaveChangesAsync(ct);
                }
            }
        }

        // Record the erasure first, with only pseudonymous ids, then blank the personal data in the audit trail.
        await _audit.LogForAsync(actorId, actorEmail, isSelf ? "account.erased_self" : "account.erased_by_admin", "User", userId.ToString(),
            deleteReports ? $"reports deleted: {reportsDeleted}; incidents removed: {incidentsRemoved}" : "filed reports kept, now unlinked", ct);

        scrubbed = await _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "AuditLog" SET "ActorEmail" = NULL, "IpAddress" = NULL
            WHERE ("ActorUserId" = {userId} OR lower("ActorEmail") = {email.ToLowerInvariant()})
              AND ("ActorEmail" IS NOT NULL OR "IpAddress" IS NOT NULL)
            """, ct);

        var result = await _users.DeleteAsync(user);   // cascades sign-in, roles, tokens; unlinks any kept reports
        if (!result.Succeeded)
        {
            await tx.RollbackAsync(ct);
            return ErasureResult.Fail(string.Join(" ", result.Errors.Select(e => e.Description)));
        }
        await tx.CommitAsync(ct);

        if (!string.IsNullOrEmpty(email))
            await _email.SendAsync(email, "Your Injury Reporting account was deleted",
                "<p>Your account" + (deleteReports ? " and the reports filed under it were" : " was") + " deleted" +
                (isSelf ? "." : " by an administrator.") +
                (deleteReports ? "" : " Reports you filed remain in the safety statistics but are no longer linked to you.") + "</p>");

        return new ErasureResult(true, null, reportsDeleted, incidentsRemoved, scrubbed);
    }
}
