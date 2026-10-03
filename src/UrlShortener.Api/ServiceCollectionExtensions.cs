using Microsoft.Extensions.DependencyInjection.Extensions;
using UrlShortener.Api.Analytics;
using UrlShortener.Api.Domain;
using UrlShortener.Api.Services;
using UrlShortener.Api.Storage;

namespace UrlShortener.Api;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddUrlShortener(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ShortenerOptions>()
            .Bind(configuration.GetSection(ShortenerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddProblemDetails();
        services.AddHealthChecks();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IShortCodeGenerator, RandomShortCodeGenerator>();
        services.TryAddSingleton<ILinkRepository, InMemoryLinkRepository>();
        services.TryAddSingleton<IClickAnalytics, InMemoryClickAnalytics>();
        services.TryAddSingleton<VisitorHasher>();
        services.TryAddSingleton<LinkService>();

        return services;
    }
}
