using InjuryReporting.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace InjuryReporting.Web.Services;

public record OperationResult(bool Succeeded, string? Error = null)
{
    public static OperationResult Ok() => new(true);
    public static OperationResult Fail(string error) => new(false, error);
}

public record IncidentEdit(
    int DisciplineId, int InjuryTypeId, Severity Severity, DateOnly InjuryDate, string EventName,
    int EventKingdomId, int? InjuredKingdomId, string? ReviewerNotes, bool MarkReviewed);

public interface IIncidentService
{
    Task<OperationResult> MergeAsync(Guid targetId, IReadOnlyCollection<Guid> sourceIds, CancellationToken ct = default);
    Task<OperationResult> SplitReportAsync(Guid reportId, CancellationToken ct = default);
    Task<OperationResult> UpdateAsync(Guid incidentId, IncidentEdit edit, CancellationToken ct = default);
}

/// <summary>
/// Combines and separates reports. Rules that keep the data free of circular and duplicate links:
/// <list type="number">
/// <item>A report belongs to exactly one incident (single FK; reports never reference each other).</item>
/// <item>Only <i>active</i> incidents can be merge targets, and merged sources are retired immediately,
/// so a retired incident can never be a target again — merge chains and cycles cannot form.</item>
/// <item>An incident cannot be merged into itself (also enforced by a DB check constraint).</item>
/// </list>
/// </summary>
public class IncidentService : IIncidentService
{
    private readonly AppDbContext _db;
    private readonly IAuditService _audit;

    public IncidentService(AppDbContext db, IAuditService audit) { _db = db; _audit = audit; }

    public async Task<OperationResult> MergeAsync(Guid targetId, IReadOnlyCollection<Guid> sourceIds, CancellationToken ct = default)
    {
        var sources = sourceIds.Distinct().ToList();
        if (sources.Count == 0) return OperationResult.Fail("Select at least one incident to merge.");
        if (sources.Contains(targetId)) return OperationResult.Fail("An incident cannot be merged into itself.");

        var target = await _db.Incidents.FirstOrDefaultAsync(i => i.Id == targetId, ct);
        if (target == null) return OperationResult.Fail("Target incident not found.");
        if (target.Status == IncidentStatus.Retired || target.MergedIntoIncidentId != null)
            return OperationResult.Fail("The target incident has already been merged into another incident.");

        var incidents = await _db.Incidents.Include(i => i.Reports).Where(i => sources.Contains(i.Id)).ToListAsync(ct);
        if (incidents.Count != sources.Count) return OperationResult.Fail("One or more selected incidents were not found.");
        if (incidents.Any(i => i.Status == IncidentStatus.Retired))
            return OperationResult.Fail("One or more selected incidents have already been merged.");

        var moved = 0;
        foreach (var src in incidents)
        {
            foreach (var report in src.Reports.ToList())
            {
                report.IncidentId = target.Id;
                moved++;
            }
            if (src.Severity > target.Severity) target.Severity = src.Severity;
            src.Status = IncidentStatus.Retired;
            src.MergedIntoIncidentId = target.Id;
            src.NeedsReview = false;
            src.UpdatedUtc = DateTime.UtcNow;
        }
        target.NeedsReview = false;
        target.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);   // single SaveChanges = single atomic transaction

        await _audit.LogAsync("incident.merged", "Incident", target.Id.ToString(),
            $"merged {incidents.Count} incident(s), moved {moved} report(s)", ct: ct);
        return OperationResult.Ok();
    }

    public async Task<OperationResult> SplitReportAsync(Guid reportId, CancellationToken ct = default)
    {
        var report = await _db.Reports.Include(r => r.Incident).ThenInclude(i => i.Reports).FirstOrDefaultAsync(r => r.Id == reportId, ct);
        if (report == null) return OperationResult.Fail("Report not found.");
        var old = report.Incident;
        if (old.Reports.Count < 2) return OperationResult.Fail("This is the only report on the incident; there is nothing to split.");

        var now = DateTime.UtcNow;
        var fresh = new Incident
        {
            DisciplineId = report.DisciplineId,
            InjuryTypeId = report.InjuryTypeId,
            Severity = report.Severity,
            InjuryDate = report.InjuryDate,
            EventName = report.EventName,
            EventKingdomId = report.EventKingdomId,
            InjuredKingdomId = report.InjuredKingdomId,
            MatchKey = report.MatchKey,
            LooseKey = report.LooseKey,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        _db.Incidents.Add(fresh);     // explicit: a preset Guid key would otherwise be assumed to already exist
        report.Incident = fresh;
        report.AutoLinked = false;
        report.PossibleDuplicate = false;
        // Recompute severity of what's left on the original incident.
        old.Severity = old.Reports.Where(r => r.Id != report.Id).Max(r => r.Severity);
        old.UpdatedUtc = now;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync("incident.report_split", "Incident", old.Id.ToString(), $"report moved to new incident {fresh.Id}", ct: ct);
        return OperationResult.Ok();
    }

    public async Task<OperationResult> UpdateAsync(Guid incidentId, IncidentEdit e, CancellationToken ct = default)
    {
        var inc = await _db.Incidents.FirstOrDefaultAsync(i => i.Id == incidentId, ct);
        if (inc == null) return OperationResult.Fail("Incident not found.");
        if (inc.Status == IncidentStatus.Retired) return OperationResult.Fail("A merged incident cannot be edited; edit the incident it was merged into.");

        inc.DisciplineId = e.DisciplineId;
        inc.InjuryTypeId = e.InjuryTypeId;
        inc.Severity = e.Severity;
        inc.InjuryDate = e.InjuryDate;
        inc.EventName = e.EventName.Trim();
        inc.EventKingdomId = e.EventKingdomId;
        inc.InjuredKingdomId = e.InjuredKingdomId;
        inc.ReviewerNotes = string.IsNullOrWhiteSpace(e.ReviewerNotes) ? null : e.ReviewerNotes.Trim();
        inc.MatchKey = MatchKeys.Strict(inc.DisciplineId, inc.InjuryTypeId, inc.InjuryDate, inc.EventName, inc.EventKingdomId, inc.InjuredKingdomId);
        inc.LooseKey = MatchKeys.Loose(inc.DisciplineId, inc.InjuryDate, inc.EventName, inc.EventKingdomId);
        if (e.MarkReviewed) { inc.Status = IncidentStatus.Reviewed; inc.NeedsReview = false; }
        inc.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync("incident.updated", "Incident", inc.Id.ToString(), ct: ct);
        return OperationResult.Ok();
    }
}
