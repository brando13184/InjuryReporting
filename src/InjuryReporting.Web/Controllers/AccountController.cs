using System.Text;
using InjuryReporting.Web.Data;
using InjuryReporting.Web.Security;
using InjuryReporting.Web.Services;
using InjuryReporting.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;

namespace InjuryReporting.Web.Controllers;

[EnableRateLimiting("auth")]
public class AccountController : AppController
{
    private const string GenericLoginError = "Invalid email or password, or the account is unavailable.";

    private readonly UserManager<ApplicationUser> _users;
    private readonly SignInManager<ApplicationUser> _signIn;
    private readonly IAppEmailSender _email;
    private readonly IAuditService _audit;
    private readonly LookupService _lookups;

    public AccountController(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn,
        IAppEmailSender email, IAuditService audit, LookupService lookups)
    {
        _users = users; _signIn = signIn; _email = email; _audit = audit; _lookups = lookups;
    }

    // ---- Registration -------------------------------------------------------------------------

    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Register() => View(new RegisterModel { Kingdoms = (await _lookups.GetAsync()).Kingdoms });

    [HttpPost, AllowAnonymous]
    public async Task<IActionResult> Register(RegisterModel model)
    {
        if (!ModelState.IsValid)
        {
            model.Kingdoms = (await _lookups.GetAsync()).Kingdoms;
            return View(model);
        }

        var existing = await _users.FindByEmailAsync(model.Email);
        if (existing != null)
        {
            // Same response as success so the form cannot be used to discover who has an account.
            await _email.SendAsync(existing.Email!, "Injury Reporting: you already have an account",
                "<p>Someone tried to register with this email address, but an account already exists. " +
                "If this was you, use <b>Forgot password</b> on the sign-in page. If not, you can ignore this message.</p>");
            await _audit.LogForAsync(existing.Id, existing.Email, "account.register_existing_email");
            return RedirectToAction(nameof(CheckEmail));
        }

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(), UserName = model.Email, Email = model.Email,
            DisplayName = model.DisplayName.Trim(), KingdomId = model.KingdomId
        };
        var result = await _users.CreateAsync(user, model.Password);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors.Where(e => !e.Code.Contains("Duplicate")))
                ModelState.AddModelError(nameof(model.Password), e.Description);
            model.Kingdoms = (await _lookups.GetAsync()).Kingdoms;
            return View(model);
        }

        await _users.AddToRoleAsync(user, AppRoles.User);
        await SendConfirmationEmail(user);
        await _audit.LogForAsync(user.Id, user.Email, "account.registered", "User", user.Id.ToString());
        return RedirectToAction(nameof(CheckEmail));
    }

    [AllowAnonymous]
    public IActionResult CheckEmail() => View();

    [AllowAnonymous]
    public async Task<IActionResult> ConfirmEmail(Guid userId, string code)
    {
        var user = await _users.FindByIdAsync(userId.ToString());
        if (user == null || string.IsNullOrEmpty(code)) return View("ConfirmEmail", false);
        var result = await _users.ConfirmEmailAsync(user, Decode(code));
        if (result.Succeeded) await _audit.LogForAsync(user.Id, user.Email, "account.email_confirmed", "User", user.Id.ToString());
        return View("ConfirmEmail", result.Succeeded);
    }

    // ---- Sign in / out ------------------------------------------------------------------------

    [HttpGet, AllowAnonymous]
    public IActionResult Login(string? returnUrl = null) => View(new LoginModel { ReturnUrl = returnUrl });

    [HttpPost, AllowAnonymous]
    public async Task<IActionResult> Login(LoginModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await _users.FindByEmailAsync(model.Email);
        if (user == null || user.IsSuspended)
        {
            await _audit.LogForAsync(user?.Id, model.Email, user == null ? "login.failed_unknown_user" : "login.blocked_suspended");
            ModelState.AddModelError("", GenericLoginError);
            return View(model);
        }

        if (!user.EmailConfirmed && await _users.CheckPasswordAsync(user, model.Password))
        {
            await SendConfirmationEmail(user);      // password proved ownership of the account; resend the link
            return RedirectToAction(nameof(CheckEmail));
        }

        var result = await _signIn.PasswordSignInAsync(user, model.Password, isPersistent: false, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            await _audit.LogForAsync(user.Id, user.Email, "login.succeeded");
            return SafeRedirect(model.ReturnUrl);
        }
        if (result.RequiresTwoFactor)
            return RedirectToAction(nameof(LoginWith2fa), new { returnUrl = model.ReturnUrl });

        await _audit.LogForAsync(user.Id, user.Email, result.IsLockedOut ? "login.locked_out" : "login.failed");
        ModelState.AddModelError("", result.IsLockedOut ? "Too many attempts. Try again later." : GenericLoginError);
        return View(model);
    }

    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> LoginWith2fa(string? returnUrl = null)
    {
        if (await _signIn.GetTwoFactorAuthenticationUserAsync() == null) return RedirectToAction(nameof(Login));
        return View(new TwoFactorLoginModel { ReturnUrl = returnUrl });
    }

    [HttpPost, AllowAnonymous]
    public async Task<IActionResult> LoginWith2fa(TwoFactorLoginModel model)
    {
        if (!ModelState.IsValid) return View(model);
        var user = await _signIn.GetTwoFactorAuthenticationUserAsync();
        if (user == null) return RedirectToAction(nameof(Login));

        var code = model.Code.Replace(" ", "").Replace("-", "");
        var result = await _signIn.TwoFactorAuthenticatorSignInAsync(code, isPersistent: false, rememberClient: false);
        if (result.Succeeded)
        {
            await _audit.LogForAsync(user.Id, user.Email, "login.succeeded_2fa");
            return SafeRedirect(model.ReturnUrl);
        }
        await _audit.LogForAsync(user.Id, user.Email, result.IsLockedOut ? "login.locked_out" : "login.2fa_failed");
        ModelState.AddModelError("", result.IsLockedOut ? "Too many attempts. Try again later." : "Invalid code.");
        return View(model);
    }

    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> LoginWithRecoveryCode(string? returnUrl = null)
    {
        if (await _signIn.GetTwoFactorAuthenticationUserAsync() == null) return RedirectToAction(nameof(Login));
        return View(new RecoveryCodeLoginModel { ReturnUrl = returnUrl });
    }

    [HttpPost, AllowAnonymous]
    public async Task<IActionResult> LoginWithRecoveryCode(RecoveryCodeLoginModel model)
    {
        if (!ModelState.IsValid) return View(model);
        var user = await _signIn.GetTwoFactorAuthenticationUserAsync();
        if (user == null) return RedirectToAction(nameof(Login));

        var result = await _signIn.TwoFactorRecoveryCodeSignInAsync(model.RecoveryCode.Replace(" ", ""));
        if (result.Succeeded)
        {
            await _audit.LogForAsync(user.Id, user.Email, "login.succeeded_recovery_code");
            return SafeRedirect(model.ReturnUrl);
        }
        await _audit.LogForAsync(user.Id, user.Email, "login.recovery_code_failed");
        ModelState.AddModelError("", "Invalid recovery code.");
        return View(model);
    }

    [HttpPost, Authorize]
    public async Task<IActionResult> Logout()
    {
        await _audit.LogAsync("logout");
        await _signIn.SignOutAsync();
        return RedirectToAction("Index", "Home");
    }

    [AllowAnonymous]
    public IActionResult AccessDenied() => View();

    // ---- Password reset -----------------------------------------------------------------------

    [HttpGet, AllowAnonymous]
    public IActionResult ForgotPassword() => View(new ForgotPasswordModel());

    [HttpPost, AllowAnonymous]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordModel model)
    {
        if (!ModelState.IsValid) return View(model);
        var user = await _users.FindByEmailAsync(model.Email);
        if (user is { EmailConfirmed: true, IsSuspended: false })
        {
            var token = Encode(await _users.GeneratePasswordResetTokenAsync(user));
            var link = AbsoluteLink(nameof(ResetPassword), "Account", new { email = user.Email, code = token });
            await _email.SendAsync(user.Email!, "Reset your Injury Reporting password",
                $"<p>Use the link below to choose a new password. It expires in 4 hours. If you did not ask for this, ignore this message.</p><p><a href=\"{link}\">Reset password</a></p>");
            await _audit.LogForAsync(user.Id, user.Email, "password.reset_requested");
        }
        return RedirectToAction(nameof(ForgotPasswordConfirmation));    // identical for known and unknown addresses
    }

    [AllowAnonymous]
    public IActionResult ForgotPasswordConfirmation() => View();

    [HttpGet, AllowAnonymous]
    public IActionResult ResetPassword(string email, string code) =>
        string.IsNullOrEmpty(code) ? BadRequest() : View(new ResetPasswordModel { Email = email, Code = code });

    [HttpPost, AllowAnonymous]
    public async Task<IActionResult> ResetPassword(ResetPasswordModel model)
    {
        if (!ModelState.IsValid) return View(model);
        var user = await _users.FindByEmailAsync(model.Email);
        if (user != null && !user.IsSuspended)
        {
            var result = await _users.ResetPasswordAsync(user, Decode(model.Code), model.Password);
            if (!result.Succeeded)
            {
                foreach (var e in result.Errors) ModelState.AddModelError("", e.Description);
                return View(model);
            }
            await _audit.LogForAsync(user.Id, user.Email, "password.reset_completed");
            await _email.SendAsync(user.Email!, "Your Injury Reporting password was changed",
                "<p>Your password was just reset. If this wasn't you, contact an administrator immediately.</p>");
        }
        return RedirectToAction(nameof(ResetPasswordConfirmation));
    }

    [AllowAnonymous]
    public IActionResult ResetPasswordConfirmation() => View();

    // ---- helpers ------------------------------------------------------------------------------

    private async Task SendConfirmationEmail(ApplicationUser user)
    {
        var token = Encode(await _users.GenerateEmailConfirmationTokenAsync(user));
        var link = AbsoluteLink(nameof(ConfirmEmail), "Account", new { userId = user.Id, code = token });
        await _email.SendAsync(user.Email!, "Confirm your Injury Reporting account",
            $"<p>Confirm your email address to finish creating your account. This link expires in 4 hours.</p><p><a href=\"{link}\">Confirm email</a></p>");
    }

    internal static string Encode(string token) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
    internal static string Decode(string code)
    {
        try { return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code)); }
        catch (FormatException) { return ""; }
    }
}
