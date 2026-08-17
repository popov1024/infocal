using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Infocal.Scraper;

/// <summary>
/// Quartz job that scrapes MozgoBoynya (Мозгобойня) schedule for Gomel
/// and pushes events to the Calendar API.
/// </summary>
public class MozgoBoynyaJob : IJob
{
    private readonly MozgoBoynyaScraperService _scraper;
    private readonly HttpClient _apiHttp;
    private readonly ILogger<MozgoBoynyaJob> _logger;

    private const string SourceUrl = "https://gom.mzgb.by";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public MozgoBoynyaJob(MozgoBoynyaScraperService scraper, IHttpClientFactory httpFactory, ILogger<MozgoBoynyaJob> logger)
    {
        _scraper = scraper;
        _apiHttp = httpFactory.CreateClient("CalendarApi");
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("🧠 Поиск расписания Мозгобойни Гомель...");

        try
        {
            var games = await _scraper.GetScheduleAsync(context.CancellationToken);
            if (games.Count == 0)
            {
                _logger.LogInformation("⏳ Нет предстоящих игр Мозгобойни");
                return;
            }

            _logger.LogInformation("📄 Найдено {Count} игр", games.Count);

            // Delete old events for this source
            try
            {
                var deleteUrl = $"/events/by-source?sourceUrl={Uri.EscapeDataString(SourceUrl)}";
                await _apiHttp.DeleteAsync(deleteUrl, context.CancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Не удалось удалить старые события");
            }

            // Push events
            int pushed = 0, failed = 0;
            foreach (var game in games)
            {
                try
                {
                    var start = game.GetStartLocal();
                    var end = game.GetEndLocal();

                    if (start is null)
                    {
                        _logger.LogWarning("   ⚠️ Пропуск игры #{Id}: не удалось разобрать дату/время (date={Date}, time={Time})",
                            game.Id, game.CalendarDate, game.EventTime);
                        continue;
                    }

                    var gameType = game.Game?.GetDisplayType() ?? "Мозгобойня";
                    var gameName = game.Game?.Name ?? "";
                    var isMusic = game.Game?.TypeId == 2;

                    // Build title
                    var title = isMusic
                        ? $"Туц Туц Квиз: {gameName}"
                        : $"Мозгобойня: {gameName}";

                    if (game.IsOnline)
                    {
                        title = isMusic
                            ? $"Туц Туц Квиз Онлайн: {gameName}"
                            : $"Мозгобойня Онлайн: {gameName}";
                    }

                    var venue = game.Venue?.Name ?? "";
                    var address = game.Venue?.Address ?? "";

                    // Build location with price and registration info for iCal DESCRIPTION
                    var location = venue;
                    if (!game.IsFree && game.Price > 0)
                        location += $"\nСтоимость: {game.Price} {game.Currency}";
                    if (!string.IsNullOrWhiteSpace(game.RegistrationText))
                    {
                        var regInfo = game.RegistrationText;
                        if (!string.IsNullOrWhiteSpace(game.RegistrationStart))
                            regInfo += $": {game.RegistrationStart}";
                        location += $"\n{regInfo}";
                    }

                    var payload = new
                    {
                        summary = title,
                        description = game.IsOnline ? "Online" : location,
                        address = game.IsOnline ? "" : address,
                        city = "Гомель",
                        type = "mozgoboynya",
                        typeDescription = "Мозгобойня",
                        start = start,
                        end = end,
                        category = "quiz",
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
                    _logger.LogWarning(ex, "   ⚡ ошибка сети");
                }
            }

            if (pushed > 0)
                _logger.LogInformation("🎉 Мозгобойня: {Pushed}/{Total} отправлено, {Failed} ошибок", pushed, games.Count, failed);
            else
                _logger.LogWarning("⚠️ Мозгобойня: ни одно событие не отправлено ({Failed} ошибок)", failed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при скрапинге Мозгобойни");
        }
    }
}
