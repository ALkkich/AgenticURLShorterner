using Agentic.Agents.Knowledge;
using Agentic.Agents.Models;
using Agentic.Orchestrator.Agents;
using Agentic.Orchestrator.Context;

namespace Agentic.Agents.Agents;

/// <summary>
/// Produces the design: components, the API contract (as OpenAPI), architecture decision records,
/// a risk register and requirement→component traceability. Reviewer feedback from a rejected
/// checkpoint is folded into the next revision and recorded.
/// </summary>
public sealed class SolutionArchitectAgent : IAgent
{
    public string Name => "solution-architect";

    public Task<AgentResult> ExecuteAsync(AgentContext ctx)
    {
        var spec = ctx.Require<RequirementSpec>(ArtifactKeys.RequirementSpec);
        var impact = spec.IsGreenfield ? null : ctx.Read<ImpactReport>(ArtifactKeys.ImpactReport);
        var caps = spec.Capabilities.Select(CapabilityCatalog.Get).ToList();

        var components = caps.Select(c => c.Component).DistinctBy(c => c.Name).ToList();
        var endpoints = MergeEndpoints(spec.IsGreenfield, caps);
        var adrs = caps.Where(c => c.Adr is not null).Select(c => c.Adr!).ToList();
        var risks = caps.SelectMany(c => c.Risks).DistinctBy(r => r.Id).ToList();
        risks.Add(new RiskItem("R-MEM", "In-memory store: data lost on restart, single instance only", "High", "High",
            "Accepted for prototype; ILinkRepository port isolates a durable adapter (e.g. PostgreSQL unique index on code)."));
        if (impact is not null && impact.Risk >= Orchestrator.Model.RiskLevel.Medium)
            risks.Add(new RiskItem("R-BLAST", $"Change touches {impact.BlastRadius} production files", "Medium", "Medium",
                "Ordered tasks per file, full regression suite, human design approval."));

        var traceability = spec.Functional.ToDictionary(
            fr => fr.Id,
            fr => (IReadOnlyList<string>)[CapabilityCatalog.Get(fr.CapabilityId).Component.Name]);
        var reviewNotes = ctx.Feedback.Select(f => $"Addressed review feedback: {f}").ToList();

        var design = new DesignSpec(
            spec.IsGreenfield
                ? "Layered minimal-API service: HTTP endpoints → LinkService → domain + ports (repository, analytics), in-memory adapters."
                : $"Incremental change to the existing layered service; impacted: {string.Join(", ", impact?.ImpactedFiles.Select(f => Path.GetFileName(f.Path)) ?? [])}.",
            components, endpoints, adrs, risks, traceability, reviewNotes);

        var result = AgentResult.Success($"{components.Count} components, {endpoints.Count} endpoints, {adrs.Count} ADRs, {risks.Count} risks.")
            .WithJson(ArtifactKeys.Design, design)
            .WithText(ArtifactKeys.ApiContract, OpenApiWriter.Write(spec.Title, endpoints), "application/json");
        foreach (var adr in adrs)
            result.WithDecision(DecisionCategory.Design, $"{adr.Id}: {adr.Title}", adr.Rationale, [.. adr.Alternatives]);
        foreach (var note in reviewNotes)
            result.WithDecision(DecisionCategory.Design, "Design revised after review", note);
        return Task.FromResult(result);
    }

    /// <summary>Brownfield contracts start from the existing API; bug fixes replace responses, enhancements extend them.</summary>
    private static List<Endpoint> MergeEndpoints(bool isGreenfield, IReadOnlyList<CapabilityDefinition> caps)
    {
        var baseline = isGreenfield ? caps : CapabilityCatalog.All.Where(c => c.Kind == ChangeType.Greenfield).ToList();
        var merged = baseline.SelectMany(c => c.Endpoints).GroupBy(e => (e.Method, e.Path)).Select(g => g.First()).ToList();
        if (isGreenfield)
            return merged;

        foreach (var cap in caps)
            foreach (var endpoint in cap.Endpoints)
            {
                var index = merged.FindIndex(e => e.Method == endpoint.Method && e.Path == endpoint.Path);
                if (index < 0)
                {
                    merged.Add(endpoint);
                    continue;
                }
                var current = merged[index];
                var responses = cap.Kind == ChangeType.BugFix
                    ? endpoint.Responses
                    : current.Responses.Concat(endpoint.Responses).GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.Last().Value);
                var change = string.Join("; ", new[] { current.Change, endpoint.Change }.Where(c => c is not null));
                merged[index] = current with { Request = endpoint.Request ?? current.Request, Responses = responses, Change = change };
            }
        return merged;
    }
}

internal static class OpenApiWriter
{
    public static string Write(string title, IReadOnlyList<Endpoint> endpoints)
    {
        var paths = endpoints.GroupBy(e => e.Path).ToDictionary(
            g => g.Key,
            g => g.ToDictionary(
                e => e.Method.ToLowerInvariant(),
                e => (object)new Dictionary<string, object?>
                {
                    ["summary"] = e.Summary,
                    ["x-change"] = e.Change,
                    ["requestBody"] = e.Request is null ? null : new { content = new Dictionary<string, object> { ["application/json"] = new { schema = new { description = e.Request } } } },
                    ["responses"] = e.Responses.ToDictionary(r => r.Key, r => new { description = r.Value }),
                }.Where(kv => kv.Value is not null).ToDictionary()));
        return Json.Serialize(new Dictionary<string, object>
        {
            ["openapi"] = "3.0.3",
            ["info"] = new { title, version = "generated" },
            ["paths"] = paths,
        });
    }
}
