using InjuryReporting.Web.Data;
using InjuryReporting.Web.Security;
using InjuryReporting.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InjuryReporting.Tests;

public sealed class NullEmailSender : IAppEmailSender
{
    public List<(string To, string Subject)> Sent { get; } = new();
    public Task SendAsync(string toEmail, string subject, string htmlBody) { Sent.Add((toEmail, subject)); return Task.CompletedTask; }
}

/// <summary>Real EF model + constraints on in-memory SQLite, with the same services the app registers.</summary>
public sealed class TestHost : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _root;
    private readonly IServiceScope _scope;

    /// <param name="postgresConnection">When set, runs against that (already migrated) Postgres database instead of SQLite.</param>
    public TestHost(string? postgresConnection = null)
    {
        _connection.Open();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDataProtectionProvider, EphemeralDataProtectionProvider>();
        services.AddDbContext<AppDbContext>(o =>
        {
            if (postgresConnection != null) o.UseNpgsql(postgresConnection); else o.UseSqlite(_connection);
        });
        services.AddIdentity<ApplicationUser, IdentityRole<Guid>>(o =>
            {
                o.Password.RequiredLength = 14;
                o.User.RequireUniqueEmail = true;
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders();
        services.AddHttpContextAccessor();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IReportService, ReportService>();
        services.AddScoped<IIncidentService, IncidentService>();
        services.AddScoped<IAnalyticsService, AnalyticsService>();
        services.AddScoped<IUserAdminService, UserAdminService>();
        services.AddScoped<IUserDataService, UserDataService>();
        services.AddSingleton<IAppEmailSender, NullEmailSender>();
        _root = services.BuildServiceProvider();
        _scope = _root.CreateScope();
        if (postgresConnection == null) Db.Database.EnsureCreated();
        foreach (var r in AppRoles.All)
            if (!Roles.RoleExistsAsync(r).GetAwaiter().GetResult())
                Roles.CreateAsync(new IdentityRole<Guid>(r)).GetAwaiter().GetResult();
    }

    public T Get<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();
    public AppDbContext Db => Get<AppDbContext>();
    public RoleManager<IdentityRole<Guid>> Roles => Get<RoleManager<IdentityRole<Guid>>>();
    public UserManager<ApplicationUser> Users => Get<UserManager<ApplicationUser>>();

    public async Task<ApplicationUser> AddUser(string email, string role)
    {
        var u = new ApplicationUser { Id = Guid.NewGuid(), UserName = email, Email = email, EmailConfirmed = true, DisplayName = email };
        var created = await Users.CreateAsync(u, "CorrectHorse-Battery9Staple");
        Assert.True(created.Succeeded, string.Join(";", created.Errors.Select(e => e.Description)));
        await Users.AddToRoleAsync(u, role);
        return u;
    }

    public void Dispose()
    {
        _scope.Dispose();
        _root.Dispose();
        _connection.Dispose();
    }
}
