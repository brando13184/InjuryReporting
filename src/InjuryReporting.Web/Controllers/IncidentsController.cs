using InjuryReporting.Web.Data;
using InjuryReporting.Web.Services;
using InjuryReporting.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InjuryReporting.Web.Controllers;

public class IncidentsController : StaffController
{
    private const int PageSize = 25;
    private readonly AppDbContext _db;
    private readonly IIncidentService _incidents;
    private readonly LookupService _lookups;
    private readonly IAuditService _audit;

    public IncidentsController(AppDbContext db, IIncidentService incidents, LookupService lookups, IAuditService audit)
    {
        _db = db; _incidents = incidents; _lookups = lookups; _audit = audit;
    }

    public async Task<IActionResult> Index(IncidentListFilter filter)
    {
        filter.Page = Math.Max(1, filter.Page);
        var q = _db.Incidents.AsNoTracking().AsQueryable();
        q = (filter.Status ?? "review") switch
        {
            "all" => q.Where(i => i.Status != IncidentStatus.Retired),
            "open" => q.Where(i => i.Status == IncidentStatus.Open),
            "reviewed" => q.Where(i => i.Status == IncidentStatus.Reviewed),
            "merged" => q.Where(i => i.Status == IncidentStatus.Retired),
            _ => q.Where(i => i.NeedsReview && i.Status != IncidentStatus.Retired)
        };
        if (filter.DisciplineId is { } d) q = q.Where(i => i.DisciplineId == d);
        if (filter.EventKingdomId is { } k) q = q.Where(i => i.EventKingdomId == k);
        if (filter.From is { } from) q = q.Where(i => i.InjuryDate >= from);
        if (filter.To is { } to) q = q.Where(i => i.InjuryDate <= to);
        if (!string.IsNullOrWhiteSpace(filter.Q))
        {
            var s = filter.Q.Trim().ToLower();
            q = q.Where(i => i.EventName.ToLower().Contains(s));
        }

        var total = await q.CountAsync();
        var rows = await ToRows(q.OrderByDescending(i => i.InjuryDate).ThenBy(i => i.Id).Skip((filter.Page - 1) * PageSize).Take(PageSize));
        return View(new IncidentListModel { Filter = filter, Rows = rows, Total = total, PageSize = PageSize, Lookups = await _lookups.GetAsync() });
    }

    public async Task<IActionResult> Details(Guid id)
    {
        var inc = await _db.Incidents.AsNoTracking()
            .Include(i => i.Discipline).Include(i => i.InjuryType).Include(i => i.EventKingdom).Include(i => i.InjuredKingdom)
            .Include(i => i.Reports).ThenInclude(r => r.Reporter)
            .AsSplitQuery()
            .FirstOrDefaultAsync(i => i.Id == id);
        if (inc == null) return NotFound();

        // Reading narratives is access to PHI: record it.
        await _audit.LogAsync("incident.viewed", "Incident", id.ToString(), $"{inc.Reports.Count} report(s) read");

        var dups = inc.Status == IncidentStatus.Retired
            ? new List<IncidentListRow>()
            : await ToRows(_db.Incidents.AsNoTracking()
                .Where(i => i.LooseKey == inc.LooseKey && i.Id != inc.Id && i.Status != IncidentStatus.Retired));
        var mergedInto = inc.MergedIntoIncidentId is { } mid
            ? await _db.Incidents.AsNoTracking().FirstOrDefaultAsync(i => i.Id == mid) : null;
        return View(new IncidentDetailsModel { Incident = inc, PossibleDuplicates = dups, MergedInto = mergedInto });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(Guid id)
    {
        var i = await _db.Incidents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (i == null) return NotFound();
        if (i.Status == IncidentStatus.Retired) return RedirectToAction(nameof(Details), new { id });
        return View(new IncidentEditModel
        {
            Id = i.Id, DisciplineId = i.DisciplineId, InjuryTypeId = i.InjuryTypeId, Severity = i.Severity,
            InjuryDate = i.InjuryDate, EventName = i.EventName, EventKingdomId = i.EventKingdomId,
            InjuredKingdomId = i.InjuredKingdomId, ReviewerNotes = i.ReviewerNotes, Lookups = await _lookups.GetAsync()
        });
    }

    [HttpPost]
    public async Task<IActionResult> Edit(IncidentEditModel m)
    {
        if (!ModelState.IsValid)
        {
            m.Lookups = await _lookups.GetAsync();
            return View(m);
        }
        var result = await _incidents.UpdateAsync(m.Id, new IncidentEdit(m.DisciplineId!.Value, m.InjuryTypeId!.Value, m.Severity!.Value,
            m.InjuryDate!.Value, m.EventName, m.EventKingdomId!.Value, m.InjuredKingdomId, m.ReviewerNotes, m.MarkReviewed));
        if (!result.Succeeded)
        {
            ModelState.AddModelError("", result.Error!);
            m.Lookups = await _lookups.GetAsync();
            return View(m);
        }
        TempData["Success"] = "Incident updated.";
        return RedirectToAction(nameof(Details), new { id = m.Id });
    }

    [HttpGet]
    public async Task<IActionResult> Merge([FromQuery] List<Guid> ids)
    {
        ids = ids.Distinct().ToList();
        if (ids.Count < 2)
        {
            TempData["Warning"] = "Select at least two incidents to merge.";
            return RedirectToAction(nameof(Index), new { status = "all" });
        }
        var candidates = await ToRows(_db.Incidents.AsNoTracking().Where(i => ids.Contains(i.Id) && i.Status != IncidentStatus.Retired));
        if (candidates.Count != ids.Count)
        {
            TempData["Warning"] = "Some selected incidents are missing or were already merged.";
            return RedirectToAction(nameof(Index), new { status = "all" });
        }
        return View(new MergeModel { Ids = ids, Candidates = candidates });
    }

    [HttpPost]
    public async Task<IActionResult> Merge(MergeModel m)
    {
        if (m.TargetId is not { } target || !m.Ids.Contains(target))
        {
            TempData["Warning"] = "Choose which incident the others should be merged into.";
            return RedirectToAction(nameof(Merge), new { ids = m.Ids });
        }
        var result = await _incidents.MergeAsync(target, m.Ids.Where(x => x != target).ToList());
        if (!result.Succeeded)
        {
            TempData["Warning"] = result.Error;
            return RedirectToAction(nameof(Index), new { status = "all" });
        }
        TempData["Success"] = "Incidents merged. Reports are now counted once.";
        return RedirectToAction(nameof(Details), new { id = target });
    }

    [HttpPost]
    public async Task<IActionResult> Split(Guid reportId, Guid incidentId)
    {
        var result = await _incidents.SplitReportAsync(reportId);
        TempData[result.Succeeded ? "Success" : "Warning"] = result.Succeeded ? "Report moved to its own incident." : result.Error;
        return RedirectToAction(nameof(Details), new { id = incidentId });
    }

    private static Task<List<IncidentListRow>> ToRows(IQueryable<Incident> q) =>
        q.Select(i => new IncidentListRow
        {
            Id = i.Id, InjuryDate = i.InjuryDate, Discipline = i.Discipline.Name, InjuryType = i.InjuryType.Name,
            Severity = i.Severity.ToString(), EventName = i.EventName, EventKingdom = i.EventKingdom.Name,
            ReportCount = i.Reports.Count, Status = i.Status, NeedsReview = i.NeedsReview
        }).ToListAsync();
}
