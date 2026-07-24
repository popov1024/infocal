using System.Globalization;

namespace Infocal.Scraper.Services;

/// <summary>
/// Parses Russian date strings from the Gomel hockey website.
/// </summary>
public static class DateParser
{
    private static readonly Dictionary<string, int> RussianMonths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["января"] = 1,   ["январь"] = 1,
        ["февраля"] = 2,  ["февраль"] = 2,
        ["марта"] = 3,    ["март"] = 3,
        ["апреля"] = 4,   ["апрель"] = 4,
        ["мая"] = 5,      ["май"] = 5,
        ["июня"] = 6,     ["июнь"] = 6,
        ["июля"] = 7,     ["июль"] = 7,
        ["августа"] = 8,  ["август"] = 8,
        ["сентября"] = 9, ["сентябрь"] = 9,
        ["октября"] = 10, ["октябрь"] = 10,
        ["ноября"] = 11,  ["ноябрь"] = 11,
        ["декабря"] = 12, ["декабрь"] = 12,
    };

    /// <summary>
    /// Parse a full date like "29 июня 2026" → DateTime
    /// </summary>
    public static DateTime? ParseFullDate(ReadOnlySpan<char> text)
    {
        // "29 июня 2026"
        text = text.Trim();
        var parts = text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;

        if (!int.TryParse(parts[0], out var day)) return null;
        if (!RussianMonths.TryGetValue(parts[1], out var month)) return null;

        int year = DateTime.UtcNow.Year; // default to current year
        if (parts.Length >= 3 && int.TryParse(parts[2], out var parsedYear))
            year = parsedYear;

        try { return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc); }
        catch { return null; }
    }

    /// <summary>
    /// Parse schedule line like "1 июля (среда): 20:00 - 20:45." or
    /// "19 июля (воскресенье): 14:00 - 14:45, 20:00 - 20:45."
    /// Returns one entry per time slot.
    /// </summary>
    public static List<(int day, int month, TimeSpan start, TimeSpan end)> ParseScheduleLine(string line)
    {
        // "1 июля (среда): 20:00 - 20:45."
        // "19 июля (воскресенье): 14:00 - 14:45, 20:00 - 20:45."
        line = line.Trim().TrimEnd('.');

        var colonIdx = line.IndexOf(':');
        if (colonIdx < 0) return [];

        var leftPart = line[..colonIdx].Trim();  // "1 июля (среда)"
        var rightPart = line[(colonIdx + 1)..].Trim(); // "20:00 - 20:45" or "14:00 - 14:45, 20:00 - 20:45"

        // Parse left: day + month (ignore day-of-week in parens)
        var parenIdx = leftPart.IndexOf('(');
        if (parenIdx >= 0) leftPart = leftPart[..parenIdx].Trim();

        var leftParts = leftPart.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (leftParts.Length < 2) return [];
        if (!int.TryParse(leftParts[0], out var day)) return [];
        if (!RussianMonths.TryGetValue(leftParts[1], out var month)) return [];

        // Parse right: split by comma first (handles multiple ranges), then by dash
        var ranges = rightPart.Split(',', StringSplitOptions.TrimEntries);
        var result = new List<(int day, int month, TimeSpan start, TimeSpan end)>();

        foreach (var range in ranges)
        {
            var timeParts = range.Split('-', StringSplitOptions.TrimEntries);
            if (timeParts.Length != 2) continue;
            if (!TimeSpan.TryParse(timeParts[0], out var start)) continue;
            if (!TimeSpan.TryParse(timeParts[1], out var end)) continue;
            result.Add((day, month, start, end));
        }

        return result;
    }
}
