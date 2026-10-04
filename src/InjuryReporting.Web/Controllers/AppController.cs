using System.Security.Claims;
using InjuryReporting.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InjuryReporting.Web.Controllers;

public abstract class AppController : Controller
{
    protected Guid? CurrentUserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    /// <summary>
    /// Absolute link for emails. Built from App:PublicBaseUrl so a forged Host header can never
    /// poison a password-reset / confirmation link. Falls back to the request host in Development only.
    /// </summary>
    protected string AbsoluteLink(string action, string controller, object values)
    {
        var path = Url.Action(action, controller, values) ?? throw new InvalidOperationException("Could not build link.");
        var baseUrl = HttpContext.RequestServices.GetRequiredService<IConfiguration>()["App:PublicBaseUrl"];
        if (!string.IsNullOrWhiteSpace(baseUrl)) return baseUrl.TrimEnd('/') + path;
        if (!HttpContext.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment())
            throw new InvalidOperationException("App:PublicBaseUrl must be configured outside Development.");
        return $"{Request.Scheme}://{Request.Host}{path}";
    }

    protected IActionResult SafeRedirect(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction("Index", "Home");
}

/// <summary>Base for every staff-only (Admin / Super Admin) controller; requires MFA enrolment.</summary>
[Authorize(Roles = AppRoles.Staff)]
[ServiceFilter(typeof(StaffMfaFilter))]
public abstract class StaffController : AppController { }
