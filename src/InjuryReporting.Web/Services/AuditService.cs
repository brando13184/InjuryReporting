using System.Security.Claims;
using InjuryReporting.Web.Data;

namespace InjuryReporting.Web.Services;

public interface IAuditService
{
    /// <summary>Records an event for the current request's user (or no actor when anonymous).</summary>
    Task LogAsync(string action, string? entityType = null, string? entityId = null, string? detail = null, bool includeActor = true, CancellationToken ct = default);

    /// <summary>Records an event for an explicit actor (e.g. a failed login where there is no signed-in principal).</summary>
    Task LogForAsync(Guid? actorUserId, string? actorEmail, string action, string? entityType = null, string? entityId = null, string? detail = null, CancellationToken ct = default);
}

/// <summary>
/// Append-only audit trail (SOC2 CC7). Never pass narrative text or other PHI as <c>detail</c>.
/// For anonymous submissions callers pass <c>includeActor: false</c> and no entity id, so the
/// trail cannot be used to de-anonymise a report.
/// </summary>
public class AuditService : IAuditService
{
    private readonly AppDbContext _db;
    private readonly IHttpContextAccessor _http;

    public AuditService(AppDbContext db, IHttpContextAccessor http) { _db = db; _http = http; }

    public Task LogAsync(string action, string? entityType = null, string? entityId = null, string? detail = null, bool includeActor = true, CancellationToken ct = default)
    {
        var user = _http.HttpContext?.User;
        Guid? id = null;
        string? email = null;
        if (includeActor && user?.Identity?.IsAuthenticated == true)
        {
            if (Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var g)) id = g;
            email = user.FindFirstValue(ClaimTypes.Email) ?? user.Identity.Name;
        }
        return Write(id, email, action, entityType, entityId, detail, includeActor, ct);
    }

    public Task LogForAsync(Guid? actorUserId, string? actorEmail, string action, string? entityType = null, string? entityId = null, string? detail = null, CancellationToken ct = default)
        => Write(actorUserId, actorEmail, action, entityType, entityId, detail, true, ct);

    private async Task Write(Guid? actorId, string? email, string action, string? type, string? entityId, string? detail, bool includeIp, CancellationToken ct)
    {
        _db.AuditLog.Add(new AuditLogEntry
        {
            TimestampUtc = DateTime.UtcNow,
            ActorUserId = actorId,
            ActorEmail = email,
            Action = action,
            EntityType = type,
            EntityId = entityId,
            Detail = detail is { Length: > 1000 } ? detail[..1000] : detail,
            IpAddress = includeIp ? _http.HttpContext?.Connection.RemoteIpAddress?.ToString() : null
        });
        await _db.SaveChangesAsync(ct);
    }
}
