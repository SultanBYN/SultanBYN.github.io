namespace SultanPortfolio.Web.Models;

/// <summary>
/// Represents a news article from any source (dev.to, thenewstack.io, etc.)
/// Simple record — no EF Core, no database annotations.
/// </summary>
public record NewsArticleDto(
    string Title,
    string Url,
    DateTime PublishedAt,
    string SourceName,
    string Summary
);
