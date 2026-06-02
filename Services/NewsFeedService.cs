using System.Net.Http.Json;
using System.Text.Json;
using SultanPortfolio.Web.Models;

namespace SultanPortfolio.Web.Services;

public class NewsFeedService : INewsFeedService
{
    private readonly HttpClient _http;
    private List<NewsArticleDto>? _cache;
    private DateTimeOffset _cacheLoadedAt = DateTimeOffset.MinValue;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(8);

    // dev.to endpoint
    private const string DevToUrl =
        "https://dev.to/api/articles?tag=dotnet&per_page=20&page=1";

    // rss2json proxy for thenewstack.io
    private const string RssProxyUrl =
        "https://api.rss2json.com/v1/api.json?rss_url=https://thenewstack.io/feed/";

    public NewsFeedService(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<NewsArticleDto>> GetArticlesAsync(int page, int pageSize)
    {
        if (_cache is null || DateTimeOffset.UtcNow - _cacheLoadedAt > CacheDuration)
        {
            _cache = await FetchAndMergeAsync();
            _cacheLoadedAt = DateTimeOffset.UtcNow;
        }

        return _cache
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
    }

    private async Task<List<NewsArticleDto>> FetchAndMergeAsync()
    {
        var sources = new[]
        {
            FetchDevToAsync(),
            FetchRssProxyAsync()
        };

        var results = await Task.WhenAll(sources);

        var all = results.SelectMany(items => items)
            .OrderByDescending(a => a.PublishedAt)
            .DistinctBy(a => a.Url)
            .ToList();

        return all;
    }

    private async Task<IEnumerable<NewsArticleDto>> FetchDevToAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(FetchTimeout);
            var response = await _http.GetFromJsonAsync<List<DevToArticle>>(DevToUrl, cts.Token);
            return response?.Select(MapDevTo) ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or OperationCanceledException)
        {
            Console.Error.WriteLine($"[NewsFeedService] dev.to fetch failed: {ex.Message}");
            return [];
        }
    }

    private async Task<IEnumerable<NewsArticleDto>> FetchRssProxyAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(FetchTimeout);
            var response = await _http.GetFromJsonAsync<Rss2JsonResponse>(RssProxyUrl, cts.Token);
            if (response?.status != "ok" || response.items is null) return [];
            return response.items.Select(MapRssItem);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or OperationCanceledException)
        {
            Console.Error.WriteLine($"[NewsFeedService] rss2json fetch failed: {ex.Message}");
            return [];
        }
    }

    private static NewsArticleDto MapDevTo(DevToArticle a) => new(
        Title: a.title ?? string.Empty,
        Url: a.url ?? string.Empty,
        PublishedAt: DateTime.TryParse(a.published_at, out var dt) ? dt : DateTime.MinValue,
        SourceName: "DEV Community",
        Summary: a.description ?? string.Empty
    );

    private static NewsArticleDto MapRssItem(Rss2JsonItem item) => new(
        Title: item.title ?? string.Empty,
        Url: item.link ?? string.Empty,
        PublishedAt: DateTime.TryParse(item.pubDate, out var dt) ? dt : DateTime.MinValue,
        SourceName: "The New Stack",
        Summary: item.description ?? string.Empty
    );
}

// Internal deserialization records — not exposed outside NewsFeedService
internal record DevToArticle(
    string? title,
    string? url,
    string? published_at,
    string? description
);

internal record Rss2JsonResponse(string? status, List<Rss2JsonItem>? items);

internal record Rss2JsonItem(
    string? title,
    string? link,
    string? pubDate,
    string? description
);
