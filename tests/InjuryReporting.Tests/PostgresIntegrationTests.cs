using InjuryReporting.Web.Data;
using InjuryReporting.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace InjuryReporting.Tests;

/// <summary>Skipped unless TEST_PG_ADMIN holds a connection string for a role that can CREATE DATABASE.</summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TEST_PG_ADMIN")))
            Skip = "Set TEST_PG_ADMIN to a Postgres admin connection string to run these.";
    }
}

/// <summary>Creates a throwaway database, applies the real EF migrations (incl. the audit triggers), drops it after.</summary>
public sealed class PgDatabase : IAsyncLifetime
{
    private readonly string? _admin = Environment.GetEnvironmentVariable("TEST_PG_ADMIN");
    private readonly string _name = "ir_test_" + Guid.NewGuid().ToString("N")[..12];
    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_admin)) return;
        await using (var c = new NpgsqlConnection(_admin))
        {
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{_name}\"", c);
            await cmd.ExecuteNonQueryAsync();
        }
        ConnectionString = new NpgsqlConnectionStringBuilder(_admin) { Database = _name }.ConnectionString;
        var opts = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options;
        await using var db = new AppDbContext(opts, new EphemeralDataProtectionProvider());
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(_admin) || ConnectionString == "") return;
        NpgsqlConnection.ClearAllPools();
        await using var c = new NpgsqlConnection(_admin);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", c);
        await cmd.ExecuteNonQueryAsync();
    }
}

public class PostgresIntegrationTests : IClassFixture<PgDatabase>
{
    private readonly PgDatabase _pg;
    private static readonly DateOnly Day = new(2026, 3, 14);
    public PostgresIntegrationTests(PgDatabase pg) => _pg = pg;

    private static ReportInput Input(string evt, int type = 1, Severity sev = Severity.FirstAidOnly, string narrative = "Postgres narrative text.") =>
        new(Guid.NewGuid(), 1, type, sev, Day, evt, 3, null, narrative);

    [PgFact]
    public async Task Migrations_apply_and_seed_the_pick_lists()
    {
        using var h = new TestHost(_pg.ConnectionString);
        Assert.Equal(20, await h.Db.Kingdoms.CountAsync());
        Assert.Equal(5, await h.Db.Disciplines.CountAsync());
        Assert.Equal(12, await h.Db.InjuryTypes.CountAsync());
    }

    [PgFact]
    public async Task Report_flow_dashboard_queries_and_exports_work_on_postgres()
    {
        using var h = new TestHost(_pg.ConnectionString);
        var svc = h.Get<IReportService>();
        Assert.Equal(SubmitOutcome.Created, (await svc.SubmitAsync(Input("Pg Flow War"), null)).Outcome);
        Assert.Equal(SubmitOutcome.CombinedWithExisting, (await svc.SubmitAsync(Input("pg flow war", sev: Severity.Hospitalization), null)).Outcome);

        var a = h.Get<IAnalyticsService>();
        var r = await a.GetAsync(new AnalyticsFilter { From = new DateOnly(2026, 1, 1), To = new DateOnly(2026, 12, 31) });
        Assert.True(r.Incidents >= 1);
        Assert.True(r.Reports >= 2);
        Assert.Contains(r.MonthlyTrend, b => b.Label == "2026-03" && b.Count >= 1);
        Assert.Contains(r.YearMonthGrid, y => y.Year == 2026 && y.Months[2] >= 1);
        Assert.NotEmpty(r.CrossTab);
        Assert.NotEmpty(await a.ExportAsync(new AnalyticsFilter()));
        Assert.NotEmpty(await a.SummaryRowsAsync(new AnalyticsFilter()));
    }

    [PgFact]
    public async Task Narratives_are_ciphertext_in_the_database_and_decrypt_through_ef()
    {
        using var h = new TestHost(_pg.ConnectionString);
        await h.Get<IReportService>().SubmitAsync(Input("Cipher War", narrative: "Plaintext-marker-xyzzy knee injury."), null);
        var raw = await h.Db.Database.SqlQueryRaw<string>("SELECT \"Narrative\" AS \"Value\" FROM \"Reports\" ORDER BY \"SubmittedUtc\" DESC LIMIT 50").ToListAsync();
        Assert.DoesNotContain(raw, s => s.Contains("xyzzy"));
        Assert.Contains(await h.Db.Reports.AsNoTracking().Select(x => x.Narrative).ToListAsync(), s => s.Contains("xyzzy"));
    }

    [PgFact]
    public async Task Audit_log_is_append_only_and_database_rejects_self_merge_and_replayed_tokens()
    {
        using var h = new TestHost(_pg.ConnectionString);
        var svc = h.Get<IReportService>();
        var token = Guid.NewGuid();
        await svc.SubmitAsync(Input("Guard War") with { SubmissionToken = token }, null);
        Assert.Equal(SubmitOutcome.AlreadySubmitted, (await svc.SubmitAsync(Input("Guard War") with { SubmissionToken = token }, null)).Outcome);

        await h.Get<IAuditService>().LogAsync("test.row");
        await Assert.ThrowsAnyAsync<Exception>(() => h.Db.Database.ExecuteSqlRawAsync("UPDATE \"AuditLog\" SET \"Detail\" = 'x'"));
        await Assert.ThrowsAnyAsync<Exception>(() => h.Db.Database.ExecuteSqlRawAsync("DELETE FROM \"AuditLog\""));
        await Assert.ThrowsAnyAsync<Exception>(() => h.Db.Database.ExecuteSqlRawAsync("TRUNCATE \"AuditLog\""));

        await Assert.ThrowsAnyAsync<Exception>(() => h.Db.Database.ExecuteSqlRawAsync(
            "UPDATE \"Incidents\" SET \"MergedIntoIncidentId\" = \"Id\", \"Status\" = 2"));
        // A retired incident must have a target and a live one must not (ck_incident_retired_has_target).
        await Assert.ThrowsAnyAsync<Exception>(() => h.Db.Database.ExecuteSqlRawAsync("UPDATE \"Incidents\" SET \"Status\" = 2"));
    }

    [PgFact]
    public async Task Audit_erasure_may_only_blank_personal_data_and_full_erasure_works_on_postgres()
    {
        using var h = new TestHost(_pg.ConnectionString);
        await h.Get<IAuditService>().LogForAsync(Guid.NewGuid(), "pii@example.org", "test.pii", detail: "keep me");

        // Allowed: blank the actor e-mail and IP, and nothing else.
        Assert.True(await h.Db.Database.ExecuteSqlRawAsync("UPDATE \"AuditLog\" SET \"ActorEmail\" = NULL, \"IpAddress\" = NULL WHERE \"Action\" = 'test.pii'") >= 1);
        // Still blocked: changing anything else, setting a non-NULL value, deleting.
        await Assert.ThrowsAnyAsync<Exception>(() => h.Db.Database.ExecuteSqlRawAsync("UPDATE \"AuditLog\" SET \"Detail\" = 'tampered' WHERE \"Action\" = 'test.pii'"));
        await Assert.ThrowsAnyAsync<Exception>(() => h.Db.Database.ExecuteSqlRawAsync("UPDATE \"AuditLog\" SET \"ActorEmail\" = 'forged@example.org' WHERE \"Action\" = 'test.pii'"));
        await Assert.ThrowsAnyAsync<Exception>(() => h.Db.Database.ExecuteSqlRawAsync("UPDATE \"AuditLog\" SET \"Action\" = 'x', \"ActorEmail\" = NULL, \"IpAddress\" = NULL WHERE \"Action\" = 'test.pii'"));
        await Assert.ThrowsAnyAsync<Exception>(() => h.Db.Database.ExecuteSqlRawAsync("DELETE FROM \"AuditLog\" WHERE \"Action\" = 'test.pii'"));

        // Full erasure through the service, against real Postgres (transaction, FK set-null, cascade, trigger path).
        var user = await h.AddUser("pg-erase@example.org", "User");
        var svc = h.Get<IReportService>();
        await svc.SubmitAsync(Input("Pg Erase Solo"), user.Id);
        await svc.SubmitAsync(Input("Pg Erase Shared", sev: Severity.Hospitalization), user.Id);
        await svc.SubmitAsync(Input("Pg Erase Shared"), null);
        await h.Get<IAuditService>().LogForAsync(user.Id, user.Email, "login.succeeded");

        var result = await h.Get<IUserDataService>().EraseAsync(user.Id, user.Id, deleteReports: true);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(2, result.ReportsDeleted);
        Assert.Equal(1, result.IncidentsRemoved);
        Assert.True(result.AuditRowsScrubbed >= 1);

        h.Db.ChangeTracker.Clear();
        Assert.False(await h.Db.Users.AnyAsync(u => u.Id == user.Id));
        Assert.False(await h.Db.AuditLog.AnyAsync(a => a.ActorEmail == "pg-erase@example.org"));
        Assert.True(await h.Db.AuditLog.AnyAsync(a => a.Action == "account.erased_self" && a.ActorUserId == user.Id));
        Assert.Equal(Severity.FirstAidOnly, (await h.Db.Incidents.SingleAsync(i => i.EventName == "Pg Erase Shared")).Severity);
    }

    [PgFact]
    public async Task Merge_and_split_work_on_postgres()
    {
        using var h = new TestHost(_pg.ConnectionString);
        var svc = h.Get<IReportService>();
        await svc.SubmitAsync(Input("Merge War A", type: 1), null);
        await svc.SubmitAsync(Input("Merge War B", type: 2), null);
        var ids = await h.Db.Incidents.AsNoTracking().Where(i => i.EventName.StartsWith("Merge War")).Select(i => i.Id).ToListAsync();
        Assert.Equal(2, ids.Count);

        var inc = h.Get<IIncidentService>();
        Assert.True((await inc.MergeAsync(ids[0], new[] { ids[1] })).Succeeded);
        Assert.False((await inc.MergeAsync(ids[1], new[] { ids[0] })).Succeeded);   // retired target

        h.Db.ChangeTracker.Clear();
        var reportId = (await h.Db.Reports.AsNoTracking().FirstAsync(r => r.IncidentId == ids[0] && r.EventName == "Merge War B")).Id;
        Assert.True((await inc.SplitReportAsync(reportId)).Succeeded);
    }

    [PgFact]
    public async Task Deleting_a_user_keeps_their_reports_but_unlinks_them()
    {
        using var h = new TestHost(_pg.ConnectionString);
        var user = await h.AddUser("pg-leaver@example.org", "User");
        await h.Get<IReportService>().SubmitAsync(Input("Leaver Pg War"), user.Id);
        Assert.True((await h.Users.DeleteAsync(user)).Succeeded);

        h.Db.ChangeTracker.Clear();
        var report = await h.Db.Reports.AsNoTracking().SingleAsync(r => r.EventName == "Leaver Pg War");
        Assert.Null(report.ReporterUserId);
    }
}
