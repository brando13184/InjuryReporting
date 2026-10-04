using Microsoft.EntityFrameworkCore;

namespace InjuryReporting.Web.Data;

public static class SeedData
{
    public static readonly string[] KingdomNames =
    {
        "An Tir", "Ansteorra", "Artemisia", "Atenveldt", "Atlantia", "Avacal", "Caid", "Calontir",
        "Drachenwald", "Ealdormere", "East", "Gleann Abhann", "Lochac", "Meridies", "Middle",
        "Northshield", "Outlands", "Æthelmearc", "Trimaris", "West"
    };

    public static readonly string[] DisciplineNames =
        { "Armored Combat", "Rapier", "Equestrian", "Archery", "Thrown Weapons" };

    public static readonly string[] InjuryTypeNames =
    {
        "Head injury / concussion", "Sprain or strain", "Fracture / broken bone", "Dislocation",
        "Laceration / puncture", "Contusion / bruise", "Eye injury", "Dental injury",
        "Neck / back / spine", "Heat or cold illness", "Cardiac / medical event", "Other"
    };

    public static void Apply(ModelBuilder b)
    {
        b.Entity<Kingdom>().HasData(KingdomNames.Select((n, i) => new Kingdom { Id = i + 1, Name = n }));
        b.Entity<Discipline>().HasData(DisciplineNames.Select((n, i) => new Discipline { Id = i + 1, Name = n }));
        b.Entity<InjuryType>().HasData(InjuryTypeNames.Select((n, i) => new InjuryType { Id = i + 1, Name = n }));
    }
}
