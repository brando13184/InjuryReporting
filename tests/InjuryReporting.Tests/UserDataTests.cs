using System.Text.Json;
using InjuryReporting.Web.Data;
using InjuryReporting.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace InjuryReporting.Tests;

public class UserDataServiceTests : IDisposable
{
    private readonly TestHost _h = new();
    private static readonly DateOnly Day = new(2026, 5, 10);

    private ReportInput In(string evt, Severity sev = Severity.FirstAidOnly, int type = 1, string narrative = "A narrative about this.") =>
        new(Guid.NewGuid(), 1, type, sev, Day, evt, 3, null, narrative);

    [Fact]
    public async Task Erasing_with_reports_removes_sole_incidents_and_recomputes_shared_ones()
    {
        var user = await _h.AddUser("gone@example.org", "User");
        var svc = _h.Get<IReportService>();
        await svc.SubmitAsync(In("Solo War", narrative: "only mine"), user.Id);                                 // sole report -> incident removed
        await svc.SubmitAsync(In("Shared War", Severity.Hospitalization), user.Id);                              // mine, severe
        await svc.SubmitAsync(In("Shared War", Severity.FirstAidOnly), null);                                    // someone else's, same incident

        var result = await _h.Get<IUserDataService>().EraseAsync(user.Id, user.Id, deleteReports: true);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(2, result.ReportsDeleted);
        Assert.Equal(1, result.IncidentsRemoved);

        _h.Db.ChangeTracker.Clear();
        Assert.False(await _h.Db.Users.AnyAsync(u => u.Id == user.Id));
        Assert.False(await _h.Db.Incidents.AnyAsync(i => i.EventName == "Solo War"));
        var shared = await _h.Db.Incidents.Include(i => i.Reports).SingleAsync(i => i.EventName == "Shared War");
        Assert.Single(shared.Reports);
        Assert.Equal(Severity.FirstAidOnly, shared.Severity);                // severity no longer inflated by the deleted report
    }

    [Fact]
    public async Task Erasing_the_last_report_of_a_merge_target_also_removes_its_retired_tombstones()
    {
        var user = await _h.AddUser("merger@example.org", "User");
        var svc = _h.Get<IReportService>();
        await svc.SubmitAsync(In("Target War"), user.Id);
        await svc.SubmitAsync(In("Source War"), null);                      // anonymous; will be merged into the user's incident
        var ids = await _h.Db.Incidents.AsNoTracking().Select(i => new { i.Id, i.EventName }).ToListAsync();
        var target = ids.Single(i => i.EventName == "Target War").Id;
        var source = ids.Single(i => i.EventName == "Source War").Id;
        Assert.True((await _h.Get<IIncidentService>().MergeAsync(target, new[] { source })).Succeeded);

        // The target now holds both reports (user's + the anonymous one), so deleting the user leaves it in place...
        var r1 = await _h.Get<IUserDataService>().EraseAsync(user.Id, user.Id, true);
        Assert.True(r1.Succeeded, r1.Error);
        _h.Db.ChangeTracker.Clear();
        Assert.True(await _h.Db.Incidents.AnyAsync(i => i.Id == target));
        Assert.Equal(1, await _h.Db.Reports.CountAsync(r => r.IncidentId == target));

        // ...and a second user whose report is the only one left cleans up the tombstone with it.
        var user2 = await _h.AddUser("lonely@example.org", "User");
        await svc.SubmitAsync(In("Lonely War"), user2.Id);
        var lonely = (await _h.Db.Incidents.AsNoTracking().SingleAsync(i => i.EventName == "Lonely War")).Id;
        await svc.SubmitAsync(In("Retire Me War"), null);
        var retire = (await _h.Db.Incidents.AsNoTracking().SingleAsync(i => i.EventName == "Retire Me War")).Id;
        Assert.True((await _h.Get<IIncidentService>().MergeAsync(lonely, new[] { retire })).Succeeded);   // tombstone -> lonely
        // move the anonymous report off the target so only the user's report remains on `lonely`
        var anon = await _h.Db.Reports.Where(r => r.IncidentId == lonely && r.ReporterUserId == null).Select(r => r.Id).SingleAsync();
        Assert.True((await _h.Get<IIncidentService>().SplitReportAsync(anon)).Succeeded);

        var r2 = await _h.Get<IUserDataService>().EraseAsync(user2.Id, user2.Id, true);
        Assert.True(r2.Succeeded, r2.Error);
        Assert.Equal(2, r2.IncidentsRemoved);                                // the incident and its retired tombstone
        _h.Db.ChangeTracker.Clear();
        Assert.False(await _h.Db.Incidents.AnyAsync(i => i.Id == lonely || i.Id == retire));
    }

    [Fact]
    public async Task Erasing_without_reports_keeps_them_unlinked()
    {
        var user = await _h.AddUser("keeper@example.org", "User");
        await _h.Get<IReportService>().SubmitAsync(In("Keep War"), user.Id);
        var r = await _h.Get<IUserDataService>().EraseAsync(user.Id, user.Id, deleteReports: false);
        Assert.True(r.Succeeded, r.Error);
        _h.Db.ChangeTracker.Clear();
        Assert.Null((await _h.Db.Reports.SingleAsync(x => x.EventName == "Keep War")).ReporterUserId);
        Assert.Equal(1, await _h.Db.Incidents.CountAsync(i => i.EventName == "Keep War"));
    }

    [Fact]
    public async Task Audit_personal_data_is_blanked_but_the_trail_remains()
    {
        var user = await _h.AddUser("trail@example.org", "User");
        _h.Db.AuditLog.Add(new AuditLogEntry { ActorUserId = user.Id, ActorEmail = "trail@example.org", Action = "login.succeeded", IpAddress = "203.0.113.5" });
        _h.Db.AuditLog.Add(new AuditLogEntry { ActorUserId = null, ActorEmail = "TRAIL@example.org", Action = "login.failed_unknown_user", IpAddress = "203.0.113.6" });
        _h.Db.AuditLog.Add(new AuditLogEntry { ActorUserId = Guid.NewGuid(), ActorEmail = "bystander@example.org", Action = "login.succeeded", IpAddress = "203.0.113.7" });
        await _h.Db.SaveChangesAsync();
        var before = await _h.Db.AuditLog.CountAsync();

        var r = await _h.Get<IUserDataService>().EraseAsync(user.Id, user.Id, true);
        Assert.True(r.Succeeded, r.Error);
        Assert.True(r.AuditRowsScrubbed >= 2);

        _h.Db.ChangeTracker.Clear();
        Assert.True(await _h.Db.AuditLog.CountAsync() > before);             // nothing deleted (the erasure event was added)
        Assert.False(await _h.Db.AuditLog.AnyAsync(a => a.ActorEmail != null && a.ActorEmail.ToLower() == "trail@example.org"));
        Assert.False(await _h.Db.AuditLog.AnyAsync(a => a.IpAddress == "203.0.113.5" || a.IpAddress == "203.0.113.6"));
        Assert.Equal("bystander@example.org", (await _h.Db.AuditLog.SingleAsync(a => a.IpAddress == "203.0.113.7")).ActorEmail);   // others untouched
    }

    [Fact]
    public async Task Only_a_super_admin_may_erase_someone_else_and_the_last_super_admin_is_protected()
    {
        var sa = await _h.AddUser("sa@example.org", "SuperAdmin");
        var admin = await _h.AddUser("admin@example.org", "Admin");
        var user = await _h.AddUser("victim@example.org", "User");
        var data = _h.Get<IUserDataService>();

        Assert.False((await data.EraseAsync(admin.Id, user.Id, true)).Succeeded);       // plain admin: refused
        Assert.False((await data.EraseAsync(user.Id, sa.Id, true)).Succeeded);          // a user can't erase a Super Admin
        Assert.False((await data.EraseAsync(sa.Id, sa.Id, true)).Succeeded);            // sole Super Admin can't erase self
        Assert.True(await _h.Db.Users.AnyAsync(u => u.Id == user.Id));

        Assert.True((await data.EraseAsync(sa.Id, user.Id, true)).Succeeded);           // Super Admin may erase another user
        Assert.False(await _h.Db.Users.AnyAsync(u => u.Id == user.Id));
    }

    [Fact]
    public async Task Export_contains_the_users_data_and_no_secrets()
    {
        var user = await _h.AddUser("export@example.org", "User");
        await _h.Get<IReportService>().SubmitAsync(In("Export War", narrative: "narrative-marker-schwifty"), user.Id);
        await _h.Get<IAuditService>().LogForAsync(user.Id, user.Email, "login.succeeded");

        var bytes = await _h.Get<IUserDataService>().ExportAsync(user.Id);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        using var doc = JsonDocument.Parse(bytes);
        Assert.Equal("export@example.org", doc.RootElement.GetProperty("account").GetProperty("email").GetString());
        Assert.Contains("schwifty", text);
        Assert.Single(doc.RootElement.GetProperty("reports").EnumerateArray());
        Assert.Contains(doc.RootElement.GetProperty("activity").EnumerateArray(), a => a.GetProperty("action").GetString() == "login.succeeded");
        Assert.DoesNotContain("passwordhash", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("securitystamp", text, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _h.Dispose();
}
