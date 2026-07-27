namespace Infocal.Calendar.Services;

/// <summary>
/// Registry of known categories and cities with Latin slugs for URL-safe identifiers.
/// </summary>
public static class SlugMap
{
    public sealed record Entry(string Slug, string Name, string Icon, string Color);

    public static readonly Entry[] Categories =
    [
        new("mass-skating",   "Массовое катание",    "⛸️", "#1565C0"),
        new("quiz",           "Квиз",                 "🧠", "#7B1FA2"),
        new("hockey",         "Хоккей",              "🏒", "#C62828"),
        new("figure-skating", "Фигурное катание",    "⛸️", "#6A1B9A"),
        new("shows",          "Шоу",                 "🎭", "#E65100"),
    ];

    public static readonly Entry[] Cities =
    [
        new("gomel", "Гомель", "🏙️", "#1565C0"),
    ];

    // ── Lookups ──

    public static Entry? FindCategoryBySlug(string slug) =>
        Categories.FirstOrDefault(c => c.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));

    public static Entry? FindCategoryByName(string name) =>
        Categories.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static Entry? FindCityBySlug(string slug) =>
        Cities.FirstOrDefault(c => c.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));

    public static Entry? FindCityByName(string name) =>
        Cities.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolve a category identifier (slug or name) to its display name.
    /// Returns the input unchanged if not found.
    /// </summary>
    public static string ResolveCategoryName(string id) =>
        FindCategoryBySlug(id)?.Name ?? FindCategoryByName(id)?.Name ?? id;

    /// <summary>
    /// Resolve a city identifier (slug or name) to its display name.
    /// Returns the input unchanged if not found.
    /// </summary>
    public static string ResolveCityName(string id) =>
        FindCityBySlug(id)?.Name ?? FindCityByName(id)?.Name ?? id;

    public static string GetCategoryColor(string name) =>
        FindCategoryByName(name)?.Color ?? "#1565C0";

    public static string GetCategoryIcon(string name) =>
        FindCategoryByName(name)?.Icon ?? "📅";
}
