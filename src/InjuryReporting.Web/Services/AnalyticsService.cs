using InjuryReporting.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace InjuryReporting.Web.Services;

public class AnalyticsFilter
{
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public int? DisciplineId { get; set; }
    public int? EventKingdomId { get; set; }
}

public record Bucket(string Label, int Count);
public record CrossTabRow(string Label, int[] Counts, int Total);

public class AnalyticsResult
{
    public int Incidents { get; init; }
    /// <summary>Raw submissions behind those incidents.</summary>
    public int Reports { get; init; }
    /// <summary>Submissions that were combined into an existing incident (Reports - Incidents).</summary>
    public int DuplicatesCombined => Reports - Incidents;
    public int NeedsReview { get; init; }
    public List<Bucket> ByDiscipline { get; init; } = new();
    public List<Bucket> ByInjuryType { get; init; } = new();
    public List<Bucket> BySeverity { get; init; } = new();
    public List<Bucket> ByEventKingdom { get; init; } = new();
    public List<Bucket> ByInjuredKingdom { get; init; } = new();
    public List<Bucket> ByMonth { get; init; } = new();
    public List<string> CrossTabColumns { get; init; } = new();
    public List<CrossTabRow> CrossTab { get; init; } = new();
}

public interface IAnalyticsService
{
    Task<AnalyticsResult> GetAsync(AnalyticsFilter filter, CancellationToken ct = default);
    Task<List<IncidentExportRow>> ExportAsync(AnalyticsFilter filter, CancellationToken ct = default);
}

public record IncidentExportRow(
    Guid Id, DateOnly InjuryDate, string Discipline, string InjuryType, string Severity,
    string EventName, string EventKingdom, string InjuredKingdom, int ReportCount, string Status);

/// <summary>
/// Analytics are computed over <b>incidents</b> (the combined record), never raw reports, so duplicates
/// can't inflate counts. Retired (merged) incidents are always excluded. Narratives are never exposed here.
/// </summary>
public class AnalyticsService : IAnalyticsService
{
    private const string NonMember = "Non-member / unknown";
    private readonly AppDbContext _db;
    public AnalyticsService(AppDbContext db) => _db = db;

    private IQueryable<Incident> Base(AnalyticsFilter f)
    {
        var q = _db.Incidents.AsNoTracking().Where(i => i.Status != IncidentStatus.Retired);
        if (f.From is { } from) q = q.Where(i => i.InjuryDate >= from);
        if (f.To is { } to) q = q.Where(i => i.InjuryDate <= to);
        if (f.DisciplineId is { } d) q = q.Where(i => i.DisciplineId == d);
        if (f.EventKingdomId is { } k) q = q.Where(i => i.EventKingdomId == k);
        return q;
    }

    public async Task<AnalyticsResult> GetAsync(AnalyticsFilter f, CancellationToken ct = default)
    {
        var q = Base(f);
        var disciplines = await _db.Disciplines.AsNoTracking().OrderBy(x => x.Id).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var types = await _db.InjuryTypes.AsNoTracking().OrderBy(x => x.Id).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var kingdoms = await _db.Kingdoms.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);

        var incidents = await q.CountAsync(ct);
        var reports = await _db.Reports.CountAsync(r => q.Select(i => i.Id).Contains(r.IncidentId), ct);
        var needsReview = await q.CountAsync(i => i.NeedsReview, ct);

        var byDisc = await q.GroupBy(i => i.DisciplineId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        var byType = await q.GroupBy(i => i.InjuryTypeId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        var bySev = await q.GroupBy(i => i.Severity).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        var byEvK = await q.GroupBy(i => i.EventKingdomId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        var byInjK = await q.GroupBy(i => i.InjuredKingdomId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        var byMonth = await q.GroupBy(i => new { i.InjuryDate.Year, i.InjuryDate.Month }).Select(g => new { g.Key.Year, g.Key.Month, N = g.Count() }).ToListAsync(ct);
        var cross = await q.GroupBy(i => new { i.DisciplineId, i.InjuryTypeId }).Select(g => new { g.Key.DisciplineId, g.Key.InjuryTypeId, N = g.Count() }).ToListAsync(ct);

        var typeIds = types.Keys.ToList();
        var crossRows = disciplines
            .Select(d =>
            {
                var counts = typeIds.Select(t => cross.Where(c => c.DisciplineId == d.Key && c.InjuryTypeId == t).Sum(c => c.N)).ToArray();
                return new CrossTabRow(d.Value, counts, counts.Sum());
            })
            .Where(r => r.Total > 0)
            .ToList();

        return new AnalyticsResult
        {
            Incidents = incidents,
            Reports = reports,
            NeedsReview = needsReview,
            ByDiscipline = byDisc.OrderByDescending(x => x.N).Select(x => new Bucket(disciplines[x.Key], x.N)).ToList(),
            ByInjuryType = byType.OrderByDescending(x => x.N).Select(x => new Bucket(types[x.Key], x.N)).ToList(),
            BySeverity = bySev.OrderBy(x => x.Key).Select(x => new Bucket(Pretty(x.Key), x.N)).ToList(),
            ByEventKingdom = byEvK.OrderByDescending(x => x.N).Select(x => new Bucket(kingdoms[x.Key], x.N)).ToList(),
            ByInjuredKingdom = byInjK.OrderByDescending(x => x.N).Select(x => new Bucket(x.Key is { } k ? kingdoms[k] : NonMember, x.N)).ToList(),
            ByMonth = byMonth.OrderBy(x => x.Year).ThenBy(x => x.Month).Select(x => new Bucket($"{x.Year}-{x.Month:00}", x.N)).ToList(),
            CrossTabColumns = typeIds.Select(t => types[t]).ToList(),
            CrossTab = crossRows
        };
    }

    public async Task<List<IncidentExportRow>> ExportAsync(AnalyticsFilter f, CancellationToken ct = default)
    {
        var rows = await Base(f)
            .OrderBy(i => i.InjuryDate)
            .Select(i => new
            {
                i.Id, i.InjuryDate, Discipline = i.Discipline.Name, InjuryType = i.InjuryType.Name, i.Severity,
                i.EventName, EventKingdom = i.EventKingdom.Name,
                InjuredKingdom = i.InjuredKingdom == null ? NonMember : i.InjuredKingdom.Name,
                Reports = i.Reports.Count, i.Status
            })
            .ToListAsync(ct);
        return rows.Select(r => new IncidentExportRow(r.Id, r.InjuryDate, r.Discipline, r.InjuryType, Pretty(r.Severity),
            r.EventName, r.EventKingdom, r.InjuredKingdom, r.Reports, r.Status.ToString())).ToList();
    }

    public static string Pretty(Severity s) => s switch
    {
        Severity.FirstAidOnly => "First aid only",
        Severity.MedicalTreatment => "Medical treatment",
        Severity.Hospitalization => "Hospitalization",
        Severity.Fatality => "Fatality",
        _ => s.ToString()
    };
}
