using Infocal.Calendar.Models;
using Microsoft.EntityFrameworkCore;

namespace Infocal.Calendar.Data;

/// <summary>
/// Seeds reference tables (categories, cities).
/// Runs on startup when the database is empty.
/// </summary>
public static class DbSeed
{
    public static async Task InitializeAsync(AppDbContext db)
    {
        await db.Database.EnsureCreatedAsync();

        // ── Seed reference tables ──
        if (!await db.Categories.AnyAsync())
        {
            db.Categories.AddRange(
                new CategoryEntity { Slug = "mass-skating", Name = "Массовое катание", Icon = "⛸️", Color = "#1565C0" },
                new CategoryEntity { Slug = "quiz", Name = "Квиз", Icon = "🧠", Color = "#7B1FA2" }
            );
        }

        if (!await db.Cities.AnyAsync())
        {
            db.Cities.AddRange(
                new CityEntity { Slug = "gomel", Name = "Гомель" }
            );
        }

        await db.SaveChangesAsync();
    }
}
