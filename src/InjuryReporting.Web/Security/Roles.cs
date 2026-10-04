namespace InjuryReporting.Web.Security;

public static class AppRoles
{
    public const string User = "User";
    public const string Admin = "Admin";
    public const string SuperAdmin = "SuperAdmin";
    public static readonly string[] All = { User, Admin, SuperAdmin };

    /// <summary>Comma-separated list for [Authorize(Roles = ...)] on staff-only areas.</summary>
    public const string Staff = Admin + "," + SuperAdmin;

    public static int Rank(string role) => role switch { SuperAdmin => 3, Admin => 2, _ => 1 };
}
