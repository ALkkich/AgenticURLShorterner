using Agentic.Orchestrator.Model;

namespace Agentic.Agents.Knowledge;

/// <summary>
/// One file operation. With <see cref="Find"/> null the file is written whole (create); otherwise the
/// unique anchor <see cref="Find"/> is replaced by <see cref="Content"/>. An anchor that is missing or
/// ambiguous means the code drifted from what impact analysis saw — the implementer refuses to guess.
/// </summary>
public sealed record FileEdit(string Path, string? Find, string Content);

/// <param name="BlueprintFiles">Reviewed component sources copied from the reference tree (greenfield).</param>
public sealed record ChangeRecipe(
    string Id,
    string Title,
    IReadOnlyList<string> DependsOn,
    RiskLevel Risk,
    IReadOnlyList<FileEdit> Edits,
    IReadOnlyList<string>? BlueprintFiles = null)
{
    public IReadOnlyList<string> Files =>
        Edits.Select(e => e.Path).Concat(BlueprintFiles ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

public static partial class RecipeBook
{
    private const string Api = "src/UrlShortener.Api/";
    private const string Tests = "tests/UrlShortener.Tests/";

    public static readonly IReadOnlyList<ChangeRecipe> All = [.. Greenfield(), .. Brownfield(), .. AbuseControls()];

    public static ChangeRecipe Get(string id) =>
        All.FirstOrDefault(r => r.Id == id) ?? throw new KeyNotFoundException($"Unknown recipe '{id}'.");

    /// <summary>Greenfield v1 is composed from reviewed components, layer by layer.</summary>
    private static IEnumerable<ChangeRecipe> Greenfield() =>
    [
        new("gf-scaffold", "Scaffold web project, configuration and options", [], RiskLevel.Low, [],
            [$"{Api}UrlShortener.Api.csproj", $"{Api}appsettings.json", $"{Api}appsettings.Development.json", $"{Api}Properties/launchSettings.json", $"{Api}ShortenerOptions.cs"]),
        new("gf-domain", "Domain model, code generator and validation", ["gf-scaffold"], RiskLevel.Low, [],
            [$"{Api}Domain/ShortLink.cs", $"{Api}Domain/ShortCodeGenerator.cs", $"{Api}Domain/LinkValidator.cs"]),
        new("gf-storage", "Repository port and in-memory adapter", ["gf-domain"], RiskLevel.Low, [],
            [$"{Api}Storage/ILinkRepository.cs", $"{Api}Storage/InMemoryLinkRepository.cs"]),
        new("gf-analytics", "Click analytics aggregation", ["gf-scaffold"], RiskLevel.Low, [],
            [$"{Api}Analytics/ClickAnalytics.cs", $"{Api}Analytics/InMemoryClickAnalytics.cs"]),
        new("gf-service", "Link service and visitor pseudonymisation", ["gf-storage", "gf-analytics"], RiskLevel.Medium, [],
            [$"{Api}Services/LinkService.cs", $"{Api}Services/VisitorHasher.cs"]),
        new("gf-api", "HTTP endpoints, contracts and hosting", ["gf-service"], RiskLevel.Medium, [],
            [$"{Api}Api/Contracts.cs", $"{Api}Api/LinkEndpoints.cs", $"{Api}Program.cs", $"{Api}ServiceCollectionExtensions.cs"]),
        new("gf-tests", "Unit and integration test suite", ["gf-api"], RiskLevel.Low, [],
            [$"{Tests}UrlShortener.Tests.csproj", $"{Tests}TestDoubles.cs", $"{Tests}DomainTests.cs", $"{Tests}LinkServiceTests.cs", $"{Tests}ApiIntegrationTests.cs"]),
    ];

    /// <summary>Recipes always added to a plan so that every change ships with tests.</summary>
    public static readonly IReadOnlyList<string> GreenfieldAlways = ["gf-tests"];
}
