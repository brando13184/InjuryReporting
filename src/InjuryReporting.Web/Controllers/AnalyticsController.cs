using System.Text;
using InjuryReporting.Web.Services;
using InjuryReporting.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace InjuryReporting.Web.Controllers;

public class AnalyticsController : StaffController
{
    private readonly IAnalyticsService _analytics;
    private readonly LookupService _lookups;
    private readonly IAuditService _audit;

    public AnalyticsController(IAnalyticsService analytics, LookupService lookups, IAuditService audit)
    {
        _analytics = analytics; _lookups = lookups; _audit = audit;
    }

    public async Task<IActionResult> Index(AnalyticsFilter filter) =>
        View(new AnalyticsPageModel { Filter = filter, Result = await _analytics.GetAsync(filter), Lookups = await _lookups.GetAsync() });

    /// <summary>De-identified incident-level export. Narratives and reporter identities are never included.</summary>
    public async Task<IActionResult> ExportCsv(AnalyticsFilter filter)
    {
        var rows = await _analytics.ExportAsync(filter);
        await _audit.LogAsync("analytics.exported", detail: $"{rows.Count} incident row(s)");

        var sb = new StringBuilder("IncidentId,InjuryDate,Discipline,InjuryType,Severity,EventName,EventKingdom,InjuredKingdomMembership,ReportCount,Status\r\n");
        foreach (var r in rows)
            sb.Append(string.Join(',', Csv(r.Id.ToString()), Csv(r.InjuryDate.ToString("yyyy-MM-dd")), Csv(r.Discipline), Csv(r.InjuryType),
                Csv(r.Severity), Csv(r.EventName), Csv(r.EventKingdom), Csv(r.InjuredKingdom), r.ReportCount, Csv(r.Status))).Append("\r\n");
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", $"incidents-{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    /// <summary>Quotes a field and neutralises spreadsheet formula injection (=, +, -, @ prefixes).</summary>
    internal static string Csv(string value)
    {
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
