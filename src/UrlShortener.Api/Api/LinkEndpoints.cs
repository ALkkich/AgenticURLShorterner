using Microsoft.Extensions.Options;
using UrlShortener.Api.Services;

namespace UrlShortener.Api.Api;

public static class LinkEndpoints
{
    public static IEndpointRouteBuilder MapUrlShortenerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health/live");
        app.MapHealthChecks("/health/ready");

        var links = app.MapGroup("/api/links").WithTags("Links");
        links.MapPost("/", CreateLink);
        links.MapGet("/{code}", GetLink);
        links.MapGet("/{code}/stats", GetStats);
        links.MapDelete("/{code}", DeleteLink);

        app.MapGet("/{code}", RedirectToTarget);
        return app;
    }

    private static IResult CreateLink(CreateLinkRequest request, LinkService service, HttpContext http, IOptions<ShortenerOptions> options)
    {
        var result = service.Create(new CreateLinkCommand(request.Url, request.CustomAlias));
        return result.Status switch
        {
            CreateLinkStatus.Created => TypedResults.Created(
                $"/api/links/{result.Link!.Code}",
                LinkResponse.From(result.Link, BaseUrl(http, options.Value))),
            CreateLinkStatus.Invalid => TypedResults.ValidationProblem(result.Errors.ToDictionary()),
            CreateLinkStatus.AliasTaken => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict, title: "Alias already in use",
                detail: result.Errors.Values.First()[0]),
            _ => TypedResults.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable, title: "Short code allocation failed",
                detail: result.Errors.Values.First()[0]),
        };
    }

    private static IResult GetLink(string code, LinkService service, HttpContext http, IOptions<ShortenerOptions> options) =>
        service.Get(code) is { } link
            ? TypedResults.Ok(LinkResponse.From(link, BaseUrl(http, options.Value)))
            : TypedResults.NotFound();

    private static IResult GetStats(string code, LinkService service) =>
        service.GetStats(code) is { } stats ? TypedResults.Ok(stats) : TypedResults.NotFound();

    private static IResult DeleteLink(string code, LinkService service) =>
        service.Delete(code) ? TypedResults.NoContent() : TypedResults.NotFound();

    private static IResult RedirectToTarget(string code, LinkService service, VisitorHasher hasher, HttpContext http)
    {
        var link = service.Resolve(code);
        if (link is null)
            return TypedResults.NotFound();

        service.RecordClick(link, hasher.Hash(http.Connection.RemoteIpAddress), ReferrerHost(http));
        return TypedResults.Redirect(link.TargetUrl.AbsoluteUri, permanent: true);
    }

    private static string? ReferrerHost(HttpContext http) =>
        Uri.TryCreate(http.Request.Headers.Referer.ToString(), UriKind.Absolute, out var referrer) ? referrer.Host : null;

    private static string BaseUrl(HttpContext http, ShortenerOptions options) =>
        options.PublicBaseUrl ?? $"{http.Request.Scheme}://{http.Request.Host}";
}
