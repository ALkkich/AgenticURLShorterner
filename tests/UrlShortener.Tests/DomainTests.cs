using UrlShortener.Api.Domain;

namespace UrlShortener.Tests;

public class ShortCodeGeneratorTests
{
    [Fact]
    public void Generates_codes_of_requested_length_from_base62_alphabet()
    {
        var generator = new RandomShortCodeGenerator();

        var code = generator.Generate(9);

        Assert.Equal(9, code.Length);
        Assert.All(code, c => Assert.Contains(c, RandomShortCodeGenerator.Alphabet));
    }

    [Fact]
    public void Generated_codes_are_not_trivially_repeated()
    {
        var generator = new RandomShortCodeGenerator();

        var codes = Enumerable.Range(0, 1000).Select(_ => generator.Generate(7)).ToHashSet();

        Assert.Equal(1000, codes.Count);
    }
}

public class LinkValidatorTests
{
    [Theory]
    [InlineData("https://example.com/path?q=1")]
    [InlineData("http://example.com")]
    public void Accepts_absolute_http_urls(string url)
    {
        Assert.Null(LinkValidator.ValidateTargetUrl(url, 2048, out var target));
        Assert.NotNull(target);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/file")]
    [InlineData("https://user:secret@example.com")]
    public void Rejects_missing_relative_non_http_or_credentialed_urls(string? url)
    {
        Assert.NotNull(LinkValidator.ValidateTargetUrl(url, 2048, out _));
    }

    [Fact]
    public void Rejects_urls_over_the_length_limit()
    {
        var url = "https://example.com/" + new string('a', 100);

        Assert.NotNull(LinkValidator.ValidateTargetUrl(url, 50, out _));
    }

    [Theory]
    [InlineData("my-link")]
    [InlineData("Promo_2026")]
    public void Accepts_well_formed_aliases(string alias) => Assert.Null(LinkValidator.ValidateAlias(alias));

    [Theory]
    [InlineData("ab")]
    [InlineData("has space")]
    [InlineData("emoji😀")]
    [InlineData("API")]
    [InlineData("health")]
    public void Rejects_malformed_or_reserved_aliases(string alias) => Assert.NotNull(LinkValidator.ValidateAlias(alias));
}
