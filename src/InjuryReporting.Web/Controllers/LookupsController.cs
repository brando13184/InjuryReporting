using InjuryReporting.Web.Data;
using InjuryReporting.Web.Services;
using InjuryReporting.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InjuryReporting.Web.Controllers;

/// <summary>
/// Staff-managed pick lists (kingdoms, disciplines, injury types). Entries are renamed or deactivated, never
/// deleted, so historical reports keep their meaning; inactive entries just stop being offered on the form.
/// </summary>
public class LookupsController : StaffController
{
    private static readonly Dictionary<string, string> Kinds = new()
    {
        ["disciplines"] = "Disciplines",
        ["injury-types"] = "Injury types",
        ["kingdoms"] = "Kingdoms"
    };

    private readonly AppDbContext _db;
    private readonly IAuditService _audit;
    public LookupsController(AppDbContext db, IAuditService audit) { _db = db; _audit = audit; }

    private IQueryable<LookupEntity> Set(string kind) => kind switch
    {
        "disciplines" => _db.Disciplines,
        "injury-types" => _db.InjuryTypes,
        "kingdoms" => _db.Kingdoms,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public async Task<IActionResult> Index(string kind = "disciplines")
    {
        if (!Kinds.ContainsKey(kind)) return NotFound();
        var items = await Set(kind).AsNoTracking().OrderByDescending(x => x.IsActive).ThenBy(x => kind == "kingdoms" ? x.Name : "").ThenBy(x => x.Id).ToListAsync();
        return View(new LookupListModel { Kind = kind, KindTitle = Kinds[kind], Kinds = Kinds, Items = items });
    }

    [HttpPost]
    public async Task<IActionResult> Add(string kind, string name)
    {
        if (!Kinds.ContainsKey(kind)) return NotFound();
        name = (name ?? "").Trim();
        if (name.Length is 0 or > 100) return Back(kind, "Enter a name of up to 100 characters.", false);
        if (await Set(kind).AnyAsync(x => x.Name.ToLower() == name.ToLower())) return Back(kind, "That name already exists.", false);

        LookupEntity entity = kind switch
        {
            "disciplines" => new Discipline { Name = name },
            "injury-types" => new InjuryType { Name = name },
            _ => new Kingdom { Name = name }
        };
        _db.Add(entity);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("lookup.added", kind, entity.Id.ToString(), name);
        return Back(kind, $"Added \"{name}\".", true);
    }

    [HttpPost]
    public async Task<IActionResult> Rename(string kind, int id, string name)
    {
        if (!Kinds.ContainsKey(kind)) return NotFound();
        name = (name ?? "").Trim();
        var item = await Set(kind).FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();
        if (name.Length is 0 or > 100) return Back(kind, "Enter a name of up to 100 characters.", false);
        if (await Set(kind).AnyAsync(x => x.Id != id && x.Name.ToLower() == name.ToLower())) return Back(kind, "That name already exists.", false);

        var old = item.Name;
        item.Name = name;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("lookup.renamed", kind, id.ToString(), $"{old} -> {name}");
        return Back(kind, "Renamed.", true);
    }

    [HttpPost]
    public async Task<IActionResult> Toggle(string kind, int id)
    {
        if (!Kinds.ContainsKey(kind)) return NotFound();
        var item = await Set(kind).FirstOrDefaultAsync(x => x.Id == id);
        if (item == null) return NotFound();
        if (item.IsActive && await Set(kind).CountAsync(x => x.IsActive) <= 1)
            return Back(kind, "At least one entry must stay active.", false);

        item.IsActive = !item.IsActive;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(item.IsActive ? "lookup.activated" : "lookup.deactivated", kind, id.ToString(), item.Name);
        return Back(kind, item.IsActive ? "Activated." : "Deactivated. It's no longer offered on the report form; existing reports are unchanged.", true);
    }

    private IActionResult Back(string kind, string message, bool ok)
    {
        TempData[ok ? "Success" : "Warning"] = message;
        return RedirectToAction(nameof(Index), new { kind });
    }
}
