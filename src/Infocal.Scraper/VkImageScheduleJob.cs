using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Infocal.Scraper;

/// <summary>
/// Generic scraper for schedules published as images on VK pages.
/// Downloads each post image and extracts the schedule via DeepSeek Vision
/// (deepseek-flash), then pushes events to the Calendar API.
/// Add a new source by extending <see cref="Sources"/>.
/// </summary>
public class VkImageScheduleJob : IJob
{
    private readonly VkScraperService _vk;
    private readonly VisionService _vision;
    private readonly HttpClient _apiHttp;
    private readonly ILogger<VkImageScheduleJob> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record VkSource(
        string Domain, string Category, string City, string Type, string TypeDescription);

    // VK pages that publish their schedule as images
    private static readonly VkSource[] Sources =
    [
        new("mindstorm_gomel", "quiz", "Гомель", "mindstorm", "MindStorm"),
    ];

    public VkImageScheduleJob(
        VkScraperService vk, VisionService vision,
        IHttpClientFactory httpFactory, ILogger<VkImageScheduleJob> logger)
    {
        _vk = vk;
        _vision = vision;
        _apiHttp = httpFactory.CreateClient("CalendarApi");
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        foreach (var source in Sources)
        {
            _logger.LogInformation("📸 Сканирую VK {Domain}...", source.Domain);

            try
            {
                var posts = await _vk.GetPhotoPostsAsync(source.Domain, count: 10, context.CancellationToken);
                if (posts.Count == 0)
                {
                    _logger.LogInformation("⏳ Нет постов с фото в {Domain}", source.Domain);
                    continue;
                }

                var sourceUrl = $"https://vk.com/{source.Domain}";
                int pushed = 0, failed = 0, skipped = 0;

                foreach (var post in posts)
                {
                    if (post._bestPhotoUrl is null) continue;

                    // Step 1: Ask DeepSeek Vision if this is a schedule
                    var extracted = await _vision.AnalyzeImageAsync(
                        post._bestPhotoUrl, post.Text, context.CancellationToken);

                    if (extracted is not { IsSchedule: true } || extracted.Games is not { Count: > 0 })
                    {
                        skipped++;
                        _logger.LogDebug("   🖼️ Пост {PostId}: не расписание, пропущен", post.Id);
                        continue;
                    }

                    var postSourceUrl = $"{sourceUrl}?w=wall{post.OwnerId}_{post.Id}";

                    // Step 2: Delete old events for this post
                    try
                    {
                        var delUrl = $"/events/by-source?sourceUrl={Uri.EscapeDataString(postSourceUrl)}";
                        await _apiHttp.DeleteAsync(delUrl, context.CancellationToken);
                    }
                    catch { /* ignore */ }

                    // Step 3: Push extracted games
                    foreach (var game in extracted.Games)
                    {
                        try
                        {
                            if (!TryParseDateTime(game.Date, game.Time, out var start))
                            {
                                _logger.LogWarning("   ⚠️ Невалидная дата: {Date} {Time}", game.Date, game.Time);
                                failed++;
                                continue;
                            }

                            var title = string.IsNullOrWhiteSpace(game.Title)
                                ? source.TypeDescription
                                : game.Title;

                            var payload = new
                            {
                                summary = title,
                                description = game.Venue ?? "",
                                address = game.Address ?? "",
                                city = string.IsNullOrWhiteSpace(extracted.City) ? source.City : extracted.City,
                                type = source.Type,
                                typeDescription = source.TypeDescription,
                                start = start,
                                end = start.AddHours(2),
                                category = source.Category,
                                sourceUrl = postSourceUrl,
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
                                _logger.LogWarning("   ❌ {Date:dd.MM HH:mm} HTTP {Status}: {Body}",
                                    start, (int)resp.StatusCode, body);
                            }
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            _logger.LogWarning(ex, "   ⚡ Ошибка отправки события");
                        }
                    }
                }

                _logger.LogInformation("🎉 VK {Domain}: {Pushed} отправлено, {Failed} ошибок, {Skipped} пропущено",
                    source.Domain, pushed, failed, skipped);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка обработки VK {Domain}", source.Domain);
            }
        }
    }

    private static bool TryParseDateTime(string? date, string? time, out DateTime result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(date)) return false;

        var dt = date.Trim();
        var tm = time?.Trim() ?? "19:00";

        if (DateTime.TryParse($"{dt} {tm}", out result))
            return true;
        if (DateTime.TryParse($"{dt}T{tm}:00", out result))
            return true;

        return false;
    }
}
