using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace InjuryReporting.Web.Data;

public class AppDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>, IDataProtectionKeyContext
{
    private readonly IDataProtectionProvider _dataProtection;

    public AppDbContext(DbContextOptions<AppDbContext> options, IDataProtectionProvider dataProtection)
        : base(options) => _dataProtection = dataProtection;

    public DbSet<Kingdom> Kingdoms => Set<Kingdom>();
    public DbSet<Discipline> Disciplines => Set<Discipline>();
    public DbSet<InjuryType> InjuryTypes => Set<InjuryType>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<InjuryReport> Reports => Set<InjuryReport>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // Field-level (application) encryption of free text, which is treated as PHI.
        var protector = _dataProtection.CreateProtector("InjuryReporting.PHI.v1");
        var encrypt = new ValueConverter<string, string>(v => protector.Protect(v), v => protector.Unprotect(v));
        var encryptNullable = new ValueConverter<string?, string?>(
            v => v == null ? null : protector.Protect(v),
            v => v == null ? null : protector.Unprotect(v));

        b.Entity<ApplicationUser>(e =>
        {
            e.Property(u => u.DisplayName).HasMaxLength(100);
            e.Property(u => u.SuspensionReason).HasMaxLength(500);
            e.HasOne(u => u.Kingdom).WithMany().HasForeignKey(u => u.KingdomId).OnDelete(DeleteBehavior.Restrict);
        });

        foreach (var t in new[] { typeof(Kingdom), typeof(Discipline), typeof(InjuryType) })
        {
            b.Entity(t, e =>
            {
                e.Property(nameof(LookupEntity.Name)).HasMaxLength(100).IsRequired();
                e.HasIndex(nameof(LookupEntity.Name)).IsUnique();
            });
        }

        b.Entity<Incident>(e =>
        {
            e.Property(i => i.EventName).HasMaxLength(200).IsRequired();
            e.Property(i => i.MatchKey).HasMaxLength(64).IsRequired();
            e.Property(i => i.LooseKey).HasMaxLength(64).IsRequired();
            e.Property(i => i.ReviewerNotes).HasConversion(encryptNullable);
            e.Property(i => i.InjuryDate).HasColumnType("date");
            e.HasOne(i => i.Discipline).WithMany().HasForeignKey(i => i.DisciplineId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(i => i.InjuryType).WithMany().HasForeignKey(i => i.InjuryTypeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(i => i.EventKingdom).WithMany().HasForeignKey(i => i.EventKingdomId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(i => i.InjuredKingdom).WithMany().HasForeignKey(i => i.InjuredKingdomId).OnDelete(DeleteBehavior.Restrict);
            // Tombstone pointer for merges. Targets must be active, so chains/cycles cannot form;
            // the check constraints are the last line of defence in the database itself.
            e.HasOne(i => i.MergedInto).WithMany().HasForeignKey(i => i.MergedIntoIncidentId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_incident_no_self_merge", "\"MergedIntoIncidentId\" IS NULL OR \"MergedIntoIncidentId\" <> \"Id\"");
                t.HasCheckConstraint("ck_incident_retired_has_target", "(\"Status\" = 2) = (\"MergedIntoIncidentId\" IS NOT NULL)");
            });
            e.HasIndex(i => i.MatchKey);
            e.HasIndex(i => i.LooseKey);
            e.HasIndex(i => new { i.Status, i.InjuryDate });
        });

        b.Entity<InjuryReport>(e =>
        {
            e.Property(r => r.EventName).HasMaxLength(200).IsRequired();
            e.Property(r => r.Narrative).HasConversion(encrypt).IsRequired();
            e.Property(r => r.MatchKey).HasMaxLength(64).IsRequired();
            e.Property(r => r.LooseKey).HasMaxLength(64).IsRequired();
            e.Property(r => r.InjuryDate).HasColumnType("date");
            e.HasOne(r => r.Incident).WithMany(i => i.Reports).HasForeignKey(r => r.IncidentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.Reporter).WithMany().HasForeignKey(r => r.ReporterUserId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(r => r.Discipline).WithMany().HasForeignKey(r => r.DisciplineId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.InjuryType).WithMany().HasForeignKey(r => r.InjuryTypeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.EventKingdom).WithMany().HasForeignKey(r => r.EventKingdomId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.InjuredKingdom).WithMany().HasForeignKey(r => r.InjuredKingdomId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(r => r.SubmissionToken).IsUnique();   // replay / double-submit protection
            e.HasIndex(r => new { r.ReporterUserId, r.MatchKey });
            e.HasIndex(r => r.IncidentId);
        });

        b.Entity<AuditLogEntry>(e =>
        {
            e.Property(a => a.Action).HasMaxLength(100).IsRequired();
            e.Property(a => a.ActorEmail).HasMaxLength(256);
            e.Property(a => a.EntityType).HasMaxLength(50);
            e.Property(a => a.EntityId).HasMaxLength(64);
            e.Property(a => a.Detail).HasMaxLength(1000);
            e.Property(a => a.IpAddress).HasMaxLength(64);
            e.HasIndex(a => a.TimestampUtc);
            e.HasIndex(a => a.Action);
        });

        SeedData.Apply(b);
    }
}
