using System.ComponentModel.DataAnnotations;
using InjuryReporting.Web.Data;
using InjuryReporting.Web.Services;

namespace InjuryReporting.Web.ViewModels;

public class ReportFormModel : IValidatableObject
{
    public static readonly DateOnly EarliestDate = new(1966, 5, 1);   // SCA founding

    /// <summary>Random per page-load; makes a double-submit or replay a no-op.</summary>
    public Guid SubmissionToken { get; set; } = Guid.NewGuid();

    [Required(ErrorMessage = "Choose a discipline.")]
    [Display(Name = "Discipline")]
    public int? DisciplineId { get; set; }

    [Required(ErrorMessage = "Choose an injury type.")]
    [Display(Name = "Injury type")]
    public int? InjuryTypeId { get; set; }

    [Required(ErrorMessage = "Choose a severity.")]
    [Display(Name = "Severity")]
    public Severity? Severity { get; set; }

    [Required(ErrorMessage = "Pick the date of the injury.")]
    [DataType(DataType.Date)]
    [Display(Name = "Date of injury")]
    public DateOnly? InjuryDate { get; set; }

    [Required, StringLength(200)]
    [Display(Name = "Event name")]
    public string EventName { get; set; } = "";

    [Required(ErrorMessage = "Choose the kingdom where the event took place.")]
    [Display(Name = "Kingdom where it occurred")]
    public int? EventKingdomId { get; set; }

    [Display(Name = "Injured person's kingdom membership")]
    public int? InjuredKingdomId { get; set; }

    [Required, StringLength(2000, MinimumLength = 10, ErrorMessage = "Please write between 10 and 2000 characters.")]
    [DataType(DataType.MultilineText)]
    [Display(Name = "Short narrative")]
    public string Narrative { get; set; } = "";

    /// <summary>Only offered to signed-in users. When ticked, nothing links the report to the account.</summary>
    [Display(Name = "Submit anonymously (do not link this report to my account)")]
    public bool SubmitAnonymously { get; set; }

    /// <summary>Honeypot: hidden from people, filled in by bots.</summary>
    public string? Website { get; set; }

    public LookupLists Lookups { get; set; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext ctx)
    {
        if (InjuryDate is { } d)
        {
            if (d > DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)))
                yield return new ValidationResult("The injury date cannot be in the future.", new[] { nameof(InjuryDate) });
            if (d < EarliestDate)
                yield return new ValidationResult("That date is too far in the past.", new[] { nameof(InjuryDate) });
        }
    }

    public ReportInput ToInput() => new(SubmissionToken, DisciplineId!.Value, InjuryTypeId!.Value, Severity!.Value,
        InjuryDate!.Value, EventName, EventKingdomId!.Value, InjuredKingdomId, Narrative);
}

public class MyReportRow
{
    public Guid Id { get; init; }
    public DateOnly InjuryDate { get; init; }
    public string Discipline { get; init; } = "";
    public string InjuryType { get; init; } = "";
    public string EventName { get; init; } = "";
    public DateTime SubmittedUtc { get; init; }
}

public class ReportDetailsModel
{
    public InjuryReport Report { get; init; } = null!;
}
