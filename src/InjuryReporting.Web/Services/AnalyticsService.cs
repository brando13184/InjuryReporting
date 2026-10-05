using InjuryReporting.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace InjuryReporting.Web.Services;

public class AnalyticsFilter
{
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public int? DisciplineId { get; set; }
    public int? EventKingdomId { get; set; }
    /// <summary>Replace counts below <see cref="AnalyticsService.SmallCountThreshold"/> with "&lt;5" (safe to share).</summary>
    public bool HideSmallCounts { get; set; }
}

/// <summary><see cref="Count"/> is <see cref="AnalyticsResult.Hidden"/> when suppressed.</summary>
public record Bucket(string Label, int Count);
public record CrossTabRow(string Label, int[] Counts, int Total);
public record YearRow(int Year, int[] Months, int Total);

public class AnalyticsResult
{
    /// <summary>Sentinel for a count hidden by small-number suppression.</summary>
    public const int Hidden = -1;

    public int Incidents { get; init; }
    /// <summary>Raw submissions behind those incidents.</summary>
    public int Reports { get; init; }
    /// <summary>Submissions that were combined into an existing incident (Reports - Incidents).</summary>
    public int DuplicatesCombined { get; init; }
    public int NeedsReview { get; init; }
    public bool Suppressed { get; init; }
    public List<Bucket> ByDiscipline { get; init; } = new();
    public List<Bucket> ByInjuryType { get; init; } = new();
    public List<Bucket> BySeverity { get; init; } = new();
    public List<Bucket> ByEventKingdom { get; init; } = new();
    public List<Bucket> ByInjuredKingdom { get; init; } = new();
    public List<Bucket> ByYear { get; init; } = new();
    /// <summary>Gap-filled month-by-month series (zero months included) for the trend chart.</summary>
    public List<Bucket> MonthlyTrend { get; init; } = new();
    public List<YearRow> YearMonthGrid { get; init; } = new();
    public List<string> CrossTabColumns { get; init; } = new();
    public List<CrossTabRow> CrossTab { get; init; } = new();
}

public interface IAnalyticsService
{
    Task<AnalyticsResult> GetAsync(AnalyticsFilter filter, CancellationToken ct = default);
    Task<List<IncidentExportRow>> ExportAsync(AnalyticsFilter filter, CancellationToken ct = default);
    /// <summary>Aggregate-only rows (dimension, label, count) with small counts always suppressed.</summary>
    Task<List<(string Dimension, string Label, int Count)>> SummaryRowsAsync(AnalyticsFilter filter, CancellationToken ct = default);
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
    /// <summary>Counts of 1..(threshold-1) are suppressed when sharing. Zero is not sensitive and is kept.</summary>
    public const int SmallCountThreshold = 5;
    private const int MaxTrendMonths = 48;
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

        var monthly = byMonth.ToDictionary(x => (x.Year, x.Month), x => x.N);
        var result = new AnalyticsResult
        {
            Incidents = incidents,
            Reports = reports,
            DuplicatesCombined = reports - incidents,
            NeedsReview = needsReview,
            ByDiscipline = byDisc.OrderByDescending(x => x.N).Select(x => new Bucket(disciplines[x.Key], x.N)).ToList(),
            ByInjuryType = byType.OrderByDescending(x => x.N).Select(x => new Bucket(types[x.Key], x.N)).ToList(),
            BySeverity = bySev.OrderBy(x => x.Key).Select(x => new Bucket(Pretty(x.Key), x.N)).ToList(),
            ByEventKingdom = byEvK.OrderByDescending(x => x.N).Select(x => new Bucket(kingdoms[x.Key], x.N)).ToList(),
            ByInjuredKingdom = byInjK.OrderByDescending(x => x.N).Select(x => new Bucket(x.Key is { } k ? kingdoms[k] : NonMember, x.N)).ToList(),
            ByYear = byMonth.GroupBy(x => x.Year).OrderBy(g => g.Key).Select(g => new Bucket(g.Key.ToString(), g.Sum(x => x.N))).ToList(),
            MonthlyTrend = BuildTrend(monthly),
            YearMonthGrid = byMonth.GroupBy(x => x.Year).OrderByDescending(g => g.Key)
                .Select(g =>
                {
                    var months = Enumerable.Range(1, 12).Select(m => monthly.GetValueOrDefault((g.Key, m))).ToArray();
                    return new YearRow(g.Key, months, months.Sum());
                }).ToList(),
            CrossTabColumns = typeIds.Select(t => types[t]).ToList(),
            CrossTab = crossRows
        };
        return f.HideSmallCounts ? Suppress(result, SmallCountThreshold) : result;
    }

    /// <summary>Months from the first to the last data month (at most the latest 48), zero-filled.</summary>
    internal static List<Bucket> BuildTrend(Dictionary<(int Year, int Month), int> monthly)
    {
        if (monthly.Count == 0) return new();
        var first = monthly.Keys.Min(k => k.Year * 12 + k.Month - 1);
        var last = monthly.Keys.Max(k => k.Year * 12 + k.Month - 1);
        first = Math.Max(first, last - (MaxTrendMonths - 1));
        return Enumerable.Range(first, last - first + 1)
            .Select(idx => (Year: idx / 12, Month: idx % 12 + 1))
            .Select(ym => new Bucket($"{ym.Year}-{ym.Month:00}", monthly.GetValueOrDefault(ym)))
            .ToList();
    }

    /// <summary>Hides every count in 1..threshold-1. Pure function so it can be unit tested.</summary>
    public static AnalyticsResult Suppress(AnalyticsResult r, int threshold)
    {
        int S(int n) => n > 0 && n < threshold ? AnalyticsResult.Hidden : n;
        List<Bucket> B(List<Bucket> l) => l.Select(b => b with { Count = S(b.Count) }).ToList();
        return new AnalyticsResult
        {
            Suppressed = true,
            Incidents = S(r.Incidents),
            Reports = S(r.Reports),
            DuplicatesCombined = S(r.DuplicatesCombined),
            NeedsReview = S(r.NeedsReview),
            ByDiscipline = B(r.ByDiscipline),
            ByInjuryType = B(r.ByInjuryType),
            BySeverity = B(r.BySeverity),
            ByEventKingdom = B(r.ByEventKingdom),
            ByInjuredKingdom = B(r.ByInjuredKingdom),
            ByYear = B(r.ByYear),
            MonthlyTrend = B(r.MonthlyTrend),
            YearMonthGrid = r.YearMonthGrid.Select(y => new YearRow(y.Year, y.Months.Select(S).ToArray(), S(y.Total))).ToList(),
            CrossTabColumns = r.CrossTabColumns,
            CrossTab = r.CrossTab.Select(c => new CrossTabRow(c.Label, c.Counts.Select(S).ToArray(), S(c.Total))).ToList()
        };
    }

    public async Task<List<(string Dimension, string Label, int Count)>> SummaryRowsAsync(AnalyticsFilter f, CancellationToken ct = default)
    {
        f.HideSmallCounts = true;
        var r = await GetAsync(f, ct);
        var rows = new List<(string, string, int)>
        {
            ("Total", "Incidents", r.Incidents), ("Total", "Reports", r.Reports)
        };
        void Add(string dim, IEnumerable<Bucket> b) => rows.AddRange(b.Select(x => (dim, x.Label, x.Count)));
        Add("Discipline", r.ByDiscipline); Add("InjuryType", r.ByInjuryType); Add("Severity", r.BySeverity);
        Add("EventKingdom", r.ByEventKingdom); Add("InjuredKingdom", r.ByInjuredKingdom);
        Add("Year", r.ByYear); Add("Month", r.MonthlyTrend);
        return rows;
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
