using System.Text.RegularExpressions;
using System.Xml.Linq;
using Agentic.Agents.Models;
using Agentic.Orchestrator.Agents;
using Agentic.Orchestrator.Context;
using Agentic.Orchestrator.Tools;

namespace Agentic.Agents.Agents;

internal static class DotNet
{
    public const string TestProject = "tests/UrlShortener.Tests/UrlShortener.Tests.csproj";

    public static Task<ToolResult> RunAsync(AgentContext ctx, params string[] args) =>
        ctx.Tools.RunAsync(new ToolInvocation("dotnet", args, ctx.Workspace.Root, TimeSpan.FromMinutes(8)), ctx.CancellationToken);

    public static bool IsInfrastructureFailure(string output) =>
        output.Contains("NU1301", StringComparison.Ordinal)
        || output.Contains("Unable to load the service index", StringComparison.Ordinal)
        || output.Contains("being used by another process", StringComparison.Ordinal);
}

/// <summary>Compiles the workspace. Compiler errors are permanent (→ remediation); infrastructure errors are transient (→ retry).</summary>
public sealed partial class BuildRunnerAgent : IAgent
{
    public string Name => "build-runner";

    [GeneratedRegex(@"error [A-Z]+\d+:[^\[\r\n]+")]
    private static partial Regex CompilerError();

    public async Task<AgentResult> ExecuteAsync(AgentContext ctx)
    {
        ctx.ReadText(WellKnownArtifacts.WorkspaceState); // lineage: rebuild whenever code changes
        if (!ctx.Workspace.Exists(DotNet.TestProject))
            return AgentResult.Permanent($"Nothing to build: {DotNet.TestProject} is missing.");

        var result = await DotNet.RunAsync(ctx, "build", DotNet.TestProject, "-nologo", "-v", "q", "-clp:ErrorsOnly");
        var errors = CompilerError().Matches(result.Output).Select(m => m.Value.Trim()).Distinct().Take(10).ToList();
        var report = new { succeeded = result.Succeeded, durationMs = Math.Round(result.Duration.TotalMilliseconds), errors };

        if (result.Succeeded)
            return AgentResult.Success($"Build succeeded in {result.Duration.TotalSeconds:F1}s.").WithJson(ArtifactKeys.BuildReport, report);
        if (result.TimedOut || DotNet.IsInfrastructureFailure(result.Output))
            return AgentResult.Transient($"Build infrastructure failure: {result.Tail(5)}");
        return AgentResult.Permanent($"Build failed with {errors.Count} compiler error(s): {string.Join(" | ", errors.DefaultIfEmpty(result.Tail(10)))}");
    }
}

/// <summary>
/// Runs the full suite, parses TRX results and builds the acceptance-criteria → test traceability
/// matrix from the tests that actually executed and passed.
/// </summary>
public sealed class TestRunnerAgent : IAgent
{
    private static readonly XNamespace Trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public string Name => "test-runner";

    public async Task<AgentResult> ExecuteAsync(AgentContext ctx)
    {
        ctx.ReadText(WellKnownArtifacts.WorkspaceState);
        var spec = ctx.Require<RequirementSpec>(ArtifactKeys.RequirementSpec);
        var resultsDir = Path.Combine(ctx.Workspace.Root, "TestResults", Guid.NewGuid().ToString("N"));

        var run = await DotNet.RunAsync(ctx, "test", DotNet.TestProject, "--no-build", "-nologo", "--logger", "trx", "--results-directory", resultsDir);
        var trx = Directory.Exists(resultsDir) ? Directory.GetFiles(resultsDir, "*.trx").FirstOrDefault() : null;
        if (trx is null)
            return run.TimedOut || DotNet.IsInfrastructureFailure(run.Output)
                ? AgentResult.Transient($"Test run produced no results: {run.Tail(5)}")
                : AgentResult.Permanent($"Test run produced no results: {run.Tail(15)}");

        var results = XDocument.Load(trx).Descendants(Trx + "UnitTestResult")
            .Select(r => new TestCaseResult(
                (string?)r.Attribute("testName") ?? "?",
                (string?)r.Attribute("outcome") ?? "Unknown",
                TimeSpan.TryParse((string?)r.Attribute("duration"), out var d) ? Math.Round(d.TotalMilliseconds, 1) : 0,
                r.Descendants(Trx + "Message").FirstOrDefault()?.Value))
            .ToList();
        var passed = results.Where(r => r.Outcome == "Passed").ToList();
        var failed = results.Where(r => r.Outcome == "Failed").ToList();
        var report = new TestReport(results.Count, passed.Count, failed.Count, results.Count - passed.Count - failed.Count,
            Math.Round(run.Duration.TotalMilliseconds), failed, passed.Select(p => p.Name).ToList());

        var passedMethods = passed.Select(p => MethodName(p.Name)).ToHashSet(StringComparer.Ordinal);
        var trace = spec.AcceptanceCriteria
            .Select(ac => new TraceLink(ac.Id, ac.Criterion, ac.VerifiedBy, ac.VerifiedBy.Count > 0 && ac.VerifiedBy.All(passedMethods.Contains)))
            .ToList();

        var result = failed.Count == 0
            ? AgentResult.Success($"{passed.Count}/{results.Count} tests passed; {trace.Count(t => t.Verified)}/{trace.Count} acceptance criteria verified.")
            : AgentResult.Permanent($"{failed.Count} test(s) failed: {string.Join(" | ", failed.Take(5).Select(f => $"{MethodName(f.Name)}: {Shorten(f.Message)}"))}");
        return result.WithJson(ArtifactKeys.TestReport, report).WithJson(ArtifactKeys.Traceability, trace);
    }

    internal static string MethodName(string testName)
    {
        var name = testName.Split('(')[0];
        return name[(name.LastIndexOf('.') + 1)..];
    }

    private static string Shorten(string? value) =>
        value is null ? "" : (value.Length > 160 ? value[..160] + "…" : value).ReplaceLineEndings(" ");
}
