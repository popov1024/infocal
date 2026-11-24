using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Infocal.Scraper;

/// <summary>
/// Quartz job: скрапит расписание квиза «УмаПарк» (Гомель) из текстовых постов VK
/// и отправляет события в Calendar API.
/// </summary>
public class UmaparkQuizJob : IJob
{
    private readonly UmaparkQuizScraperService _scraper;
    private readonly HttpClient _apiHttp;
    private readonly ILogger<UmaparkQuizJob> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const string TypeSlug = "umapark";

    private sealed record ExistingEvent(string? SourceUrl, DateTime Start);

    public UmaparkQuizJob(UmaparkQuizScraperService scraper, IHttpClientFactory httpFactory, ILogger<UmaparkQuizJob> logger)
    {
        _scraper = scraper;
        _apiHttp = httpFactory.CreateClient("CalendarApi");
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("🎩 Поиск расписания УмаПарк Гомель...");

        try
        {
            var games = await _scraper.GetUpcomingGamesAsync(context.CancellationToken);

            // null — источник недоступен: существующие события не трогаем
            if (games is null)
            {
                _logger.LogError("УмаПарк: не удалось получить расписание, события не изменялись");
                return;
            }

            // Пустой список — анонсов нет: убираем только прошедшие события,
            // будущие не трогаем, чтобы сбой разбора не стёр расписание
            var existing = await LoadExistingEventsAsync(context.CancellationToken);

            var current = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            foreach (var game in games)
                current[game.SourceUrl] = game.Start;

            var deleted = await DeleteStaleEventsAsync(
                existing, current, games.Count > 0, context.CancellationToken);

            int pushed = 0, failed = 0;
            foreach (var game in games)
            {
                try
                {
                    var payload = new
                    {
                        // Префикс источника, как у «ВАУ КВИЗ:» и «Смузи:», — чтобы тип был виден в заголовке события
                        summary = $"УмаПарк: {game.Title}",
                        description = game.Description,
                        address = string.IsNullOrWhiteSpace(game.Venue)
                            ? game.Address
                            : string.IsNullOrWhiteSpace(game.Address)
                                ? game.Venue
                                : $"{game.Venue}, {game.Address}",
                        city = "Гомель",
                        type = TypeSlug,
                        typeDescription = "УмаПарк",
                        start = game.Start,
                        // Игра идёт 2,5 часа — так написано в анонсах квиза
                        end = game.Start.AddMinutes(150),
                        category = "quiz",
                        sourceUrl = game.SourceUrl,
                        isAllDay = false
                    };

                    var json = JsonSerializer.Serialize(payload, JsonOpts);
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    using var resp = await _apiHttp.PostAsync("/events", content, context.CancellationToken);

                    if (resp.IsSuccessStatusCode)
                    {
                        pushed++;
                        _logger.LogInformation("   ✅ {Date:dd.MM HH:mm} {Title}", game.Start, game.Title);
                    }
                    else
                    {
                        failed++;
                        var body = await resp.Content.ReadAsStringAsync(context.CancellationToken);
                        _logger.LogWarning("   ❌ {Date:dd.MM HH:mm} — HTTP {Status}: {Body}",
                            game.Start, (int)resp.StatusCode, body);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
                {
                    failed++;
                    _logger.LogWarning(ex, "   ⚡ {Date:dd.MM HH:mm} — ошибка сети", game.Start);
                }
            }

            _logger.LogInformation("🎉 УмаПарк: {Pushed} отправлено, {Deleted} удалено, {Failed} ошибок",
                pushed, deleted, failed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при скрапинге УмаПарк");
        }
    }

    /// <summary>
    /// Существующие события типа umapark. При сбое возвращает пустой набор,
    /// чтобы рассинхрон источника не блокировал отправку новых игр.
    /// </summary>
    private async Task<List<ExistingEvent>> LoadExistingEventsAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _apiHttp.GetAsync($"/events?type={TypeSlug}", ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("УмаПарк: не удалось прочитать существующие события — HTTP {Status}",
                    (int)resp.StatusCode);
                return [];
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<List<ExistingEvent>>(body, ReadOpts) ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            _logger.LogWarning(ex, "УмаПарк: не удалось прочитать существующие события");
            return [];
        }
    }

    /// <summary>
    /// Удаляет события, которых больше нет в источнике или у которых изменилось время.
    /// Один sourceUrl удаляется не более одного раза. При allowFutureDelete = false
    /// (источник не дал ни одной игры) будущие события не удаляются.
    /// </summary>
    private async Task<int> DeleteStaleEventsAsync(
        List<ExistingEvent> existing,
        Dictionary<string, DateTime> current,
        bool allowFutureDelete,
        CancellationToken ct)
    {
        var deleted = 0;
        var handled = new HashSet<string>(StringComparer.Ordinal);
        var now = DateTime.Now;

        foreach (var ev in existing)
        {
            var sourceUrl = ev.SourceUrl;
            if (string.IsNullOrWhiteSpace(sourceUrl))
                continue;

            if (current.TryGetValue(sourceUrl, out var start) && start == ev.Start)
                continue;

            if (!allowFutureDelete && ev.Start >= now)
                continue;

            if (!handled.Add(sourceUrl))
                continue;

            try
            {
                var deleteUrl = $"/events/by-source?sourceUrl={Uri.EscapeDataString(sourceUrl)}";
                using var resp = await _apiHttp.DeleteAsync(deleteUrl, ct);

                if (resp.IsSuccessStatusCode)
                {
                    deleted++;
                }
                else
                {
                    _logger.LogWarning("УмаПарк: удаление {SourceUrl} — HTTP {Status}",
                        sourceUrl, (int)resp.StatusCode);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                _logger.LogWarning(ex, "УмаПарк: не удалось удалить устаревшее событие {SourceUrl}", sourceUrl);
            }
        }

        return deleted;
    }
}
