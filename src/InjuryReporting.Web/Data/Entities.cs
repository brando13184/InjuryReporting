using Microsoft.AspNetCore.Identity;

namespace InjuryReporting.Web.Data;

public class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = "";
    /// <summary>The SCA kingdom in which this user holds membership (null = non-member / not stated).</summary>
    public int? KingdomId { get; set; }
    public Kingdom? Kingdom { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public bool IsSuspended { get; set; }
    public DateTime? SuspendedUtc { get; set; }
    public string? SuspensionReason { get; set; }
}

public enum Severity
{
    FirstAidOnly = 1,
    MedicalTreatment = 2,
    Hospitalization = 3,
    Fatality = 4
}

public enum IncidentStatus
{
    Open = 0,
    Reviewed = 1,
    /// <summary>Merged into another incident. Terminal: a retired incident is never a merge target.</summary>
    Retired = 2
}

public abstract class LookupEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class Kingdom : LookupEntity { }
public class Discipline : LookupEntity { }
public class InjuryType : LookupEntity { }

/// <summary>
/// The aggregated, canonical record used by analytics. One or more <see cref="InjuryReport"/>s
/// describing the same event are combined into a single incident so nothing is double counted.
/// </summary>
public class Incident
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int DisciplineId { get; set; }
    public Discipline Discipline { get; set; } = null!;
    public int InjuryTypeId { get; set; }
    public InjuryType InjuryType { get; set; } = null!;
    public Severity Severity { get; set; }
    public DateOnly InjuryDate { get; set; }
    public string EventName { get; set; } = "";
    public int EventKingdomId { get; set; }
    public Kingdom EventKingdom { get; set; } = null!;
    public int? InjuredKingdomId { get; set; }
    public Kingdom? InjuredKingdom { get; set; }

    /// <summary>Hash of all identifying fields; equal keys are treated as the same incident.</summary>
    public string MatchKey { get; set; } = "";
    /// <summary>Hash of discipline/date/event/kingdom only; equal loose keys are "possible duplicates".</summary>
    public string LooseKey { get; set; } = "";

    public IncidentStatus Status { get; set; } = IncidentStatus.Open;
    public bool NeedsReview { get; set; }
    public Guid? MergedIntoIncidentId { get; set; }
    public Incident? MergedInto { get; set; }

    /// <summary>Reviewer notes. Encrypted at rest (PHI).</summary>
    public string? ReviewerNotes { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public List<InjuryReport> Reports { get; set; } = new();
}

/// <summary>An individual submission. Immutable once stored; only its incident link can change.</summary>
public class InjuryReport
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IncidentId { get; set; }
    public Incident Incident { get; set; } = null!;

    /// <summary>Null for anonymous reports. No other identifying data is ever stored for those.</summary>
    public Guid? ReporterUserId { get; set; }
    public ApplicationUser? Reporter { get; set; }

    public int DisciplineId { get; set; }
    public Discipline Discipline { get; set; } = null!;
    public int InjuryTypeId { get; set; }
    public InjuryType InjuryType { get; set; } = null!;
    public Severity Severity { get; set; }
    public DateOnly InjuryDate { get; set; }
    public string EventName { get; set; } = "";
    public int EventKingdomId { get; set; }
    public Kingdom EventKingdom { get; set; } = null!;
    public int? InjuredKingdomId { get; set; }
    public Kingdom? InjuredKingdom { get; set; }

    /// <summary>Short narrative. Encrypted at rest (PHI).</summary>
    public string Narrative { get; set; } = "";

    /// <summary>Random per-form token: makes a resubmit/replay idempotent. Not linked to the person.</summary>
    public Guid SubmissionToken { get; set; }
    public string MatchKey { get; set; } = "";
    public string LooseKey { get; set; } = "";
    public bool AutoLinked { get; set; }
    public bool PossibleDuplicate { get; set; }

    /// <summary>Anonymous reports are truncated to the day so they cannot be correlated with logs.</summary>
    public DateTime SubmittedUtc { get; set; }
}

public class AuditLogEntry
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public Guid? ActorUserId { get; set; }
    public string? ActorEmail { get; set; }
    public string Action { get; set; } = "";
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    /// <summary>Operational detail only. Never PHI / narrative content.</summary>
    public string? Detail { get; set; }
    public string? IpAddress { get; set; }
}
