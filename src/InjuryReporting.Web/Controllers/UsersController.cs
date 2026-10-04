using InjuryReporting.Web.Data;
using InjuryReporting.Web.Security;
using InjuryReporting.Web.Services;
using InjuryReporting.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace InjuryReporting.Web.Controllers;

public class UsersController : StaffController
{
    private const int PageSize = 25;
    private readonly IUserAdminService _admin;
    private readonly UserManager<ApplicationUser> _users;

    public UsersController(IUserAdminService admin, UserManager<ApplicationUser> users) { _admin = admin; _users = users; }

    public async Task<IActionResult> Index(string? search, int page = 1)
    {
        page = Math.Max(1, page);
        var (rows, total) = await _admin.ListAsync(search, page, PageSize);
        return View(new UserListModel { Rows = rows, Search = search, Page = page, PageSize = PageSize, Total = total });
    }

    public async Task<IActionResult> Details(Guid id)
    {
        var row = await _admin.GetAsync(id);
        if (row == null) return NotFound();
        var user = await _users.FindByIdAsync(id.ToString());
        return View(new UserDetailsModel
        {
            User = row, SuspensionReason = user?.SuspensionReason,
            ViewerIsSuperAdmin = User.IsInRole(AppRoles.SuperAdmin), IsSelf = id == CurrentUserId
        });
    }

    [HttpPost]
    public async Task<IActionResult> Suspend(SuspendModel m) =>
        Report(await _admin.SuspendAsync(CurrentUserId!.Value, m.Id, m.Reason), "User suspended and signed out.", m.Id);

    [HttpPost]
    public async Task<IActionResult> Unsuspend(Guid id) =>
        Report(await _admin.UnsuspendAsync(CurrentUserId!.Value, id), "User reinstated.", id);

    [HttpPost, Authorize(Roles = AppRoles.SuperAdmin)]
    public async Task<IActionResult> SetRole(Guid id, string role) =>
        Report(await _admin.SetRoleAsync(CurrentUserId!.Value, id, role), "Role updated.", id);

    private IActionResult Report(OperationResult r, string success, Guid id)
    {
        TempData[r.Succeeded ? "Success" : "Warning"] = r.Succeeded ? success : r.Error;
        return RedirectToAction(nameof(Details), new { id });
    }
}
