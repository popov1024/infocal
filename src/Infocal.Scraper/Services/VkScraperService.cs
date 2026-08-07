using System.Text.Json;
using System.Text.Json.Serialization;

namespace Infocal.Scraper.Services;

/// <summary>
/// Fetches wall posts from VK public pages via VK API.
/// Requires a VK Service Token (create app at vk.com/dev).
/// </summary>
public class VkScraperService(HttpClient http, IConfiguration config, ILogger<VkScraperService> logger)
{
    private readonly string _token = config["VkApi:Token"] ?? throw new InvalidOperationException("VkApi:Token not configured");

    private const string ApiBase = "https://api.vk.com/method";
    private const string ApiVersion = "5.199";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Returns recent wall posts with photo attachments for a VK domain.
    /// </summary>
    public async Task<IReadOnlyList<VkPost>> GetPhotoPostsAsync(string domain, int count = 10, CancellationToken ct = default)
    {
        var url = $"{ApiBase}/wall.get?domain={domain}&count={count}&v={ApiVersion}&access_token={_token}";
        logger.LogDebug("VK API: {Url}", url);

        var resp = await http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync(ct);
        var apiResp = JsonSerializer.Deserialize<VkWallResponse>(body, JsonOpts);

        if (apiResp?.Error is not null)
        {
            logger.LogError("VK API error: {Code} {Msg}", apiResp.Error.Code, apiResp.Error.Msg);
            return [];
        }

        var posts = apiResp?.Response?.Items ?? [];
        var photoPosts = posts
            .Where(p => p.Attachments?.Any(a => a.Type == "photo") == true)
            .Select(p =>
            {
                // Resolve largest photo URL
                var photo = p.Attachments!.First(a => a.Type == "photo").Photo;
                if (photo?.Sizes is { Count: > 0 })
                {
                    var best = photo.Sizes.OrderByDescending(s => s.Width * s.Height).First();
                    p._bestPhotoUrl = best.Url;
                }
                return p;
            })
            .Where(p => p._bestPhotoUrl is not null)
            .ToList();

        logger.LogInformation("VK: {Total} постов, {Photos} с фото для {Domain}",
            posts.Count, photoPosts.Count, domain);

        return photoPosts;
    }
}

// ── VK API DTOs ──

public class VkWallResponse
{
    public VkResponse? Response { get; set; }
    public VkError? Error { get; set; }
}

public class VkError
{
    [JsonPropertyName("error_code")]
    public int Code { get; set; }

    [JsonPropertyName("error_msg")]
    public string Msg { get; set; } = "";
}

public class VkResponse
{
    public List<VkPost> Items { get; set; } = [];
}

public class VkPost
{
    public long Id { get; set; }

    [JsonPropertyName("owner_id")]
    public long OwnerId { get; set; }

    public string Text { get; set; } = "";

    [JsonPropertyName("date")]
    public long DateUnix { get; set; }

    public List<VkAttachment>? Attachments { get; set; }

    // Resolved best photo URL (not from JSON)
    [JsonIgnore]
    public string? _bestPhotoUrl { get; set; }
}

public class VkAttachment
{
    public string Type { get; set; } = "";
    public VkPhoto? Photo { get; set; }
}

public class VkPhoto
{
    public List<VkPhotoSize>? Sizes { get; set; }
}

public class VkPhotoSize
{
    public string Url { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
}
