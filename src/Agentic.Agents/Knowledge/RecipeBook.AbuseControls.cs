using Agentic.Orchestrator.Model;

namespace Agentic.Agents.Knowledge;

public static partial class RecipeBook
{
    /// <summary>Placeholders like <c>{{CreatePermitLimit}}</c> are filled from the clarified spec's parameters.</summary>
    private static IEnumerable<ChangeRecipe> AbuseControls() =>
    [
        new("ab-destination-safety", "Reject internal/private destination URLs", [], RiskLevel.Medium,
        [
            new($"{Api}Domain/LinkValidator.cs",
                "using System.Text.RegularExpressions;",
                """
                using System.Net;
                using System.Net.Sockets;
                using System.Text.RegularExpressions;
                """),
            new($"{Api}Domain/LinkValidator.cs",
                """
                        if (!string.IsNullOrEmpty(uri.UserInfo))
                            return "Destination URLs must not embed credentials.";
                """,
                """
                        if (!string.IsNullOrEmpty(uri.UserInfo))
                            return "Destination URLs must not embed credentials.";
                        if (IsInternalHost(uri))
                            return "Destination URLs must not point to internal, loopback or private network hosts.";
                """),
            new($"{Api}Domain/LinkValidator.cs",
                "    public static string? ValidateAlias(string alias)",
                """
                    /// <summary>
                    /// Blocks destinations that would make the shortener a pivot to internal services. Only IP
                    /// literals and well-known internal names are checked — no DNS resolution on the create path
                    /// (latency, DNS-rebinding TOCTOU); egress controls remain the defence in depth.
                    /// </summary>
                    public static bool IsInternalHost(Uri uri)
                    {
                        var host = uri.IdnHost.Trim('[', ']').TrimEnd('.');
                        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
                            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
                            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
                            return true;

                        if (!IPAddress.TryParse(host, out var ip))
                            return false;
                        if (ip.IsIPv4MappedToIPv6)
                            ip = ip.MapToIPv4();
                        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)
                            || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal)
                            return true;
                        if (ip.AddressFamily != AddressFamily.InterNetwork)
                            return false;

                        var b = ip.GetAddressBytes();
                        return b[0] is 0 or 10 or 127
                            || (b[0] == 172 && b[1] is >= 16 and <= 31)
                            || (b[0] == 192 && b[1] == 168)
                            || (b[0] == 169 && b[1] == 254)
                            || (b[0] == 100 && b[1] is >= 64 and <= 127);
                    }

                    public static string? ValidateAlias(string alias)
                """),
            new($"{Tests}DestinationSafetyTests.cs", null,
                """
                using System.Net;
                using System.Net.Http.Json;
                using UrlShortener.Api.Api;
                using UrlShortener.Api.Domain;

                namespace UrlShortener.Tests;

                public class DestinationSafetyTests(ShortenerApiFactory factory) : IClassFixture<ShortenerApiFactory>
                {
                    [Theory]
                    [InlineData("http://localhost/admin")]
                    [InlineData("http://127.0.0.1:8080/")]
                    [InlineData("http://10.0.0.5/")]
                    [InlineData("http://172.16.4.2/")]
                    [InlineData("http://192.168.1.1/router")]
                    [InlineData("http://169.254.169.254/latest/meta-data")]
                    [InlineData("http://[::1]/")]
                    [InlineData("https://payroll.corp.internal/")]
                    public void Rejects_internal_destinations(string url) =>
                        Assert.NotNull(LinkValidator.ValidateTargetUrl(url, 2048, out _));

                    [Theory]
                    [InlineData("https://example.com/")]
                    [InlineData("http://8.8.8.8/")]
                    [InlineData("http://172.32.0.1/")]
                    public void Accepts_public_destinations(string url) =>
                        Assert.Null(LinkValidator.ValidateTargetUrl(url, 2048, out _));

                    [Fact]
                    public async Task Create_with_internal_destination_returns_400()
                    {
                        var client = factory.CreateNonRedirectingClient();

                        var response = await client.PostAsJsonAsync("/api/links", new CreateLinkRequest("http://169.254.169.254/latest", null));

                        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                    }
                }
                """),
        ]),

        new("ab-rate-limit-creation", "Per-client rate limiting for link creation", [], RiskLevel.Medium,
        [
            new($"{Api}RateLimiting.cs", null,
                """
                using System.ComponentModel.DataAnnotations;
                using System.Globalization;
                using System.Threading.RateLimiting;
                using Microsoft.AspNetCore.RateLimiting;
                using Microsoft.Extensions.Options;

                namespace UrlShortener.Api;

                public sealed class RateLimitingOptions
                {
                    public const string SectionName = "RateLimiting";

                    [Range(1, 100_000)]
                    public int CreatePermitLimit { get; set; } = {{CreatePermitLimit}};

                    [Range(1, 1_000_000)]
                    public int RedirectPermitLimit { get; set; } = 600;

                    [Range(1, 3600)]
                    public int WindowSeconds { get; set; } = 60;
                }

                public static class RateLimitPolicies
                {
                    public const string CreateLink = "create-link";
                    public const string Redirect = "redirect";

                    public static IServiceCollection AddLinkRateLimiting(this IServiceCollection services, IConfiguration configuration)
                    {
                        services.AddOptions<RateLimitingOptions>()
                            .Bind(configuration.GetSection(RateLimitingOptions.SectionName))
                            .ValidateDataAnnotations()
                            .ValidateOnStart();

                        services.AddRateLimiter(limiter =>
                        {
                            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                            limiter.OnRejected = (context, _) =>
                            {
                                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                                    context.HttpContext.Response.Headers.RetryAfter =
                                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                                return ValueTask.CompletedTask;
                            };
                            limiter.AddPolicy(CreateLink, http => FixedWindowPerClient(http, o => o.CreatePermitLimit));
                            limiter.AddPolicy(Redirect, http => FixedWindowPerClient(http, o => o.RedirectPermitLimit));
                        });
                        return services;
                    }

                    // Partitioned per client address. Behind a reverse proxy, ForwardedHeaders must be
                    // configured or every client shares the proxy's partition.
                    private static RateLimitPartition<string> FixedWindowPerClient(HttpContext http, Func<RateLimitingOptions, int> permits)
                    {
                        var options = http.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
                        var client = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                        return RateLimitPartition.GetFixedWindowLimiter(client, _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = permits(options),
                            Window = TimeSpan.FromSeconds(options.WindowSeconds),
                            QueueLimit = 0,
                        });
                    }
                }
                """),
            new($"{Api}ServiceCollectionExtensions.cs",
                "        services.AddHealthChecks();",
                """
                        services.AddHealthChecks();
                        services.AddLinkRateLimiting(configuration);
                """),
            new($"{Api}Program.cs",
                "app.UseStatusCodePages();",
                """
                app.UseStatusCodePages();
                app.UseRateLimiter();
                """),
            new($"{Api}Api/LinkEndpoints.cs",
                """links.MapPost("/", CreateLink);""",
                """links.MapPost("/", CreateLink).RequireRateLimiting(RateLimitPolicies.CreateLink);"""),
            new($"{Api}appsettings.json",
                """  "Shortener": {""",
                """
                  "RateLimiting": {
                    "CreatePermitLimit": {{CreatePermitLimit}},
                    "RedirectPermitLimit": 600,
                    "WindowSeconds": 60
                  },
                  "Shortener": {
                """),
            new($"{Tests}RateLimitingTests.cs", null,
                """
                using System.Net;
                using System.Net.Http.Json;
                using Microsoft.AspNetCore.Hosting;
                using Microsoft.AspNetCore.Mvc.Testing;
                using Microsoft.Extensions.Configuration;
                using UrlShortener.Api.Api;

                namespace UrlShortener.Tests;

                /// <summary>Separate host with tiny limits so limiter state cannot leak into other test classes.</summary>
                public sealed class LowLimitFactory : WebApplicationFactory<Program>
                {
                    protected override void ConfigureWebHost(IWebHostBuilder builder)
                    {
                        builder.UseEnvironment("Testing");
                        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["RateLimiting:CreatePermitLimit"] = "3",
                            ["RateLimiting:RedirectPermitLimit"] = "3",
                        }));
                    }
                }

                public class RateLimitingTests(LowLimitFactory factory) : IClassFixture<LowLimitFactory>
                {
                    [Fact]
                    public async Task Create_beyond_limit_returns_429_with_retry_after()
                    {
                        var client = factory.CreateClient();
                        for (var i = 0; i < 3; i++)
                        {
                            var ok = await client.PostAsJsonAsync("/api/links", new CreateLinkRequest($"https://example.com/{i}", null));
                            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
                        }

                        var limited = await client.PostAsJsonAsync("/api/links", new CreateLinkRequest("https://example.com/x", null));

                        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
                        Assert.True(limited.Headers.Contains("Retry-After"));
                    }
                }
                """),
        ]),

        new("ab-rate-limit-redirects", "Per-client rate limiting for redirects", ["ab-rate-limit-creation"], RiskLevel.Medium,
        [
            new($"{Api}Api/LinkEndpoints.cs",
                """app.MapGet("/{code}", RedirectToTarget);""",
                """app.MapGet("/{code}", RedirectToTarget).RequireRateLimiting(RateLimitPolicies.Redirect);"""),
            new($"{Tests}RedirectRateLimitingTests.cs", null,
                """
                using System.Net;
                using System.Net.Http.Json;
                using Microsoft.AspNetCore.Mvc.Testing;
                using UrlShortener.Api.Api;

                namespace UrlShortener.Tests;

                public class RedirectRateLimitingTests(LowLimitFactory factory) : IClassFixture<LowLimitFactory>
                {
                    [Fact]
                    public async Task Redirects_beyond_limit_return_429()
                    {
                        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
                        await client.PostAsJsonAsync("/api/links", new CreateLinkRequest("https://example.com/hot", "hot-link"));
                        for (var i = 0; i < 3; i++)
                            Assert.InRange((int)(await client.GetAsync("/hot-link")).StatusCode, 300, 399);

                        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/hot-link")).StatusCode);
                    }
                }
                """),
        ]),
    ];
}
