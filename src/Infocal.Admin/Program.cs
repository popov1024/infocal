using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var apiBase = builder.Configuration["CalendarApi:BaseUrl"] ?? "http://localhost:5223";
var apiKey = builder.Configuration["INFOCAL_API_KEY"] ?? "dev-key-change-me";

using var http = new HttpClient
{
    BaseAddress = new Uri(apiBase),
    Timeout = TimeSpan.FromSeconds(30)
};
http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

// ── List ──
app.MapGet("/", async (CancellationToken ct) =>
{
    var events = await http.GetFromJsonAsync<List<EventDto>>("/events", ct) ?? [];
    return Results.Content(BuildListPage(events), "text/html; charset=utf-8");
});

// ── Create form ──
app.MapGet("/create", async (CancellationToken ct) =>
{
    var cats = await http.GetFromJsonAsync<List<CatDto>>("/categories", ct) ?? [];
    var types = await http.GetFromJsonAsync<List<TypeDto>>("/types", ct) ?? [];
    var cities = await http.GetFromJsonAsync<List<CityDto>>("/cities", ct) ?? [];
    return Results.Content(BuildFormPage(null, cats, cities, types), "text/html; charset=utf-8");
});

app.MapPost("/create", async (HttpContext ctx, CancellationToken ct) =>
{
    var form = await ctx.Request.ReadFormAsync(ct);
    var ev = FormToEvent(form);
    var resp = await http.PostAsJsonAsync("/events", ev, ct);
    return Results.Redirect(resp.IsSuccessStatusCode ? "/" : "/create");
});

// ── Edit ──
app.MapGet("/{id:guid}/edit", async (Guid id, CancellationToken ct) =>
{
    var ev = await http.GetFromJsonAsync<EventDto>($"/events/{id}", ct);
    if (ev is null) return Results.NotFound();
    var cats = await http.GetFromJsonAsync<List<CatDto>>("/categories", ct) ?? [];
    var types = await http.GetFromJsonAsync<List<TypeDto>>("/types", ct) ?? [];
    var cities = await http.GetFromJsonAsync<List<CityDto>>("/cities", ct) ?? [];
    return Results.Content(BuildFormPage(ev, cats, cities, types), "text/html; charset=utf-8");
});

app.MapPost("/{id:guid}/edit", async (HttpContext ctx, Guid id, CancellationToken ct) =>
{
    var form = await ctx.Request.ReadFormAsync(ct);
    var ev = FormToEvent(form);
    ev.Id = id;
    var resp = await http.PostAsJsonAsync("/events", ev, ct);
    return Results.Redirect(resp.IsSuccessStatusCode ? "/" : $"/{id}/edit");
});

// ── Delete ──
app.MapPost("/{id:guid}/delete", async (Guid id, CancellationToken ct) =>
{
    await http.DeleteAsync($"/events/{id}", ct);
    return Results.Redirect("/");
});

// ── Categories ──
app.MapGet("/categories", async (CancellationToken ct) =>
{
    var cats = await http.GetFromJsonAsync<List<CatDto>>("/categories", ct) ?? [];
    return Results.Content(BuildCatListPage(cats), "text/html; charset=utf-8");
});

app.MapPost("/categories", async (HttpContext ctx, CancellationToken ct) =>
{
    var form = await ctx.Request.ReadFormAsync(ct);
    var cat = new CatDto
    {
        Slug = form["slug"].FirstOrDefault() ?? "",
        Name = form["name"].FirstOrDefault() ?? "",
        Icon = form["icon"].FirstOrDefault() ?? "",
        Color = form["color"].FirstOrDefault() ?? "#1565C0"
    };
    if (!string.IsNullOrWhiteSpace(cat.Slug))
        await http.PostAsJsonAsync("/categories", cat, ct);
    return Results.Redirect("/categories");
});

app.MapPost("/categories/{slug}/delete", async (string slug, CancellationToken ct) =>
{
    await http.DeleteAsync($"/categories/{slug}", ct);
    return Results.Redirect("/categories");
});

// ── Cities ──
app.MapGet("/cities", async (CancellationToken ct) =>
{
    var cities = await http.GetFromJsonAsync<List<CityDto>>("/cities", ct) ?? [];
    return Results.Content(BuildCityListPage(cities), "text/html; charset=utf-8");
});

app.MapPost("/cities", async (HttpContext ctx, CancellationToken ct) =>
{
    var form = await ctx.Request.ReadFormAsync(ct);
    var city = new CityDto
    {
        Slug = form["slug"].FirstOrDefault() ?? "",
        Name = form["name"].FirstOrDefault() ?? ""
    };
    if (!string.IsNullOrWhiteSpace(city.Slug))
        await http.PostAsJsonAsync("/cities", city, ct);
    return Results.Redirect("/cities");
});

app.MapPost("/cities/{slug}/delete", async (string slug, CancellationToken ct) =>
{
    await http.DeleteAsync($"/cities/{slug}", ct);
    return Results.Redirect("/cities");
});

// ── Types ──
app.MapGet("/types", async (CancellationToken ct) =>
{
    var types = await http.GetFromJsonAsync<List<TypeDto>>("/types", ct) ?? [];
    var cats = await http.GetFromJsonAsync<List<CatDto>>("/categories", ct) ?? [];
    return Results.Content(BuildTypeListPage(types, cats), "text/html; charset=utf-8");
});

app.MapPost("/types", async (HttpContext ctx, CancellationToken ct) =>
{
    var form = await ctx.Request.ReadFormAsync(ct);
    var type = new TypeDto
    {
        Slug = form["slug"].FirstOrDefault() ?? "",
        Name = form["name"].FirstOrDefault() ?? "",
        CategorySlug = form["categorySlug"].FirstOrDefault() ?? ""
    };
    if (!string.IsNullOrWhiteSpace(type.Slug))
        await http.PostAsJsonAsync("/types", type, ct);
    return Results.Redirect("/types");
});

app.MapPost("/types/{slug}/delete", async (string slug, CancellationToken ct) =>
{
    await http.DeleteAsync($"/types/{slug}", ct);
    return Results.Redirect("/types");
});

app.Run();

// ── Helpers ──

static EventDto FormToEvent(IFormCollection form) => new()
{
    Description = form["description"].FirstOrDefault() ?? "",
    Location = form["location"].FirstOrDefault() ?? "",
    Address = form["address"].FirstOrDefault() ?? "",
    Category = form["category"].FirstOrDefault() ?? "",
    Type = form["type"].FirstOrDefault() ?? "",
    TypeDescription = form["typeDescription"].FirstOrDefault() ?? "",
    City = form["city"].FirstOrDefault() ?? "",
    Start = DateTime.TryParse(form["start"].FirstOrDefault(), out var s) ? s : DateTime.UtcNow,
    End = DateTime.TryParse(form["end"].FirstOrDefault(), out var e) ? e : DateTime.UtcNow,
    SourceUrl = form["sourceUrl"].FirstOrDefault() ?? "",
};

static string BuildListPage(List<EventDto> events)
{
    var sb = new StringBuilder();
    PageStart(sb, "События");
    sb.AppendLine("<h1>📅 Управление событиями</h1>");
    sb.AppendLine("<a class='btn' href='/create' style='background:#1565C0;margin-bottom:1rem;'>+ Новое событие</a>");

    var grouped = events.OrderBy(e => e.Start).GroupBy(e => e.CityDescription ?? "—");

    foreach (var city in grouped)
    {
        sb.AppendLine($"<h2>🏙️ {HtmlEncoder.Default.Encode(city.Key)}</h2>");
        sb.AppendLine("<table class='event-table'>");
        sb.AppendLine("<tr><th>Дата</th><th>Описание</th><th>Место</th><th>Категория</th><th></th></tr>");

        foreach (var ev in city)
        {
            sb.AppendLine("<tr>");
            sb.AppendLine($"<td>{ev.Start:dd.MM.yyyy HH:mm}</td>");
            sb.AppendLine($"<td>{HtmlEncoder.Default.Encode(ev.Description)}</td>");
            sb.AppendLine($"<td>{HtmlEncoder.Default.Encode(ev.Location ?? "")}</td>");
            sb.AppendLine($"<td><span class='cat-tag'>{HtmlEncoder.Default.Encode(ev.CategoryDescription ?? ev.Category ?? "")}</span></td>");
            sb.AppendLine("<td class='actions'>");
            sb.AppendLine($"<a href='/{ev.Id}/edit' class='btn-sm'>✏️</a>");
            sb.AppendLine($"<form method='post' action='/{ev.Id}/delete' style='display:inline' onsubmit='return confirm(\"Удалить?\")'><button class='btn-sm btn-del'>🗑️</button></form>");
            sb.AppendLine("</td></tr>");
        }
        sb.AppendLine("</table>");
    }

    PageEnd(sb);
    return sb.ToString();
}

static string BuildFormPage(EventDto? ev, List<CatDto> cats, List<CityDto> cities, List<TypeDto> types)
{
    var isNew = ev is null;
    var id = ev?.Id.ToString() ?? "";
    var title = isNew ? "Новое событие" : "Редактирование";

    var sb = new StringBuilder();
    PageStart(sb, title);
    sb.AppendLine($"<h1>{title}</h1>");
    sb.AppendLine($"<form method='post' action='/{(isNew ? "create" : $"{id}/edit")}' class='form'>");

    Input(sb, "Описание", "description", ev?.Description ?? "", true);
    Input(sb, "Место", "location", ev?.Location ?? "");
    Input(sb, "Адрес", "address", ev?.Address ?? "");

    sb.AppendLine("<label>Тип</label><select name='type'>");
    sb.AppendLine("<option value=''>—</option>");
    foreach (var t in types)
    {
        var sel = t.Slug == ev?.Type ? "selected" : "";
        var catName = cats.FirstOrDefault(c => c.Slug == t.CategorySlug)?.Name ?? "";
        var label = string.IsNullOrEmpty(catName) ? t.Name : $"{t.Name} ({catName})";
        sb.AppendLine($"<option value='{t.Slug}' {sel}>{HtmlEncoder.Default.Encode(label)}</option>");
    }
    sb.AppendLine("</select>");

    sb.AppendLine("<label>Категория</label><select name='category'>");
    foreach (var cat in cats)
    {
        var sel = cat.Slug == ev?.Category ? "selected" : "";
        sb.AppendLine($"<option value='{cat.Slug}' {sel}>{HtmlEncoder.Default.Encode(cat.Name)}</option>");
    }
    sb.AppendLine("</select>");

    sb.AppendLine("<label>Город</label><select name='city'>");
    foreach (var city in cities)
    {
        var sel = city.Slug == ev?.City ? "selected" : "";
        sb.AppendLine($"<option value='{city.Slug}' {sel}>{HtmlEncoder.Default.Encode(city.Name)}</option>");
    }
    sb.AppendLine("</select>");

    Input(sb, "Начало", "start", ev?.Start.ToString("yyyy-MM-ddTHH:mm") ?? "", true, "datetime-local");
    Input(sb, "Окончание", "end", ev?.End.ToString("yyyy-MM-ddTHH:mm") ?? "", true, "datetime-local");
    Input(sb, "Source URL", "sourceUrl", ev?.SourceUrl ?? "");

    sb.AppendLine("<div class='form-actions'>");
    sb.AppendLine("<button type='submit' class='btn' style='background:#1565C0;'>Сохранить</button>");
    sb.AppendLine("<a href='/' class='btn' style='background:#888;'>Отмена</a>");
    sb.AppendLine("</div></form>");

    PageEnd(sb);
    return sb.ToString();
}

static void Input(StringBuilder sb, string label, string name, string value, bool required = false, string type = "text")
{
    var req = required ? "required" : "";
    sb.AppendLine($"<label>{label}</label>");
    sb.AppendLine($"<input type='{type}' name='{name}' value='{HtmlEncoder.Default.Encode(value)}' {req}>");
}

static void PageStart(StringBuilder sb, string title)
{
    sb.AppendLine("<!DOCTYPE html><html lang='ru'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>");
    sb.AppendLine($"<title>Админ — {title}</title>");
    sb.AppendLine("<style>");
    sb.AppendLine("*{box-sizing:border-box;margin:0;padding:0}");
    sb.AppendLine("body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;background:#f0f4f8;color:#1a1a2e;padding:2rem;max-width:900px;margin:auto}");
    sb.AppendLine("h1{font-size:1.6rem;margin-bottom:1rem}");
    sb.AppendLine("h2{font-size:1.2rem;margin:1.5rem 0 0.5rem}");
    sb.AppendLine(".btn{display:inline-block;padding:0.5rem 1.2rem;color:#fff;border-radius:8px;text-decoration:none;font-weight:600;font-size:0.9rem;border:none;cursor:pointer}");
    sb.AppendLine(".btn-sm{display:inline-block;padding:0.3rem 0.6rem;color:#fff;border-radius:6px;text-decoration:none;font-weight:600;font-size:0.85rem;background:#1565C0;border:none;cursor:pointer}");
    sb.AppendLine(".btn-del{background:#C62828}");
    sb.AppendLine("table{border-collapse:collapse;width:100%;background:#fff;border-radius:8px;overflow:hidden;box-shadow:0 2px 8px rgba(0,0,0,.06)}");
    sb.AppendLine("th,td{padding:0.6rem 0.8rem;text-align:left;font-size:0.9rem;border-bottom:1px solid #eee}");
    sb.AppendLine("th{background:#f5f5f5;font-weight:600}");
    sb.AppendLine("td.actions{white-space:nowrap}");
    sb.AppendLine(".cat-tag{background:#E3F2FD;color:#1565C0;padding:0.1rem 0.5rem;border-radius:10px;font-size:0.8rem}");
    sb.AppendLine(".form{background:#fff;padding:1.5rem;border-radius:12px;box-shadow:0 2px 8px rgba(0,0,0,.06)}");
    sb.AppendLine(".form label{display:block;margin-top:1rem;font-weight:600;font-size:0.9rem}");
    sb.AppendLine(".form input,.form select{display:block;width:100%;padding:0.5rem;margin-top:0.25rem;border:1px solid #ddd;border-radius:6px;font-size:0.9rem}");
    sb.AppendLine(".form-actions{display:flex;gap:0.5rem;margin-top:1.5rem}");
    sb.AppendLine("</style></head><body>");
    sb.AppendLine("<nav style='margin-bottom:1.5rem;display:flex;gap:1rem;'><a href='/'>📅 События</a> · <a href='/categories'>🏷️ Категории</a> · <a href='/types'>📌 Типы</a> · <a href='/cities'>🏙️ Города</a></nav>");
}

static void PageEnd(StringBuilder sb) => sb.AppendLine("</body></html>");

static string BuildCatListPage(List<CatDto> cats)
{
    var sb = new StringBuilder();
    PageStart(sb, "Категории");
    sb.AppendLine("<h1>🏷️ Категории</h1>");

    sb.AppendLine("<table><tr><th>Slug</th><th>Название</th><th>Иконка</th><th>Цвет</th><th></th></tr>");
    foreach (var c in cats)
    {
        sb.AppendLine("<tr>");
        sb.AppendLine($"<td>{HtmlEncoder.Default.Encode(c.Slug)}</td>");
        sb.AppendLine($"<td>{HtmlEncoder.Default.Encode(c.Name)}</td>");
        sb.AppendLine($"<td>{HtmlEncoder.Default.Encode(c.Icon ?? "")}</td>");
        sb.AppendLine($"<td><span style='display:inline-block;width:20px;height:20px;border-radius:50%;background:{c.Color};vertical-align:middle;'></span> {c.Color}</td>");
        sb.AppendLine($"<td class='actions'><form method='post' action='/categories/{c.Slug}/delete' style='display:inline' onsubmit='return confirm(\"Удалить {HtmlEncoder.Default.Encode(c.Name)}?\")'><button class='btn-sm btn-del'>🗑️</button></form></td>");
        sb.AppendLine("</tr>");
    }
    sb.AppendLine("</table>");

    sb.AppendLine("<h2 style='margin-top:1.5rem;'>+ Добавить категорию</h2>");
    sb.AppendLine("<form method='post' action='/categories' class='form'>");
    Input(sb, "Slug (латиница)", "slug", "", true);
    Input(sb, "Название", "name", "", true);
    Input(sb, "Иконка (emoji)", "icon", "");
    Input(sb, "Цвет (#HEX)", "color", "#1565C0");
    sb.AppendLine("<div class='form-actions'><button type='submit' class='btn' style='background:#1565C0;'>Добавить</button></div>");
    sb.AppendLine("</form>");

    PageEnd(sb);
    return sb.ToString();
}

static string BuildCityListPage(List<CityDto> cities)
{
    var sb = new StringBuilder();
    PageStart(sb, "Города");
    sb.AppendLine("<h1>🏙️ Города</h1>");

    sb.AppendLine("<table><tr><th>Slug</th><th>Название</th><th></th></tr>");
    foreach (var c in cities)
    {
        sb.AppendLine("<tr>");
        sb.AppendLine($"<td>{HtmlEncoder.Default.Encode(c.Slug)}</td>");
        sb.AppendLine($"<td>{HtmlEncoder.Default.Encode(c.Name)}</td>");
        sb.AppendLine($"<td class='actions'><form method='post' action='/cities/{c.Slug}/delete' style='display:inline' onsubmit='return confirm(\"Удалить {HtmlEncoder.Default.Encode(c.Name)}?\")'><button class='btn-sm btn-del'>🗑️</button></form></td>");
        sb.AppendLine("</tr>");
    }
    sb.AppendLine("</table>");

    sb.AppendLine("<h2 style='margin-top:1.5rem;'>+ Добавить город</h2>");
    sb.AppendLine("<form method='post' action='/cities' class='form'>");
    Input(sb, "Slug (латиница)", "slug", "", true);
    Input(sb, "Название", "name", "", true);
    sb.AppendLine("<div class='form-actions'><button type='submit' class='btn' style='background:#1565C0;'>Добавить</button></div>");
    sb.AppendLine("</form>");

    PageEnd(sb);
    return sb.ToString();
}

static string BuildTypeListPage(List<TypeDto> types, List<CatDto> cats)
{
    var sb = new StringBuilder();
    PageStart(sb, "Типы");
    sb.AppendLine("<h1>📌 Типы</h1>");

    sb.AppendLine("<table><tr><th>Slug</th><th>Название</th><th>Категория</th><th></th></tr>");
    foreach (var t in types)
    {
        var catName = cats.FirstOrDefault(c => c.Slug == t.CategorySlug)?.Name ?? t.CategorySlug ?? "—";
        sb.AppendLine("<tr>");
        sb.AppendLine($"<td>{HtmlEncoder.Default.Encode(t.Slug)}</td>");
        sb.AppendLine($"<td>{HtmlEncoder.Default.Encode(t.Name)}</td>");
        sb.AppendLine($"<td>{HtmlEncoder.Default.Encode(catName)}</td>");
        sb.AppendLine($"<td class='actions'><form method='post' action='/types/{t.Slug}/delete' style='display:inline' onsubmit='return confirm(\"Удалить {HtmlEncoder.Default.Encode(t.Name)}?\")'><button class='btn-sm btn-del'>🗑️</button></form></td>");
        sb.AppendLine("</tr>");
    }
    sb.AppendLine("</table>");

    sb.AppendLine("<h2 style='margin-top:1.5rem;'>+ Добавить тип</h2>");
    sb.AppendLine("<form method='post' action='/types' class='form'>");
    Input(sb, "Slug (латиница)", "slug", "", true);
    Input(sb, "Название", "name", "", true);
    sb.AppendLine("<label>Категория</label><select name='categorySlug'>");
    sb.AppendLine("<option value=''>—</option>");
    foreach (var cat in cats)
    {
        sb.AppendLine($"<option value='{cat.Slug}'>{HtmlEncoder.Default.Encode(cat.Name)}</option>");
    }
    sb.AppendLine("</select>");
    sb.AppendLine("<div class='form-actions'><button type='submit' class='btn' style='background:#1565C0;'>Добавить</button></div>");
    sb.AppendLine("</form>");

    PageEnd(sb);
    return sb.ToString();
}

// ── DTOs ──

public class EventDto
{
    public Guid Id { get; set; }
    public string Description { get; set; } = "";
    public string? Location { get; set; }
    public string? Address { get; set; }
    public string? Category { get; set; }
    public string? CategoryDescription { get; set; }
    public string? Type { get; set; }
    public string? TypeDescription { get; set; }
    public string? City { get; set; }
    public string? CityDescription { get; set; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string? SourceUrl { get; set; }
}

public class CatDto
{
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Icon { get; set; }
    public string? Color { get; set; }
}

public class CityDto
{
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
}

public class TypeDto
{
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string? CategorySlug { get; set; }
}
