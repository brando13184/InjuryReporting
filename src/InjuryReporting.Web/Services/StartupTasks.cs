using InjuryReporting.Web.Data;
using InjuryReporting.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace InjuryReporting.Web.Services;

/// <summary>
/// Runs once at startup: optional migrations, role creation and first Super Admin bootstrap.
/// Implemented as a hosted service (not inline in Program.cs) so EF design-time tooling never executes it.
/// </summary>
public class StartupTasks : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _config;
    private readonly ILogger<StartupTasks> _log;

    public StartupTasks(IServiceProvider services, IConfiguration config, ILogger<StartupTasks> log)
    {
        _services = services; _config = config; _log = log;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (_config.GetValue<bool>("Database:MigrateOnStartup"))
            await db.Database.MigrateAsync(ct);

        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var r in AppRoles.All)
            if (!await roles.RoleExistsAsync(r))
                await roles.CreateAsync(new IdentityRole<Guid>(r));

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        if ((await users.GetUsersInRoleAsync(AppRoles.SuperAdmin)).Count > 0) return;

        var email = _config["Seed:SuperAdminEmail"];
        var password = _config["Seed:SuperAdminPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            _log.LogWarning("No Super Admin exists. Set Seed:SuperAdminEmail and Seed:SuperAdminPassword (user-secrets / env vars) and restart to create the first one.");
            return;
        }

        var user = await users.FindByEmailAsync(email);
        if (user == null)
        {
            user = new ApplicationUser
            {
                Id = Guid.NewGuid(), UserName = email, Email = email, EmailConfirmed = true, DisplayName = "Super Admin"
            };
            var created = await users.CreateAsync(user, password);
            if (!created.Succeeded)
            {
                _log.LogError("Could not create the seed Super Admin: {Errors}", string.Join("; ", created.Errors.Select(e => e.Description)));
                return;
            }
        }
        await users.AddToRoleAsync(user, AppRoles.SuperAdmin);
        _log.LogWarning("Seed Super Admin created. Remove Seed:SuperAdminPassword from configuration now and enrol in two-factor authentication at first sign-in.");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
