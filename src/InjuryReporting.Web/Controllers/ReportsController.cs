using InjuryReporting.Web.Data;
using InjuryReporting.Web.Services;
using InjuryReporting.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace InjuryReporting.Web.Controllers;

public class ReportsController : AppController
{
    private readonly IReportService _reports;
    private readonly LookupService _lookups;
    private readonly AppDbContext _db;
    private readonly IAuditService _audit;

    public ReportsController(IReportService reports, LookupService lookups, AppDbContext db, IAuditService audit)
    {
        _reports = reports; _lookups = lookups; _db = db; _audit = audit;
    }

    [AllowAnonymous]
    public async Task<IActionResult> Create()
        => View(new ReportFormModel { Lookups = await _lookups.GetAsync() });

    [HttpPost, AllowAnonymous, EnableRateLimiting("report")]
    public async Task<IActionResult> Create(ReportFormModel model)
    {
        // Bots fill the hidden field; pretend success without storing anything.
        if (!string.IsNullOrEmpty(model.Website)) return RedirectToAction(nameof(Thanks));

        if (!ModelState.IsValid)
        {
            model.Lookups = await _lookups.GetAsync();
            return View(model);
        }

        var signedIn = User.Identity?.IsAuthenticated == true;
        Guid? reporter = signedIn && !model.SubmitAnonymously ? CurrentUserId : null;

        var result = await _reports.SubmitAsync(model.ToInput(), reporter);
        TempData["Anonymous"] = reporter is null;
        if (result.Outcome == SubmitOutcome.DuplicateByReporter)
        {
            TempData["Warning"] = "You have already submitted an identical report, so nothing new was recorded.";
            return RedirectToAction(nameof(Mine));
        }
        return RedirectToAction(nameof(Thanks));
    }

    [AllowAnonymous]
    public IActionResult Thanks() => View();

    [Authorize]
    public async Task<IActionResult> Mine()
    {
        var uid = CurrentUserId;
        var rows = await _db.Reports.AsNoTracking()
            .Where(r => r.ReporterUserId == uid)
            .OrderByDescending(r => r.SubmittedUtc)
            .Select(r => new MyReportRow
            {
                Id = r.Id, InjuryDate = r.InjuryDate, Discipline = r.Discipline.Name,
                InjuryType = r.InjuryType.Name, EventName = r.EventName, SubmittedUtc = r.SubmittedUtc
            })
            .ToListAsync();
        return View(rows);
    }

    [Authorize]
    public async Task<IActionResult> Details(Guid id)
    {
        var uid = CurrentUserId;
        var report = await _db.Reports.AsNoTracking()
            .Include(r => r.Discipline).Include(r => r.InjuryType).Include(r => r.EventKingdom).Include(r => r.InjuredKingdom)
            .FirstOrDefaultAsync(r => r.Id == id && r.ReporterUserId == uid);   // only the owner can read it here
        if (report == null) return NotFound();
        await _audit.LogAsync("report.viewed_by_owner", "Report", id.ToString());
        return View(new ReportDetailsModel { Report = report, CanEdit = ReportRules.OwnerCanEdit(report, DateTime.UtcNow) });
    }

    /// <summary>The owner may correct the narrative for a short time. Nothing that drives duplicate matching can change.</summary>
    [Authorize, HttpGet]
    public async Task<IActionResult> Edit(Guid id)
    {
        var uid = CurrentUserId;
        var report = await _db.Reports.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.ReporterUserId == uid);
        if (report == null) return NotFound();
        if (!ReportRules.OwnerCanEdit(report, DateTime.UtcNow))
        {
            TempData["Warning"] = "Reports can only be edited for 7 days after they're submitted.";
            return RedirectToAction(nameof(Details), new { id });
        }
        return View(new EditNarrativeModel { Id = id, Narrative = report.Narrative });
    }

    [Authorize, HttpPost]
    public async Task<IActionResult> Edit(EditNarrativeModel model)
    {
        var uid = CurrentUserId;
        var report = await _db.Reports.FirstOrDefaultAsync(r => r.Id == model.Id && r.ReporterUserId == uid);
        if (report == null) return NotFound();
        if (!ReportRules.OwnerCanEdit(report, DateTime.UtcNow))
        {
            TempData["Warning"] = "Reports can only be edited for 7 days after they're submitted.";
            return RedirectToAction(nameof(Details), new { id = model.Id });
        }
        if (!ModelState.IsValid) return View(model);

        report.Narrative = model.Narrative.Trim();
        report.NarrativeEditedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("report.narrative_edited", "Report", report.Id.ToString());
        TempData["Success"] = "Narrative updated.";
        return RedirectToAction(nameof(Details), new { id = model.Id });
    }
}
