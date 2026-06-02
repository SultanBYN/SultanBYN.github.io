using SultanPortfolio.Web.Models;

namespace SultanPortfolio.Web.Services;

/// <summary>
/// Service for fetching and aggregating developer news articles from external sources.
/// </summary>
public interface INewsFeedService
{
    /// <summary>
    /// Gets a paginated list of news articles merged from all configured sources,
    /// ordered by publication date descending.
    /// </summary>
    /// <param name="page">1-based page number</param>
    /// <param name="pageSize">Number of articles per page</param>
    Task<IReadOnlyList<NewsArticleDto>> GetArticlesAsync(int page, int pageSize);
}
