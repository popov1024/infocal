using Infocal.Calendar.Models;
using System.Text;

namespace Infocal.Calendar;

public static class Endpoints
{
    public static void Map(WebApplication app, string apiKey)
    {
        // ── Home page ──
        app.MapGet("/", async (HttpContext ctx, EventStore store, CancellationToken ct) =>
        {
            var scheme = ctx.Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? ctx.Request.Scheme;
            var host = ctx.Request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? ctx.Request.Host.Value ?? "localhost";
            var baseUrl = $"{scheme}://{host}";
            var cities = await store.GetCitiesAsync(ct);
            var categories = await store.GetCategoriesAsync(ct);
            var types = await store.GetTypesAsync(ct);

            var html = BuildHomePage(baseUrl, host, cities, categories, types);
            return Results.Content(html, "text/html; charset=utf-8");
        });

        // ── iCalendar: catch-all path-based API ──
        //   /calendar.ics                       → all events
        //   /calendar/city/gomel,minsk.ics       → filter by cities
        //   /calendar/category/quiz.ics           → filter by categories
        //   /calendar/type/wow-quiz.ics           → filter by types
        //   /calendar/city/gomel/category/quiz.ics → combined (order free)
        app.MapGet("/calendar.ics", async (
            EventStore store, CalendarService cal,
            CancellationToken ct) =>
        {
            var events = await store.GetAllAsync(ct: ct);
            var ics = cal.GenerateIcs(events, "Все события", "Календарь событий");
            return Results.Text(ics, "text/calendar; charset=utf-8");
        });

        app.MapGet("/calendar/{**rest}", async (
            EventStore store, CalendarService cal,
            string? rest,
            CancellationToken ct) =>
        {
            var (cities, categories, types) = ParseCalendarPath(rest);
            var events = await store.GetAllAsync(categories, cities, types, ct);
            var name = await BuildCalendarName(store, categories, cities, types, ct);
            var color = await GetCalendarColor(store, categories, ct);
            var ics = cal.GenerateIcs(events, name, "Календарь событий", color);
            return Results.Text(ics, "text/calendar; charset=utf-8");
        });

        // ── JSON API ──
        app.MapGet("/events", async (EventStore store, string[]? category, string[]? city, string[]? type, CancellationToken ct) =>
            Results.Ok(await store.GetAllAsync(category, city, type, ct)));

        app.MapGet("/events/{id:guid}", async (EventStore store, Guid id, CancellationToken ct) =>
        {
            var ev = await store.GetByIdAsync(id, ct);
            return ev is null ? Results.NotFound() : Results.Ok(ev);
        });

        app.MapGet("/categories", async (EventStore store, CancellationToken ct) =>
            Results.Ok(await store.GetCategoriesAsync(ct)));

        app.MapGet("/cities", async (EventStore store, CancellationToken ct) =>
            Results.Ok(await store.GetCitiesAsync(ct)));

        // ── Write endpoints (require API key) ──

        app.MapPost("/events", async (EventStore store, EventItem e, CancellationToken ct) =>
        {
            var result = await store.UpsertAsync(e, ct);
            return Results.Ok(result);
        }).RequireApiKey(apiKey);

        app.MapDelete("/events/by-source", async (EventStore store, string sourceUrl, CancellationToken ct) =>
        {
            var deleted = await store.DeleteBySourceUrlAsync(sourceUrl, ct);
            return Results.Ok(new { deleted });
        }).RequireApiKey(apiKey);

        app.MapDelete("/events/{id:guid}", async (EventStore store, Guid id, CancellationToken ct) =>
        {
            var deleted = await store.DeleteAsync(id, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).RequireApiKey(apiKey);

        // ── Categories CRUD ──
        app.MapPost("/categories", async (EventStore store, CategoryEntity cat, CancellationToken ct) =>
        {
            await store.UpsertCategoryAsync(cat, ct);
            return Results.Ok(cat);
        }).RequireApiKey(apiKey);

        app.MapDelete("/categories/{slug}", async (EventStore store, string slug, CancellationToken ct) =>
        {
            var deleted = await store.DeleteCategoryAsync(slug, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).RequireApiKey(apiKey);

        // ── Cities CRUD ──
        app.MapPost("/cities", async (EventStore store, CityEntity city, CancellationToken ct) =>
        {
            await store.UpsertCityAsync(city, ct);
            return Results.Ok(city);
        }).RequireApiKey(apiKey);

        app.MapDelete("/cities/{slug}", async (EventStore store, string slug, CancellationToken ct) =>
        {
            var deleted = await store.DeleteCityAsync(slug, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).RequireApiKey(apiKey);

        // ── Types CRUD ──
        app.MapGet("/types", async (EventStore store, CancellationToken ct) =>
            Results.Ok(await store.GetTypesAsync(ct)));

        app.MapPost("/types", async (EventStore store, TypeEntity type, CancellationToken ct) =>
        {
            await store.UpsertTypeAsync(type, ct);
            return Results.Ok(type);
        }).RequireApiKey(apiKey);

        app.MapDelete("/types/{slug}", async (EventStore store, string slug, CancellationToken ct) =>
        {
            var deleted = await store.DeleteTypeAsync(slug, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).RequireApiKey(apiKey);
    }

    private static async Task<string> BuildCalendarName(
        EventStore store, string[]? categorySlugs, string[]? citySlugs, string[]? typeSlugs, CancellationToken ct)
    {
        var parts = new List<string>();

        if (citySlugs is { Length: > 0 })
        {
            var cities = await store.GetCitiesAsync(ct);
            var names = citySlugs
                .Select(s => cities.FirstOrDefault(c => c.Slug == s)?.Name ?? s)
                .Where(n => !string.IsNullOrWhiteSpace(n));
            parts.Add(string.Join("+", names));
        }

        if (categorySlugs is { Length: > 0 })
        {
            var cats = await store.GetCategoriesAsync(ct);
            var names = categorySlugs
                .Select(s => cats.FirstOrDefault(c => c.Slug == s)?.Name ?? s)
                .Where(n => !string.IsNullOrWhiteSpace(n));
            parts.Add(string.Join("+", names));
        }

        if (typeSlugs is { Length: > 0 })
        {
            var types = await store.GetTypesAsync(ct);
            var names = typeSlugs
                .Select(s => types.FirstOrDefault(t => t.Slug == s)?.Name ?? s)
                .Where(n => !string.IsNullOrWhiteSpace(n));
            parts.Add(string.Join("+", names));
        }

        return parts.Count > 0 ? string.Join(" — ", parts) : "Все события";
    }

    /// <summary>
    /// Parses /calendar/{**rest} path like "city/gomel,minsk/category/quiz.ics"
    /// into separate filter arrays. Supports comma and underscore as value separators.
    /// </summary>
    private static (string[]? cities, string[]? categories, string[]? types) ParseCalendarPath(string? rest)
    {
        if (string.IsNullOrWhiteSpace(rest))
            return (null, null, null);

        // Strip trailing .ics from the last segment
        rest = rest.TrimEnd('/');
        if (rest.EndsWith(".ics", StringComparison.OrdinalIgnoreCase))
            rest = rest[..^4];

        var segments = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var cities = new List<string>();
        var categories = new List<string>();
        var types = new List<string>();

        for (var i = 0; i < segments.Length; i++)
        {
            var key = segments[i].ToLowerInvariant();
            var values = i + 1 < segments.Length ? segments[i + 1] : null;

            if (values is null) break;

            // Values may be comma or underscore separated
            var slugs = values.Split([',', '_'], StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().ToLowerInvariant())
                .Where(s => s.Length > 0)
                .ToArray();

            if (slugs.Length == 0) break;

            switch (key)
            {
                case "city":
                    cities.AddRange(slugs);
                    i++;
                    break;
                case "category":
                    categories.AddRange(slugs);
                    i++;
                    break;
                case "type":
                    types.AddRange(slugs);
                    i++;
                    break;
            }
        }

        return (
            cities.Count > 0 ? cities.ToArray() : null,
            categories.Count > 0 ? [.. categories] : null,
            types.Count > 0 ? [.. types] : null
        );
    }

    private static async Task<string> GetCalendarColor(
        EventStore store, string[]? categorySlugs, CancellationToken ct)
    {
        if (categorySlugs is { Length: 1 })
        {
            var cats = await store.GetCategoriesAsync(ct);
            var cat = cats.FirstOrDefault(c => c.Slug == categorySlugs[0]);
            if (cat?.Color is { } color)
                return color;
        }
        return "#1565C0";
    }

    private static string BuildHomePage(string baseUrl, string host,
        IReadOnlyList<CityEntity> cities, IReadOnlyList<CategoryEntity> categories, IReadOnlyList<TypeEntity> types)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"ru\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine("<title>Календарь событий</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("* { box-sizing: border-box; margin: 0; padding: 0; }");
        sb.AppendLine("body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;");
        sb.AppendLine("       background: #f0f4f8; color: #1a1a2e; padding: 2rem; max-width: 700px; margin: auto; }");
        sb.AppendLine("h1 { font-size: 1.6rem; margin-bottom: 0.5rem; }");
        sb.AppendLine(".subtitle { color: #555; margin-bottom: 1rem; }");
        sb.AppendLine("h2 { font-size: 1.1rem; margin-bottom: 0.5rem; }");
        sb.AppendLine(".card { background: #fff; border-radius: 12px; padding: 1.2rem 1.5rem; margin-bottom: 0.8rem;");
        sb.AppendLine("        box-shadow: 0 2px 8px rgba(0,0,0,0.06); }");
        sb.AppendLine(".row { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 0.5rem; }");
        sb.AppendLine(".tags { display: flex; flex-wrap: wrap; gap: 0.35rem; }");
        sb.AppendLine(".tag { display: inline-block; padding: 0.4rem 0.9rem; border-radius: 20px;");
        sb.AppendLine("        font-size: 0.85rem; text-decoration: none; cursor: pointer; user-select: none;");
        sb.AppendLine("        border: 2px solid transparent; transition: all 0.15s; }");
        sb.AppendLine(".tag-on { background: #1565C0; color: #fff; border-color: #1565C0; }");
        sb.AppendLine(".tag-off { background: #e8e8e8; color: #555; border-color: #e8e8e8; }");
        sb.AppendLine(".tag-off:hover { background: #d0d0d0; }");
        sb.AppendLine(".tag-hidden { display: none; }");
        sb.AppendLine(".btn { display: inline-block; padding: 0.5rem 1.5rem; color: #fff; border-radius: 8px;");
        sb.AppendLine("        text-decoration: none; font-weight: 600; font-size: 0.95rem; }");
        sb.AppendLine(".url-card { background: #e8eaed; border-radius: 12px; padding: 1.2rem 1.5rem; margin-bottom: 0.8rem; }");
        sb.AppendLine(".url-card h2 { color: #1a1a2e; }");
        sb.AppendLine(".url-box { background: #d2d5da; border-radius: 8px; padding: 0.6rem 1rem;");
        sb.AppendLine("        font-family: 'SF Mono', 'Fira Code', monospace; font-size: 0.85rem; color: #37474F;");
        sb.AppendLine("        word-break: break-all; display: flex; align-items: center; justify-content: space-between; gap: 0.5rem; }");
        sb.AppendLine(".url-text { flex: 1; min-width: 0; }");
        sb.AppendLine(".copy-btn { background: none; color: #78909C; border: none; padding: 0.3rem; cursor: pointer;");
        sb.AppendLine("        border-radius: 6px; flex-shrink: 0; display: flex; align-items: center; }");
        sb.AppendLine(".copy-btn:hover { color: #1a1a2e; background: rgba(0,0,0,0.08); }");
        sb.AppendLine(".actions { display: flex; gap: 0.5rem; margin-top: 0.8rem; }");
        sb.AppendLine(".empty-hint { color: #aaa; font-size: 0.85rem; font-style: italic; }");
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("<h1>📅 Календарь событий</h1>");
        sb.AppendLine("<p class=\"subtitle\">Выберите фильтры — ссылка на календарь сформируется автоматически.</p>");

        // ── Cities ──
        sb.AppendLine("<div class=\"card\" id=\"city-card\">");
        sb.AppendLine("<h2>🏙️ Города</h2>");
        sb.AppendLine("<div class=\"tags\" id=\"city-tags\">");
        foreach (var city in cities)
            sb.AppendLine($"<span class='tag tag-off' data-slug='{city.Slug}' onclick='toggleTag(this,\"city\")'>{city.Name}</span>");
        if (cities.Count == 0)
            sb.AppendLine("<span class='empty-hint'>Нет городов</span>");
        sb.AppendLine("</div></div>");

        // ── Categories ──
        sb.AppendLine("<div class=\"card\" id=\"category-card\">");
        sb.AppendLine("<h2>🏷️ Категории</h2>");
        sb.AppendLine("<div class=\"tags\" id=\"category-tags\">");
        foreach (var cat in categories)
            sb.AppendLine($"<span class='tag tag-off' data-slug='{cat.Slug}' onclick='toggleTag(this,\"category\")'>{cat.Icon} {cat.Name}</span>");
        sb.AppendLine("</div></div>");

        // ── Types ──
        sb.AppendLine("<div class=\"card\" id=\"type-card\">");
        sb.AppendLine("<h2>📌 Типы</h2>");
        sb.AppendLine("<div class=\"tags\" id=\"type-tags\">");
        foreach (var t in types)
            sb.AppendLine($"<span class='tag tag-off' data-slug='{t.Slug}' data-category='{t.CategorySlug}' onclick='toggleTag(this,\"type\")'>{t.Name}</span>");
        sb.AppendLine("</div></div>");

        // ── Generated URL ──
        sb.AppendLine("<div class=\"url-card\">");
        sb.AppendLine("<h2>🔗 Ссылка на календарь</h2>");
        sb.AppendLine($"<div class='url-box'><span class='url-text' id='cal-url'>{baseUrl}/calendar.ics</span>");
        sb.AppendLine($"<button class='copy-btn' onclick='copyUrl()' title='Копировать'>" +
            "<svg width='18' height='18' viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'>" +
            "<rect x='9' y='9' width='13' height='13' rx='2' ry='2'></rect><path d='M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1'></path></svg></button></div>");
        sb.AppendLine("<div class='actions'>");
        sb.AppendLine($"<a class='btn' style='background:#37474F;' id='webcal-link' href='webcal://{host}/calendar.ics'>📥 Подписаться</a>");
        sb.AppendLine($"<a class='btn' style='background:#1565C0;' id='download-link' href='{baseUrl}/calendar.ics' download>⬇️ Скачать</a>");
        sb.AppendLine("</div></div>");

        // ── Footer ──
        sb.AppendLine("<p style='margin-top:1.5rem;text-align:center;color:#aaa;font-size:0.8rem;'>");
        sb.AppendLine("<a href='/events'>JSON API</a> · <a href='/categories'>Категории</a> · <a href='/cities'>Города</a>");
        sb.AppendLine("</p>");

        // ── JS: interactive filters ──
        var typesJson = System.Text.Json.JsonSerializer.Serialize(types.Select(t => new { t.Slug, t.CategorySlug }));

        sb.AppendLine("<script>");
        sb.AppendLine($"const baseUrl = '{baseUrl}';");
        sb.AppendLine($"const webcalHost = '{host}';");
        sb.AppendLine($"const allTypes = {typesJson};");
        sb.AppendLine("const state = { city: [], category: [], type: [] };");
        sb.AppendLine();
        sb.AppendLine("function toggleTag(el, kind) {");
        sb.AppendLine("  const slug = el.dataset.slug;");
        sb.AppendLine("  const idx = state[kind].indexOf(slug);");
        sb.AppendLine("  if (idx >= 0) {");
        sb.AppendLine("    state[kind].splice(idx, 1);");
        sb.AppendLine("    el.classList.remove('tag-on');");
        sb.AppendLine("    el.classList.add('tag-off');");
        sb.AppendLine("  } else {");
        sb.AppendLine("    state[kind].push(slug);");
        sb.AppendLine("    el.classList.remove('tag-off');");
        sb.AppendLine("    el.classList.add('tag-on');");
        sb.AppendLine("  }");
        sb.AppendLine("  if (kind === 'category') filterTypes();");
        sb.AppendLine("  updateUrl();");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("function filterTypes() {");
        sb.AppendLine("  document.querySelectorAll('#type-tags .tag').forEach(el => {");
        sb.AppendLine("    const cat = el.dataset.category;");
        sb.AppendLine("    if (state.category.length === 0 || state.category.includes(cat)) {");
        sb.AppendLine("      el.classList.remove('tag-hidden');");
        sb.AppendLine("    } else {");
        sb.AppendLine("      el.classList.add('tag-hidden');");
        sb.AppendLine("      if (el.classList.contains('tag-on')) {");
        sb.AppendLine("        const idx = state.type.indexOf(el.dataset.slug);");
        sb.AppendLine("        if (idx >= 0) state.type.splice(idx, 1);");
        sb.AppendLine("        el.classList.remove('tag-on');");
        sb.AppendLine("        el.classList.add('tag-off');");
        sb.AppendLine("      }");
        sb.AppendLine("    }");
        sb.AppendLine("  });");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("function buildPath() {");
        sb.AppendLine("  const parts = [];");
        sb.AppendLine("  if (state.city.length) parts.push('city/' + state.city.join(','));");
        sb.AppendLine("  if (state.category.length) parts.push('category/' + state.category.join(','));");
        sb.AppendLine("  if (state.type.length) parts.push('type/' + state.type.join(','));");
        sb.AppendLine("  return parts.length ? '/' + parts.join('/') + '.ics' : '.ics';");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("function updateUrl() {");
        sb.AppendLine("  const path = buildPath();");
        sb.AppendLine("  const url = baseUrl + '/calendar' + path;");
        sb.AppendLine("  document.getElementById('cal-url').textContent = url;");
        sb.AppendLine("  document.getElementById('webcal-link').href = 'webcal://' + webcalHost + '/calendar' + path;");
        sb.AppendLine("  document.getElementById('download-link').href = url;");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("function copyUrl() {");
        sb.AppendLine("  const url = document.getElementById('cal-url').textContent;");
        sb.AppendLine("  navigator.clipboard.writeText(url).then(() => {");
        sb.AppendLine("    const btn = document.querySelector('.copy-btn');");
        sb.AppendLine("    btn.innerHTML = '<svg width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"#4CAF50\" stroke-width=\"3\" stroke-linecap=\"round\" stroke-linejoin=\"round\"><polyline points=\"20 6 9 17 4 12\"></polyline></svg>';");
        sb.AppendLine("    setTimeout(() => {");
        sb.AppendLine("      btn.innerHTML = '<svg width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\"><rect x=\"9\" y=\"9\" width=\"13\" height=\"13\" rx=\"2\" ry=\"2\"></rect><path d=\"M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1\"></path></svg>';");
        sb.AppendLine("    }, 1500);");
        sb.AppendLine("  });");
        sb.AppendLine("}");
        sb.AppendLine("</script>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    private static void RequireApiKey(this RouteHandlerBuilder builder, string expectedKey)
    {
        builder.AddEndpointFilter(async (context, next) =>
        {
            var request = context.HttpContext.Request;
            var providedKey = request.Headers["X-Api-Key"].FirstOrDefault();

            if (string.IsNullOrEmpty(providedKey) || providedKey != expectedKey)
                return Results.Unauthorized();

            return await next(context);
        });
    }
}
