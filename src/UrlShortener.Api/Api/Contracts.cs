using UrlShortener.Api.Domain;

namespace UrlShortener.Api.Api;

public sealed record CreateLinkRequest(string? Url, string? CustomAlias);

public sealed record LinkResponse(string Code, string ShortUrl, string TargetUrl, DateTimeOffset CreatedAt, bool IsCustomAlias)
{
    public static LinkResponse From(ShortLink link, string baseUrl) =>
        new(link.Code, $"{baseUrl.TrimEnd('/')}/{link.Code}", link.TargetUrl.AbsoluteUri, link.CreatedAt, link.IsCustomAlias);
}
