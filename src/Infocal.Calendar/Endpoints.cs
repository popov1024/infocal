using Infocal.Calendar.Data;
using Infocal.Calendar.Models;
using Infocal.Calendar.Services;
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

            var html = BuildHomePage(baseUrl, host, cities, categories);
            return Results.Content(html, "text/html; charset=utf-8");
        });

        // ── iCalendar: with query params ──
        app.MapGet("/calendar.ics", async (
            EventStore store, CalendarService cal,
            string[]? category, string? city,
            CancellationToken ct) =>
        {
            var events = await store.GetAllAsync(category, city, ct);
            var name = await BuildCalendarName(store, category, city, ct);
            var ics = cal.GenerateIcs(events, name, "Календарь событий");
            return Results.Text(ics, "text/calendar; charset=utf-8");
        });

        // ── iCalendar: per category slug (path-based) ──
        app.MapGet("/calendar/{categorySlug}.ics", async (
            EventStore store, CalendarService cal,
            string categorySlug, string? city,
            CancellationToken ct) =>
        {
            var events = await store.GetAllAsync([categorySlug], city, ct);
            var name = await BuildCalendarName(store, [categorySlug], city, ct);
            var categories = await store.GetCategoriesAsync(ct);
            var cat = categories.FirstOrDefault(c => c.Slug == categorySlug);
            var color = cat?.Color ?? "#1565C0";
            var ics = cal.GenerateIcs(events, name, description: null, color: color);
            return Results.Text(ics, "text/calendar; charset=utf-8");
        });

        // ── JSON API ──
        app.MapGet("/events", async (EventStore store, string[]? category, string? city, CancellationToken ct) =>
            Results.Ok(await store.GetAllAsync(category, city, ct)));

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
        EventStore store, string[]? categorySlugs, string? city, CancellationToken ct)
    {
        var parts = new List<string>();

        if (categorySlugs is { Length: > 0 })
        {
            var cats = await store.GetCategoriesAsync(ct);
            var names = categorySlugs
                .Select(s => cats.FirstOrDefault(c => c.Slug == s)?.Name ?? s)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct();
            parts.Add(string.Join(" + ", names));
        }

        if (!string.IsNullOrWhiteSpace(city))
        {
            var cities = await store.GetCitiesAsync(ct);
            var cityName = cities.FirstOrDefault(c => c.Slug == city)?.Name ?? city;
            parts.Add(cityName);
        }

        return parts.Count > 0 ? string.Join(" — ", parts) : "Все события";
    }

    private static string BuildHomePage(string baseUrl, string host,
        IReadOnlyList<CityEntity> cities, IReadOnlyList<CategoryEntity> categories)
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
        sb.AppendLine(".btn { display: inline-block; padding: 0.5rem 1.2rem; color: #fff; border-radius: 8px;");
        sb.AppendLine("        text-decoration: none; font-weight: 600; font-size: 0.9rem; }");
        sb.AppendLine(".tag { display: inline-block; padding: 0.3rem 0.8rem; border-radius: 20px;");
        sb.AppendLine("        font-size: 0.85rem; text-decoration: none; margin: 0.2rem; }");
        sb.AppendLine(".tag-on { background: #1565C0; color: #fff; }");
        sb.AppendLine(".tag-off { background: #e0e0e0; color: #555; }");
        sb.AppendLine("code { background: #e8e8e8; padding: 2px 6px; border-radius: 4px; font-size: 0.9rem; word-break: break-all; }");
        sb.AppendLine(".steps { padding-left: 1.2rem; }");
        sb.AppendLine(".steps li { margin-bottom: 0.3rem; }");
        sb.AppendLine(".copy-btn { background: none; color: #888; border: none; padding: 0.2rem; cursor: pointer; border-radius: 4px; float: right; margin-left: 0.5rem; }");
        sb.AppendLine(".copy-btn:hover { color: #1565C0; background: #e8e8e8; }");
        sb.AppendLine(".url-box { background: #e8e8e8; border-radius: 4px; font-size: 0.8rem;");
        sb.AppendLine("        padding: 0.4rem 0.6rem; word-break: break-all; overflow: hidden; }");
        sb.AppendLine(".url-box.mt { margin-top: 0.5rem; }");
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("<h1>📅 Календарь событий</h1>");
        sb.AppendLine("<p class=\"subtitle\">Выберите город и категории — каждая подписка создаст отдельный календарь с автообновлением.</p>");

        // ── City filter ──
        if (cities.Count > 0)
        {
            sb.AppendLine("<div class=\"card\">");
            sb.AppendLine("<h2>🏙️ Города</h2>");
            sb.AppendLine("<div>");
            foreach (var city in cities)
            {
                sb.AppendLine($"<a class=\"tag tag-on\" href=\"/calendar.ics?city={city.Slug}\">{city.Name}</a>");
            }
            sb.AppendLine("<a class=\"tag tag-off\" href=\"/\">Все</a>");
            sb.AppendLine("</div>");
            sb.AppendLine("</div>");
        }

        var copyIcon = "<svg width='14' height='14' viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><rect x='9' y='9' width='13' height='13' rx='2' ry='2'></rect><path d='M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1'></path></svg>";

        // ── All events ──
        sb.AppendLine("<div class=\"card\">");
        sb.AppendLine("<div class=\"row\">");
        sb.AppendLine("<h2>📅 Все события</h2>");
        sb.AppendLine($"<a class=\"btn\" style=\"background:#37474F;\" href=\"webcal://{host}/calendar.ics\">Подписаться на всё</a>");
        sb.AppendLine("</div>");
        var allUrl = $"{baseUrl}/calendar.ics";
        sb.AppendLine($"<div class=\"url-box mt\">{allUrl}<button onclick=\"copyUrl(this,'{allUrl}')\" title=\"Копировать\" class=\"copy-btn\">{copyIcon}</button></div>");
        sb.AppendLine("</div>");

        // ── Per category ──
        foreach (var cat in categories)
        {
            var icon = cat.Icon ?? "📅";
            var color = cat.Color ?? "#1565C0";

            sb.AppendLine("<div class=\"card\">");
            sb.AppendLine("<div class=\"row\">");
            sb.AppendLine($"<h2>{icon} {cat.Name}</h2>");
            sb.AppendLine($"<a class=\"btn\" style=\"background:{color}\" href=\"webcal://{host}/calendar/{cat.Slug}.ics\">Подписаться</a>");
            sb.AppendLine("</div>");
            var catUrl = $"{baseUrl}/calendar/{cat.Slug}.ics";
            sb.AppendLine($"<div class=\"url-box mt\">{catUrl}<button onclick=\"copyUrl(this,'{catUrl}')\" title=\"Копировать\" class=\"copy-btn\">{copyIcon}</button></div>");
            sb.AppendLine("</div>");
        }

        // ── Copy-to-clipboard script ──
        sb.AppendLine("<script>");
        sb.AppendLine("function copyUrl(btn,url){navigator.clipboard.writeText(url).then(()=>{btn.innerHTML='" + 
            "' + '<svg width=\"14\" height=\"14\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"#4CAF50\" stroke-width=\"3\" stroke-linecap=\"round\" stroke-linejoin=\"round\"><polyline points=\"20 6 9 17 4 12\"></polyline></svg>'" + 
            ";btn.title='Скопировано!';setTimeout(()=>{btn.innerHTML='" +
            "' + '<svg width=\"14\" height=\"14\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\"><rect x=\"9\" y=\"9\" width=\"13\" height=\"13\" rx=\"2\" ry=\"2\"></rect><path d=\"M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1\"></path></svg>'" +
            ";btn.title='Копировать';},1500)})");
        sb.AppendLine("</script>");

        sb.AppendLine("<p style=\"margin-top:2rem;text-align:center;color:#aaa;font-size:0.8rem;\">");
        sb.AppendLine("<a href=\"/events\">JSON API</a> · <a href=\"/categories\">Категории</a> · <a href=\"/cities\">Города</a>");
        sb.AppendLine("</p>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    private static RouteHandlerBuilder RequireApiKey(this RouteHandlerBuilder builder, string expectedKey)
    {
        return builder.AddEndpointFilter(async (context, next) =>
        {
            var request = context.HttpContext.Request;
            var providedKey = request.Headers["X-Api-Key"].FirstOrDefault();

            if (string.IsNullOrEmpty(providedKey) || providedKey != expectedKey)
                return Results.Unauthorized();

            return await next(context);
        });
    }
}
