using InjuryReporting.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace InjuryReporting.Web.Services;

public record ReportInput(
    Guid SubmissionToken,
    int DisciplineId,
    int InjuryTypeId,
    Severity Severity,
    DateOnly InjuryDate,
    string EventName,
    int EventKingdomId,
    int? InjuredKingdomId,
    string Narrative);

public enum SubmitOutcome
{
    /// <summary>A new incident was created.</summary>
    Created,
    /// <summary>An identical incident already existed; the report was combined into it.</summary>
    CombinedWithExisting,
    /// <summary>The same registered user already filed an identical report. Nothing stored.</summary>
    DuplicateByReporter,
    /// <summary>This exact form submission (token) was already processed. Nothing stored.</summary>
    AlreadySubmitted
}

public record SubmitResult(SubmitOutcome Outcome, Guid? ReportId);

public interface IReportService
{
    Task<SubmitResult> SubmitAsync(ReportInput input, Guid? reporterUserId, CancellationToken ct = default);
}

public class ReportService : IReportService
{
    private readonly AppDbContext _db;
    private readonly IAuditService _audit;

    public ReportService(AppDbContext db, IAuditService audit) { _db = db; _audit = audit; }

    public async Task<SubmitResult> SubmitAsync(ReportInput input, Guid? reporterUserId, CancellationToken ct = default)
    {
        // 1. Idempotency: the same form posted twice (double-click, back button, replay) is a no-op.
        if (await _db.Reports.AnyAsync(r => r.SubmissionToken == input.SubmissionToken, ct))
            return new SubmitResult(SubmitOutcome.AlreadySubmitted, null);

        var strict = MatchKeys.Strict(input.DisciplineId, input.InjuryTypeId, input.InjuryDate, input.EventName, input.EventKingdomId, input.InjuredKingdomId);
        var loose = MatchKeys.Loose(input.DisciplineId, input.InjuryDate, input.EventName, input.EventKingdomId);

        // 2. A registered user cannot file the same report twice.
        if (reporterUserId is { } uid &&
            await _db.Reports.AnyAsync(r => r.ReporterUserId == uid && r.MatchKey == strict, ct))
            return new SubmitResult(SubmitOutcome.DuplicateByReporter, null);

        var now = DateTime.UtcNow;
        var report = new InjuryReport
        {
            ReporterUserId = reporterUserId,
            DisciplineId = input.DisciplineId,
            InjuryTypeId = input.InjuryTypeId,
            Severity = input.Severity,
            InjuryDate = input.InjuryDate,
            EventName = input.EventName.Trim(),
            EventKingdomId = input.EventKingdomId,
            InjuredKingdomId = input.InjuredKingdomId,
            Narrative = input.Narrative.Trim(),
            SubmissionToken = input.SubmissionToken,
            MatchKey = strict,
            LooseKey = loose,
            SubmittedUtc = reporterUserId is null ? now.Date : now
        };

        // 3. Combine with an identical, still-active incident; otherwise open a new one.
        var existing = await _db.Incidents
            .Where(i => i.MatchKey == strict && i.Status != IncidentStatus.Retired)
            .OrderBy(i => i.CreatedUtc)
            .FirstOrDefaultAsync(ct);

        SubmitOutcome outcome;
        if (existing != null)
        {
            report.IncidentId = existing.Id;
            report.AutoLinked = true;
            if (input.Severity > existing.Severity) existing.Severity = input.Severity;
            existing.NeedsReview = true;     // an admin should confirm these really are the same event
            existing.UpdatedUtc = now;
            outcome = SubmitOutcome.CombinedWithExisting;
        }
        else
        {
            var possibleDuplicate = await _db.Incidents
                .AnyAsync(i => i.LooseKey == loose && i.Status != IncidentStatus.Retired, ct);
            var incident = new Incident
            {
                DisciplineId = input.DisciplineId,
                InjuryTypeId = input.InjuryTypeId,
                Severity = input.Severity,
                InjuryDate = input.InjuryDate,
                EventName = report.EventName,
                EventKingdomId = input.EventKingdomId,
                InjuredKingdomId = input.InjuredKingdomId,
                MatchKey = strict,
                LooseKey = loose,
                NeedsReview = possibleDuplicate,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            report.Incident = incident;
            report.PossibleDuplicate = possibleDuplicate;
            outcome = SubmitOutcome.Created;
        }

        _db.Reports.Add(report);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a race with an identical concurrent submit (unique index on SubmissionToken)?
            if (await TokenAlreadyStored(input.SubmissionToken))
                return new SubmitResult(SubmitOutcome.AlreadySubmitted, null);
            throw;
        }

        if (reporterUserId is null)
            await _audit.LogAsync("report.submitted.anonymous", "Report", includeActor: false, ct: ct);   // no id, no actor: cannot be re-linked
        else
            await _audit.LogAsync("report.submitted", "Report", report.Id.ToString(), ct: ct);

        return new SubmitResult(outcome, report.Id);
    }

    private async Task<bool> TokenAlreadyStored(Guid token)
    {
        _db.ChangeTracker.Clear();
        return await _db.Reports.AnyAsync(r => r.SubmissionToken == token);
    }
}
