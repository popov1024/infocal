using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Infocal.Scraper.Services;

/// <summary>
/// Скрапер расписания квизов «Смузи» (Гомель).
/// Со страницы расписания берутся storepart/recid, затем карточки тянутся из Tilda Store API.
/// Года в данных источника нет, поэтому он выводится по порядку месяцев.
/// </summary>
public class SmuziQuizScraperService(
    HttpClient http,
    IConfiguration config,
    ILogger<SmuziQuizScraperService> logger)
{
    // ── Настройки источника ──
    private readonly string _schedulePageUrl =
        config["SmuziQuiz:SchedulePageUrl"] ?? "https://gomel.smuzi-quiz.com/games";

    private readonly string _storeApiUrl =
        (config["SmuziQuiz:StoreApiUrl"] ?? "https://store.tildaapi.com").TrimEnd('/');

    private readonly int _pageSize = int.TryParse(config["SmuziQuiz:PageSize"], out var pageSize) ? pageSize : 100;
    private readonly int _maxPages = int.TryParse(config["SmuziQuiz:MaxPages"], out var maxPages) ? maxPages : 3;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Regex StorePartRx = new(@"storepart\s*:\s*'(\d+)'", RegexOptions.CultureInvariant);
    private static readonly Regex RecIdRx = new(@"recid\s*:\s*'(\d+)'", RegexOptions.CultureInvariant);

    // 24 ноября (ВТ) 19:00 (СмузиЛофт) Топовые КМС №16
    private static readonly Regex TitleRx = new(
        @"^(\d{1,2})\s+([а-яё]+)\s*\([^)]*\)\s*(\d{1,2}:\d{2})\s*\(([^)]*)\)\s*(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex BrRx = new(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TagRx = new(@"<[^>]+>", RegexOptions.CultureInvariant);
    private static readonly Regex InlineSpaceRx = new(@"[ \t\u00A0]+", RegexOptions.CultureInvariant);

    /// <summary>
    /// Возвращает предстоящие игры «Смузи».
    /// null — данные получить не удалось (страница, API или парсинг), пустой список — игр нет.
    /// </summary>
    public async Task<IReadOnlyList<SmuziGame>?> GetUpcomingGamesAsync(CancellationToken ct = default)
    {
        var (part, recid) = await ReadStoreIdsAsync(ct);
        if (part is null || recid is null)
            return null;

        var products = await LoadProductsAsync(part, recid, ct);
        if (products is null)
            return null;

        var games = products.Count == 0 ? [] : MapProducts(products);

        logger.LogInformation("Смузи: получено {Products} карточек, {Games} предстоящих игр",
            products.Count, games.Count);

        return games;
    }

    // ── Страница расписания: storepart/recid ──

    private async Task<(string? Part, string? RecId)> ReadStoreIdsAsync(CancellationToken ct)
    {
        string html;
        try
        {
            using var resp = await http.GetAsync(_schedulePageUrl, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogError("Смузи: страница расписания вернула HTTP {Status}", (int)resp.StatusCode);
                return (null, null);
            }

            html = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            logger.LogError(ex, "Смузи: не удалось скачать страницу расписания");
            return (null, null);
        }

        var part = StorePartRx.Match(html);
        var recid = RecIdRx.Match(html);

        if (!part.Success || !recid.Success)
        {
            logger.LogError("Смузи: на странице расписания не найдены storepart/recid");
            return (null, null);
        }

        return (part.Groups[1].Value, recid.Groups[1].Value);
    }

    // ── Tilda Store API: карточки товаров ──

    private async Task<List<SmuziProduct>?> LoadProductsAsync(string part, string recid, CancellationToken ct)
    {
        var products = new List<SmuziProduct>();

        for (var slice = 1; slice <= _maxPages; slice++)
        {
            var url = $"{_storeApiUrl}/api/getproductslist/" +
                      $"?storepartuid={part}&recid={recid}&size={_pageSize}&slice={slice}";
            logger.LogDebug("Смузи: загрузка {Url}", url);

            try
            {
                using var resp = await http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    logger.LogError("Смузи: API вернул HTTP {Status}", (int)resp.StatusCode);
                    return null;
                }

                var body = await resp.Content.ReadAsStringAsync(ct);
                var page = JsonSerializer.Deserialize<ProductsPage>(body, JsonOpts);
                if (page is null)
                {
                    logger.LogError("Смузи: API вернул пустой ответ");
                    return null;
                }

                if (page.Products is not { Count: > 0 })
                    break;

                products.AddRange(page.Products);

                // Дошли до total — следующие слайсы не нужны
                if (page.Total > 0 && products.Count >= page.Total)
                    break;

                // nextslice = null означает конец списка
                if (page.Nextslice.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    break;
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, "Смузи: не удалось разобрать JSON ответа API");
                return null;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                logger.LogError(ex, "Смузи: ошибка запроса к API");
                return null;
            }
        }

        return products;
    }

    // ── Маппинг карточек в игры ──

    private List<SmuziGame> MapProducts(List<SmuziProduct> products)
    {
        var games = new List<SmuziGame>();
        var now = DateTime.Now;

        // Источник отдаёт только предстоящие игры, а список отсортирован по возрастанию даты,
        // поэтому монотонный счёт месяцев по порядку карточек даёт корректный год.
        // Якорем служит текущий месяц: первая же карточка с меньшим месяцем — это уже следующий
        // год (декабрь → январь), иначе январь, опубликованный в декабре, ушёл бы в прошлое.
        var anchorYear = now.Year;
        var previousMonth = now.Month;
        var skippedPast = 0;

        foreach (var product in products)
        {
            var rawTitle = product.Title;
            if (string.IsNullOrWhiteSpace(rawTitle))
            {
                logger.LogWarning("Смузи: карточка без названия, пропущена");
                continue;
            }

            var match = TitleRx.Match(rawTitle.Trim());
            if (!match.Success)
            {
                logger.LogWarning("Смузи: не удалось разобрать заголовок «{Title}»", rawTitle);
                continue;
            }

            var month = DateParser.MonthFromName(match.Groups[2].Value);
            if (month is null)
            {
                logger.LogWarning("Смузи: неизвестный месяц в заголовке «{Title}»", rawTitle);
                continue;
            }

            if (!int.TryParse(match.Groups[1].Value, out var day)
                || !TryParseTime(match.Groups[3].Value, out var hour, out var minute))
            {
                logger.LogWarning("Смузи: не удалось разобрать дату/время в заголовке «{Title}»", rawTitle);
                continue;
            }

            // Месяц пошёл назад — значит, карточки перешли на следующий год
            if (month.Value < previousMonth)
                anchorYear++;
            previousMonth = month.Value;

            DateTime start;
            try
            {
                start = new DateTime(anchorYear, month.Value, day, hour, minute, 0, DateTimeKind.Unspecified);
            }
            catch (ArgumentOutOfRangeException)
            {
                logger.LogWarning("Смузи: невалидная дата в заголовке «{Title}»", rawTitle);
                continue;
            }

            if (start < now)
            {
                skippedPast++;
                continue;
            }

            var sourceUrl = product.Url;
            if (string.IsNullOrWhiteSpace(sourceUrl))
            {
                logger.LogWarning("Смузи: у карточки «{Title}» нет url, пропущена", rawTitle);
                continue;
            }

            games.Add(new SmuziGame(
                Start: start,
                Title: match.Groups[5].Value.Trim(),
                Description: CleanDescription(product.Descr),
                Venue: ExtractVenue(product.Characteristics, match.Groups[4].Value),
                Address: ExtractAddress(product.JsonOptions),
                SourceUrl: sourceUrl.Trim()));
        }

        if (skippedPast > 0)
            logger.LogDebug("Смузи: пропущено {Count} прошедших игр", skippedPast);

        return games;
    }

    private static bool TryParseTime(string raw, out int hour, out int minute)
    {
        hour = 0;
        minute = 0;

        var parts = raw.Split(':');
        return parts.Length == 2
               && int.TryParse(parts[0], out hour)
               && int.TryParse(parts[1], out minute)
               && hour is >= 0 and < 24
               && minute is >= 0 and < 60;
    }

    private static string ExtractVenue(List<SmuziCharacteristic> characteristics, string fallback)
    {
        if (characteristics is null)
            return fallback.Trim();

        foreach (var characteristic in characteristics)
        {
            if (characteristic is null)
                continue;

            if (string.Equals(characteristic.Title?.Trim(), "Место проведения", StringComparison.OrdinalIgnoreCase))
                return characteristic.Value?.Trim() ?? "";
        }

        return fallback.Trim();
    }

    /// <summary>
    /// Адрес лежит в json_options: [{"values":["СмузиЛофт (Подгорная 12)"]}].
    /// Берём текст в последних скобках. Любой сбой разбора — пустая строка.
    /// </summary>
    private static string ExtractAddress(string? jsonOptions)
    {
        if (string.IsNullOrWhiteSpace(jsonOptions))
            return "";

        try
        {
            using var doc = JsonDocument.Parse(jsonOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return "";

            if (!doc.RootElement[0].TryGetProperty("values", out var values)
                || values.ValueKind != JsonValueKind.Array
                || values.GetArrayLength() == 0)
                return "";

            var raw = values[0].GetString();
            if (string.IsNullOrWhiteSpace(raw))
                return "";

            var open = raw.LastIndexOf('(');
            var close = raw.LastIndexOf(')');
            if (open < 0 || close <= open)
                return "";

            return raw[(open + 1)..close].Trim();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return "";
        }
    }

    private static string CleanDescription(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";

        var text = BrRx.Replace(html, "\n");
        text = TagRx.Replace(text, " ");
        text = WebUtility.HtmlDecode(text);

        var lines = text.Split('\n')
            .Select(line => InlineSpaceRx.Replace(line, " ").Trim())
            .Where(line => line.Length > 0);

        return string.Join("\n", lines);
    }

    // ── DTO ответа Tilda Store API ──

    private sealed class ProductsPage
    {
        public List<SmuziProduct> Products { get; set; } = [];
        public int Total { get; set; }
        public JsonElement Nextslice { get; set; }
    }

    private sealed class SmuziProduct
    {
        public string? Title { get; set; }
        public string? Descr { get; set; }
        public string? Url { get; set; }
        public List<SmuziCharacteristic> Characteristics { get; set; } = [];

        [JsonPropertyName("json_options")]
        public string? JsonOptions { get; set; }
    }

    private sealed class SmuziCharacteristic
    {
        public string? Title { get; set; }
        public string? Value { get; set; }
    }
}

/// <summary>Предстоящая игра «Смузи», готовая к отправке в Calendar.</summary>
public sealed record SmuziGame(
    DateTime Start,
    string Title,
    string Description,
    string Venue,
    string Address,
    string SourceUrl);
