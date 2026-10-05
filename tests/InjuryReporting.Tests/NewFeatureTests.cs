using System.Net;
using System.Text.RegularExpressions;
using InjuryReporting.Web.Data;
using InjuryReporting.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InjuryReporting.Tests;

public class SuppressionAndTrendTests
{
    private static AnalyticsResult Sample() => new()
    {
        Incidents = 12, Reports = 14, DuplicatesCombined = 2, NeedsReview = 3,
        ByDiscipline = new() { new("Rapier", 9), new("Archery", 3), new("Equestrian", 0) },
        YearMonthGrid = new() { new(2026, new[] { 1, 0, 5, 4, 2, 0, 0, 0, 0, 0, 0, 0 }, 12) },
        CrossTabColumns = new() { "A" },
        CrossTab = new() { new("Rapier", new[] { 4 }, 4) }
    };

    [Fact]
    public void Counts_between_1_and_4_are_hidden_zero_and_five_plus_are_kept()
    {
        var r = AnalyticsService.Suppress(Sample(), 5);
        Assert.True(r.Suppressed);
        Assert.Equal(12, r.Incidents);
        Assert.Equal(AnalyticsResult.Hidden, r.DuplicatesCombined); // 2 -> hidden
        Assert.Equal(AnalyticsResult.Hidden, r.NeedsReview);        // 3 -> hidden
        Assert.Equal(new[] { 9, AnalyticsResult.Hidden, 0 }, r.ByDiscipline.Select(b => b.Count).ToArray());
        Assert.Equal(new[] { AnalyticsResult.Hidden, 0, 5, AnalyticsResult.Hidden, AnalyticsResult.Hidden, 0, 0, 0, 0, 0, 0, 0 }, r.YearMonthGrid[0].Months);
        Assert.Equal(AnalyticsResult.Hidden, r.CrossTab[0].Total);
    }

    [Fact]
    public void Monthly_trend_fills_gaps_with_zero()
    {
        var trend = AnalyticsService.BuildTrend(new()
        {
            [(2025, 11)] = 3, [(2026, 2)] = 1
        });
        Assert.Equal(new[] { "2025-11", "2025-12", "2026-01", "2026-02" }, trend.Select(b => b.Label).ToArray());
        Assert.Equal(new[] { 3, 0, 0, 1 }, trend.Select(b => b.Count).ToArray());
    }

    [Fact]
    public void Trend_is_capped_to_the_latest_48_months()
    {
        var trend = AnalyticsService.BuildTrend(new() { [(2018, 1)] = 1, [(2026, 6)] = 1 });
        Assert.Equal(48, trend.Count);
        Assert.Equal("2026-06", trend[^1].Label);
    }

    [Fact]
    public void Owner_edit_window_is_seven_days_and_registered_only()
    {
        var now = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        var owned = new InjuryReport { ReporterUserId = Guid.NewGuid(), SubmittedUtc = now.AddDays(-6) };
        Assert.True(ReportRules.OwnerCanEdit(owned, now));
        owned.SubmittedUtc = now.AddDays(-8);
        Assert.False(ReportRules.OwnerCanEdit(owned, now));
        Assert.False(ReportRules.OwnerCanEdit(new InjuryReport { ReporterUserId = null, SubmittedUtc = now }, now));
    }
}

public class AnalyticsQueryTests : IDisposable
{
    private readonly TestHost _h = new();

    [Fact]
    public async Task Dashboard_queries_run_and_suppress_a_single_incident()
    {
        await _h.Get<IReportService>().SubmitAsync(new ReportInput(Guid.NewGuid(), 1, 1, Severity.FirstAidOnly,
            new DateOnly(2026, 3, 14), "Spring Fight", 3, null, "Narrative text here."), null);
        var svc = _h.Get<IAnalyticsService>();

        var exact = await svc.GetAsync(new AnalyticsFilter());
        Assert.Equal(1, exact.Incidents);
        Assert.Equal("2026-03", exact.MonthlyTrend.Single().Label);
        Assert.Equal(1, exact.YearMonthGrid.Single().Months[2]);
        Assert.Equal("2026", exact.ByYear.Single().Label);

        var shared = await svc.GetAsync(new AnalyticsFilter { HideSmallCounts = true });
        Assert.Equal(AnalyticsResult.Hidden, shared.Incidents);
        var summary = await svc.SummaryRowsAsync(new AnalyticsFilter());
        Assert.All(summary, row => Assert.NotEqual(1, row.Count));   // no raw "1" ever leaves in the shareable export
    }

    public void Dispose() => _h.Dispose();
}

public class NewFeatureWebTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;
    public NewFeatureWebTests(AppFactory factory) => _factory = factory;

    private static async Task<string> Token(HttpClient c, string url)
    {
        var html = await c.GetStringAsync(url);
        var m = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(m.Success, "antiforgery token not found on " + url);
        return m.Groups[1].Value;
    }

    private static FormUrlEncodedContent Form(params (string, string)[] kv) =>
        new(kv.ToDictionary(x => x.Item1, x => x.Item2));

    private async Task<(HttpClient Client, Guid UserId)> SignedInUser(string email)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
            var u = new ApplicationUser { Id = Guid.NewGuid(), UserName = email, Email = email, EmailConfirmed = true, DisplayName = "Tester" };
            Assert.True((await users.CreateAsync(u, "Another-Long-Passw0rd")).Succeeded);
            await users.AddToRoleAsync(u, "User");
        }
        var c = _factory.NewClient();
        var res = await c.PostAsync("/Account/Login", Form(
            ("__RequestVerificationToken", await Token(c, "/Account/Login")), ("Email", email), ("Password", "Another-Long-Passw0rd")));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        using var scope2 = _factory.Services.CreateScope();
        var id = (await scope2.ServiceProvider.GetRequiredService<AppDbContext>().Users.SingleAsync(x => x.Email == email)).Id;
        return (c, id);
    }

    private const string StaffPassword = "Another-Long-Passw0rd";

    /// <summary>A fresh staff user (so tests never share/flip the seeded admin), signed in with 2FA still off.</summary>
    private async Task<HttpClient> SignedInStaff(string email, string role)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
            var u = new ApplicationUser { Id = Guid.NewGuid(), UserName = email, Email = email, EmailConfirmed = true, DisplayName = "Staff" };
            Assert.True((await users.CreateAsync(u, StaffPassword)).Succeeded);
            await users.AddToRoleAsync(u, role);
        }
        var c = _factory.NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await c.PostAsync("/Account/Login", Form(
            ("__RequestVerificationToken", await Token(c, "/Account/Login")), ("Email", email), ("Password", StaffPassword)))).StatusCode);
        return c;
    }

    /// <summary>Marks 2FA enabled in the store; the live session carries on (the TOTP ceremony is tested separately).</summary>
    private async Task EnableTwoFactorDirectly(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Users.SingleAsync(x => x.Email == email)).TwoFactorEnabled = true;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Owner_can_edit_narrative_but_not_after_the_window_and_not_others_reports()
    {
        var (c, uid) = await SignedInUser("editor@example.org");
        var post = await c.PostAsync("/Reports/Create", Form(
            ("__RequestVerificationToken", await Token(c, "/Reports/Create")),
            ("SubmissionToken", Guid.NewGuid().ToString()), ("DisciplineId", "1"), ("InjuryTypeId", "2"), ("Severity", "FirstAidOnly"),
            ("InjuryDate", "2026-05-02"), ("EventName", "Edit Test War"), ("EventKingdomId", "4"), ("InjuredKingdomId", ""),
            ("Narrative", "Original narrative text.")));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);

        Guid rid;
        using (var scope = _factory.Services.CreateScope())
            rid = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Reports.SingleAsync(r => r.EventName == "Edit Test War")).Id;

        var edit = await c.PostAsync("/Reports/Edit", Form(
            ("__RequestVerificationToken", await Token(c, $"/Reports/Edit/{rid}")), ("Id", rid.ToString()), ("Narrative", "Corrected narrative text.")));
        Assert.Equal(HttpStatusCode.Redirect, edit.StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var r = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Reports.SingleAsync(x => x.Id == rid);
            Assert.Equal("Corrected narrative text.", r.Narrative);
            Assert.NotNull(r.NarrativeEditedUtc);
        }

        // Past the 7-day window the page redirects back instead of showing the form.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Reports.SingleAsync(x => x.Id == rid)).SubmittedUtc = DateTime.UtcNow.AddDays(-9);
            await db.SaveChangesAsync();
        }
        var late = await c.GetAsync($"/Reports/Edit/{rid}");
        Assert.Equal(HttpStatusCode.Redirect, late.StatusCode);

        // Another signed-in user gets a 404 for someone else's report.
        var (other, _) = await SignedInUser("other@example.org");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/Reports/Edit/{rid}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/Reports/Details/{rid}")).StatusCode);
    }

    [Fact]
    public async Task Deleting_an_account_removes_the_user_and_unlinks_their_reports()
    {
        var (c, uid) = await SignedInUser("leaver@example.org");
        await c.PostAsync("/Reports/Create", Form(
            ("__RequestVerificationToken", await Token(c, "/Reports/Create")),
            ("SubmissionToken", Guid.NewGuid().ToString()), ("DisciplineId", "3"), ("InjuryTypeId", "1"), ("Severity", "MedicalTreatment"),
            ("InjuryDate", "2026-04-02"), ("EventName", "Leaver War"), ("EventKingdomId", "6"), ("InjuredKingdomId", ""),
            ("Narrative", "A report that should outlive the account.")));

        // Wrong password does nothing.
        var bad = await c.PostAsync("/Manage/DeleteAccount", Form(
            ("__RequestVerificationToken", await Token(c, "/Manage/DeleteAccount")), ("CurrentPassword", "wrong"), ("Confirm", "true")));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);

        var ok = await c.PostAsync("/Manage/DeleteAccount", Form(
            ("__RequestVerificationToken", await Token(c, "/Manage/DeleteAccount")), ("CurrentPassword", "Another-Long-Passw0rd"),
            ("DeleteReports", "false"), ("Confirm", "true")));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Users.AnyAsync(u => u.Id == uid));
        var report = await db.Reports.SingleAsync(r => r.EventName == "Leaver War");
        Assert.Null(report.ReporterUserId);                                   // kept, but now anonymous
        Assert.True(await db.AuditLog.AnyAsync(a => a.Action == "account.erased_self" && a.ActorUserId == uid));
    }

    [Fact]
    public async Task User_can_download_their_data_and_delete_everything_including_reports()
    {
        var (c, uid) = await SignedInUser("everything@example.org");
        await c.PostAsync("/Reports/Create", Form(
            ("__RequestVerificationToken", await Token(c, "/Reports/Create")),
            ("SubmissionToken", Guid.NewGuid().ToString()), ("DisciplineId", "4"), ("InjuryTypeId", "5"), ("Severity", "FirstAidOnly"),
            ("InjuryDate", "2026-02-02"), ("EventName", "Erase All War"), ("EventKingdomId", "7"), ("InjuredKingdomId", ""),
            ("Narrative", "Narrative-marker-plumbus about my own injury.")));

        var dl = await c.GetAsync("/Manage/DownloadMyData");
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
        Assert.Equal("application/json", dl.Content.Headers.ContentType!.MediaType);
        var json = await dl.Content.ReadAsStringAsync();
        Assert.Contains("plumbus", json);                    // their narrative is in the export...
        Assert.Contains("everything@example.org", json);
        Assert.DoesNotContain("PasswordHash", json, StringComparison.OrdinalIgnoreCase);   // ...secrets are not
        Assert.DoesNotContain("SecurityStamp", json, StringComparison.OrdinalIgnoreCase);

        var del = await c.PostAsync("/Manage/DeleteAccount", Form(
            ("__RequestVerificationToken", await Token(c, "/Manage/DeleteAccount")), ("CurrentPassword", "Another-Long-Passw0rd"),
            ("DeleteReports", "true"), ("Confirm", "true")));
        Assert.Equal(HttpStatusCode.Redirect, del.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Users.AnyAsync(u => u.Id == uid));
        Assert.False(await db.Reports.AnyAsync(r => r.EventName == "Erase All War"));
        Assert.False(await db.Incidents.AnyAsync(i => i.EventName == "Erase All War"));
        // Personal data is gone from the audit trail but the rows (and the erasure event) remain.
        Assert.False(await db.AuditLog.AnyAsync(a => a.ActorEmail == "everything@example.org"));
        Assert.True(await db.AuditLog.AnyAsync(a => a.Action == "account.erased_self" && a.ActorUserId == uid && a.ActorEmail == null && a.IpAddress == null));
    }

    [Fact]
    public async Task Super_admin_can_retrieve_and_delete_another_user_with_all_their_data()
    {
        // A target user with a report.
        var (tc, targetId) = await SignedInUser("target@example.org");
        await tc.PostAsync("/Reports/Create", Form(
            ("__RequestVerificationToken", await Token(tc, "/Reports/Create")),
            ("SubmissionToken", Guid.NewGuid().ToString()), ("DisciplineId", "5"), ("InjuryTypeId", "3"), ("Severity", "Hospitalization"),
            ("InjuryDate", "2026-01-15"), ("EventName", "Admin Erase War"), ("EventKingdomId", "8"), ("InjuredKingdomId", ""),
            ("Narrative", "Narrative-marker-gazorpazorp for the admin export.")));

        var c = await SignedInStaff("sa-eraser@example.org", "SuperAdmin");
        await EnableTwoFactorDirectly("sa-eraser@example.org");

        // Retrieve.
        var export = await c.GetAsync($"/Users/ExportData/{targetId}");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Contains("gazorpazorp", await export.Content.ReadAsStringAsync());

        var page = await c.GetStringAsync($"/Users/Details/{targetId}");
        Assert.Contains("Permanently delete user", page);
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

        // Guards: wrong password and wrong typed email change nothing.
        await c.PostAsync("/Users/DeleteUser", Form(("__RequestVerificationToken", token), ("Id", targetId.ToString()),
            ("ConfirmEmail", "target@example.org"), ("AdminPassword", "not-my-password"), ("DeleteReports", "true")));
        await c.PostAsync("/Users/DeleteUser", Form(("__RequestVerificationToken", token), ("Id", targetId.ToString()),
            ("ConfirmEmail", "someone-else@example.org"), ("AdminPassword", StaffPassword), ("DeleteReports", "true")));
        using (var scope = _factory.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.AnyAsync(u => u.Id == targetId));

        // Delete for real.
        var del = await c.PostAsync("/Users/DeleteUser", Form(("__RequestVerificationToken", token), ("Id", targetId.ToString()),
            ("ConfirmEmail", "TARGET@example.org"), ("AdminPassword", StaffPassword), ("DeleteReports", "true")));
        Assert.Equal(HttpStatusCode.Redirect, del.StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.Users.AnyAsync(u => u.Id == targetId));
            Assert.False(await db.Reports.AnyAsync(r => r.EventName == "Admin Erase War"));
            Assert.True(await db.AuditLog.AnyAsync(a => a.Action == "account.erased_by_admin" && a.EntityId == targetId.ToString()));
            Assert.True(await db.AuditLog.AnyAsync(a => a.Action == "user.data_exported"));
        }

        // A regular (non-super) admin cannot use either action.
        var (plain, _) = await SignedInUser("plainuser@example.org");
        Assert.Equal(HttpStatusCode.Redirect, (await plain.GetAsync($"/Users/ExportData/{targetId}")).StatusCode);   // login/denied
    }

    /// <summary>RFC 6238 TOTP (SHA-1, 30 s, 6 digits) from Identity's base32 authenticator key.</summary>
    private static string Totp(string base32Key)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = new System.Text.StringBuilder();
        foreach (var ch in base32Key.TrimEnd('=').ToUpperInvariant())
            bits.Append(Convert.ToString(alphabet.IndexOf(ch), 2).PadLeft(5, '0'));
        var key = Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.ToString(i * 8, 8), 2)).ToArray();
        var counter = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);
        var hash = new System.Security.Cryptography.HMACSHA1(key).ComputeHash(counter);
        var o = hash[^1] & 0x0F;
        var bin = ((hash[o] & 0x7F) << 24) | (hash[o + 1] << 16) | (hash[o + 2] << 8) | hash[o + 3];
        return (bin % 1_000_000).ToString("D6");
    }

    [Fact]
    public async Task Staff_can_enrol_in_two_factor_without_being_signed_out_and_then_reach_admin_pages()
    {
        // Regression: viewing the setup page and enabling 2FA both change the security stamp. Without re-issuing the
        // session cookie the user was silently signed out mid-setup and bounced to a POST-only address (405).
        const string email = "newstaff@example.org";
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
            var u = new ApplicationUser { Id = Guid.NewGuid(), UserName = email, Email = email, EmailConfirmed = true, DisplayName = "New Staff" };
            Assert.True((await users.CreateAsync(u, "Another-Long-Passw0rd")).Succeeded);
            await users.AddToRoleAsync(u, "Admin");
        }
        var c = _factory.NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await c.PostAsync("/Account/Login", Form(
            ("__RequestVerificationToken", await Token(c, "/Account/Login")), ("Email", email), ("Password", "Another-Long-Passw0rd")))).StatusCode);

        // Staff without 2FA are sent to enrol.
        Assert.Contains("/Manage/TwoFactor", (await c.GetAsync("/Incidents")).Headers.Location!.OriginalString);

        var setupToken = await Token(c, "/Manage/TwoFactor");                    // generates the authenticator key (stamp changes)
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/Manage")).StatusCode); // still signed in

        string key;
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
            key = (await users.GetAuthenticatorKeyAsync((await users.FindByEmailAsync(email))!))!;
        }
        var enable = await c.PostAsync("/Manage/EnableTwoFactor", Form(("__RequestVerificationToken", setupToken), ("Code", Totp(key))));
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        Assert.Contains("recovery codes", await enable.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        // Still signed in after enabling, and staff pages are now open.
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/Manage")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/Incidents")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/Analytics")).StatusCode);

        // POST-only addresses opened as pages redirect to the 2FA page instead of a 405.
        Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("/Manage/EnableTwoFactor")).StatusCode);
    }

    [Fact]
    public async Task Two_factor_setup_shows_a_qr_code_and_staff_pages_for_new_features_render()
    {
        var c = await SignedInStaff("qr-staff@example.org", "SuperAdmin");

        var tf = await c.GetStringAsync("/Manage/TwoFactor");
        Assert.Contains("<svg", tf);
        Assert.Contains("shape-rendering", tf);

        await EnableTwoFactorDirectly("qr-staff@example.org");

        foreach (var url in new[] { "/Lookups", "/Lookups?kind=kingdoms", "/Lookups?kind=injury-types", "/Analytics?HideSmallCounts=true",
                     "/Analytics/ExportSummaryCsv" })
            Assert.True((await c.GetAsync(url)).StatusCode == HttpStatusCode.OK, url);

        // Add, rename and deactivate a discipline; the public form follows.
        var add = await c.PostAsync("/Lookups/Add", Form(
            ("__RequestVerificationToken", await Token(c, "/Lookups")), ("kind", "disciplines"), ("name", "Siege Weapons")));
        Assert.Equal(HttpStatusCode.Redirect, add.StatusCode);
        Assert.Contains("Siege Weapons", await _factory.NewClient().GetStringAsync("/Reports/Create"));

        int id;
        using (var scope = _factory.Services.CreateScope())
            id = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Disciplines.SingleAsync(d => d.Name == "Siege Weapons")).Id;
        await c.PostAsync("/Lookups/Toggle", Form(("__RequestVerificationToken", await Token(c, "/Lookups")), ("kind", "disciplines"), ("id", id.ToString())));
        Assert.DoesNotContain("Siege Weapons", await _factory.NewClient().GetStringAsync("/Reports/Create"));

        // Duplicate names are refused.
        await c.PostAsync("/Lookups/Add", Form(("__RequestVerificationToken", await Token(c, "/Lookups")), ("kind", "disciplines"), ("name", "rapier")));
        using var scope2 = _factory.Services.CreateScope();
        Assert.Equal(1, await scope2.ServiceProvider.GetRequiredService<AppDbContext>().Disciplines.CountAsync(d => d.Name.ToLower() == "rapier"));
    }
}
