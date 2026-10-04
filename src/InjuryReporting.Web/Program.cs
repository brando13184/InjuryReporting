using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using InjuryReporting.Web.Data;
using InjuryReporting.Web.Security;
using InjuryReporting.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---- Database ------------------------------------------------------------------------------
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseNpgsql(config.GetConnectionString("Default")
                ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.")));

// ---- Data protection: encrypts PHI columns and cookies/tokens. Keys live in the database. ---
var dp = builder.Services.AddDataProtection().SetApplicationName("InjuryReporting").PersistKeysToDbContext<AppDbContext>();
var certPath = config["DataProtection:CertificatePath"];
if (!string.IsNullOrWhiteSpace(certPath))
{
    dp.ProtectKeysWithCertificate(X509CertificateLoader.LoadPkcs12FromFile(certPath, config["DataProtection:CertificatePassword"]));
}
else if (!builder.Environment.IsDevelopment() && !config.GetValue<bool>("DataProtection:AllowUnprotectedKeys"))
{
    throw new InvalidOperationException(
        "DataProtection:CertificatePath must be set outside Development so the key ring that encrypts PHI is itself encrypted at rest.");
}

// ---- Identity: email login, strong passwords, lockout --------------------------------------
builder.Services.AddIdentity<ApplicationUser, IdentityRole<Guid>>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.SignIn.RequireConfirmedEmail = true;
        o.Password.RequiredLength = 14;
        o.Password.RequireDigit = true;
        o.Password.RequireLowercase = true;
        o.Password.RequireUppercase = true;
        o.Password.RequireNonAlphanumeric = false;
        o.Lockout.AllowedForNewUsers = true;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(4));

builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "__Host-InjuryReporting.Auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.ExpireTimeSpan = TimeSpan.FromMinutes(20);     // idle session timeout
    o.SlidingExpiration = true;
    o.LoginPath = "/Account/Login";
    o.AccessDeniedPath = "/Account/AccessDenied";
});
// Re-check the security stamp every minute so suspensions / role changes / password changes take effect quickly.
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));

builder.Services.AddAntiforgery(o =>
{
    o.Cookie.Name = "__Host-InjuryReporting.Csrf";
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.Cookie.SameSite = SameSiteMode.Strict;
});

// ---- Rate limiting (in-memory only; IPs are never persisted for anonymous submissions) ------
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    static string Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    o.AddPolicy("report", c => RateLimitPartition.GetFixedWindowLimiter(Ip(c),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromHours(1) }));
    o.AddPolicy("auth", c => RateLimitPartition.GetFixedWindowLimiter(Ip(c),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
});

// ---- App services --------------------------------------------------------------------------
builder.Services.Configure<SmtpOptions>(config.GetSection("Smtp"));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IAppEmailSender, SmtpEmailSender>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IReportService, ReportService>();
builder.Services.AddScoped<IIncidentService, IncidentService>();
builder.Services.AddScoped<IAnalyticsService, AnalyticsService>();
builder.Services.AddScoped<IUserAdminService, UserAdminService>();
builder.Services.AddScoped<LookupService>();
builder.Services.AddScoped<StaffMfaFilter>();
builder.Services.AddHostedService<StartupTasks>();

builder.Services.AddControllersWithViews(o => o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));

var app = builder.Build();

// Behind a reverse proxy / load balancer: trust forwarded headers from the listed proxies only.
var proxies = config.GetSection("Proxy:KnownProxies").Get<string[]>();
if (proxies is { Length: > 0 })
{
    var fh = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
    fh.KnownIPNetworks.Clear();
    fh.KnownProxies.Clear();
    foreach (var p in proxies) fh.KnownProxies.Add(IPAddress.Parse(p));
    app.UseForwardedHeaders(fh);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseCookiePolicy(new CookiePolicyOptions
{
    Secure = CookieSecurePolicy.Always,
    HttpOnly = Microsoft.AspNetCore.CookiePolicy.HttpOnlyPolicy.Always,
    MinimumSameSitePolicy = SameSiteMode.Lax
});
app.UseStatusCodePagesWithReExecute("/Home/Error", "?code={0}");
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}").WithStaticAssets();

app.Run();

public partial class Program;
