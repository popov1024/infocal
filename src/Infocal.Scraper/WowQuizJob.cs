using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Infocal.Scraper.Services;
using Quartz;

namespace Infocal.Scraper;

/// <summary>
/// Quartz job that scrapes WowQuiz schedule for Gomel
/// and pushes events to the Calendar API.
/// </summary>
public class WowQuizJob : IJob
{
    private readonly WowQuizScraperService _scraper;
    private readonly HttpClient _apiHttp;
    private readonly ILogger<WowQuizJob> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private const string SourceUrl = "https://gomel.wowquiz.ru/schedule";

    public WowQuizJob(WowQuizScraperService scraper, IHttpClientFactory httpFactory, ILogger<WowQuizJob> logger)
    {
        _scraper = scraper;
        _apiHttp = httpFactory.CreateClient("CalendarApi");
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("🧠 Поиск расписания WowQuiz Гомель...");

        try
        {
            var games = await _scraper.GetScheduleAsync(context.CancellationToken);
            if (games.Count == 0)
            {
                _logger.LogInformation("⏳ Нет предстоящих игр WowQuiz");
                return;
            }

            _logger.LogInformation("📄 Найдено {Count} предстоящих игр", games.Count);

            // Delete old events for this source
            try
            {
                var deleteUrl = $"/events/by-source?sourceUrl={Uri.EscapeDataString(SourceUrl)}";
                await _apiHttp.DeleteAsync(deleteUrl, context.CancellationToken);
                _logger.LogDebug("🗑️ Старые события WowQuiz удалены");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Не удалось удалить старые события");
            }

            // Push new events
            int pushed = 0, failed = 0;
            foreach (var game in games)
            {
                try
                {
                    // Duration: assume 2 hours for quiz games
                    var start = game.Date;
                    var end = start.AddHours(2);

                    var title = game.Theme is { Length: > 0 }
                        ? $"ВАУ КВИЗ: {game.Title} — {game.Theme}"
                        : $"ВАУ КВИЗ: {game.Title}";

                    var venue = game.Bar?.Title ?? "";
                    var address = game.Bar?.Address ?? "";

                    var payload = new
                    {
                        description = title,
                        location = venue,
                        address = address,
                        city = "Гомель",
                        start = start,
                        end = end,
                        category = "Квиз",
                        sourceUrl = SourceUrl,
                        isAllDay = false
                    };

                    var json = JsonSerializer.Serialize(payload, JsonOpts);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    var resp = await _apiHttp.PostAsync("/events", content, context.CancellationToken);

                    if (resp.IsSuccessStatusCode)
                    {
                        pushed++;
                        _logger.LogInformation("   ✅ {Date:dd.MM HH:mm} {Title}", start, title);
                    }
                    else
                    {
                        failed++;
                        var body = await resp.Content.ReadAsStringAsync(context.CancellationToken);
                        _logger.LogWarning("   ❌ {Date:dd.MM HH:mm} — HTTP {Status}: {Body}",
                            start, (int)resp.StatusCode, body);
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogWarning(ex, "   ⚡ {Date:dd.MM HH:mm} — ошибка сети", game.Date);
                }
            }

            if (pushed > 0)
                _logger.LogInformation("🎉 WowQuiz: {Pushed}/{Total} отправлено, {Failed} ошибок", pushed, games.Count, failed);
            else
                _logger.LogWarning("⚠️ WowQuiz: ни одно событие не отправлено ({Failed} ошибок)", failed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при скрапинге WowQuiz");
        }
    }
}
