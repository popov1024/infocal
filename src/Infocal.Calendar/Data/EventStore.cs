using Infocal.Calendar.Models;

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
        string[]? categories = null, string[]? cities = null, string[]? types = null, CancellationToken ct = default)
    {
        var query = db.Events.AsNoTracking().OrderBy(e => e.Start);

        if (categories is { Length: > 0 })
        {
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

        if (cities is { Length: > 0 })
        {
            var citySlugs = await ResolveCitySlugsAsync(cities, ct);
            if (citySlugs.Count == 1)
            {
                var slug = citySlugs[0];
                query = (IOrderedQueryable<EventItem>)query.Where(e => e.City == slug);
            }
            else if (citySlugs.Count > 1)
            {
                query = (IOrderedQueryable<EventItem>)query.Where(e => citySlugs.Contains(e.City));
            }
        }

        if (types is { Length: > 0 })
        {
            var typeSlugs = await ResolveTypeSlugsAsync(types, ct);
            if (typeSlugs.Count == 1)
            {
                var slug = typeSlugs[0];
                query = (IOrderedQueryable<EventItem>)query.Where(e => e.Type == slug);
            }
            else if (typeSlugs.Count > 1)
            {
                query = (IOrderedQueryable<EventItem>)query.Where(e => typeSlugs.Contains(e.Type));
            }
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

    public async Task<IReadOnlyList<TypeEntity>> GetTypesAsync(CancellationToken ct = default)
        => await db.Types.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);

    // ── Write ──

    public async Task<EventItem> UpsertAsync(EventItem e, CancellationToken ct = default)
    {
        // Resolve category slug from slug or name
        if (!string.IsNullOrWhiteSpace(e.Category))
        {
            var resolved = await ResolveCategorySlugAsync(e.Category, ct);
            e.Category = resolved;
            e.CategoryDescription = await GetCategoryNameAsync(resolved, ct);
        }

        // Resolve city slug from slug or name
        if (!string.IsNullOrWhiteSpace(e.City))
        {
            var resolved = await ResolveCitySlugAsync(e.City, ct);
            e.City = resolved;
            e.CityDescription = await GetCityNameAsync(resolved, ct);
        }

        // Resolve type slug from slug or name
        if (!string.IsNullOrWhiteSpace(e.Type))
        {
            var resolved = await ResolveTypeSlugAsync(e.Type, ct);
            e.Type = resolved;
            e.TypeDescription = await GetTypeNameAsync(resolved, ct);
        }

        // Upsert: find existing by SourceUrl + Start
        var existing = await db.Events
            .FirstOrDefaultAsync(ev => ev.SourceUrl == e.SourceUrl && ev.Start == e.Start, ct);

        if (existing is not null)
        {
            existing.Summary = e.Summary;
            existing.Description = e.Description;
            existing.Address = e.Address;
            existing.End = e.End;
            existing.Category = e.Category;
            existing.CategoryDescription = e.CategoryDescription;
            existing.Type = e.Type;
            existing.TypeDescription = e.TypeDescription;
            existing.City = e.City;
            existing.CityDescription = e.CityDescription;
            existing.IsAllDay = e.IsAllDay;
        }
        else
        {
            e.Id = Guid.NewGuid();
            db.Events.Add(e);
        }

        await db.SaveChangesAsync(ct);
        return existing ?? e;
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

    // ── Category CRUD ──

    public async Task UpsertCategoryAsync(CategoryEntity cat, CancellationToken ct = default)
    {
        var existing = await db.Categories.FindAsync([cat.Slug], ct);
        if (existing is not null)
        {
            existing.Name = cat.Name;
            existing.Icon = cat.Icon;
            existing.Color = cat.Color;
        }
        else
        {
            db.Categories.Add(cat);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteCategoryAsync(string slug, CancellationToken ct = default)
    {
        var cat = await db.Categories.FindAsync([slug], ct);
        if (cat is null) return false;
        db.Categories.Remove(cat);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── City CRUD ──

    public async Task UpsertCityAsync(CityEntity city, CancellationToken ct = default)
    {
        var existing = await db.Cities.FindAsync([city.Slug], ct);
        if (existing is not null)
        {
            existing.Name = city.Name;
        }
        else
        {
            db.Cities.Add(city);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteCityAsync(string slug, CancellationToken ct = default)
    {
        var city = await db.Cities.FindAsync([slug], ct);
        if (city is null) return false;
        db.Cities.Remove(city);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── Type CRUD ──

    public async Task UpsertTypeAsync(TypeEntity type, CancellationToken ct = default)
    {
        var existing = await db.Types.FindAsync([type.Slug], ct);
        if (existing is not null)
        {
            existing.Name = type.Name;
            existing.CategorySlug = type.CategorySlug;
        }
        else
        {
            db.Types.Add(type);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteTypeAsync(string slug, CancellationToken ct = default)
    {
        var type = await db.Types.FindAsync([slug], ct);
        if (type is null) return false;
        db.Types.Remove(type);
        await db.SaveChangesAsync(ct);
        return true;
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

    private async Task<List<string>> ResolveTypeSlugsAsync(string[] ids, CancellationToken ct)
    {
        var result = new List<string>();
        foreach (var id in ids)
            result.Add(await ResolveTypeSlugAsync(id, ct));
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

    private async Task<List<string>> ResolveCitySlugsAsync(string[] ids, CancellationToken ct)
    {
        var result = new List<string>();
        foreach (var id in ids)
            result.Add(await ResolveCitySlugAsync(id, ct));
        return result.Distinct().ToList();
    }

    private async Task<string> GetCategoryNameAsync(string slug, CancellationToken ct)
    {
        var entity = await db.Categories.FindAsync([slug], ct);
        return entity?.Name ?? slug;
    }

    private async Task<string> GetCityNameAsync(string slug, CancellationToken ct)
    {
        var entity = await db.Cities.FindAsync([slug], ct);
        return entity?.Name ?? slug;
    }

    private async Task<string> ResolveTypeSlugAsync(string id, CancellationToken ct)
    {
        var entity = await db.Types.FindAsync([id], ct);
        if (entity is not null) return entity.Slug;
        entity = await db.Types.FirstOrDefaultAsync(t => t.Name == id, ct);
        if (entity is not null) return entity.Slug;
        return id.ToLowerInvariant().Replace(' ', '-');
    }

    private async Task<string> GetTypeNameAsync(string slug, CancellationToken ct)
    {
        var entity = await db.Types.FindAsync([slug], ct);
        return entity?.Name ?? slug;
    }
}
