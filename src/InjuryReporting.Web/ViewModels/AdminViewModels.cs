using System.ComponentModel.DataAnnotations;
using InjuryReporting.Web.Data;
using InjuryReporting.Web.Services;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace InjuryReporting.Web.ViewModels;

public class IncidentListFilter
{
    public string? Status { get; set; }          // all | review | open | reviewed | merged
    public int? DisciplineId { get; set; }
    public int? EventKingdomId { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public string? Q { get; set; }
    public int Page { get; set; } = 1;
}

public class IncidentListRow
{
    public Guid Id { get; init; }
    public DateOnly InjuryDate { get; init; }
    public string Discipline { get; init; } = "";
    public string InjuryType { get; init; } = "";
    public string Severity { get; init; } = "";
    public string EventName { get; init; } = "";
    public string EventKingdom { get; init; } = "";
    public int ReportCount { get; init; }
    public IncidentStatus Status { get; init; }
    public bool NeedsReview { get; init; }
}

public class IncidentListModel
{
    public IncidentListFilter Filter { get; init; } = new();
    public List<IncidentListRow> Rows { get; init; } = new();
    public int Total { get; init; }
    public int PageSize { get; init; }
    public LookupLists Lookups { get; init; } = new();
}

public class IncidentDetailsModel
{
    public Incident Incident { get; init; } = null!;
    public List<IncidentListRow> PossibleDuplicates { get; init; } = new();
    public Incident? MergedInto { get; init; }
}

public class IncidentEditModel
{
    public Guid Id { get; set; }

    [Required] public int? DisciplineId { get; set; }
    [Required] public int? InjuryTypeId { get; set; }
    [Required] public Severity? Severity { get; set; }
    [Required, DataType(DataType.Date)] public DateOnly? InjuryDate { get; set; }
    [Required, StringLength(200)] public string EventName { get; set; } = "";
    [Required] public int? EventKingdomId { get; set; }
    public int? InjuredKingdomId { get; set; }

    [StringLength(2000), DataType(DataType.MultilineText)]
    [Display(Name = "Reviewer notes (encrypted, staff only)")]
    public string? ReviewerNotes { get; set; }

    [Display(Name = "Mark as reviewed")]
    public bool MarkReviewed { get; set; }

    public LookupLists Lookups { get; set; } = new();
}

public class MergeModel
{
    [Required] public List<Guid> Ids { get; set; } = new();
    public Guid? TargetId { get; set; }
    public List<IncidentListRow> Candidates { get; set; } = new();
}

public class AnalyticsPageModel
{
    public AnalyticsFilter Filter { get; init; } = new();
    public AnalyticsResult Result { get; init; } = new();
    public LookupLists Lookups { get; init; } = new();
}

public class UserListModel
{
    public List<UserRow> Rows { get; init; } = new();
    public string? Search { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int Total { get; init; }
}

public class UserDetailsModel
{
    public UserRow User { get; init; } = null!;
    public string? SuspensionReason { get; init; }
    public bool ViewerIsSuperAdmin { get; init; }
    public bool IsSelf { get; init; }
}

public class DeleteUserModel
{
    [Required] public Guid Id { get; set; }

    [Required, Display(Name = "Type the user's email to confirm")]
    public string ConfirmEmail { get; set; } = "";

    [Required, DataType(DataType.Password), Display(Name = "Your password")]
    public string AdminPassword { get; set; } = "";

    /// <summary>Checkbox; false when absent, so a missing value never deletes reports.</summary>
    public bool DeleteReports { get; set; }
}

public class SuspendModel
{
    public Guid Id { get; set; }
    [Required, StringLength(500)] public string Reason { get; set; } = "";
}

public class LookupListModel
{
    public string Kind { get; init; } = "";
    public string KindTitle { get; init; } = "";
    public Dictionary<string, string> Kinds { get; init; } = new();
    public List<LookupEntity> Items { get; init; } = new();
}

public class AuditListModel
{
    public List<AuditLogEntry> Rows { get; init; } = new();
    public string? Action { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int Total { get; init; }
}
