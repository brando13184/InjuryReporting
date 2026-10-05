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
    private readonly IUserDataService _data;
    private readonly IAuditService _audit;

    public UsersController(IUserAdminService admin, UserManager<ApplicationUser> users, IUserDataService data, IAuditService audit)
    {
        _admin = admin; _users = users; _data = data; _audit = audit;
    }

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

    /// <summary>Retrieve everything held about a user (subject access request). Super Admin only; logged.</summary>
    [HttpGet, Authorize(Roles = AppRoles.SuperAdmin)]
    public async Task<IActionResult> ExportData(Guid id)
    {
        if (await _users.FindByIdAsync(id.ToString()) == null) return NotFound();
        var bytes = await _data.ExportAsync(id);
        await _audit.LogAsync("user.data_exported", "User", id.ToString(), "by super admin");
        return File(bytes, "application/json", $"user-data-{id.ToString()[..8]}-{DateTime.UtcNow:yyyyMMdd}.json");
    }

    /// <summary>
    /// Permanently delete a user and, optionally, every report filed under their account. Requires the Super Admin's own
    /// password and the user's email typed back, because it can't be undone.
    /// </summary>
    [HttpPost, Authorize(Roles = AppRoles.SuperAdmin)]
    public async Task<IActionResult> DeleteUser(DeleteUserModel m)
    {
        var actorId = CurrentUserId!.Value;
        if (!ModelState.IsValid) return Back(m.Id, "Fill in both confirmation fields.", false);
        if (m.Id == actorId) return Back(m.Id, "Use Manage → Delete my account for your own account.", false);

        var target = await _users.FindByIdAsync(m.Id.ToString());
        if (target == null) return NotFound();
        var actor = await _users.FindByIdAsync(actorId.ToString());
        if (actor == null || !await _users.CheckPasswordAsync(actor, m.AdminPassword))
        {
            await _audit.LogAsync("user.erase_bad_password", "User", m.Id.ToString());
            return Back(m.Id, "Your password was not correct.", false);
        }
        if (!string.Equals(m.ConfirmEmail.Trim(), target.Email, StringComparison.OrdinalIgnoreCase))
            return Back(m.Id, "The email you typed doesn't match this user.", false);

        var result = await _data.EraseAsync(actorId, m.Id, m.DeleteReports);
        if (!result.Succeeded) return Back(m.Id, result.Error!, false);

        TempData["Success"] = m.DeleteReports
            ? $"User deleted with {result.ReportsDeleted} report(s) and {result.IncidentsRemoved} now-empty incident(s)."
            : "User deleted. Their reports were kept, unlinked.";
        return RedirectToAction(nameof(Index));
    }

    private IActionResult Back(Guid id, string message, bool ok)
    {
        TempData[ok ? "Success" : "Warning"] = message;
        return RedirectToAction(nameof(Details), new { id });
    }

    private IActionResult Report(OperationResult r, string success, Guid id)
    {
        TempData[r.Succeeded ? "Success" : "Warning"] = r.Succeeded ? success : r.Error;
        return RedirectToAction(nameof(Details), new { id });
    }
}
