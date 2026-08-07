using Ical.Net;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Infocal.Calendar.Models;

namespace Infocal.Calendar.Services;

public class CalendarService
{
    public string GenerateIcs(
        IReadOnlyList<EventItem> events,
        string calendarName,
        string? description = null,
        string color = "#1565C0")
    {
        var calendar = new Ical.Net.Calendar
        {
            ProductId = "-//Infocal//Calendar",
            Method = "PUBLISH",
            Scale = "GREGORIAN"
        };

        calendar.AddProperty("NAME", calendarName);
        calendar.AddProperty("X-WR-CALNAME", calendarName);
        if (!string.IsNullOrWhiteSpace(description))
            calendar.AddProperty("X-WR-CALDESC", description);

        calendar.AddProperty("X-APPLE-CALENDAR-COLOR", color);
        calendar.AddProperty("COLOR", color);
        calendar.AddProperty("REFRESH-INTERVAL;VALUE=DURATION", "PT12H");
        calendar.AddProperty("X-PUBLISHED-TTL", "PT12H");

        foreach (var ev in events)
        {
            var desc = ev.Description ?? "";
            if (!string.IsNullOrWhiteSpace(ev.SourceUrl))
            {
                if (desc.Length > 0)
                    desc += "\n";
                desc += ev.SourceUrl;
            }

            var calEvent = new Ical.Net.CalendarComponents.CalendarEvent
            {
                Uid = ev.Id.ToString(),
                Summary = ev.Summary,
                Description = desc.Length > 0 ? desc : null,
                Location = ev.CityDescription != null && ev.Address != null
                    ? $"{ev.CityDescription}, {ev.Address}"
                    : ev.CityDescription ?? ev.Address ?? "",
                DtStamp = new CalDateTime(DateTime.UtcNow)
            };

            if (ev.IsAllDay)
            {
                calEvent.DtStart = new CalDateTime(ev.Start.Date);
                calEvent.DtEnd = new CalDateTime(ev.End.Date);
            }
            else
            {
                calEvent.DtStart = new CalDateTime(ev.Start);
                calEvent.DtEnd = new CalDateTime(ev.End);
            }

            if (!string.IsNullOrWhiteSpace(ev.CategoryDescription))
                calEvent.Categories.Add(ev.CategoryDescription);
            if (!string.IsNullOrWhiteSpace(ev.TypeDescription) && ev.TypeDescription != ev.CategoryDescription)
                calEvent.Categories.Add(ev.TypeDescription);

            calendar.Events.Add(calEvent);
        }

        var serializer = new CalendarSerializer();
        return serializer.SerializeToString(calendar) ?? string.Empty;
    }

    /// <summary>
    /// Category → color mapping for visual distinction in Calendar apps.
    /// </summary>
    public static string GetCategoryColor(string category) =>
        SlugMap.GetCategoryColor(category);

    /// <summary>
    /// Category → icon emoji for display on the landing page.
    /// </summary>
    public static string GetCategoryIcon(string category) =>
        SlugMap.GetCategoryIcon(category);
}
