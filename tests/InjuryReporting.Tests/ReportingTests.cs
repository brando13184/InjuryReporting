using InjuryReporting.Web.Data;
using InjuryReporting.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace InjuryReporting.Tests;

public class MatchKeyTests
{
    [Fact]
    public void Event_names_are_normalised()
    {
        var d = new DateOnly(2026, 6, 1);
        Assert.Equal(
            MatchKeys.Strict(1, 1, d, "Pennsic War  55!", 5, null),
            MatchKeys.Strict(1, 1, d, "pennsic war 55", 5, null));
    }

    [Fact]
    public void Different_fields_give_different_keys()
    {
        var d = new DateOnly(2026, 6, 1);
        var a = MatchKeys.Strict(1, 1, d, "Event", 5, null);
        Assert.NotEqual(a, MatchKeys.Strict(2, 1, d, "Event", 5, null));
        Assert.NotEqual(a, MatchKeys.Strict(1, 2, d, "Event", 5, null));
        Assert.NotEqual(a, MatchKeys.Strict(1, 1, d.AddDays(1), "Event", 5, null));
        Assert.NotEqual(a, MatchKeys.Strict(1, 1, d, "Event", 6, null));
        Assert.NotEqual(a, MatchKeys.Strict(1, 1, d, "Event", 5, 3));
    }

    [Fact]
    public void Csv_cells_cannot_start_a_spreadsheet_formula()
    {
        Assert.Equal("\"'=HYPERLINK(\"\"x\"\")\"", Web.Controllers.AnalyticsController.Csv("=HYPERLINK(\"x\")"));
        Assert.Equal("\"'@cmd\"", Web.Controllers.AnalyticsController.Csv("@cmd"));
        Assert.Equal("\"Plain, text\"", Web.Controllers.AnalyticsController.Csv("Plain, text"));
    }
}

public class ReportServiceTests : IDisposable
{
    private readonly TestHost _h = new();
    private static readonly DateOnly Day = new(2026, 5, 10);

    private static ReportInput Input(Guid? token = null, int type = 1, Severity sev = Severity.FirstAidOnly, string evt = "Gulf Wars", int kingdom = 3, int? injured = null, string narrative = "Fell during a bout and twisted a knee.") =>
        new(token ?? Guid.NewGuid(), DisciplineId: 1, type, sev, Day, evt, kingdom, injured, narrative);

    [Fact]
    public async Task First_report_creates_an_incident()
    {
        var r = await _h.Get<IReportService>().SubmitAsync(Input(), null);
        Assert.Equal(SubmitOutcome.Created, r.Outcome);
        Assert.Equal(1, await _h.Db.Incidents.CountAsync());
        Assert.Equal(1, await _h.Db.Reports.CountAsync());
    }

    [Fact]
    public async Task Identical_report_from_someone_else_is_combined_not_double_counted()
    {
        var svc = _h.Get<IReportService>();
        await svc.SubmitAsync(Input(sev: Severity.FirstAidOnly), null);
        var second = await svc.SubmitAsync(Input(evt: "gulf wars", sev: Severity.Hospitalization), null);

        Assert.Equal(SubmitOutcome.CombinedWithExisting, second.Outcome);
        var incident = await _h.Db.Incidents.SingleAsync();
        Assert.Equal(2, await _h.Db.Reports.CountAsync(r => r.IncidentId == incident.Id));
        Assert.Equal(Severity.Hospitalization, incident.Severity);   // highest severity wins
        Assert.True(incident.NeedsReview);

        var analytics = await _h.Get<IAnalyticsService>().GetAsync(new AnalyticsFilter());
        Assert.Equal(1, analytics.Incidents);
        Assert.Equal(2, analytics.Reports);
        Assert.Equal(1, analytics.DuplicatesCombined);
    }

    [Fact]
    public async Task Replaying_the_same_form_token_is_a_noop()
    {
        var svc = _h.Get<IReportService>();
        var token = Guid.NewGuid();
        await svc.SubmitAsync(Input(token), null);
        var again = await svc.SubmitAsync(Input(token), null);
        Assert.Equal(SubmitOutcome.AlreadySubmitted, again.Outcome);
        Assert.Equal(1, await _h.Db.Reports.CountAsync());
    }

    [Fact]
    public async Task Registered_user_cannot_file_the_same_report_twice()
    {
        var user = await _h.AddUser("a@example.org", "User");
        var svc = _h.Get<IReportService>();
        Assert.Equal(SubmitOutcome.Created, (await svc.SubmitAsync(Input(), user.Id)).Outcome);
        Assert.Equal(SubmitOutcome.DuplicateByReporter, (await svc.SubmitAsync(Input(), user.Id)).Outcome);
        Assert.Equal(1, await _h.Db.Reports.CountAsync());
    }

    [Fact]
    public async Task Same_event_different_injury_is_flagged_as_possible_duplicate_but_kept_separate()
    {
        var svc = _h.Get<IReportService>();
        await svc.SubmitAsync(Input(type: 1), null);
        await svc.SubmitAsync(Input(type: 2), null);
        Assert.Equal(2, await _h.Db.Incidents.CountAsync());
        Assert.Equal(1, await _h.Db.Reports.CountAsync(r => r.PossibleDuplicate));
    }

    [Fact]
    public async Task Anonymous_reports_store_no_reporter_and_no_time_of_day()
    {
        await _h.Get<IReportService>().SubmitAsync(Input(), null);
        var report = await _h.Db.Reports.SingleAsync();
        Assert.Null(report.ReporterUserId);
        Assert.Equal(TimeSpan.Zero, report.SubmittedUtc.TimeOfDay);
        // anonymous submissions leave an audit event that carries no actor and no entity id
        var audit = await _h.Db.AuditLog.SingleAsync(a => a.Action == "report.submitted.anonymous");
        Assert.Null(audit.ActorUserId);
        Assert.Null(audit.EntityId);
        Assert.Null(audit.IpAddress);
    }

    [Fact]
    public async Task Narrative_is_encrypted_in_the_database()
    {
        const string text = "Secret narrative about a knee injury.";
        await _h.Get<IReportService>().SubmitAsync(Input(narrative: text), null);

        var raw = await _h.Db.Database.SqlQueryRaw<string>("SELECT \"Narrative\" AS \"Value\" FROM \"Reports\"").SingleAsync();
        Assert.DoesNotContain("knee", raw);
        Assert.Equal(text, (await _h.Db.Reports.AsNoTracking().SingleAsync()).Narrative);   // transparently decrypted
    }

    public void Dispose() => _h.Dispose();
}

public class IncidentServiceTests : IDisposable
{
    private readonly TestHost _h = new();
    private static readonly DateOnly Day = new(2026, 5, 10);

    private async Task<Guid> NewIncident(string evt, int type = 1)
    {
        await _h.Get<IReportService>().SubmitAsync(new ReportInput(Guid.NewGuid(), 1, type, Severity.FirstAidOnly, Day, evt, 3, null, "Narrative text here."), null);
        return (await _h.Db.Incidents.AsNoTracking().OrderByDescending(i => i.CreatedUtc).FirstAsync(i => i.EventName == evt)).Id;
    }

    [Fact]
    public async Task Merge_moves_reports_and_retires_sources()
    {
        var a = await NewIncident("Event A");
        var b = await NewIncident("Event B");
        var result = await _h.Get<IIncidentService>().MergeAsync(a, new[] { b });
        Assert.True(result.Succeeded, result.Error);

        _h.Db.ChangeTracker.Clear();
        Assert.Equal(2, await _h.Db.Reports.CountAsync(r => r.IncidentId == a));
        var src = await _h.Db.Incidents.SingleAsync(i => i.Id == b);
        Assert.Equal(IncidentStatus.Retired, src.Status);
        Assert.Equal(a, src.MergedIntoIncidentId);
        Assert.Equal(1, (await _h.Get<IAnalyticsService>().GetAsync(new AnalyticsFilter())).Incidents);
    }

    [Fact]
    public async Task Cannot_merge_into_self_or_into_a_retired_incident()
    {
        var a = await NewIncident("Event A");
        var b = await NewIncident("Event B");
        var c = await NewIncident("Event C");
        var svc = _h.Get<IIncidentService>();

        Assert.False((await svc.MergeAsync(a, new[] { a })).Succeeded);

        await svc.MergeAsync(a, new[] { b });                 // b is now retired
        Assert.False((await svc.MergeAsync(b, new[] { c })).Succeeded);   // retired target -> would create a chain
        Assert.False((await svc.MergeAsync(c, new[] { b })).Succeeded);   // retired source -> already merged
    }

    [Fact]
    public async Task Database_rejects_a_self_referencing_merge_pointer()
    {
        var a = await NewIncident("Event A");
        var inc = await _h.Db.Incidents.SingleAsync(i => i.Id == a);
        inc.MergedIntoIncidentId = a;
        inc.Status = IncidentStatus.Retired;
        await Assert.ThrowsAsync<DbUpdateException>(() => _h.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task Split_moves_a_report_to_its_own_incident_and_recomputes_severity()
    {
        var svc = _h.Get<IReportService>();
        await svc.SubmitAsync(new ReportInput(Guid.NewGuid(), 1, 1, Severity.FirstAidOnly, Day, "Event A", 3, null, "First narrative."), null);
        await svc.SubmitAsync(new ReportInput(Guid.NewGuid(), 1, 1, Severity.Hospitalization, Day, "Event A", 3, null, "Second narrative."), null);
        var incident = await _h.Db.Incidents.Include(i => i.Reports).SingleAsync();
        var severe = incident.Reports.Single(r => r.Severity == Severity.Hospitalization);

        var result = await _h.Get<IIncidentService>().SplitReportAsync(severe.Id);
        Assert.True(result.Succeeded, result.Error);

        _h.Db.ChangeTracker.Clear();
        Assert.Equal(2, await _h.Db.Incidents.CountAsync());
        Assert.Equal(Severity.FirstAidOnly, (await _h.Db.Incidents.SingleAsync(i => i.Id == incident.Id)).Severity);
    }

    public void Dispose() => _h.Dispose();
}

public class UserAdminServiceTests : IDisposable
{
    private readonly TestHost _h = new();

    [Fact]
    public async Task Admin_can_suspend_a_plain_user_but_not_staff()
    {
        var admin = await _h.AddUser("admin@example.org", "Admin");
        var other = await _h.AddUser("admin2@example.org", "Admin");
        var user = await _h.AddUser("user@example.org", "User");
        var svc = _h.Get<IUserAdminService>();

        Assert.True((await svc.SuspendAsync(admin.Id, user.Id, "Harassment")).Succeeded);
        Assert.True((await _h.Users.FindByIdAsync(user.Id.ToString()))!.IsSuspended);
        Assert.False((await svc.SuspendAsync(admin.Id, other.Id, "x")).Succeeded);
        Assert.True((await svc.UnsuspendAsync(admin.Id, user.Id)).Succeeded);
    }

    [Fact]
    public async Task Only_super_admins_change_roles_and_nobody_changes_themselves()
    {
        var admin = await _h.AddUser("admin@example.org", "Admin");
        var sa = await _h.AddUser("sa@example.org", "SuperAdmin");
        var user = await _h.AddUser("user@example.org", "User");
        var svc = _h.Get<IUserAdminService>();

        Assert.False((await svc.SetRoleAsync(admin.Id, user.Id, "Admin")).Succeeded);
        Assert.False((await svc.SetRoleAsync(sa.Id, sa.Id, "User")).Succeeded);
        Assert.False((await svc.SuspendAsync(sa.Id, sa.Id, "x")).Succeeded);

        Assert.True((await svc.SetRoleAsync(sa.Id, user.Id, "Admin")).Succeeded);
        Assert.Equal("Admin", (await svc.GetAsync(user.Id))!.Role);
        Assert.Single(await _h.Users.GetRolesAsync(user));   // exactly one role at a time
    }

    public void Dispose() => _h.Dispose();
}
