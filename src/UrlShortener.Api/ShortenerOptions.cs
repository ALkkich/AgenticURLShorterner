using System.ComponentModel.DataAnnotations;

namespace UrlShortener.Api;

public sealed class ShortenerOptions
{
    public const string SectionName = "Shortener";

    /// <summary>Length of generated (non-custom) short codes. 7 base62 chars ≈ 3.5e12 combinations.</summary>
    [Range(5, 16)]
    public int CodeLength { get; set; } = 7;

    /// <summary>Bounded retries when a generated code collides with an existing one.</summary>
    [Range(1, 20)]
    public int MaxGenerationAttempts { get; set; } = 5;

    [Range(16, 8192)]
    public int MaxUrlLength { get; set; } = 2048;

    /// <summary>Public base URL used to build short links. Falls back to the request host when unset.</summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Salt for one-way hashing of visitor identifiers. Raw IPs are never stored.</summary>
    [Required, MinLength(8)]
    public string VisitorHashSalt { get; set; } = string.Empty;
}
