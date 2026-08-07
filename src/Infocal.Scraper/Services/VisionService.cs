using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Infocal.Scraper.Services;

/// <summary>
/// Sends images to a vision-capable LLM (OpenAI, DeepSeek, or compatible API)
/// to extract quiz schedule information from images.
/// Supports any OpenAI-compatible endpoint.
/// </summary>
public class VisionService(HttpClient http, IConfiguration config, ILogger<VisionService> logger)
{
    private readonly string _token = config["Vision:ApiKey"] ?? throw new InvalidOperationException("Vision:ApiKey not configured");
    private readonly string _model = config["Vision:Model"] ?? "gpt-4o-mini";
    private readonly string _endpoint = config["Vision:Endpoint"] ?? "https://api.openai.com/v1/chat/completions";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Analyzes an image and returns extracted schedule or null if it's not a schedule.
    /// Downloads the image and sends as base64.
    /// </summary>
    public async Task<ExtractedSchedule?> AnalyzeImageAsync(string imageUrl, string postText, CancellationToken ct = default)
    {
        // Download image first (VK URLs may require auth)
        byte[] imageBytes;
        try
        {
            using var imgClient = new HttpClient();
            imgClient.Timeout = TimeSpan.FromSeconds(15);
            imageBytes = await imgClient.GetByteArrayAsync(imageUrl, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось скачать картинку: {Url}", imageUrl);
            return null;
        }

        var base64 = Convert.ToBase64String(imageBytes);
        var mimeType = ImageUtils.DetectMime(imageBytes);
        var dataUri = $"data:{mimeType};base64,{base64}";

        var prompt = """
            Ты парсер расписаний квизов. Тебе дали картинку из поста VK.
            Определи, является ли эта картинка расписанием квизов (игр на ближайшие даты).
            Если НЕ является — ответь {"is_schedule": false}.
            Если является — извлеки все игры в JSON:

            {
              "is_schedule": true,
              "city": "город (например Гомель)",
              "games": [
                {
                  "date": "YYYY-MM-DD",
                  "time": "HH:MM в 24-часовом формате",
                  "title": "название квиза",
                  "venue": "название заведения",
                  "address": "адрес если указан"
                }
              ]
            }

            Текст поста (может содержать подсказки): 
            """ + postText + "\"";

        var messages = new[]
        {
            new { role = "user", content = new object[]
            {
                new { type = "text", text = prompt },
                new { type = "image_url", image_url = new { url = dataUri } }
            }}
        };

        var request = new
        {
            model = _model,
            messages,
            max_tokens = 2000,
            temperature = 0
        };

        var json = JsonSerializer.Serialize(request, JsonOpts);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = content
        };
        req.Headers.Add("Authorization", $"Bearer {_token}");

        var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct);
            logger.LogError("Vision API error {Status}: {Body}", (int)resp.StatusCode,
                errBody[..Math.Min(500, errBody.Length)]);
            return null;
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<VisionResponse>(body, JsonOpts);

        var responseText = result?.Choices?.FirstOrDefault()?.Message?.Content;
        if (responseText is null)
        {
            logger.LogWarning("Vision API вернул пустой ответ");
            return null;
        }

        logger.LogDebug("Vision ответ: {Text}", responseText[..Math.Min(200, responseText.Length)]);

        // Extract JSON from response (may be wrapped in markdown)
        var jsonStart = responseText.IndexOf('{');
        var jsonEnd = responseText.LastIndexOf('}');
        if (jsonStart < 0 || jsonEnd < 0) return null;

        var jsonBlock = responseText[jsonStart..(jsonEnd + 1)];
        try
        {
            var schedule = JsonSerializer.Deserialize<ExtractedSchedule>(jsonBlock, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return schedule;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Не удалось разобрать ответ Vision как JSON");
            return null;
        }
    }
}

// ── Vision API DTOs ──

public class VisionResponse
{
    public List<VisionChoice>? Choices { get; set; }
}

public class VisionChoice
{
    public VisionMessage? Message { get; set; }
}

public class VisionMessage
{
    public string? Content { get; set; }
}

public static class ImageUtils
{
    public static string DetectMime(byte[] bytes)
    {
        if (bytes.Length < 4) return "image/jpeg";

        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return "image/png";
        if (bytes[0] == 0xFF && bytes[1] == 0xD8)
            return "image/jpeg";
        if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
            return "image/gif";
        if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            return "image/webp";

        return "image/jpeg";
    }
}

public class ExtractedSchedule
{
    [JsonPropertyName("is_schedule")]
    public bool IsSchedule { get; set; }

    public string? City { get; set; }
    public List<ExtractedGame>? Games { get; set; }
}

public class ExtractedGame
{
    public string? Date { get; set; }
    public string? Time { get; set; }
    public string? Title { get; set; }
    public string? Venue { get; set; }
    public string? Address { get; set; }
}
