namespace Infocal.Calendar.Models;

/// <summary>
/// Category reference entity. Slug is the primary key.
/// </summary>
public class CategoryEntity
{
    public string Slug { get; init; } = "";
    public string Name { get; set; } = "";
    public string? Icon { get; set; }
    public string? Color { get; set; }
}
