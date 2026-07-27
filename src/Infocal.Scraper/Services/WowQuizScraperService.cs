using System.Text.Json;
using System.Text.Json.Serialization;

namespace Infocal.Scraper.Services;

/// <summary>
/// Scrapes WowQuiz schedule from api.etowow.ru.
/// Filters games for a specific franchise (city).
/// </summary>
public class WowQuizScraperService
{
    private readonly HttpClient _http;
    private readonly ILogger<WowQuizScraperService> _logger;

    private const string ApiBase = "https://api.etowow.ru";
    private const string Domain = "https://gomel.wowquiz.ru";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public WowQuizScraperService(HttpClient http, ILogger<WowQuizScraperService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<IReadOnlyList<QuizGame>> GetScheduleAsync(CancellationToken ct = default)
    {
        var allGames = new List<QuizGame>();
        var domainEncoded = Uri.EscapeDataString(Domain);

        var page = 1;
        var totalPages = int.MaxValue;

        while (page <= totalPages)
        {
            var url = $"{ApiBase}/games/all?upcoming=1&page={page}&domain={domainEncoded}";
            _logger.LogDebug("Загрузка {Url}", url);

            HttpResponseMessage resp;
            try
            {
                resp = await _http.GetAsync(url, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка запроса к API WowQuiz");
                break;
            }

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("WowQuiz API вернул {Status}", (int)resp.StatusCode);
                break;
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            var apiResp = JsonSerializer.Deserialize<WowQuizResponse>(body, JsonOpts);

            if (apiResp?.Data?.Games is not { Count: > 0 })
                break;

            allGames.AddRange(apiResp.Data.Games);
            totalPages = apiResp.Data.PageCount;
            page++;
        }

        _logger.LogInformation("Найдено {Count} предстоящих игр WowQuiz для Гомеля", allGames.Count);
        return allGames;
    }
}

// ── API response DTOs ──

public class WowQuizResponse
{
    public string Status { get; set; } = "";
    public int Code { get; set; }
    public WowQuizData? Data { get; set; }
}

public class WowQuizData
{
    public List<QuizGame> Games { get; set; } = [];
    public int PageCount { get; set; }
    public int PerPage { get; set; }
}

public class QuizGame
{
    public int Id { get; set; }
    public int FranchiseId { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    public string? Description { get; set; }

    [JsonPropertyName("template")]
    public string Template { get; set; } = "";

    public string? Theme { get; set; }
    public decimal Price { get; set; }
    public string Currency { get; set; } = "BYN";

    [JsonPropertyName("date")]
    public string DateRaw { get; set; } = "";

    [JsonIgnore]
    public DateTime Date => DateTime.TryParse(DateRaw, out var d) ? d : DateTime.MinValue;

    public QuizBar? Bar { get; set; }
}

public class QuizBar
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    public string Address { get; set; } = "";
}
