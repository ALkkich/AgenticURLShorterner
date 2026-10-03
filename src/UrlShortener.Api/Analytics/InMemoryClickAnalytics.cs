using System.Collections.Concurrent;
using System.Globalization;

namespace UrlShortener.Api.Analytics;

/// <summary>
/// Aggregates clicks into per-link counters instead of retaining raw events, which bounds
/// memory and avoids keeping a click-level trail. Distinct referrer hosts are capped.
/// </summary>
public sealed class InMemoryClickAnalytics : IClickAnalytics
{
    public const int MaxTrackedReferrers = 100;
    public const string OtherReferrers = "(other)";
    public const string DirectTraffic = "(direct)";
    private const int TopReferrerCount = 5;

    private readonly ConcurrentDictionary<string, Counters> _counters = new(StringComparer.Ordinal);

    public void Record(ClickEvent click)
    {
        var counters = _counters.GetOrAdd(click.Code, _ => new Counters());
        lock (counters)
        {
            counters.Total++;
            if (counters.LastClickAt is null || click.OccurredAt > counters.LastClickAt)
                counters.LastClickAt = click.OccurredAt;
            if (click.VisitorHash is not null)
                counters.Visitors.Add(click.VisitorHash);

            var day = click.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            counters.ByDay[day] = counters.ByDay.GetValueOrDefault(day) + 1;

            var referrer = click.ReferrerHost ?? DirectTraffic;
            if (!counters.Referrers.ContainsKey(referrer) && counters.Referrers.Count >= MaxTrackedReferrers)
                referrer = OtherReferrers;
            counters.Referrers[referrer] = counters.Referrers.GetValueOrDefault(referrer) + 1;
        }
    }

    public LinkStats GetStats(string code)
    {
        if (!_counters.TryGetValue(code, out var counters))
            return new LinkStats(code, 0, 0, null, new Dictionary<string, long>(), []);

        lock (counters)
        {
            var top = counters.Referrers
                .OrderByDescending(r => r.Value).ThenBy(r => r.Key, StringComparer.Ordinal)
                .Take(TopReferrerCount)
                .Select(r => new ReferrerCount(r.Key, r.Value))
                .ToList();
            return new LinkStats(code, counters.Total, counters.Visitors.Count, counters.LastClickAt,
                new SortedDictionary<string, long>(counters.ByDay, StringComparer.Ordinal), top);
        }
    }

    public void Remove(string code) => _counters.TryRemove(code, out _);

    private sealed class Counters
    {
        public long Total;
        public DateTimeOffset? LastClickAt;
        public readonly HashSet<string> Visitors = new(StringComparer.Ordinal);
        public readonly Dictionary<string, long> ByDay = new(StringComparer.Ordinal);
        public readonly Dictionary<string, long> Referrers = new(StringComparer.OrdinalIgnoreCase);
    }
}
