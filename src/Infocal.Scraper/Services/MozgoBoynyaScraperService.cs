using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Infocal.Scraper.Services;

/// <summary>
/// Scrapes MozgoBoynya (Мозгобойня) schedule from mzgb.by city subdomains.
/// Gets a session cookie + XSRF token, then calls /api/load-data.
/// </summary>
public class MozgoBoynyaScraperService
{
    private readonly HttpClient _http;
    private readonly ILogger<MozgoBoynyaScraperService> _logger;

    private readonly string _cityUrl; // e.g. https://gom.mzgb.by

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public MozgoBoynyaScraperService(HttpClient http, IConfiguration config, ILogger<MozgoBoynyaScraperService> logger)
    {
        _http = http;
        _logger = logger;
        _cityUrl = config["MozgoBoynya:CityUrl"] ?? "https://gom.mzgb.by";
    }

    /// <summary>
    /// Gets upcoming games for the configured city.
    /// Returns empty list if no games are scheduled.
    /// </summary>
    public async Task<IReadOnlyList<MzgbGame>> GetScheduleAsync(CancellationToken ct = default)
    {
        // Step 1: get a fresh session + XSRF token by visiting the home page
        _logger.LogInformation("Получение сессии с {Url}...", _cityUrl);

        var sessionCookies = new CookieContainer();
        string? xsrfToken = null;

        using (var sessionHandler = new HttpClientHandler { CookieContainer = sessionCookies })
        using (var sessionClient = new HttpClient(sessionHandler) { Timeout = TimeSpan.FromSeconds(30) })
        {
            sessionClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            var homeResp = await sessionClient.GetAsync(_cityUrl, ct);
            homeResp.EnsureSuccessStatusCode();

            // Extract XSRF token from cookies
            var cookies = sessionCookies.GetAllCookies();
            foreach (Cookie cookie in cookies)
            {
                if (cookie.Name == "XSRF-TOKEN")
                {
                    xsrfToken = Uri.UnescapeDataString(cookie.Value);
                    _logger.LogDebug("XSRF-TOKEN получен");
                }
            }

            if (xsrfToken is null)
            {
                _logger.LogWarning("XSRF-TOKEN не найден в cookies");
                return [];
            }

            // Step 2: call /api/load-data with the session
            var apiUrl = $"{_cityUrl}/api/load-data?page=main&locale=ru";

            using var apiRequest = new HttpRequestMessage(HttpMethod.Post, apiUrl);
            apiRequest.Headers.Add("X-XSRF-TOKEN", xsrfToken);
            apiRequest.Headers.Add("X-Requested-With", "XMLHttpRequest");
            apiRequest.Headers.Referrer = new Uri(_cityUrl);

            var apiResp = await sessionClient.SendAsync(apiRequest, ct);
            apiResp.EnsureSuccessStatusCode();

            var body = await apiResp.Content.ReadAsStringAsync(ct);
            var data = JsonSerializer.Deserialize<MzgbLoadDataResponse>(body, JsonOpts);

            if (data is null)
            {
                _logger.LogWarning("Не удалось десериализовать ответ load-data");
                return [];
            }

            var allGames = new List<MzgbGame>();
            if (data.UpcomingGames is { Count: > 0 })
                allGames.AddRange(data.UpcomingGames);
            if (data.CommonGames is { Count: > 0 })
                allGames.AddRange(data.CommonGames);

            _logger.LogInformation("Найдено {Count} игр Мозгобойни ({Upcoming} upcoming, {Common} common)",
                allGames.Count,
                data.UpcomingGames?.Count ?? 0,
                data.CommonGames?.Count ?? 0);

            return allGames;
        }
    }
}

// ── API response DTOs ──

public class MzgbLoadDataResponse
{
    [JsonPropertyName("upcomingGames")]
    public List<MzgbGame>? UpcomingGames { get; set; }

    [JsonPropertyName("commonGames")]
    public List<MzgbGame>? CommonGames { get; set; }
}

public class MzgbGame
{
    public int Id { get; set; }

    /// <summary>Date in YYYYMMDD format, e.g. "20250805"</summary>
    [JsonPropertyName("calendar_date")]
    public string CalendarDate { get; set; } = "";

    [JsonPropertyName("calendar_time_start")]
    public string? CalendarTimeStart { get; set; }

    [JsonPropertyName("calendar_time_end")]
    public string? CalendarTimeEnd { get; set; }

    /// <summary>Human-readable date, e.g. "5 августа, среда"</summary>
    [JsonPropertyName("event_date")]
    public string EventDate { get; set; } = "";

    /// <summary>Human-readable time, e.g. "19:30"</summary>
    [JsonPropertyName("event_time")]
    public string EventTime { get; set; } = "";

    [JsonPropertyName("is_online")]
    public bool IsOnline { get; set; }

    [JsonPropertyName("price")]
    public decimal Price { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "BYN";

    [JsonPropertyName("is_free")]
    public bool IsFree { get; set; }

    [JsonPropertyName("registrationEnabled")]
    public bool RegistrationEnabled { get; set; }

    [JsonPropertyName("registrationStart")]
    public string? RegistrationStart { get; set; }

    [JsonPropertyName("registrationText")]
    public string? RegistrationText { get; set; }

    [JsonPropertyName("img")]
    public string? Img { get; set; }

    [JsonPropertyName("venue")]
    public MzgbVenue? Venue { get; set; }

    [JsonPropertyName("game")]
    public MzgbGameInfo? Game { get; set; }

    /// <summary>TryParse calendar_date (YYYYMMDD) + calendar_time_start (HHmm) into DateTime (Minsk timezone).</summary>
    public DateTime? GetStartUtc()
    {
        if (CalendarDate.Length != 8) return null;
        if (!int.TryParse(CalendarDate[..4], out var y)) return null;
        if (!int.TryParse(CalendarDate[4..6], out var m)) return null;
        if (!int.TryParse(CalendarDate[6..8], out var d)) return null;

        var hour = 0;
        var min = 0;

        // calendar_time_start is HHmm format, e.g. "1900"
        if (CalendarTimeStart is { Length: >= 4 })
        {
            _ = int.TryParse(CalendarTimeStart[..2], out hour);
            _ = int.TryParse(CalendarTimeStart[2..4], out min);
        }
        else if (EventTime is { Length: >= 5 })
        {
            // Fallback: event_time is HH:mm format, e.g. "19:00"
            var parts = EventTime.Split(':');
            if (parts.Length >= 2)
            {
                _ = int.TryParse(parts[0], out hour);
                _ = int.TryParse(parts[1], out min);
            }
        }

        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Minsk");
            var unspecified = new DateTime(y, m, d, hour, min, 0, DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTimeToUtc(unspecified, tz);
        }
        catch
        {
            return new DateTime(y, m, d, hour, min, 0, DateTimeKind.Utc).AddHours(-3);
        }
    }

    /// <summary>TryParse end time, or fall back to start + 2h.</summary>
    public DateTime? GetEndUtc()
    {
        var start = GetStartUtc();
        if (start is null) return null;

        // calendar_time_end is HHmm format, e.g. "2100"
        if (CalendarTimeEnd is { Length: >= 4 })
        {
            if (int.TryParse(CalendarTimeEnd[..2], out var h) &&
                int.TryParse(CalendarTimeEnd[2..4], out var m))
            {
                try
                {
                    var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Minsk");
                    var endLocal = new DateTime(start.Value.Year, start.Value.Month, start.Value.Day, h, m, 0, DateTimeKind.Unspecified);
                    var endUtc = TimeZoneInfo.ConvertTimeToUtc(endLocal, tz);
                    if (endUtc < start) endUtc = endUtc.AddDays(1);
                    return endUtc;
                }
                catch
                {
                    // fall through to default
                }
            }
        }

        return start.Value.AddHours(2);
    }
}

public class MzgbVenue
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";

    [JsonPropertyName("max_players_in_team")]
    public int? MaxPlayersInTeam { get; set; }
}

public class MzgbGameInfo
{
    public string Name { get; set; } = "";

    [JsonPropertyName("category_id")]
    public int CategoryId { get; set; }

    public string Category { get; set; } = "";

    [JsonPropertyName("subcategory_description")]
    public string? SubcategoryDescription { get; set; }

    [JsonPropertyName("type_id")]
    public int TypeId { get; set; }

    public string Type { get; set; } = "";

    /// <summary>Returns the game type as a display string.
    /// For music quizzes (type_id=2) it's "Туц Туц Квиз",
    /// otherwise the category name ("Мозгобойня").</summary>
    public string GetDisplayType() => TypeId == 2
        ? "Туц Туц Квиз"
        : string.IsNullOrWhiteSpace(Category) ? Name : Category;
}
