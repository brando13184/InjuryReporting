using InjuryReporting.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace InjuryReporting.Web.Security;

/// <summary>
/// Admins and Super Admins must have two-factor authentication enabled before they can use any staff page
/// (SOC2 CC6.1 — strong authentication for privileged access to PHI).
/// </summary>
public class StaffMfaFilter : IAsyncAuthorizationFilter
{
    private readonly UserManager<ApplicationUser> _users;
    public StaffMfaFilter(UserManager<ApplicationUser> users) => _users = users;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var principal = context.HttpContext.User;
        // Unauthenticated / non-staff requests are handled by [Authorize]; this only gates staff.
        if (principal.Identity?.IsAuthenticated != true || !(principal.IsInRole(AppRoles.Admin) || principal.IsInRole(AppRoles.SuperAdmin)))
            return;

        var user = await _users.GetUserAsync(principal);
        if (user is { TwoFactorEnabled: false })
        {
            var tempData = context.HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataDictionaryFactory>().GetTempData(context.HttpContext);
            tempData["Warning"] = "Staff accounts must enable two-factor authentication before using administration pages.";
            context.Result = new RedirectToActionResult("TwoFactor", "Manage", null);
        }
    }
}
