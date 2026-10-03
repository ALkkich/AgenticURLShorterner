namespace UrlShortener.Api.Analytics;

/// <param name="VisitorHash">Salted one-way hash of the client address; never the raw IP.</param>
/// <param name="ReferrerHost">Host part of the Referer header only (path/query may contain PII).</param>
public sealed record ClickEvent(string Code, DateTimeOffset OccurredAt, string? VisitorHash, string? ReferrerHost);

public sealed record ReferrerCount(string Host, long Clicks);

public sealed record LinkStats(
    string Code,
    long TotalClicks,
    int UniqueVisitors,
    DateTimeOffset? LastClickAt,
    IReadOnlyDictionary<string, long> ClicksByDay,
    IReadOnlyList<ReferrerCount> TopReferrers);

public interface IClickAnalytics
{
    void Record(ClickEvent click);
    LinkStats GetStats(string code);
    void Remove(string code);
}
