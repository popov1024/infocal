using Infocal.Calendar.Models;
using Microsoft.EntityFrameworkCore;

namespace Infocal.Calendar.Data;

/// <summary>
/// Business-logic wrapper around EF Core DbContext for calendar events.
/// All read/write operations go through here.
/// Category and City are stored as slugs; *_Description are denormalized names.
/// </summary>
public class EventStore(AppDbContext db)
{
    // ── Read ──

    public async Task<IReadOnlyList<EventItem>> GetAllAsync(
        string[]? categories = null, string? city = null, CancellationToken ct = default)
    {
        var query = db.Events.AsNoTracking().OrderBy(e => e.Start);

        if (categories is { Length: > 0 })
        {
            // Resolve slugs (input may be slug or name)
            var slugs = await ResolveCategorySlugsAsync(categories, ct);
            if (slugs.Count == 1)
            {
                var slug = slugs[0];
                query = (IOrderedQueryable<EventItem>)query.Where(e => e.Category == slug);
            }
            else if (slugs.Count > 1)
            {
                query = (IOrderedQueryable<EventItem>)query.Where(e => slugs.Contains(e.Category));
            }
        }

        if (string.IsNullOrWhiteSpace(city)) return await query.ToListAsync(ct);
        {
            var citySlug = await ResolveCitySlugAsync(city, ct);
            query = (IOrderedQueryable<EventItem>)query.Where(e => e.City == citySlug);
        }

        return await query.ToListAsync(ct);
    }

    public async Task<EventItem?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);

    public async Task<IReadOnlyList<string>> GetCategorySlugsAsync(string? city = null, CancellationToken ct = default)
    {
        var query = db.Events.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(city))
        {
            var citySlug = await ResolveCitySlugAsync(city, ct);
            if (citySlug is not null)
                query = query.Where(e => e.City == citySlug);
        }

        return await query
            .Select(e => e.Category)
            .Distinct()
            .Order()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CategoryEntity>> GetCategoriesAsync(CancellationToken ct = default)
        => await db.Categories.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);

    public async Task<IReadOnlyList<string>> GetCitySlugsAsync(CancellationToken ct = default)
        => await db.Events
            .AsNoTracking()
            .Select(e => e.City)
            .Distinct()
            .Order()
            .ToListAsync(ct);

    public async Task<IReadOnlyList<CityEntity>> GetCitiesAsync(CancellationToken ct = default)
        => await db.Cities.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);

    // ── Write ──

    public async Task<EventItem> AddAsync(EventItem e, CancellationToken ct = default)
    {
        // Resolve category slug from slug or name
        if (!string.IsNullOrWhiteSpace(e.Category))
        {
            var resolved = await ResolveCategorySlugAsync(e.Category, ct);
            e.Category = resolved; // store slug
            e.CategoryDescription = await GetCategoryNameAsync(resolved, ct);
        }

        // Resolve city slug from slug or name
        if (!string.IsNullOrWhiteSpace(e.City))
        {
            var resolved = await ResolveCitySlugAsync(e.City, ct);
            e.City = resolved;
            e.CityDescription = await GetCityNameAsync(resolved, ct);
        }

        db.Events.Add(e);
        await db.SaveChangesAsync(ct);
        return e;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var ev = await db.Events.FindAsync([id], ct);
        if (ev is null) return false;
        db.Events.Remove(ev);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> DeleteBySourceUrlAsync(string sourceUrl, CancellationToken ct = default)
    {
        var deleted = await db.Events
            .Where(e => e.SourceUrl == sourceUrl)
            .ExecuteDeleteAsync(ct);
        return deleted;
    }

    // ── Slug resolution helpers ──

    private async Task<string> ResolveCategorySlugAsync(string id, CancellationToken ct)
    {
        // Try exact slug match
        var entity = await db.Categories.FindAsync([id], ct);
        if (entity is not null) return entity.Slug;

        // Try name match
        entity = await db.Categories.FirstOrDefaultAsync(c => c.Name == id, ct);
        if (entity is not null) return entity.Slug;

        // Not found — use the input as slug
        return id.ToLowerInvariant().Replace(' ', '-');
    }

    private async Task<List<string>> ResolveCategorySlugsAsync(string[] ids, CancellationToken ct)
    {
        var result = new List<string>();
        foreach (var id in ids)
            result.Add(await ResolveCategorySlugAsync(id, ct));
        return result.Distinct().ToList();
    }

    private async Task<string> ResolveCitySlugAsync(string id, CancellationToken ct)
    {
        var entity = await db.Cities.FindAsync([id], ct);
        if (entity is not null) return entity.Slug;

        entity = await db.Cities.FirstOrDefaultAsync(c => c.Name == id, ct);
        if (entity is not null) return entity.Slug;

        return id.ToLowerInvariant().Replace(' ', '-');
    }

    private async Task<string?> GetCategoryNameAsync(string slug, CancellationToken ct)
    {
        var entity = await db.Categories.FindAsync([slug], ct);
        return entity?.Name ?? slug;
    }

    private async Task<string?> GetCityNameAsync(string slug, CancellationToken ct)
    {
        var entity = await db.Cities.FindAsync([slug], ct);
        return entity?.Name ?? slug;
    }
}
