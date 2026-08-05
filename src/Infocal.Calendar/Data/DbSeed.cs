using Infocal.Calendar.Models;
using Microsoft.EntityFrameworkCore;

namespace Infocal.Calendar.Data;

/// <summary>
/// Seeds reference tables (categories, cities).
/// Runs on startup when the database is empty.
/// </summary>
public static class DbSeed
{
    private static readonly (string Slug, string Name, string Icon, string Color)[] SeedCategories =
    [
        ("mass-skating", "Массовое катание", "⛸️", "#1565C0"),
        ("quiz", "Квиз", "🧠", "#7B1FA2")
    ];

    private static readonly (string Slug, string Name)[] SeedCities =
    [
        ("gomel", "Гомель")
    ];

    private static readonly (string Slug, string Name)[] SeedTypes =
    [
        ("ice-palace", "Ледовый дворец"),
        ("wow-quiz", "ВАУ КВИЗ")
    ];

    public static async Task InitializeAsync(AppDbContext db)
    {
        await db.Database.EnsureCreatedAsync();

        // ── Seed reference tables: check each slug individually ──

        foreach (var (slug, name, icon, color) in SeedCategories)
        {
            if (!await db.Categories.AnyAsync(c => c.Slug == slug))
            {
                db.Categories.Add(new CategoryEntity { Slug = slug, Name = name, Icon = icon, Color = color });
            }
        }

        foreach (var (slug, name) in SeedCities)
        {
            if (!await db.Cities.AnyAsync(c => c.Slug == slug))
            {
                db.Cities.Add(new CityEntity { Slug = slug, Name = name });
            }
        }

        foreach (var (slug, name) in SeedTypes)
        {
            if (!await db.Types.AnyAsync(t => t.Slug == slug))
            {
                db.Types.Add(new TypeEntity { Slug = slug, Name = name });
            }
        }

        await db.SaveChangesAsync();
    }
}
