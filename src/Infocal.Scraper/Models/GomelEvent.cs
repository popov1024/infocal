namespace Infocal.Scraper.Models;

/// <summary>
/// Parsed event from Gomel Ice Palace website.
/// </summary>
public record GomelEvent
{
    public string Title { get; init; } = "Массовое катание";

    public string Location { get; init; } = "Ледовый дворец";

    public string Address { get; init; } = "ул. Мазурова, 110";

    public string City { get; init; } = "Гомель";

    public string Category { get; init; } = "Массовое катание";

    public DateTime Start { get; init; }

    public DateTime End { get; init; }

    public string? SourceUrl { get; init; }
}
