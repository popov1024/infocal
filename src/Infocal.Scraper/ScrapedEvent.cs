using Infocal.Scraper.Models;

namespace Infocal.Scraper;

/// <summary>
/// C# 15 proof-of-concept: a discriminated union over the four parsed-event shapes
/// produced by the scraper sources. Demonstrates union declarations, implicit
/// conversions, and compiler-enforced exhaustive pattern matching.
///
/// This file is a demo and is NOT wired into the production jobs.
/// </summary>
public union ScrapedEvent(GomelEvent, QuizGame, MzgbGame, ExtractedGame)
{
    /// <summary>
    /// Maps any case to the canonical payload pushed to the Calendar API.
    /// Adding a new case type without a matching arm here produces a compile-time error.
    /// </summary>
    public ScrapedEventPayload ToPayload(string sourceUrl) => this switch
    {
        GomelEvent e => new(
            Title: e.Title,
            City: e.City,
            Category: e.Category,
            Start: e.Start,
            End: e.End,
            SourceUrl: sourceUrl),

        QuizGame g => new(
            Title: $"ВАУ КВИЗ: {g.Title}",
            City: "Гомель",
            Category: "Квиз",
            Start: g.Date,
            End: g.Date.AddHours(2),
            SourceUrl: sourceUrl),

        MzgbGame g => new(
            Title: $"Мозгобойня: {g.Game?.Name}",
            City: "Гомель",
            Category: "Квиз",
            Start: g.GetStartLocal() ?? DateTime.MinValue,
            End: g.GetEndLocal() ?? DateTime.MinValue,
            SourceUrl: sourceUrl),

        ExtractedGame g => new(
            Title: g.Title ?? "Квиз",
            City: "Гомель",
            Category: "Квиз",
            Start: ParseLocal(g.Date, g.Time),
            End: ParseLocal(g.Date, g.Time).AddHours(2),
            SourceUrl: sourceUrl),

        null => throw new InvalidOperationException("ScrapedEvent has no value"),
    };

    private static DateTime ParseLocal(string? date, string? time)
        => DateTime.TryParse($"{date} {time}", out var dt) ? dt : DateTime.MinValue;

    /// <summary>Demonstrates the implicit conversion from a case type to the union.</summary>
    public static ScrapedEventPayload FromGomel(GomelEvent e)
    {
        ScrapedEvent ev = e; // implicit union conversion
        return ev.ToPayload(e.SourceUrl ?? "https://gomel.hockey.by");
    }
}

/// <summary>Canonical event shape — mirrors the anonymous payloads the jobs build today.</summary>
public record ScrapedEventPayload(
    string Title, string City, string Category,
    DateTime Start, DateTime End, string SourceUrl);
