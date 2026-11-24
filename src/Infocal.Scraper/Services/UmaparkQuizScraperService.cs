using System.Net;
using System.Text.RegularExpressions;

namespace Infocal.Scraper.Services;

/// <summary>
/// Скрапер расписания квиза «УмаПарк» (Гомель) по тексту постов VK.
/// Афиш в постах нет: дата, время и площадка набраны текстом —
/// «🎂26 ноября / четверг / 19:00» и «🍕Пиццерия Кардинале (Кирова 123/3)».
/// Года в тексте нет, он выводится от даты публикации поста.
/// </summary>
public class UmaparkQuizScraperService(
    VkScraperService vk,
    IConfiguration config,
    ILogger<UmaparkQuizScraperService> logger)
{
    // ── Настройки источника ──
    private readonly string _domain = config["UmaparkQuiz:VkDomain"] ?? "umapark_gomel";
    private readonly int _postCount = int.TryParse(config["UmaparkQuiz:PostCount"], out var count) ? count : 20;

    // 26 ноября / четверг / 19:00
    private static readonly Regex DateRx = new(
        @"(\d{1,2})\s+([А-Яа-яЁё]{3,})[^\n]*?(\d{1,2}):(\d{2})",
        RegexOptions.CultureInvariant);

    // Пиццерия Кардинале (Кирова 123/3)
    private static readonly Regex VenueRx = new(
        @"^(.*?)\s*\(([^()]+)\)\s*$",
        RegexOptions.CultureInvariant);

    private static readonly Regex GameNumberRx = new(
        @"Регистрация\s+на\s+(\d+)-ю\s+игру",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex PriceRx = new(
        @"(Участие\s*:?\s*[^\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex HasLetterRx = new(@"[А-Яа-яЁё]", RegexOptions.CultureInvariant);
    private static readonly Regex HasDigitRx = new(@"\d", RegexOptions.CultureInvariant);
    private static readonly Regex LeadingNonLetterRx = new(@"^[^А-Яа-яЁё]+", RegexOptions.CultureInvariant);

    /// <summary>
    /// Возвращает предстоящие игры «УмаПарк».
    /// null — посты получить не удалось, пустой список — анонсов игр нет.
    /// </summary>
    public async Task<IReadOnlyList<UmaparkGame>?> GetUpcomingGamesAsync(CancellationToken ct = default)
    {
        IReadOnlyList<VkPost> posts;
        try
        {
            posts = await vk.GetPostsAsync(_domain, _postCount, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            logger.LogError(ex, "УмаПарк: не удалось получить посты VK");
            return null;
        }

        if (posts.Count == 0)
        {
            logger.LogError("УмаПарк: VK не вернул ни одного поста");
            return null;
        }

        var games = new List<UmaparkGame>();
        var skippedPast = 0;

        foreach (var post in posts)
        {
            var game = TryParsePost(post, ref skippedPast);
            if (game is not null)
                games.Add(game);
        }

        if (skippedPast > 0)
            logger.LogDebug("УмаПарк: пропущено {Count} прошедших игр", skippedPast);

        logger.LogInformation("УмаПарк: из {Posts} постов получено {Games} предстоящих игр",
            posts.Count, games.Count);

        return games;
    }

    // ── Разбор поста ──

    /// <summary>
    /// Разбирает один пост. null — пост не является анонсом предстоящей игры
    /// (итоги, фотоотчёты, прошедшие даты).
    /// </summary>
    private UmaparkGame? TryParsePost(VkPost post, ref int skippedPast)
    {
        if (string.IsNullOrWhiteSpace(post.Text))
            return null;

        var text = WebUtility.HtmlDecode(post.Text).Replace('\u00A0', ' ');
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Года в тексте поста нет — считаем его от даты публикации
        var postDate = DateTimeOffset.FromUnixTimeSeconds(post.DateUnix).LocalDateTime;

        DateTime? start = null;
        string? venue = null;
        string? address = null;
        string? price = null;

        foreach (var line in lines)
        {
            if (start is null && TryParseStart(line, postDate, out var parsedStart))
            {
                start = parsedStart;
                continue;
            }

            if (venue is null && TryParseVenue(line, out var parsedVenue, out var parsedAddress))
            {
                venue = parsedVenue;
                address = parsedAddress;
                continue;
            }

            if (price is null)
            {
                var priceMatch = PriceRx.Match(line);
                if (priceMatch.Success)
                    price = priceMatch.Groups[1].Value.Trim();
            }
        }

        if (start is null)
            return null;

        if (start < DateTime.Now)
        {
            skippedPast++;
            return null;
        }

        var numberMatch = GameNumberRx.Match(text);
        var title = numberMatch.Success ? $"Игра №{numberMatch.Groups[1].Value}" : "Квиз";

        return new UmaparkGame(
            Start: start.Value,
            Title: title,
            Description: price ?? "",
            Venue: venue ?? "",
            Address: address ?? "",
            SourceUrl: $"https://vk.com/{_domain}?w=wall{post.OwnerId}_{post.Id}");
    }

    /// <summary>«26 ноября / четверг / 19:00» → дата игры с годом, выведенным от поста.</summary>
    private static bool TryParseStart(string line, DateTime postDate, out DateTime start)
    {
        start = default;

        var match = DateRx.Match(line);
        if (!match.Success)
            return false;

        var month = DateParser.MonthFromName(match.Groups[2].Value);
        if (month is null)
            return false;

        if (!int.TryParse(match.Groups[1].Value, out var day)
            || !int.TryParse(match.Groups[3].Value, out var hour)
            || !int.TryParse(match.Groups[4].Value, out var minute)
            || hour is < 0 or > 23
            || minute is < 0 or > 59)
            return false;

        // Месяц игры ещё не наступил на момент поста — значит речь про следующий год
        var year = postDate.Year;
        if (month.Value < postDate.Month)
            year++;

        try
        {
            start = new DateTime(year, month.Value, day, hour, minute, 0, DateTimeKind.Unspecified);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>«🍕Пиццерия Кардинале (Кирова 123/3)» → площадка и адрес.</summary>
    private static bool TryParseVenue(string line, out string venue, out string address)
    {
        venue = "";
        address = "";

        var match = VenueRx.Match(line);
        if (!match.Success)
            return false;

        var name = LeadingNonLetterRx.Replace(match.Groups[1].Value, "").Trim();
        var place = match.Groups[2].Value.Trim();

        // Отсекаем обычные скобки в тексте: в адресе должны быть цифры
        if (!HasLetterRx.IsMatch(name) || !HasDigitRx.IsMatch(place))
            return false;

        venue = name;
        address = place;
        return true;
    }
}

/// <summary>Предстоящая игра «УмаПарк», готовая к отправке в Calendar.</summary>
public sealed record UmaparkGame(
    DateTime Start,
    string Title,
    string Description,
    string Venue,
    string Address,
    string SourceUrl);
