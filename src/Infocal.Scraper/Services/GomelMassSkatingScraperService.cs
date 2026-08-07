using HtmlAgilityPack;
using Infocal.Scraper.Models;

namespace Infocal.Scraper.Services;

/// <summary>
/// Scrapes mass skating schedule from gomel.hockey.by
/// </summary>
public class GomelMassSkatingScraperService(HttpClient http, ILogger<GomelMassSkatingScraperService> logger)
{
    private const string EventsPage = "/news/sobytie/";
    private const string SchedulePrefix = "Расписание массовых катаний";

    /// <summary>
    /// Discover all schedule post URLs from the events listing page.
    /// Returns newest first.
    /// </summary>
    public async Task<IReadOnlyList<(string url, string title)>> DiscoverSchedulePostsAsync(CancellationToken ct = default)
    {
        var mainHtml = await http.GetStringAsync(EventsPage, ct);
        var doc = new HtmlDocument();
        doc.LoadHtml(mainHtml);

        var posts = new List<(string url, string title)>();

        var titleNodes = doc.DocumentNode.SelectNodes("//div[@class='title']/a");
        if (titleNodes is null) return posts;

        foreach (var a in titleNodes)
        {
            var title = HtmlEntity.DeEntitize(a.InnerText).Trim();
            if (title.StartsWith(SchedulePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var href = a.GetAttributeValue("href", "");
                if (!string.IsNullOrEmpty(href))
                {
                    // Make absolute if relative
                    if (!href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        href = http.BaseAddress?.ToString().TrimEnd('/') + href;
                    posts.Add((href, title));
                }
            }
        }

        return posts;
    }

    /// <summary>
    /// Parse a single schedule post page into structured events.
    /// </summary>
    public async Task<IReadOnlyList<GomelEvent>> ParseSchedulePostAsync(string url, CancellationToken ct = default)
    {
        logger.LogDebug("Парсинг {Url}", url);

        var html = await http.GetAsync(url, ct) is { IsSuccessStatusCode: true } response
            ? await response.Content.ReadAsStringAsync(ct)
            : string.Empty;

        if (string.IsNullOrEmpty(html))
        {
            logger.LogWarning("Не удалось загрузить {Url}", url);
            return [];
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var article = doc.DocumentNode.SelectSingleNode("//article");
        if (article is null) return [];

        foreach (var bad in article.SelectNodes(".//script|.//style") ?? Enumerable.Empty<HtmlNode>())
            bad.Remove();

        var text = HtmlEntity.DeEntitize(article.InnerText);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        // Extract news publish date for the year
        var dateNode = doc.DocumentNode.SelectSingleNode("//span[@class='date']");
        int year = DateTime.UtcNow.Year;
        if (dateNode is not null)
        {
            var dateStr = HtmlEntity.DeEntitize(dateNode.InnerText).Trim();
            var parsed = DateParser.ParseFullDate(dateStr);
            if (parsed.HasValue) year = parsed.Value.Year;
        }

        var events = new List<GomelEvent>();
        foreach (var line in lines)
        {
            var slots = DateParser.ParseScheduleLine(line);
            if (slots.Count == 0) continue;

            foreach (var (day, month, start, end) in slots)
            {
                try
                {
                    var date = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
                    events.Add(new GomelEvent
                    {
                        Title = "Массовое катание",
                        Start = date + start,
                        End = date + end,
                        Location = "Ледовый дворец",
                        Address = "ул. Мазурова, 110",
                        City = "Гомель",
                        Category = "Массовое катание",
                        SourceUrl = url
                    });
                }
                catch (ArgumentOutOfRangeException)
                {
                    logger.LogWarning("Некорректная дата: {Day}.{Month}.{Year}", day, month, year);
                }
            }
        }

        return events;
    }

    /// <summary>
    /// Full pipeline: discover latest schedule, parse, return events.
    /// </summary>
    public async Task<IReadOnlyList<GomelEvent>> ScrapeLatestAsync(CancellationToken ct = default)
    {
        var posts = await DiscoverSchedulePostsAsync(ct);
        if (posts.Count == 0)
        {
            logger.LogWarning("Посты с расписанием не найдены");
            return [];
        }

        logger.LogInformation("Найден пост: {Title} ({Url})", posts[0].title, posts[0].url);

        // Parse the most recent one
        var (url, _) = posts[0];
        return await ParseSchedulePostAsync(url, ct);
    }
}
