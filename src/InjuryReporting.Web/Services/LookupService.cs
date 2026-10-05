using InjuryReporting.Web.Data;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace InjuryReporting.Web.Services;

public class LookupLists
{
    public List<SelectListItem> Disciplines { get; init; } = new();
    public List<SelectListItem> InjuryTypes { get; init; } = new();
    public List<SelectListItem> Kingdoms { get; init; } = new();
    public List<SelectListItem> Severities { get; init; } = new();
}

public class LookupService
{
    private readonly AppDbContext _db;
    public LookupService(AppDbContext db) => _db = db;

    /// <param name="includeInactive">Staff screens pass true so retired list entries still display on old records
    /// and in filters; public forms only offer active entries.</param>
    public async Task<LookupLists> GetAsync(bool includeInactive = false)
    {
        static List<SelectListItem> Map(IEnumerable<LookupEntity> e) =>
            e.Select(x => new SelectListItem(x.IsActive ? x.Name : x.Name + " (inactive)", x.Id.ToString())).ToList();

        var disciplines = await _db.Disciplines.AsNoTracking().Where(x => includeInactive || x.IsActive).OrderBy(x => x.Id).ToListAsync();
        var types = await _db.InjuryTypes.AsNoTracking().Where(x => includeInactive || x.IsActive).OrderBy(x => x.Id).ToListAsync();
        var kingdoms = await _db.Kingdoms.AsNoTracking().Where(x => includeInactive || x.IsActive).OrderBy(x => x.Name).ToListAsync();
        return new LookupLists
        {
            Disciplines = Map(disciplines),
            InjuryTypes = Map(types),
            Kingdoms = Map(kingdoms),
            Severities = Enum.GetValues<Severity>().Select(s => new SelectListItem(AnalyticsService.Pretty(s), s.ToString())).ToList()
        };
    }
}
