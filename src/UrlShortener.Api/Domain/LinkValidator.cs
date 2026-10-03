using System.Text.RegularExpressions;

namespace UrlShortener.Api.Domain;

public static partial class LinkValidator
{
    /// <summary>Aliases that would shadow (or be confused with) service routes.</summary>
    public static readonly IReadOnlySet<string> ReservedAliases =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "api", "health", "admin", "static", "favicon.ico" };

    [GeneratedRegex("^[A-Za-z0-9_-]{3,32}$")]
    private static partial Regex AliasPattern();

    public static string? ValidateTargetUrl(string? raw, int maxLength, out Uri? target)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(raw))
            return "A destination URL is required.";
        if (raw.Length > maxLength)
            return $"The destination URL must be at most {maxLength} characters.";
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
            return "The destination URL must be an absolute URL.";
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return "Only http and https destination URLs are allowed.";
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return "Destination URLs must not embed credentials.";

        target = uri;
        return null;
    }

    public static string? ValidateAlias(string alias)
    {
        if (!AliasPattern().IsMatch(alias))
            return "Custom aliases must be 3-32 characters of letters, digits, '-' or '_'.";
        if (ReservedAliases.Contains(alias))
            return $"'{alias}' is a reserved word and cannot be used as an alias.";
        return null;
    }
}
