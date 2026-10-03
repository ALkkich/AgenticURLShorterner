using System.Text.RegularExpressions;
using Agentic.Agents.Knowledge;
using Agentic.Agents.Models;
using Agentic.Orchestrator.Agents;
using Agentic.Orchestrator.Context;
using Agentic.Orchestrator.Model;

namespace Agentic.Agents.Agents;

/// <summary>
/// Brownfield reasoning: maps requested changes onto the existing code — impacted files and symbols,
/// current and changed endpoints, data flows, affected tests, blast radius, and (for defects) the
/// root cause located in source.
/// </summary>
public sealed partial class CodebaseAnalystAgent : IAgent
{
    public string Name => "codebase-analyst";

    [GeneratedRegex(@"(\w+)\s*=\s*\w+\.MapGroup\(""([^""]+)""\)")]
    private static partial Regex GroupPattern();

    [GeneratedRegex(@"(\w+)\.Map(Get|Post|Put|Delete|Patch)\(\s*""([^""]*)""")]
    private static partial Regex RoutePattern();

    public Task<AgentResult> ExecuteAsync(AgentContext ctx)
    {
        var spec = ctx.Require<RequirementSpec>(ArtifactKeys.RequirementSpec);
        var sources = ctx.Workspace.ListFiles()
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => f, ctx.Workspace.Read);
        if (sources.Count == 0)
            return Task.FromResult(AgentResult.Permanent("Brownfield analysis found no source files in the workspace baseline."));

        var endpoints = DiscoverEndpoints(sources);
        var caps = spec.Capabilities.Select(CapabilityCatalog.Get).ToList();
        var impacted = new Dictionary<string, (HashSet<string> Symbols, HashSet<string> Reasons)>(StringComparer.Ordinal);
        foreach (var cap in caps)
            foreach (var symbol in cap.Touchpoints)
                foreach (var (path, content) in sources.Where(s => Regex.IsMatch(s.Value, $@"\b{Regex.Escape(symbol)}\b")))
                {
                    if (!impacted.TryGetValue(path, out var entry))
                        impacted[path] = entry = ([], []);
                    entry.Symbols.Add(symbol);
                    entry.Reasons.Add(cap.Id);
                }

        var rootCauses = new List<RootCause>();
        foreach (var cap in caps.Where(c => c.RootCause is not null))
            foreach (var (path, content) in sources.Where(s => !IsTest(s.Key)))
            {
                var lines = content.ReplaceLineEndings("\n").Split('\n');
                for (var i = 0; i < lines.Length; i++)
                    if (Regex.IsMatch(lines[i], cap.RootCause!.Pattern))
                        rootCauses.Add(new RootCause(cap.Id, $"{path}:{i + 1}", cap.RootCause.Explanation));
            }

        var files = impacted.OrderBy(i => i.Key, StringComparer.Ordinal)
            .Select(i => new ImpactedFile(i.Key, $"touches {string.Join(", ", i.Value.Reasons)}", i.Value.Symbols.Order().ToList()))
            .ToList();
        var blastRadius = files.Count(f => !IsTest(f.Path));
        var risk = blastRadius > 6 ? RiskLevel.High : blastRadius > 2 ? RiskLevel.Medium : RiskLevel.Low;
        var report = new ImpactReport(
            files,
            endpoints,
            caps.SelectMany(c => c.Endpoints.Where(e => e.Change is not null).Select(e => $"{e.Method} {e.Path} ({e.Change})")).Distinct().ToList(),
            caps.SelectMany(c => c.DataFlows).ToList(),
            files.Where(f => IsTest(f.Path)).Select(f => f.Path).ToList(),
            rootCauses,
            blastRadius,
            risk);

        var result = AgentResult.Success($"{files.Count} impacted files (blast radius {blastRadius}, risk {risk}); {endpoints.Count} existing endpoints; {rootCauses.Count} root causes located.")
            .WithJson(ArtifactKeys.ImpactReport, report);
        foreach (var cause in rootCauses)
            result.WithDecision(DecisionCategory.Design, $"Root cause for {cause.Capability} at {cause.Location}", cause.Explanation);
        var bugFixesWithoutCause = caps.Where(c => c.Kind == ChangeType.BugFix && rootCauses.All(r => r.Capability != c.Id)).ToList();
        if (bugFixesWithoutCause.Count > 0)
            return Task.FromResult(AgentResult.Permanent($"Could not locate a root cause for: {string.Join(", ", bugFixesWithoutCause.Select(c => c.Id))}. A fix without a diagnosed cause is not allowed."));
        return Task.FromResult(result);
    }

    private static List<string> DiscoverEndpoints(IReadOnlyDictionary<string, string> sources)
    {
        var endpoints = new List<string>();
        foreach (var content in sources.Where(s => !IsTest(s.Key)).Select(s => s.Value))
        {
            var groups = GroupPattern().Matches(content).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
            foreach (Match route in RoutePattern().Matches(content))
            {
                var prefix = groups.GetValueOrDefault(route.Groups[1].Value, "");
                var path = (prefix + route.Groups[3].Value).TrimEnd('/');
                endpoints.Add($"{route.Groups[2].Value.ToUpperInvariant()} {(path.Length == 0 ? "/" : path)}");
            }
        }
        return endpoints.Distinct().ToList();
    }

    private static bool IsTest(string path) => path.StartsWith("tests/", StringComparison.OrdinalIgnoreCase);
}
