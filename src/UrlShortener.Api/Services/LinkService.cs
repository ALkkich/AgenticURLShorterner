using Microsoft.Extensions.Options;
using UrlShortener.Api.Analytics;
using UrlShortener.Api.Domain;
using UrlShortener.Api.Storage;

namespace UrlShortener.Api.Services;

public enum CreateLinkStatus { Created, Invalid, AliasTaken, CodeSpaceExhausted }

public sealed record CreateLinkCommand(string? Url, string? CustomAlias);

public sealed record CreateLinkResult(CreateLinkStatus Status, ShortLink? Link, IReadOnlyDictionary<string, string[]> Errors)
{
    public static CreateLinkResult Ok(ShortLink link) => new(CreateLinkStatus.Created, link, Empty);
    public static CreateLinkResult Fail(CreateLinkStatus status, string field, string error) =>
        new(status, null, new Dictionary<string, string[]> { [field] = [error] });

    private static readonly IReadOnlyDictionary<string, string[]> Empty = new Dictionary<string, string[]>();
}

public sealed class LinkService(
    ILinkRepository repository,
    IShortCodeGenerator codeGenerator,
    IClickAnalytics analytics,
    TimeProvider time,
    IOptions<ShortenerOptions> options,
    ILogger<LinkService> logger)
{
    private readonly ShortenerOptions _options = options.Value;

    public CreateLinkResult Create(CreateLinkCommand command)
    {
        var urlError = LinkValidator.ValidateTargetUrl(command.Url, _options.MaxUrlLength, out var target);
        if (urlError is not null)
            return CreateLinkResult.Fail(CreateLinkStatus.Invalid, "url", urlError);

        var now = time.GetUtcNow();

        if (!string.IsNullOrEmpty(command.CustomAlias))
        {
            var aliasError = LinkValidator.ValidateAlias(command.CustomAlias);
            if (aliasError is not null)
                return CreateLinkResult.Fail(CreateLinkStatus.Invalid, "customAlias", aliasError);

            var aliased = new ShortLink(command.CustomAlias, target!, now, IsCustomAlias: true);
            return repository.TryAdd(aliased)
                ? Created(aliased)
                : CreateLinkResult.Fail(CreateLinkStatus.AliasTaken, "customAlias", $"The alias '{command.CustomAlias}' is already in use.");
        }

        for (var attempt = 1; attempt <= _options.MaxGenerationAttempts; attempt++)
        {
            var link = new ShortLink(codeGenerator.Generate(_options.CodeLength), target!, now, IsCustomAlias: false);
            if (repository.TryAdd(link))
                return Created(link);
            logger.LogWarning("Short code collision on attempt {Attempt}", attempt);
        }

        logger.LogError("Exhausted {Attempts} attempts generating a unique short code", _options.MaxGenerationAttempts);
        return CreateLinkResult.Fail(CreateLinkStatus.CodeSpaceExhausted, "code", "Could not allocate a unique short code. Retry later.");
    }

    /// <summary>Management lookup (metadata, stats). The redirect path uses <see cref="Resolve"/>, which can apply extra rules.</summary>
    public ShortLink? Get(string code) => repository.Find(code);

    public ShortLink? Resolve(string code) => repository.Find(code);

    public void RecordClick(ShortLink link, string? visitorHash, string? referrerHost) =>
        analytics.Record(new ClickEvent(link.Code, time.GetUtcNow(), visitorHash, referrerHost));

    public LinkStats? GetStats(string code) =>
        repository.Find(code) is null ? null : analytics.GetStats(code);

    public bool Delete(string code)
    {
        if (!repository.Remove(code))
            return false;
        analytics.Remove(code);
        return true;
    }

    private CreateLinkResult Created(ShortLink link)
    {
        logger.LogInformation("Created short link {Code} (custom alias: {IsCustom})", link.Code, link.IsCustomAlias);
        return CreateLinkResult.Ok(link);
    }
}
