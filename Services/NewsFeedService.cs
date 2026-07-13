using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

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
    private enum SourceKind
    {
        DevToApi,
        AtomFeed,
        RssFeed,
        HackerNewsApi
    }

    private sealed record SourceDefinition(
        string Key,
        string Name,
        NewsItemType Type,
        SourceKind Kind,
        string Url);

    private static readonly IReadOnlyList<SourceDefinition> Sources =
    [
        new("devto", "dev.to", NewsItemType.Article, SourceKind.DevToApi, "https://dev.to/api/articles?per_page=30&page=1"),
        new("dotnet-blog", ".NET Blog", NewsItemType.Blog, SourceKind.RssFeed, "https://devblogs.microsoft.com/dotnet/feed/"),
        new("github-news", "dotnet/core News", NewsItemType.News, SourceKind.AtomFeed, "https://github.com/dotnet/core/discussions/categories/news.atom"),
        new("github-general", "dotnet/core General", NewsItemType.News, SourceKind.AtomFeed, "https://github.com/dotnet/core/discussions/categories/general.atom"),
        new("hacker-news", "Hacker News", NewsItemType.News, SourceKind.HackerNewsApi, "https://hacker-news.firebaseio.com/v0/topstories.json"),
        new("the-hacker-news", "The Hacker News", NewsItemType.News, SourceKind.RssFeed, "https://feeds.feedburner.com/TheHackersNews")
    ];

    private static readonly string[] EcosystemKeywords =
    [
        ".net",
        "dotnet",
        "asp.net",
        "aspnet",
        "ef core",
        "entity framework",
        "blazor",
        "c#",
        "minimal api",
        "minimal apis",
        "maui",
        "nuget",
        "signalr",
        "identity",
        "azure",
        "open telemetry",
        "linq"
    ];

    private static readonly Regex HtmlRegex = new("<[^>]+>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WhiteSpaceRegex = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, IReadOnlyList<NewsItem>> _sourceCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _sourceCursors = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dedupe = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, JsonElement> _hackerNewsItemCache = new();
    private int _sourceIndex;

    public NewsFeedService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public DateTimeOffset? LastRefreshed { get; private set; }

    public string? LastErrorMessage { get; private set; }

    public bool HasMoreItems => _sourceIndex < Sources.Count;

    public void Reset()
    {
        _sourceCache.Clear();
        _sourceCursors.Clear();
        _dedupe.Clear();
        _hackerNewsItemCache.Clear();
        _sourceIndex = 0;
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
        var batch = new List<NewsItem>(pageSize);

        while (batch.Count < pageSize && _sourceIndex < Sources.Count)
        {
            var source = Sources[_sourceIndex];
            var items = await GetSourceItemsAsync(source, cancellationToken);

            if (items.Count == 0)
            {
                _sourceIndex++;
                continue;
            }

            var cursor = _sourceCursors.TryGetValue(source.Key, out var currentCursor) ? currentCursor : 0;

            while (cursor < items.Count && batch.Count < pageSize)
            {
                var item = items[cursor++];

                if (!_dedupe.Add(item.Id))
                {
                    continue;
                }

                batch.Add(item);
            }

            _sourceCursors[source.Key] = cursor;

            if (cursor >= items.Count)
            {
                _sourceIndex++;
                continue;
            }

            break;
        }

        if (batch.Count > 0)
        {
            LastRefreshed = DateTimeOffset.UtcNow;
        }

        return batch;
    }

    private async Task<IReadOnlyList<NewsItem>> GetSourceItemsAsync(SourceDefinition source, CancellationToken cancellationToken)
    {
        if (_sourceCache.TryGetValue(source.Key, out var cached))
        {
            return cached;
        }

        IReadOnlyList<NewsItem> items = source.Kind switch
        {
            SourceKind.DevToApi => await LoadDevToAsync(source, cancellationToken),
            SourceKind.HackerNewsApi => await LoadHackerNewsAsync(source, cancellationToken),
            SourceKind.RssFeed or SourceKind.AtomFeed => await LoadFeedAsync(source, cancellationToken),
            _ => []
        };

        _sourceCache[source.Key] = items;
        return items;
    }

    private async Task<IReadOnlyList<NewsItem>> LoadDevToAsync(SourceDefinition source, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await _httpClient.GetStringAsync(source.Url, cancellationToken);
            using var document = JsonDocument.Parse(payload);
            var results = new List<NewsItem>();

            foreach (var article in document.RootElement.EnumerateArray())
            {
                var title = ReadString(article, "title") ?? string.Empty;
                var description = ReadString(article, "description") ?? string.Empty;
                var itemUrl = ReadString(article, "url") ?? ReadString(article, "canonical_url");
                var author = ReadNestedString(article, "user", "name");
                var publishedAt = ParseDate(ReadString(article, "published_at") ?? ReadString(article, "created_at"));
                var updatedAt = ParseNullableDate(ReadString(article, "edited_at"));
                var tagList = ReadTags(article, "tag_list");
                var technologyTags = InferTechnologyTags($"{title} {description} {string.Join(' ', tagList)}");
                var matchedKeywords = FindKeywords($"{title} {description} {string.Join(' ', technologyTags)}");

                if (!IsRelevant(matchedKeywords))
                {
                    continue;
                }

                results.Add(CreateItem(
                    source,
                    $"{source.Key}:{itemUrl ?? title}",
                    title,
                    description,
                    itemUrl ?? source.Url,
                    author,
                    publishedAt,
                    updatedAt,
                    technologyTags,
                    matchedKeywords));
            }

            return results.OrderByDescending(item => item.PublishedAt).ToList();
        }
        catch
        {
            LastErrorMessage = $"We could not load updates from {source.Name} right now.";
            return [];
        }
    }

    private async Task<IReadOnlyList<NewsItem>> LoadFeedAsync(SourceDefinition source, CancellationToken cancellationToken)
    {
        try
        {
            var xml = await GetXmlWithProxyFallbackAsync(source.Url, cancellationToken);
            var document = XDocument.Parse(xml);
            var rootName = document.Root?.Name.LocalName ?? string.Empty;

            return rootName switch
            {
                "rss" => ParseRssFeed(source, document),
                "feed" => ParseAtomFeed(source, document),
                _ => []
            };
        }
        catch
        {
            LastErrorMessage = $"We could not read the feed from {source.Name} right now.";
            return [];
        }
    }

    private async Task<IReadOnlyList<NewsItem>> LoadHackerNewsAsync(SourceDefinition source, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await _httpClient.GetStringAsync(source.Url, cancellationToken);
            var ids = JsonSerializer.Deserialize<int[]>(payload) ?? [];
            var candidates = ids.Take(30).ToArray();
            var results = new List<NewsItem>();

            foreach (var id in candidates)
            {
                if (!await TryLoadHackerNewsItemAsync(id, source, results, cancellationToken))
                {
                    continue;
                }
            }

            return results.OrderByDescending(item => item.PublishedAt).ToList();
        }
        catch
        {
            LastErrorMessage = $"We could not load updates from {source.Name} right now.";
            return [];
        }
    }

    private async Task<bool> TryLoadHackerNewsItemAsync(int id, SourceDefinition source, List<NewsItem> results, CancellationToken cancellationToken)
    {
        try
        {
            JsonElement item;

            if (!_hackerNewsItemCache.TryGetValue(id, out item))
            {
                var json = await _httpClient.GetStringAsync($"https://hacker-news.firebaseio.com/v0/item/{id}.json", cancellationToken);
                using var document = JsonDocument.Parse(json);
                item = document.RootElement.Clone();
                _hackerNewsItemCache[id] = item;
            }

            var title = ReadString(item, "title") ?? string.Empty;
            var description = ReadString(item, "text") ?? string.Empty;
            var author = ReadString(item, "by");
            var itemUrl = ReadString(item, "url") ?? $"https://news.ycombinator.com/item?id={id}";
            var publishedAt = FromUnixSeconds(ReadNumber(item, "time"));
            var updatedAt = ParseNullableDate(ReadString(item, "updated_at"));
            var technologyTags = InferTechnologyTags($"{title} {description} {source.Name}");
            var matchedKeywords = FindKeywords($"{title} {description} {string.Join(' ', technologyTags)}");

            if (!IsRelevant(matchedKeywords))
            {
                return false;
            }

            results.Add(CreateItem(
                source,
                $"{source.Key}:{id}",
                title,
                description,
                itemUrl,
                author,
                publishedAt,
                updatedAt,
                technologyTags,
                matchedKeywords));

            return true;
        }
        catch
        {
            LastErrorMessage = $"We could not load updates from {source.Name} right now.";
            return false;
        }
    }

    private List<NewsItem> ParseRssFeed(SourceDefinition source, XDocument document)
    {
        var items = document
            .Descendants()
            .Where(element => element.Name.LocalName == "item")
            .Select(item => MapRssItem(source, item))
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderByDescending(item => item.PublishedAt)
            .ToList();

        return items;
    }

    private List<NewsItem> ParseAtomFeed(SourceDefinition source, XDocument document)
    {
        var items = document
            .Descendants()
            .Where(element => element.Name.LocalName == "entry")
            .Select(entry => MapAtomItem(source, entry))
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderByDescending(item => item.PublishedAt)
            .ToList();

        return items;
    }

    private NewsItem? MapRssItem(SourceDefinition source, XElement item)
    {
        var title = CleanText(ReadText(item, "title"));
        var description = CleanText(ReadText(item, "description") ?? ReadText(item, "summary") ?? ReadText(item, "content"));
        var itemUrl = ReadText(item, "link") ?? source.Url;
        var author = CleanText(ReadText(item, "creator") ?? ReadText(item, "author")) ?? source.Name;
        var publishedAt = ParseDate(ReadText(item, "pubDate") ?? ReadText(item, "published") ?? ReadText(item, "updated"));
        var updatedAt = ParseNullableDate(ReadText(item, "updated"));
        var combinedText = $"{title} {description} {source.Name}";
        var technologyTags = InferTechnologyTags(combinedText);
        var matchedKeywords = FindKeywords(combinedText);

        if (!IsRelevant(matchedKeywords))
        {
            return null;
        }

        return CreateItem(
            source,
            $"{source.Key}:{itemUrl}",
            title,
            description,
            itemUrl,
            author,
            publishedAt,
            updatedAt,
            technologyTags,
            matchedKeywords);
    }

    private NewsItem? MapAtomItem(SourceDefinition source, XElement entry)
    {
        var title = CleanText(ReadText(entry, "title"));
        var description = CleanText(ReadText(entry, "summary") ?? ReadText(entry, "content"));
        var itemUrl = ReadAtomLink(entry) ?? source.Url;
        var author = CleanText(ReadNestedText(entry, "author", "name") ?? ReadText(entry, "author")) ?? source.Name;
        var publishedAt = ParseDate(ReadText(entry, "published") ?? ReadText(entry, "updated"));
        var updatedAt = ParseNullableDate(ReadText(entry, "updated"));
        var combinedText = $"{title} {description} {source.Name}";
        var technologyTags = InferTechnologyTags(combinedText);
        var matchedKeywords = FindKeywords(combinedText);

        if (!IsRelevant(matchedKeywords))
        {
            return null;
        }

        return CreateItem(
            source,
            $"{source.Key}:{itemUrl}",
            title,
            description,
            itemUrl,
            author,
            publishedAt,
            updatedAt,
            technologyTags,
            matchedKeywords);
    }

    private static NewsItem CreateItem(
        SourceDefinition source,
        string id,
        string title,
        string description,
        string itemUrl,
        string? author,
        DateTimeOffset publishedAt,
        DateTimeOffset? updatedAt,
        IReadOnlyList<string> technologyTags,
        IReadOnlyList<string> matchedKeywords)
    {
        return new NewsItem(
            id,
            title,
            Truncate(description, 260),
            source.Name,
            source.Url,
            itemUrl,
            string.IsNullOrWhiteSpace(author) ? source.Name : author.Trim(),
            publishedAt,
            updatedAt,
            source.Type,
            technologyTags,
            matchedKeywords);
    }

    private async Task<string> GetXmlWithProxyFallbackAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.GetStringAsync(url, cancellationToken);
        }
        catch
        {
            var proxyUrl = $"https://api.allorigins.win/raw?url={Uri.EscapeDataString(url)}";
            return await _httpClient.GetStringAsync(proxyUrl, cancellationToken);
        }
    }

    private static bool IsRelevant(IReadOnlyCollection<string> matchedKeywords) => matchedKeywords.Count > 0;

    private static IReadOnlyList<string> FindKeywords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var normalized = $" {text.ToLowerInvariant()} ";
        var matches = EcosystemKeywords
            .Where(keyword => normalized.Contains($" {keyword} ") || normalized.Contains(keyword))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return matches;
    }

    private static IReadOnlyList<string> InferTechnologyTags(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var normalized = text.ToLowerInvariant();
        var tags = new List<string>();

        void AddIfMatch(string tag, params string[] aliases)
        {
            if (aliases.Any(alias => normalized.Contains(alias)))
            {
                tags.Add(tag);
            }
        }

        AddIfMatch(".NET", ".net", "dotnet");
        AddIfMatch("ASP.NET Core", "asp.net", "aspnet", "minimal api", "minimal apis");
        AddIfMatch("EF Core", "ef core", "entity framework");
        AddIfMatch("Blazor", "blazor");
        AddIfMatch("C#", "c#");
        AddIfMatch("Azure", "azure");
        AddIfMatch("MAUI", "maui");
        AddIfMatch("SignalR", "signalr");
        AddIfMatch("NuGet", "nuget");
        AddIfMatch("LINQ", "linq");
        AddIfMatch("Identity", "identity");

        return tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string CleanText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var withoutHtml = HtmlRegex.Replace(value, " ");
        return WhiteSpaceRegex.Replace(withoutHtml, " ").Trim();
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
        {
            return value?.Trim() ?? string.Empty;
        }

        return value[..(maxLength - 3)].TrimEnd() + "...";
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string? ReadNestedString(JsonElement element, string parentProperty, string childProperty)
    {
        if (!element.TryGetProperty(parentProperty, out var parent) || parent.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ReadString(parent, childProperty);
    }

    private static IReadOnlyList<string> ReadTags(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property
            .EnumerateArray()
            .Select(item => item.GetString())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag!.Trim())
            .ToArray();
    }

    private static int ReadNumber(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : 0;
    }

    private static string? ReadText(XElement element, string localName)
    {
        return element.Elements().FirstOrDefault(child => child.Name.LocalName == localName)?.Value;
    }

    private static string? ReadNestedText(XElement element, string parentLocalName, string childLocalName)
    {
        return element.Elements()
            .FirstOrDefault(child => child.Name.LocalName == parentLocalName)?
            .Elements()
            .FirstOrDefault(child => child.Name.LocalName == childLocalName)?
            .Value;
    }

    private static string? ReadAtomLink(XElement entry)
    {
        return entry.Elements()
            .FirstOrDefault(child => child.Name.LocalName == "link")?
            .Attribute("href")?
            .Value;
    }

    private static DateTimeOffset ParseDate(string? value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }

        return DateTimeOffset.UtcNow;
    }

    private static DateTimeOffset? ParseNullableDate(string? value)
    {
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static DateTimeOffset FromUnixSeconds(int unixSeconds)
    {
        return unixSeconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
            : DateTimeOffset.UtcNow;
    }
}
