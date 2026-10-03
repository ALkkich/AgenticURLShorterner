using System.Net;
using System.Net.Http.Json;
using UrlShortener.Api.Analytics;
using UrlShortener.Api.Api;

namespace UrlShortener.Tests;

public class ApiIntegrationTests(ShortenerApiFactory factory) : IClassFixture<ShortenerApiFactory>
{
    private readonly HttpClient _client = factory.CreateNonRedirectingClient();

    private async Task<LinkResponse> CreateAsync(string url, string? alias = null)
    {
        var response = await _client.PostAsJsonAsync("/api/links", new CreateLinkRequest(url, alias));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LinkResponse>())!;
    }

    [Fact]
    public async Task Create_returns_201_with_location_and_short_url()
    {
        var response = await _client.PostAsJsonAsync("/api/links", new CreateLinkRequest("https://example.com/docs", null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LinkResponse>();
        Assert.Equal($"/api/links/{body!.Code}", response.Headers.Location!.OriginalString);
        Assert.EndsWith("/" + body.Code, body.ShortUrl);
        Assert.Equal("https://example.com/docs", body.TargetUrl);
    }

    [Fact]
    public async Task Create_with_invalid_url_returns_validation_problem()
    {
        var response = await _client.PostAsJsonAsync("/api/links", new CreateLinkRequest("javascript:alert(1)", null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Duplicate_alias_returns_409()
    {
        await CreateAsync("https://example.com/a", "dup-alias");

        var response = await _client.PostAsJsonAsync("/api/links", new CreateLinkRequest("https://example.com/b", "dup-alias"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Redirect_sends_client_to_target()
    {
        await CreateAsync("https://example.com/target", "go-target");

        var response = await _client.GetAsync("/go-target");

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal("https://example.com/target", response.Headers.Location!.AbsoluteUri);
    }

    [Fact]
    public async Task Unknown_code_returns_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/nope-404")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/links/nope-404")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/links/nope-404/stats")).StatusCode);
    }

    [Fact]
    public async Task Stats_reflect_redirects_with_referrer_host_only()
    {
        await CreateAsync("https://example.com/stats", "stats-link");
        var request = new HttpRequestMessage(HttpMethod.Get, "/stats-link");
        request.Headers.Referrer = new Uri("https://news.example/article?user=42");
        await _client.SendAsync(request);
        await _client.GetAsync("/stats-link");

        var stats = await _client.GetFromJsonAsync<LinkStats>("/api/links/stats-link/stats");

        Assert.Equal(2, stats!.TotalClicks);
        Assert.Contains(stats.TopReferrers, r => r.Host == "news.example");
        Assert.DoesNotContain(stats.TopReferrers, r => r.Host.Contains("user=42"));
    }

    [Fact]
    public async Task Delete_makes_link_unreachable()
    {
        await CreateAsync("https://example.com/delete", "to-delete");

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync("/api/links/to-delete")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/to-delete")).StatusCode);
    }

    [Fact]
    public async Task Health_endpoints_report_healthy()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health/ready")).StatusCode);
    }
}
