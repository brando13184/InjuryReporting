using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using InjuryReporting.Web.Data;
using InjuryReporting.Web.Services;
using InjuryReporting.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace InjuryReporting.Web.Controllers;

[Authorize, EnableRateLimiting("auth")]
public class ManageController : AppController
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly SignInManager<ApplicationUser> _signIn;
    private readonly IAppEmailSender _email;
    private readonly IAuditService _audit;
    private readonly LookupService _lookups;

    public ManageController(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn,
        IAppEmailSender email, IAuditService audit, LookupService lookups)
    {
        _users = users; _signIn = signIn; _email = email; _audit = audit; _lookups = lookups;
    }

    // ---- Profile ------------------------------------------------------------------------------

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return Challenge();
        return View(await BuildProfile(user));
    }

    [HttpPost]
    public async Task<IActionResult> Index(ProfileModel model)
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return Challenge();
        if (!ModelState.IsValid) return View(await BuildProfile(user, model));

        user.DisplayName = model.DisplayName.Trim();
        user.KingdomId = model.KingdomId;
        await _users.UpdateAsync(user);
        await _audit.LogAsync("profile.updated", "User", user.Id.ToString());
        TempData["Success"] = "Profile updated.";
        return RedirectToAction(nameof(Index));
    }

    // ---- Change password ----------------------------------------------------------------------

    [HttpGet]
    public IActionResult ChangePassword() => View(new ChangePasswordModel());

    [HttpPost]
    public async Task<IActionResult> ChangePassword(ChangePasswordModel model)
    {
        if (!ModelState.IsValid) return View(model);
        var user = await _users.GetUserAsync(User);
        if (user == null) return Challenge();

        var result = await _users.ChangePasswordAsync(user, model.CurrentPassword, model.NewPassword);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors) ModelState.AddModelError("", e.Description);
            await _audit.LogAsync("password.change_failed", "User", user.Id.ToString());
            return View(model);
        }
        await _signIn.RefreshSignInAsync(user);   // security stamp changed; keep this session, drop all others
        await _audit.LogAsync("password.changed", "User", user.Id.ToString());
        await _email.SendAsync(user.Email!, "Your Injury Reporting password was changed",
            "<p>Your password was just changed. If this wasn't you, contact an administrator immediately.</p>");
        TempData["Success"] = "Your password has been changed.";
        return RedirectToAction(nameof(Index));
    }

    // ---- Change email -------------------------------------------------------------------------

    [HttpGet]
    public IActionResult ChangeEmail() => View(new ChangeEmailModel());

    [HttpPost]
    public async Task<IActionResult> ChangeEmail(ChangeEmailModel model)
    {
        if (!ModelState.IsValid) return View(model);
        var user = await _users.GetUserAsync(User);
        if (user == null) return Challenge();

        if (!await _users.CheckPasswordAsync(user, model.CurrentPassword))
        {
            ModelState.AddModelError(nameof(model.CurrentPassword), "That password is not correct.");
            await _audit.LogAsync("email.change_bad_password", "User", user.Id.ToString());
            return View(model);
        }

        // Only send when the address is free, but respond identically either way (no account discovery).
        var taken = await _users.FindByEmailAsync(model.NewEmail);
        if (taken == null)
        {
            var token = AccountController.Encode(await _users.GenerateChangeEmailTokenAsync(user, model.NewEmail));
            var link = AbsoluteLink(nameof(ConfirmEmailChange), "Manage", new { userId = user.Id, email = model.NewEmail, code = token });
            await _email.SendAsync(model.NewEmail, "Confirm your new Injury Reporting email address",
                $"<p>Confirm this address to use it to sign in. The link expires in 4 hours.</p><p><a href=\"{HtmlEncoder.Default.Encode(link)}\">Confirm new email</a></p>");
            await _email.SendAsync(user.Email!, "An email change was requested on your account",
                "<p>A request was made to change the sign-in email on your account. Nothing changes until the new address is confirmed. If this wasn't you, change your password now.</p>");
        }
        await _audit.LogAsync("email.change_requested", "User", user.Id.ToString());
        TempData["Success"] = "If that address can be used, we've sent a confirmation link to it. Your current email keeps working until you confirm.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> ConfirmEmailChange(Guid userId, string email, string code)
    {
        var user = await _users.FindByIdAsync(userId.ToString());
        if (user == null || string.IsNullOrEmpty(code) || string.IsNullOrEmpty(email)) return View("ConfirmEmailChange", false);

        var oldEmail = user.Email;
        var result = await _users.ChangeEmailAsync(user, email, AccountController.Decode(code));
        if (!result.Succeeded) return View("ConfirmEmailChange", false);

        await _users.SetUserNameAsync(user, email);   // login is by email, so the user name follows it
        await _users.UpdateSecurityStampAsync(user);  // sign out every session on the old address
        await _audit.LogForAsync(user.Id, email, "email.changed", "User", user.Id.ToString());
        if (!string.IsNullOrEmpty(oldEmail))
            await _email.SendAsync(oldEmail, "Your Injury Reporting email address was changed",
                "<p>The sign-in email for your account was changed. If this wasn't you, contact an administrator immediately.</p>");
        if (User.Identity?.IsAuthenticated == true) await _signIn.SignOutAsync();
        return View("ConfirmEmailChange", true);
    }

    // ---- Two-factor authentication ------------------------------------------------------------

    [HttpGet]
    public async Task<IActionResult> TwoFactor()
    {
        var user = await _users.GetUserAsync(User);
        return user == null ? Challenge() : View(await BuildTwoFactor(user));
    }

    [HttpPost]
    public async Task<IActionResult> EnableTwoFactor(TwoFactorSetupModel model)
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return Challenge();
        var code = (model.Code ?? "").Replace(" ", "").Replace("-", "");
        var valid = await _users.VerifyTwoFactorTokenAsync(user, _users.Options.Tokens.AuthenticatorTokenProvider, code);
        if (!valid)
        {
            TempData["Warning"] = "That code didn't match. Check the key was entered correctly and your device clock is accurate, then try again.";
            return RedirectToAction(nameof(TwoFactor));
        }
        await _users.SetTwoFactorEnabledAsync(user, true);
        var codes = await _users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        await _audit.LogAsync("mfa.enabled", "User", user.Id.ToString());
        return View("RecoveryCodes", codes!.ToList());
    }

    [HttpPost]
    public async Task<IActionResult> ResetRecoveryCodes()
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return Challenge();
        if (!user.TwoFactorEnabled) return RedirectToAction(nameof(TwoFactor));
        var codes = await _users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        await _audit.LogAsync("mfa.recovery_codes_reset", "User", user.Id.ToString());
        return View("RecoveryCodes", codes!.ToList());
    }

    [HttpPost]
    public async Task<IActionResult> DisableTwoFactor(DisableTwoFactorModel model)
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return Challenge();
        if (!ModelState.IsValid || !await _users.CheckPasswordAsync(user, model.CurrentPassword))
        {
            TempData["Warning"] = "Enter your current password to turn off two-factor authentication.";
            return RedirectToAction(nameof(TwoFactor));
        }
        await _users.SetTwoFactorEnabledAsync(user, false);
        await _users.ResetAuthenticatorKeyAsync(user);
        await _signIn.RefreshSignInAsync(user);
        await _audit.LogAsync("mfa.disabled", "User", user.Id.ToString());
        TempData["Success"] = "Two-factor authentication is off.";
        return RedirectToAction(nameof(TwoFactor));
    }

    // ---- Delete account -----------------------------------------------------------------------

    [HttpGet]
    public IActionResult DeleteAccount() => View(new DeleteAccountModel());

    /// <summary>
    /// Self-service erasure. The account, its sign-in and personal profile are removed. Reports already filed
    /// stay (they feed safety statistics) but lose their link to the person, i.e. they become anonymous.
    /// The audit trail is append-only and keeps the event, as required for SOC2.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> DeleteAccount(DeleteAccountModel model)
    {
        if (!ModelState.IsValid) return View(model);
        var user = await _users.GetUserAsync(User);
        if (user == null) return Challenge();

        if (!await _users.CheckPasswordAsync(user, model.CurrentPassword))
        {
            ModelState.AddModelError(nameof(model.CurrentPassword), "That password is not correct.");
            await _audit.LogAsync("account.delete_bad_password", "User", user.Id.ToString());
            return View(model);
        }

        if (await _users.IsInRoleAsync(user, Security.AppRoles.SuperAdmin))
        {
            var others = (await _users.GetUsersInRoleAsync(Security.AppRoles.SuperAdmin)).Count(u => u.Id != user.Id && !u.IsSuspended);
            if (others == 0)
            {
                ModelState.AddModelError("", "You're the only active Super Admin. Promote another Super Admin before deleting this account.");
                return View(model);
            }
        }

        var email = user.Email!;
        await _audit.LogAsync("account.deleted", "User", user.Id.ToString(), "self-service; filed reports kept but unlinked");
        var result = await _users.DeleteAsync(user);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors) ModelState.AddModelError("", e.Description);
            return View(model);
        }
        await _signIn.SignOutAsync();
        await _email.SendAsync(email, "Your Injury Reporting account was deleted",
            "<p>Your account has been deleted. Reports you filed remain in the safety statistics but are no longer linked to you.</p>");
        TempData["Success"] = "Your account has been deleted.";
        return RedirectToAction("Index", "Home");
    }

    // ---- helpers ------------------------------------------------------------------------------

    private async Task<ProfileModel> BuildProfile(ApplicationUser user, ProfileModel? posted = null)
    {
        var roles = await _users.GetRolesAsync(user);
        return new ProfileModel
        {
            DisplayName = posted?.DisplayName ?? user.DisplayName,
            KingdomId = posted != null ? posted.KingdomId : user.KingdomId,
            Email = user.Email ?? "",
            Role = UserAdminService.HighestRole(roles),
            TwoFactorEnabled = user.TwoFactorEnabled,
            Kingdoms = (await _lookups.GetAsync()).Kingdoms
        };
    }

    private async Task<TwoFactorSetupModel> BuildTwoFactor(ApplicationUser user)
    {
        var key = await _users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await _users.ResetAuthenticatorKeyAsync(user);
            key = await _users.GetAuthenticatorKeyAsync(user);
        }
        var uri = string.Format(CultureInfo.InvariantCulture, "otpauth://totp/{0}:{1}?secret={2}&issuer={0}&digits=6",
            UrlEncoder.Default.Encode("Injury Reporting"), UrlEncoder.Default.Encode(user.Email!), key);
        string? qr = null;
        if (!user.TwoFactorEnabled)
        {
            using var generator = new QRCoder.QRCodeGenerator();
            using var data = generator.CreateQrCode(uri, QRCoder.QRCodeGenerator.ECCLevel.M);
            qr = new QRCoder.SvgQRCode(data).GetGraphic(4, "#000000", "#ffffff", drawQuietZones: true);
        }
        return new TwoFactorSetupModel
        {
            IsEnabled = user.TwoFactorEnabled,
            SharedKey = FormatKey(key!),
            AuthenticatorUri = uri,
            QrSvg = qr,
            RecoveryCodesLeft = await _users.CountRecoveryCodesAsync(user)
        };
    }

    private static string FormatKey(string key)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < key.Length; i += 4)
            sb.Append(key.AsSpan(i, Math.Min(4, key.Length - i))).Append(' ');
        return sb.ToString().Trim().ToLowerInvariant();
    }
}
