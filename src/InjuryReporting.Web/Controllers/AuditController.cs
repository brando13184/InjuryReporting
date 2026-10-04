using InjuryReporting.Web.Data;
using InjuryReporting.Web.Security;
using InjuryReporting.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InjuryReporting.Web.Controllers;

[Authorize(Roles = AppRoles.SuperAdmin)]
public class AuditController : StaffController
{
    private const int PageSize = 50;
    private readonly AppDbContext _db;
    public AuditController(AppDbContext db) => _db = db;

    public async Task<IActionResult> Index(string? action, int page = 1)
    {
        page = Math.Max(1, page);
        var q = _db.AuditLog.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action.StartsWith(action));
        var total = await q.CountAsync();
        var rows = await q.OrderByDescending(a => a.Id).Skip((page - 1) * PageSize).Take(PageSize).ToListAsync();
        return View(new AuditListModel { Rows = rows, Action = action, Page = page, PageSize = PageSize, Total = total });
    }
}
