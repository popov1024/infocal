using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Infocal.Scraper;

/// <summary>
/// Scrapes quiz schedules from VK pages by analyzing post images via DeepSeek Vision.
/// </summary>
public class VkQuizJob : IJob
{
    private readonly VkScraperService _vk;
    private readonly VisionService _vision;
    private readonly HttpClient _apiHttp;
    private readonly ILogger<VkQuizJob> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // VK domains to scrape
    private static readonly (string Domain, string Category, string City)[] Sources =
    [
        ("mzgb_gom", "Квиз", "Гомель"),
    ];

    public VkQuizJob(
        VkScraperService vk, VisionService vision,
        IHttpClientFactory httpFactory, ILogger<VkQuizJob> logger)
    {
        _vk = vk;
        _vision = vision;
        _apiHttp = httpFactory.CreateClient("CalendarApi");
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        foreach (var (domain, category, city) in Sources)
        {
            _logger.LogInformation("📸 Сканирую VK {Domain}...", domain);

            try
            {
                var posts = await _vk.GetPhotoPostsAsync(domain, count: 10, context.CancellationToken);
                if (posts.Count == 0)
                {
                    _logger.LogInformation("⏳ Нет постов с фото в {Domain}", domain);
                    continue;
                }

                var sourceUrl = $"https://vk.com/{domain}";
                int pushed = 0, failed = 0, skipped = 0;

                foreach (var post in posts)
                {
                    if (post._bestPhotoUrl is null) continue;

                    // Step 1: Ask DeepSeek if this is a schedule
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

                            var payload = new
                            {
                                description = game.Title ?? "Квиз",
                                location = game.Venue ?? "",
                                address = game.Address ?? "",
                                city = extracted.City ?? city,
                                start = start,
                                end = start.AddHours(2),
                                category = category,
                                sourceUrl = postSourceUrl,
                                isAllDay = false
                            };

                            var json = JsonSerializer.Serialize(payload, JsonOpts);
                            var content = new StringContent(json, Encoding.UTF8, "application/json");
                            var resp = await _apiHttp.PostAsync("/events", content, context.CancellationToken);

                            if (resp.IsSuccessStatusCode)
                            {
                                pushed++;
                                _logger.LogInformation("   ✅ {Date:dd.MM HH:mm} {Title}", start, game.Title);
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
                    domain, pushed, failed, skipped);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка обработки VK {Domain}", domain);
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
