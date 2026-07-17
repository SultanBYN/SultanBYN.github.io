using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace SultanPortfolio.Web.Services;

public enum NewsItemType
{
    Blog,
    News,
    Article
}

public sealed record NewsItem(
    string Id,
    string Title,
    string Summary,
    string SourceName,
    string SourceUrl,
    string ItemUrl,
    string Author,
    DateTimeOffset PublishedAt,
    DateTimeOffset? UpdatedAt,
    NewsItemType Type,
    IReadOnlyList<string> TechnologyTags,
    IReadOnlyList<string> MatchedKeywords)
{
    public string PrimaryTechnology => TechnologyTags.FirstOrDefault() ?? "General";
}

public sealed class NewsFeedService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly List<NewsItem> _items = [];
    private bool _hydrated;
    private int _cursor;

    public NewsFeedService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public DateTimeOffset? LastRefreshed { get; private set; }

    public string? LastErrorMessage { get; private set; }

    public bool HasMoreItems => !_hydrated || _cursor < _items.Count;

    public void Reset()
    {
        _items.Clear();
        _cursor = 0;
        _hydrated = false;
        LastRefreshed = null;
        LastErrorMessage = null;
    }

    public async Task<IReadOnlyList<NewsItem>> LoadNextBatchAsync(int pageSize, CancellationToken cancellationToken = default)
    {
        if (pageSize <= 0)
        {
            return [];
        }

        LastErrorMessage = null;

        if (!_hydrated)
        {
            await HydrateAsync(cancellationToken);
        }

        if (_items.Count == 0 || _cursor >= _items.Count)
        {
            return [];
        }

        var batch = _items.Skip(_cursor).Take(pageSize).ToList();
        _cursor += batch.Count;
        return batch;
    }

    private async Task HydrateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var requestUri = _configuration["Supabase:NewsFeedUrl"];
            var anonKey = _configuration["Supabase:AnonKey"];

            if (string.IsNullOrWhiteSpace(requestUri))
            {
                LastErrorMessage = "Supabase News Feed URL is not configured.";
                return;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            if (!string.IsNullOrWhiteSpace(anonKey))
            {
                request.Headers.TryAddWithoutValidation("apikey", anonKey);
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {anonKey}");
            }

            using var responseMessage = await _httpClient.SendAsync(request, cancellationToken);
            var response = await responseMessage.Content.ReadFromJsonAsync<NewsFeedResponse>(JsonOptions, cancellationToken);

            if (!responseMessage.IsSuccessStatusCode)
            {
                _items.Clear();
                LastRefreshed = response?.LastRefreshed;
                LastErrorMessage = response?.ErrorMessage ?? $"News feed request failed with status code {(int)responseMessage.StatusCode}.";
                return;
            }

            if (response?.Items is { Count: > 0 } items)
            {
                _items.Clear();
                _items.AddRange(items
                    .Select(MapItem)
                    .Where(item => item is not null)
                    .Select(item => item!)
                    .OrderByDescending(item => item.PublishedAt));
                LastRefreshed = response.LastRefreshed;
                LastErrorMessage = response.ErrorMessage;
            }
            else
            {
                _items.Clear();
                LastRefreshed = response?.LastRefreshed;
                LastErrorMessage = response?.ErrorMessage ?? "No news items were returned from the feed service.";
            }
        }
        catch (Exception ex)
        {
            _items.Clear();
            LastErrorMessage = $"We could not load the live feed yet: {ex.Message}";
        }
        finally
        {
            _hydrated = true;
        }
    }

    private static NewsItem? MapItem(NewsFeedItemDto item)
    {
        if (string.IsNullOrWhiteSpace(item.Id) ||
            string.IsNullOrWhiteSpace(item.Title) ||
            string.IsNullOrWhiteSpace(item.SourceName) ||
            string.IsNullOrWhiteSpace(item.SourceUrl) ||
            string.IsNullOrWhiteSpace(item.ItemUrl))
        {
            return null;
        }

        var type = Enum.TryParse<NewsItemType>(item.Type, true, out var parsedType)
            ? parsedType
            : NewsItemType.News;

        return new NewsItem(
            item.Id.Trim(),
            item.Title.Trim(),
            item.Summary?.Trim() ?? string.Empty,
            item.SourceName.Trim(),
            item.SourceUrl.Trim(),
            item.ItemUrl.Trim(),
            item.Author?.Trim() ?? "Unknown",
            ParseDateTimeOffset(item.PublishedAt),
            ParseNullableDateTimeOffset(item.UpdatedAt),
            type,
            item.TechnologyTags?.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag.Trim()).ToArray() ?? [],
            item.MatchedKeywords?.Where(keyword => !string.IsNullOrWhiteSpace(keyword)).Select(keyword => keyword.Trim()).ToArray() ?? []);
    }

    private static DateTimeOffset ParseDateTimeOffset(string? value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result))
        {
            return result;
        }

        return DateTimeOffset.UtcNow;
    }

    private static DateTimeOffset? ParseNullableDateTimeOffset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result)
            ? result
            : null;
    }

    private sealed record NewsFeedResponse(
        IReadOnlyList<NewsFeedItemDto>? Items,
        DateTimeOffset? LastRefreshed,
        string? ErrorMessage);

    private sealed record NewsFeedItemDto(
        string Id,
        string Title,
        string Summary,
        string SourceName,
        string SourceUrl,
        string ItemUrl,
        string Author,
        string PublishedAt,
        string? UpdatedAt,
        string Type,
        IReadOnlyList<string>? TechnologyTags,
        IReadOnlyList<string>? MatchedKeywords);
}
