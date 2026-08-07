namespace Infocal.Calendar.Models;

/// <summary>
/// Calendar event entity stored in SQLite.
/// Category and City store slugs (FKs to reference tables).
/// *_Description are denormalized display names.
/// </summary>
public class EventItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Summary { get; set; } = "";

    public string Description { get; set; } = "";

    public string Address { get; set; } = "";

    public string Category { get; set; } = "";

    public string CategoryDescription { get; set; } = "";

    public string Type { get; set; } = "";

    public string TypeDescription { get; set; } = "";

    public string City { get; set; } = "";

    public string CityDescription { get; set; } = "";

    public DateTime Start { get; init; }

    public DateTime End { get; set; }

    public bool IsAllDay { get; set; }

    public string? SourceUrl { get; init; }
}
