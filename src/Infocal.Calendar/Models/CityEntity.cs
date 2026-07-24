namespace Infocal.Calendar.Models;

/// <summary>
/// City reference entity. Slug is the primary key.
/// </summary>
public class CityEntity
{
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
}
