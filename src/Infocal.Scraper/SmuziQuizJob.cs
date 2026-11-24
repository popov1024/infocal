using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Infocal.Scraper;

/// <summary>
/// Quartz job: скрапит расписание квизов «Смузи» (Гомель)
/// и отправляет события в Calendar API.
/// </summary>
public class SmuziQuizJob : IJob
{
    private readonly SmuziQuizScraperService _scraper;
    private readonly HttpClient _apiHttp;
    private readonly ILogger<SmuziQuizJob> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record ExistingEvent(string? SourceUrl, DateTime Start);

    public SmuziQuizJob(SmuziQuizScraperService scraper, IHttpClientFactory httpFactory, ILogger<SmuziQuizJob> logger)
    {
        _scraper = scraper;
        _apiHttp = httpFactory.CreateClient("CalendarApi");
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("🥤 Поиск расписания Смузи Гомель...");

        try
        {
            var games = await _scraper.GetUpcomingGamesAsync(context.CancellationToken);

            // null — источник недоступен: существующие события не трогаем
            if (games is null)
            {
                _logger.LogError("Смузи: не удалось получить расписание, события не изменялись");
                return;
            }

            if (games.Count == 0)
            {
                _logger.LogWarning("Смузи: расписание пустое, события не изменялись");
                return;
            }

            var existing = await LoadExistingEventsAsync(context.CancellationToken);

            var current = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            foreach (var game in games)
                current[game.SourceUrl] = game.Start;

            var deleted = await DeleteStaleEventsAsync(existing, current, context.CancellationToken);

            int pushed = 0, failed = 0;
            foreach (var game in games)
            {
                try
                {
                    var payload = new
                    {
                        // Префикс источника, как у «ВАУ КВИЗ:», — чтобы тип был виден в заголовке события
                        summary = $"Смузи: {game.Title}",
                        description = game.Description,
                        address = string.IsNullOrWhiteSpace(game.Venue)
                            ? game.Address
                            : string.IsNullOrWhiteSpace(game.Address)
                                ? game.Venue
                                : $"{game.Venue}, {game.Address}",
                        city = "Гомель",
                        type = "smuzi",
                        typeDescription = "Смузи",
                        start = game.Start,
                        end = game.Start.AddHours(2),
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

            _logger.LogInformation("🎉 Смузи: {Pushed} отправлено, {Deleted} удалено, {Failed} ошибок",
                pushed, deleted, failed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при скрапинге Смузи");
        }
    }

    /// <summary>
    /// Существующие события типа smuzi. При сбое возвращает пустой набор,
    /// чтобы рассинхрон источника не блокировал отправку новых игр.
    /// </summary>
    private async Task<List<ExistingEvent>> LoadExistingEventsAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _apiHttp.GetAsync("/events?type=smuzi", ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Смузи: не удалось прочитать существующие события — HTTP {Status}",
                    (int)resp.StatusCode);
                return [];
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<List<ExistingEvent>>(body, ReadOpts) ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            _logger.LogWarning(ex, "Смузи: не удалось прочитать существующие события");
            return [];
        }
    }

    /// <summary>
    /// Удаляет события, которых больше нет в источнике или у которых изменилось время.
    /// Один sourceUrl удаляется не более одного раза.
    /// </summary>
    private async Task<int> DeleteStaleEventsAsync(
        List<ExistingEvent> existing,
        Dictionary<string, DateTime> current,
        CancellationToken ct)
    {
        var deleted = 0;
        var handled = new HashSet<string>(StringComparer.Ordinal);

        foreach (var ev in existing)
        {
            var sourceUrl = ev.SourceUrl;
            if (string.IsNullOrWhiteSpace(sourceUrl))
                continue;

            if (current.TryGetValue(sourceUrl, out var start) && start == ev.Start)
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
                    _logger.LogWarning("Смузи: удаление {SourceUrl} — HTTP {Status}",
                        sourceUrl, (int)resp.StatusCode);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                _logger.LogWarning(ex, "Смузи: не удалось удалить устаревшее событие {SourceUrl}", sourceUrl);
            }
        }

        return deleted;
    }
}
