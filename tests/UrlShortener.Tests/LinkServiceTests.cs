using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UrlShortener.Api;
using UrlShortener.Api.Analytics;
using UrlShortener.Api.Domain;
using UrlShortener.Api.Services;
using UrlShortener.Api.Storage;

namespace UrlShortener.Tests;

public class LinkServiceTests
{
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly InMemoryLinkRepository _repository = new();
    private readonly InMemoryClickAnalytics _analytics = new();

    private LinkService CreateService(IShortCodeGenerator? generator = null, int maxAttempts = 3) => new(
        _repository,
        generator ?? new RandomShortCodeGenerator(),
        _analytics,
        _clock,
        Options.Create(new ShortenerOptions { MaxGenerationAttempts = maxAttempts, VisitorHashSalt = "test-salt-123" }),
        NullLogger<LinkService>.Instance);

    [Fact]
    public void Create_generates_a_code_and_stamps_creation_time()
    {
        var result = CreateService().Create(new CreateLinkCommand("https://example.com", null));

        Assert.Equal(CreateLinkStatus.Created, result.Status);
        Assert.Equal(7, result.Link!.Code.Length);
        Assert.Equal(_clock.GetUtcNow(), result.Link.CreatedAt);
        Assert.False(result.Link.IsCustomAlias);
    }

    [Fact]
    public void Create_retries_on_code_collision()
    {
        var service = CreateService(new SequenceCodeGenerator("dup0001", "dup0001", "fresh01"));
        service.Create(new CreateLinkCommand("https://a.example", null));

        var result = service.Create(new CreateLinkCommand("https://b.example", null));

        Assert.Equal(CreateLinkStatus.Created, result.Status);
        Assert.Equal("fresh01", result.Link!.Code);
    }

    [Fact]
    public void Create_fails_safely_when_collision_retries_are_exhausted()
    {
        var service = CreateService(new SequenceCodeGenerator("same123"), maxAttempts: 2);
        service.Create(new CreateLinkCommand("https://a.example", null));

        var result = service.Create(new CreateLinkCommand("https://b.example", null));

        Assert.Equal(CreateLinkStatus.CodeSpaceExhausted, result.Status);
    }

    [Fact]
    public void Create_rejects_duplicate_custom_alias()
    {
        var service = CreateService();
        service.Create(new CreateLinkCommand("https://a.example", "launch"));

        var result = service.Create(new CreateLinkCommand("https://b.example", "launch"));

        Assert.Equal(CreateLinkStatus.AliasTaken, result.Status);
        Assert.Equal(new Uri("https://a.example"), service.Get("launch")!.TargetUrl);
    }

    [Fact]
    public void Create_reports_validation_errors_per_field()
    {
        var result = CreateService().Create(new CreateLinkCommand("ftp://x", null));

        Assert.Equal(CreateLinkStatus.Invalid, result.Status);
        Assert.True(result.Errors.ContainsKey("url"));
    }

    [Fact]
    public void Delete_removes_link_and_its_analytics()
    {
        var service = CreateService();
        var link = service.Create(new CreateLinkCommand("https://a.example", "gone")).Link!;
        service.RecordClick(link, "v1", null);

        Assert.True(service.Delete("gone"));
        Assert.Null(service.Get("gone"));
        Assert.Null(service.GetStats("gone"));
        Assert.Equal(0, _analytics.GetStats("gone").TotalClicks);
    }
}

public class InMemoryClickAnalyticsTests
{
    private static readonly DateTimeOffset Day1 = new(2026, 5, 1, 23, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Aggregates_totals_unique_visitors_days_and_referrers()
    {
        var analytics = new InMemoryClickAnalytics();
        analytics.Record(new ClickEvent("c", Day1, "v1", "news.example"));
        analytics.Record(new ClickEvent("c", Day1.AddHours(1), "v1", null));
        analytics.Record(new ClickEvent("c", Day1.AddHours(2), "v2", "news.example"));

        var stats = analytics.GetStats("c");

        Assert.Equal(3, stats.TotalClicks);
        Assert.Equal(2, stats.UniqueVisitors);
        Assert.Equal(Day1.AddHours(2), stats.LastClickAt);
        Assert.Equal(1, stats.ClicksByDay["2026-05-01"]);
        Assert.Equal(2, stats.ClicksByDay["2026-05-02"]);
        Assert.Equal(new ReferrerCount("news.example", 2), stats.TopReferrers[0]);
    }

    [Fact]
    public void Caps_distinct_referrers_to_bound_memory()
    {
        var analytics = new InMemoryClickAnalytics();
        for (var i = 0; i < InMemoryClickAnalytics.MaxTrackedReferrers + 10; i++)
            analytics.Record(new ClickEvent("c", Day1, null, $"r{i}.example"));

        var stats = analytics.GetStats("c");

        Assert.Equal(InMemoryClickAnalytics.MaxTrackedReferrers + 10, stats.TotalClicks);
        Assert.Contains(stats.TopReferrers, r => r.Host == InMemoryClickAnalytics.OtherReferrers && r.Clicks == 10);
    }

    [Fact]
    public async Task Concurrent_recording_loses_no_clicks()
    {
        var analytics = new InMemoryClickAnalytics();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
                analytics.Record(new ClickEvent("hot", Day1, null, null));
        })));

        Assert.Equal(4000, analytics.GetStats("hot").TotalClicks);
    }
}
