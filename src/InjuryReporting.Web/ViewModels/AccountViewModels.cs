using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace InjuryReporting.Web.ViewModels;

public class RegisterModel
{
    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = "";

    [Required, StringLength(100)]
    [Display(Name = "Display name")]
    public string DisplayName { get; set; } = "";

    [Display(Name = "SCA kingdom membership")]
    public int? KingdomId { get; set; }

    [Required, DataType(DataType.Password), StringLength(100, MinimumLength = 14, ErrorMessage = "Use at least 14 characters.")]
    public string Password { get; set; } = "";

    [DataType(DataType.Password), Compare(nameof(Password), ErrorMessage = "The passwords do not match.")]
    [Display(Name = "Confirm password")]
    public string ConfirmPassword { get; set; } = "";

    public List<SelectListItem> Kingdoms { get; set; } = new();
}

public class LoginModel
{
    [Required, EmailAddress]
    public string Email { get; set; } = "";

    [Required, DataType(DataType.Password)]
    public string Password { get; set; } = "";

    public string? ReturnUrl { get; set; }
}

public class ForgotPasswordModel
{
    [Required, EmailAddress]
    public string Email { get; set; } = "";
}

public class ResetPasswordModel
{
    [Required, EmailAddress]
    public string Email { get; set; } = "";

    [Required]
    public string Code { get; set; } = "";

    [Required, DataType(DataType.Password), StringLength(100, MinimumLength = 14, ErrorMessage = "Use at least 14 characters.")]
    [Display(Name = "New password")]
    public string Password { get; set; } = "";

    [DataType(DataType.Password), Compare(nameof(Password), ErrorMessage = "The passwords do not match.")]
    [Display(Name = "Confirm new password")]
    public string ConfirmPassword { get; set; } = "";
}

public class TwoFactorLoginModel
{
    [Required, StringLength(8, MinimumLength = 6)]
    [Display(Name = "Authenticator code")]
    public string Code { get; set; } = "";
    public string? ReturnUrl { get; set; }
}

public class RecoveryCodeLoginModel
{
    [Required]
    [Display(Name = "Recovery code")]
    public string RecoveryCode { get; set; } = "";
    public string? ReturnUrl { get; set; }
}

public class ProfileModel
{
    [Required, StringLength(100)]
    [Display(Name = "Display name")]
    public string DisplayName { get; set; } = "";

    [Display(Name = "SCA kingdom membership")]
    public int? KingdomId { get; set; }

    public string Email { get; set; } = "";
    public string Role { get; set; } = "";
    public bool TwoFactorEnabled { get; set; }
    public List<SelectListItem> Kingdoms { get; set; } = new();
}

public class ChangePasswordModel
{
    [Required, DataType(DataType.Password)]
    [Display(Name = "Current password")]
    public string CurrentPassword { get; set; } = "";

    [Required, DataType(DataType.Password), StringLength(100, MinimumLength = 14, ErrorMessage = "Use at least 14 characters.")]
    [Display(Name = "New password")]
    public string NewPassword { get; set; } = "";

    [DataType(DataType.Password), Compare(nameof(NewPassword), ErrorMessage = "The passwords do not match.")]
    [Display(Name = "Confirm new password")]
    public string ConfirmPassword { get; set; } = "";
}

public class ChangeEmailModel
{
    [Required, EmailAddress, StringLength(256)]
    [Display(Name = "New email address")]
    public string NewEmail { get; set; } = "";

    [Required, DataType(DataType.Password)]
    [Display(Name = "Current password")]
    public string CurrentPassword { get; set; } = "";
}

public class DeleteAccountModel
{
    [Required, DataType(DataType.Password)]
    [Display(Name = "Current password")]
    public string CurrentPassword { get; set; } = "";

    /// <summary>Checkbox: true = also permanently delete every report filed under this account; false = keep them, unlinked.
    /// Defaults to false when the field is absent, so a missing value can never delete data by accident.</summary>
    [Display(Name = "Also permanently delete the reports I filed")]
    public bool DeleteReports { get; set; }

    [Range(typeof(bool), "true", "true", ErrorMessage = "Tick the box to confirm.")]
    [Display(Name = "I understand this can't be undone")]
    public bool Confirm { get; set; }
}

public class TwoFactorSetupModel
{
    public bool IsEnabled { get; set; }
    public string? SharedKey { get; set; }
    public string? AuthenticatorUri { get; set; }
    /// <summary>Inline SVG QR code of <see cref="AuthenticatorUri"/> (rendered server-side; nothing leaves the server).</summary>
    public string? QrSvg { get; set; }
    public int RecoveryCodesLeft { get; set; }

    [StringLength(8, MinimumLength = 6)]
    [Display(Name = "Verification code")]
    public string? Code { get; set; }
}

public class DisableTwoFactorModel
{
    [Required, DataType(DataType.Password)]
    [Display(Name = "Current password")]
    public string CurrentPassword { get; set; } = "";
}
