namespace UrlShortener.Api.Domain;

public sealed record ShortLink(string Code, Uri TargetUrl, DateTimeOffset CreatedAt, bool IsCustomAlias);
