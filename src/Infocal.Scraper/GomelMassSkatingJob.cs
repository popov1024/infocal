using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Infocal.Scraper.Services;
using Quartz;

namespace Infocal.Scraper;

/// <summary>
/// Quartz job that scrapes the Gomel ice palace mass skating schedule
/// and pushes events to the Calendar API.
/// Deduplicates via Calendar API: deletes old events by source URL before pushing new ones.
/// </summary>
public class GomelMassSkatingJob : IJob
{
    private readonly GomelMassSkatingScraperService _scraper;
    private readonly HttpClient _apiHttp;
    private readonly ILogger<GomelMassSkatingJob> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public GomelMassSkatingJob(GomelMassSkatingScraperService scraper, IHttpClientFactory httpFactory, ILogger<GomelMassSkatingJob> logger)
    {
        _scraper = scraper;
        _apiHttp = httpFactory.CreateClient("CalendarApi");
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("🔍 Поиск расписания массовых катаний Гомельского ледового дворца...");

        try
        {
            // Step 1: discover the latest schedule post URL
            var posts = await _scraper.DiscoverSchedulePostsAsync(context.CancellationToken);
            if (posts.Count == 0)
            {
                _logger.LogInformation("⏳ Расписание ещё не опубликовано");
                return;
            }

            var (url, title) = posts[0];
            _logger.LogInformation("📄 Новый пост: {Title} ({Url})", title, url);

            // Step 2: parse
            var events = await _scraper.ParseSchedulePostAsync(url, context.CancellationToken);
            if (events.Count == 0)
            {
                _logger.LogWarning("❌ Не удалось извлечь события из поста");
                return;
            }

            // Step 3: delete old events for this source
            try
            {
                var deleteUrl = $"/events/by-source?sourceUrl={Uri.EscapeDataString(url)}";
                await _apiHttp.DeleteAsync(deleteUrl, context.CancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️  Не удалось удалить старые события");
            }

            // Step 4: push events (upsert handles dedup)
            int pushed = 0;
            int failed = 0;
            foreach (var ev in events)
            {
                try
                {
                    var payload = new
                    {
                        summary = ev.Title,
                        description = ev.Location,
                        address = ev.Address,
                        city = ev.City,
                        type = "ice-palace",
                        typeDescription = "Ледовый дворец",
                        start = ev.Start,
                        end = ev.End,
                        category = ev.Category,
                        sourceUrl = url,
                        isAllDay = false
                    };

                    var json = JsonSerializer.Serialize(payload, JsonOpts);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    var resp = await _apiHttp.PostAsync("/events", content, context.CancellationToken);

                    if (resp.IsSuccessStatusCode)
                    {
                        pushed++;
                        _logger.LogInformation("   ✅ {Date:dd.MM HH:mm}-{End:HH:mm}", ev.Start, ev.End);
                    }
                    else
                    {
                        failed++;
                        var body = await resp.Content.ReadAsStringAsync(context.CancellationToken);
                        _logger.LogWarning("   ❌ {Date:dd.MM HH:mm} — HTTP {Status}: {Body}",
                            ev.Start, (int)resp.StatusCode, body);
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogWarning(ex, "   ⚡ {Date:dd.MM HH:mm} — ошибка сети", ev.Start);
                }
            }

            if (pushed > 0)
                _logger.LogInformation("🎉 Готово! {Pushed}/{Total} отправлено, {Failed} ошибок", pushed, events.Count, failed);
            else
                _logger.LogWarning("⚠️  Ни одно событие не отправлено ({Failed} ошибок). Будет повторная попытка по расписанию.", failed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при скрапинге");
        }
    }
}
