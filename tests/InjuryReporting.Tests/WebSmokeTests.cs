using System.Net;
using System.Text.RegularExpressions;
using InjuryReporting.Web.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InjuryReporting.Tests;

public class AppFactory : WebApplicationFactory<Program>
{
    public const string AdminEmail = "root@example.org";
    public const string AdminPassword = "Test-Only-Passw0rd-Long!";
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "Host=unused",
            ["Database:MigrateOnStartup"] = "false",
            ["Seed:SuperAdminEmail"] = AdminEmail,
            ["Seed:SuperAdminPassword"] = AdminPassword
        }));
        builder.ConfigureServices(services =>
        {
            _connection.Open();
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<DbContextOptions>();
            // EF registers the provider (Npgsql) in an internal IDbContextOptionsConfiguration<AppDbContext>; drop it.
            foreach (var d in services.Where(d => d.ServiceType.IsGenericType
                         && d.ServiceType.Name.StartsWith("IDbContextOptionsConfiguration")
                         && d.ServiceType.GenericTypeArguments[0] == typeof(AppDbContext)).ToList())
                services.Remove(d);
            services.AddDbContext<AppDbContext>(o => o.UseSqlite(_connection));
            // Validate the security stamp on every request (prod: every minute) so stamp-related sign-outs show up in tests.
            services.Configure<Microsoft.AspNetCore.Identity.SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero);
            var opts = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
            using var db = new AppDbContext(opts, new EphemeralDataProtectionProvider());
            db.Database.EnsureCreated();
        });
    }

    public HttpClient NewClient(bool follow = false) => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = follow,
        HandleCookies = true
    });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }
}

public class WebSmokeTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;
    public WebSmokeTests(AppFactory factory) => _factory = factory;

    private static async Task<string> Token(HttpClient c, string url)
    {
        var html = await c.GetStringAsync(url);
        var m = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(m.Success, "antiforgery token not found on " + url);
        return m.Groups[1].Value;
    }

    [Fact]
    public async Task Home_page_renders_with_hardening_headers_and_local_bootstrap()
    {
        var c = _factory.NewClient();
        var res = await c.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("default-src 'self'", res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("no-store", res.Headers.GetValues("Cache-Control").Single());
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("/lib/bootstrap/dist/css/bootstrap.min.css", html);
        Assert.DoesNotContain("style=\"", html);      // strict CSP: no inline styles
    }

    [Fact]
    public async Task Report_form_offers_all_disciplines_and_a_calendar_picker()
    {
        var html = await _factory.NewClient().GetStringAsync("/Reports/Create");
        foreach (var d in new[] { "Armored Combat", "Rapier", "Equestrian", "Archery", "Thrown Weapons" })
            Assert.Contains(d, html);
        Assert.Contains("type=\"date\"", html);
        Assert.Contains("Ealdormere", html);
        Assert.Contains("Non-member / not sure", html);
    }

    [Fact]
    public async Task Anonymous_visitor_can_submit_a_report_and_is_not_asked_to_sign_in()
    {
        var c = _factory.NewClient();
        var token = await Token(c, "/Reports/Create");
        var res = await c.PostAsync("/Reports/Create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["SubmissionToken"] = Guid.NewGuid().ToString(),
            ["DisciplineId"] = "2", ["InjuryTypeId"] = "1", ["Severity"] = "MedicalTreatment",
            ["InjuryDate"] = "2026-05-10", ["EventName"] = "Smoke Test War",
            ["EventKingdomId"] = "5", ["InjuredKingdomId"] = "",
            ["Narrative"] = "Took a thrust to the mask and felt dizzy afterward."
        }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.EndsWith("/Reports/Thanks", res.Headers.Location!.OriginalString);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var report = await db.Reports.SingleAsync(r => r.EventName == "Smoke Test War");
        Assert.Null(report.ReporterUserId);
    }

    [Fact]
    public async Task Invalid_report_is_rejected_with_validation_messages()
    {
        var c = _factory.NewClient();
        var token = await Token(c, "/Reports/Create");
        var res = await c.PostAsync("/Reports/Create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["EventName"] = "Nothing else filled in"
        }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("Choose a discipline", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_without_antiforgery_token_is_rejected()
    {
        var res = await _factory.NewClient().PostAsync("/Reports/Create", new FormUrlEncodedContent(new Dictionary<string, string> { ["EventName"] = "x" }));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Theory]
    [InlineData("/Incidents")]
    [InlineData("/Analytics")]
    [InlineData("/Users")]
    [InlineData("/Audit")]
    [InlineData("/Manage")]
    [InlineData("/Reports/Mine")]
    public async Task Protected_pages_redirect_anonymous_users_to_login(string url)
    {
        var res = await _factory.NewClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("/Account/Login", res.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Bad_password_is_rejected_and_super_admin_is_forced_to_enrol_in_mfa()
    {
        var c = _factory.NewClient();

        var bad = await c.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = await Token(c, "/Account/Login"),
            ["Email"] = AppFactory.AdminEmail, ["Password"] = "wrong-password-entirely"
        }));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.Contains("Invalid email or password", await bad.Content.ReadAsStringAsync());

        var ok = await c.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = await Token(c, "/Account/Login"),
            ["Email"] = AppFactory.AdminEmail, ["Password"] = AppFactory.AdminPassword
        }));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);

        // Authenticated staff without 2FA are bounced to the enrolment page for every staff area.
        foreach (var url in new[] { "/Incidents", "/Analytics", "/Users", "/Audit" })
        {
            var res = await c.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
            Assert.Contains("/Manage/TwoFactor", res.Headers.Location!.OriginalString);
        }

        // The enrolment page itself works and shows a setup key.
        var page = await c.GetAsync("/Manage/TwoFactor");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("authenticator", await page.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        // Self-service pages render for a signed-in user.
        foreach (var url in new[] { "/Manage", "/Manage/ChangePassword", "/Manage/ChangeEmail", "/Reports/Mine" })
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Staff_with_mfa_can_open_every_admin_page()
    {
        // Turn 2FA on directly in the store (the TOTP ceremony itself is Identity's), then sign in. A second
        // 2FA-gated login would need a code, so instead sign in first and flip the flag for the session.
        var c = _factory.NewClient();
        await c.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = await Token(c, "/Account/Login"),
            ["Email"] = AppFactory.AdminEmail, ["Password"] = AppFactory.AdminPassword
        }));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var u = await db.Users.SingleAsync(x => x.Email == AppFactory.AdminEmail);
            u.TwoFactorEnabled = true;
            await db.SaveChangesAsync();
        }

        foreach (var url in new[] { "/Incidents", "/Incidents?status=all", "/Analytics", "/Analytics/ExportCsv", "/Users", "/Audit" })
        {
            var res = await c.GetAsync(url);
            Assert.True(res.StatusCode == HttpStatusCode.OK, $"{url} returned {(int)res.StatusCode}");
        }
        var users = await c.GetStringAsync("/Users");
        Assert.Contains(AppFactory.AdminEmail, users);
    }
}
