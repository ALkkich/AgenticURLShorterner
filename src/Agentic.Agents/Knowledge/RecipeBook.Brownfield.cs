using Agentic.Orchestrator.Model;

namespace Agentic.Agents.Knowledge;

public static partial class RecipeBook
{
    private static IEnumerable<ChangeRecipe> Brownfield() =>
    [
        new("bf-redirect-caching", "Make redirects temporary and non-cacheable (fix analytics undercount)", [], RiskLevel.Medium,
        [
            new($"{Api}Api/LinkEndpoints.cs",
                """
                        service.RecordClick(link, hasher.Hash(http.Connection.RemoteIpAddress), ReferrerHost(http));
                        return TypedResults.Redirect(link.TargetUrl.AbsoluteUri, permanent: true);
                """,
                """
                        service.RecordClick(link, hasher.Hash(http.Connection.RemoteIpAddress), ReferrerHost(http));

                        // 302 + no-store: a cached 301 would let browsers skip the service and undercount clicks.
                        http.Response.Headers.CacheControl = "no-store";
                        return TypedResults.Redirect(link.TargetUrl.AbsoluteUri, permanent: false);
                """),
            new($"{Tests}ApiIntegrationTests.cs",
                """
                        var response = await _client.GetAsync("/go-target");

                        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
                        Assert.Equal("https://example.com/target", response.Headers.Location!.AbsoluteUri);
                    }
                """,
                """
                        var response = await _client.GetAsync("/go-target");

                        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
                        Assert.Equal("https://example.com/target", response.Headers.Location!.AbsoluteUri);
                    }

                    [Fact]
                    public async Task Redirect_is_temporary_and_not_cacheable()
                    {
                        await CreateAsync("https://example.com/nocache", "no-cache-link");

                        var response = await _client.GetAsync("/no-cache-link");

                        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
                        Assert.True(response.Headers.CacheControl!.NoStore);
                    }

                    [Fact]
                    public async Task Repeat_clicks_are_each_counted()
                    {
                        await CreateAsync("https://example.com/repeat", "repeat-link");

                        for (var i = 0; i < 3; i++)
                            await _client.GetAsync("/repeat-link");

                        var stats = await _client.GetFromJsonAsync<LinkStats>("/api/links/repeat-link/stats");
                        Assert.Equal(3, stats!.TotalClicks);
                    }
                """),
        ]),

        new("bf-expiry-domain", "Add optional expiry to the ShortLink domain model", [], RiskLevel.Low,
        [
            new($"{Api}Domain/ShortLink.cs",
                "public sealed record ShortLink(string Code, Uri TargetUrl, DateTimeOffset CreatedAt, bool IsCustomAlias);",
                """
                public sealed record ShortLink(string Code, Uri TargetUrl, DateTimeOffset CreatedAt, bool IsCustomAlias, DateTimeOffset? ExpiresAt = null)
                {
                    public bool IsExpiredAt(DateTimeOffset now) => ExpiresAt is { } expiresAt && now >= expiresAt;
                }
                """),
        ]),

        new("bf-expiry-service", "Validate expiry on create; report expired links on resolve", ["bf-expiry-domain"], RiskLevel.Medium,
        [
            new($"{Api}Services/LinkService.cs",
                "public enum CreateLinkStatus { Created, Invalid, AliasTaken, CodeSpaceExhausted }",
                """
                public enum CreateLinkStatus { Created, Invalid, AliasTaken, CodeSpaceExhausted }

                public enum ResolveStatus { Found, NotFound, Expired }

                public sealed record ResolveResult(ResolveStatus Status, ShortLink? Link);
                """),
            new($"{Api}Services/LinkService.cs",
                "public sealed record CreateLinkCommand(string? Url, string? CustomAlias);",
                "public sealed record CreateLinkCommand(string? Url, string? CustomAlias, DateTimeOffset? ExpiresAt = null);"),
            new($"{Api}Services/LinkService.cs",
                """
                        var now = time.GetUtcNow();

                """,
                """
                        var now = time.GetUtcNow();
                        if (command.ExpiresAt is { } expiresAt && expiresAt <= now)
                            return CreateLinkResult.Fail(CreateLinkStatus.Invalid, "expiresAt", "Expiry must be in the future.");

                """),
            new($"{Api}Services/LinkService.cs",
                "new ShortLink(command.CustomAlias, target!, now, IsCustomAlias: true);",
                "new ShortLink(command.CustomAlias, target!, now, IsCustomAlias: true, ExpiresAt: command.ExpiresAt);"),
            new($"{Api}Services/LinkService.cs",
                "new ShortLink(codeGenerator.Generate(_options.CodeLength), target!, now, IsCustomAlias: false);",
                "new ShortLink(codeGenerator.Generate(_options.CodeLength), target!, now, IsCustomAlias: false, ExpiresAt: command.ExpiresAt);"),
            new($"{Api}Services/LinkService.cs",
                "    public ShortLink? Resolve(string code) => repository.Find(code);",
                """
                    /// <summary>Redirect-path lookup; expired links are reported distinctly so the API can answer 410 Gone.</summary>
                    public ResolveResult Resolve(string code) => repository.Find(code) switch
                    {
                        null => new ResolveResult(ResolveStatus.NotFound, null),
                        var link when link.IsExpiredAt(time.GetUtcNow()) => new ResolveResult(ResolveStatus.Expired, link),
                        var link => new ResolveResult(ResolveStatus.Found, link),
                    };
                """),
        ]),

        new("bf-expiry-api", "Expose expiresAt in the API and answer 410 for expired links", ["bf-expiry-service"], RiskLevel.Medium,
        [
            new($"{Api}Api/Contracts.cs",
                "public sealed record CreateLinkRequest(string? Url, string? CustomAlias);",
                "public sealed record CreateLinkRequest(string? Url, string? CustomAlias, DateTimeOffset? ExpiresAt = null);"),
            new($"{Api}Api/Contracts.cs",
                """
                public sealed record LinkResponse(string Code, string ShortUrl, string TargetUrl, DateTimeOffset CreatedAt, bool IsCustomAlias)
                {
                    public static LinkResponse From(ShortLink link, string baseUrl) =>
                        new(link.Code, $"{baseUrl.TrimEnd('/')}/{link.Code}", link.TargetUrl.AbsoluteUri, link.CreatedAt, link.IsCustomAlias);
                }
                """,
                """
                public sealed record LinkResponse(string Code, string ShortUrl, string TargetUrl, DateTimeOffset CreatedAt, bool IsCustomAlias, DateTimeOffset? ExpiresAt = null)
                {
                    public static LinkResponse From(ShortLink link, string baseUrl) =>
                        new(link.Code, $"{baseUrl.TrimEnd('/')}/{link.Code}", link.TargetUrl.AbsoluteUri, link.CreatedAt, link.IsCustomAlias, link.ExpiresAt);
                }
                """),
            new($"{Api}Api/LinkEndpoints.cs",
                "service.Create(new CreateLinkCommand(request.Url, request.CustomAlias));",
                "service.Create(new CreateLinkCommand(request.Url, request.CustomAlias, request.ExpiresAt));"),
            new($"{Api}Api/LinkEndpoints.cs",
                """
                        var link = service.Resolve(code);
                        if (link is null)
                            return TypedResults.NotFound();
                """,
                """
                        var resolved = service.Resolve(code);
                        if (resolved.Status == ResolveStatus.NotFound)
                            return TypedResults.NotFound();
                        if (resolved.Status == ResolveStatus.Expired)
                            return TypedResults.Problem(statusCode: StatusCodes.Status410Gone, title: "Link expired");

                        var link = resolved.Link!;
                """),
        ]),

        new("bf-expiry-tests", "Expiration unit and integration tests", ["bf-expiry-api"], RiskLevel.Low,
        [
            new($"{Tests}ExpirationTests.cs", null,
                """
                using System.Net;
                using System.Net.Http.Json;
                using UrlShortener.Api.Analytics;
                using UrlShortener.Api.Api;

                namespace UrlShortener.Tests;

                public class ExpirationTests(ShortenerApiFactory factory) : IClassFixture<ShortenerApiFactory>
                {
                    private readonly HttpClient _client = factory.CreateNonRedirectingClient();

                    private DateTimeOffset Now => factory.Clock.GetUtcNow();

                    private Task<HttpResponseMessage> CreateAsync(string alias, DateTimeOffset? expiresAt) =>
                        _client.PostAsJsonAsync("/api/links", new CreateLinkRequest($"https://example.com/{alias}", alias, expiresAt));

                    [Fact]
                    public async Task Create_accepts_future_expiry()
                    {
                        var expiresAt = Now.AddHours(1);

                        var response = await CreateAsync("exp-future", expiresAt);

                        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                        var body = await response.Content.ReadFromJsonAsync<LinkResponse>();
                        Assert.Equal(expiresAt, body!.ExpiresAt);
                    }

                    [Fact]
                    public async Task Create_rejects_expiry_in_the_past()
                    {
                        var response = await CreateAsync("exp-past", Now.AddSeconds(-1));

                        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                    }

                    [Fact]
                    public async Task Expired_link_returns_410_and_is_not_counted()
                    {
                        await CreateAsync("exp-410", Now.AddMinutes(10));
                        Assert.Equal(HttpStatusCode.Found, (await _client.GetAsync("/exp-410")).StatusCode);

                        factory.Clock.Advance(TimeSpan.FromMinutes(11));

                        Assert.Equal(HttpStatusCode.Gone, (await _client.GetAsync("/exp-410")).StatusCode);
                        var stats = await _client.GetFromJsonAsync<LinkStats>("/api/links/exp-410/stats");
                        Assert.Equal(1, stats!.TotalClicks);
                    }

                    [Fact]
                    public async Task Link_without_expiry_never_expires()
                    {
                        await CreateAsync("exp-never", null);

                        factory.Clock.Advance(TimeSpan.FromDays(365));

                        Assert.Equal(HttpStatusCode.Found, (await _client.GetAsync("/exp-never")).StatusCode);
                    }

                    [Fact]
                    public async Task Metadata_includes_expiry()
                    {
                        var expiresAt = Now.AddDays(7);
                        await CreateAsync("exp-meta", expiresAt);

                        var link = await _client.GetFromJsonAsync<LinkResponse>("/api/links/exp-meta");

                        Assert.Equal(expiresAt, link!.ExpiresAt);
                    }
                }
                """),
        ]),
    ];
}
