using System.Text;
using Agentic.Agents.Models;
using Agentic.Agents.Workflow;
using Agentic.Orchestrator.Context;
using Agentic.Orchestrator.Engine;
using Agentic.Orchestrator.Model;
using Agentic.Orchestrator.Observability;

namespace Agentic.Agents.Reporting;

/// <summary>
/// Persists the evidence of a run (artifacts with lineage, decisions, metrics, graph) and renders the
/// engineering summary that a human reviewer signs off.
/// </summary>
public static class RunExporter
{
    public static string Export(ScenarioDefinition scenario, RunResult result, string runDir, string repoRoot)
    {
        var artifactsDir = Path.Combine(runDir, "artifacts");
        Directory.CreateDirectory(artifactsDir);
        foreach (var artifact in result.Context.Artifacts.AllLatest())
        {
            var ext = artifact.ContentType switch { "application/json" => "json", "text/markdown" => "md", _ => "txt" };
            File.WriteAllText(Path.Combine(artifactsDir, $"{artifact.Key}.{ext}"), artifact.Content);
        }
        File.WriteAllText(Path.Combine(runDir, "lineage.json"), Json.Serialize(
            result.Context.Artifacts.AllLatest().SelectMany(a => result.Context.Artifacts.History(a.Key))
                .Select(a => new { a.Key, a.Version, Hash = a.Hash[..12], a.ProducedBy, a.CreatedAt, DerivedFrom = a.DerivedFrom.Select(d => $"{d.Key}@v{d.Version}") })));
        File.WriteAllText(Path.Combine(runDir, "decisions.json"), Json.Serialize(result.Context.Decisions.All));
        File.WriteAllText(Path.Combine(runDir, "metrics.json"), Json.Serialize(result.Metrics));
        File.WriteAllText(Path.Combine(runDir, "graph.mmd"), result.Graph.ToMermaid(id => result.Nodes.TryGetValue(id, out var s) ? s.Status : null));

        var historyPath = Path.Combine(repoRoot, ".runs", "metrics-history.jsonl");
        File.AppendAllText(historyPath, Json.Serialize(result.Metrics).ReplaceLineEndings(" ") + Environment.NewLine);

        var reportPath = Path.Combine(runDir, "ENGINEERING_SUMMARY.md");
        File.WriteAllText(reportPath, EngineeringSummary.Render(scenario, result));
        return reportPath;
    }
}

public static class EngineeringSummary
{
    public static string Render(ScenarioDefinition scenario, RunResult result)
    {
        var store = result.Context.Artifacts;
        var spec = store.LatestJson<RequirementSpec>(ArtifactKeys.RequirementSpec);
        var impact = store.LatestJson<ImpactReport>(ArtifactKeys.ImpactReport);
        var design = store.LatestJson<DesignSpec>(ArtifactKeys.Design);
        var plan = store.LatestJson<WorkPlan>(ArtifactKeys.WorkPlan);
        var tests = store.LatestJson<TestReport>(ArtifactKeys.TestReport);
        var trace = store.LatestJson<List<TraceLink>>(ArtifactKeys.Traceability);
        var security = store.LatestJson<SecurityReport>(ArtifactKeys.SecurityReport);
        var release = store.LatestJson<ReleaseReadiness>(ArtifactKeys.ReleaseReadiness);
        var m = result.Metrics;
        var events = result.Context.Audit.Events;
        var sb = new StringBuilder();

        sb.AppendLine($"# Engineering summary — {scenario.Title}");
        sb.AppendLine();
        sb.AppendLine($"| Run | Scenario | Status | End-to-end | Graph version | Audit chain |");
        sb.AppendLine("|---|---|---|---|---|---|");
        sb.AppendLine($"| `{m.RunId}` | {scenario.Kind} | **{result.Status}** | {m.EndToEndLatencyMs / 1000:F1}s | v{result.Graph.Version} | {(AuditLog.Verify(events).Valid ? "verified" : "BROKEN")} ({events.Count} events) |");
        sb.AppendLine();
        sb.AppendLine($"> {result.Reason}");
        if (scenario.Narrative is not null)
            sb.AppendLine().AppendLine($"**What this scenario demonstrates:** {scenario.Narrative}");

        sb.AppendLine().AppendLine("## 1. Requirement understanding").AppendLine();
        sb.AppendLine("**Raw request** (every version, including upstream changes):").AppendLine();
        foreach (var raw in store.History(WellKnownArtifacts.RequirementsRaw))
            sb.AppendLine($"- v{raw.Version} by `{raw.ProducedBy}`: \"{raw.Content.Trim()}\"");
        if (spec is not null)
        {
            sb.AppendLine().AppendLine($"**Normalised problem:** {spec.ProblemStatement}").AppendLine();
            if (spec.Ambiguities.Count > 0)
            {
                sb.AppendLine("| Ambiguous term | Found | Resolution |").AppendLine("|---|---|---|");
                foreach (var a in spec.Ambiguities)
                    sb.AppendLine($"| {a.Term} | \"{a.Excerpt}\" | {a.Resolution} |");
                sb.AppendLine();
            }
            sb.AppendLine("| Id | Requirement | Capability | Priority |").AppendLine("|---|---|---|---|");
            foreach (var r in spec.Functional.Concat(spec.NonFunctional))
                sb.AppendLine($"| {r.Id} | {r.Text} | {r.CapabilityId} | {r.Priority} |");
            sb.AppendLine().AppendLine("**Assumptions**").AppendLine();
            spec.Assumptions.ToList().ForEach(a => sb.AppendLine($"- {a}"));
            if (spec.OutOfScope.Count > 0)
            {
                sb.AppendLine().AppendLine("**Out of scope / follow-ups**").AppendLine();
                spec.OutOfScope.ToList().ForEach(o => sb.AppendLine($"- {o}"));
            }
        }

        if (impact is not null)
        {
            sb.AppendLine().AppendLine("## 2. Codebase impact (brownfield)").AppendLine();
            sb.AppendLine($"Blast radius **{impact.BlastRadius}** production files, risk **{impact.Risk}**. Existing endpoints: {string.Join(", ", impact.ExistingEndpoints.Select(e => $"`{e}`"))}.").AppendLine();
            foreach (var cause in impact.RootCauses)
                sb.AppendLine($"- **Root cause ({cause.Capability})** at `{cause.Location}`: {cause.Explanation}");
            sb.AppendLine().AppendLine("| File | Why | Symbols |").AppendLine("|---|---|---|");
            foreach (var f in impact.ImpactedFiles)
                sb.AppendLine($"| `{f.Path}` | {f.Reason} | {string.Join(", ", f.Symbols)} |");
            sb.AppendLine().AppendLine("Data flows:").AppendLine();
            impact.DataFlows.ToList().ForEach(d => sb.AppendLine($"- {d}"));
        }

        if (design is not null)
        {
            sb.AppendLine().AppendLine("## 3. Design").AppendLine().AppendLine(design.Summary).AppendLine();
            sb.AppendLine("| Method | Path | Responses | Change |").AppendLine("|---|---|---|---|");
            foreach (var e in design.Endpoints)
                sb.AppendLine($"| {e.Method} | `{e.Path}` | {string.Join(", ", e.Responses.Keys)} | {e.Change} |");
            sb.AppendLine();
            foreach (var adr in design.Decisions)
                sb.AppendLine($"- **{adr.Id} {adr.Title}** — {adr.Decision} *Why:* {adr.Rationale} *Trade-off:* {adr.Consequences}");
            design.ReviewNotes.ToList().ForEach(n => sb.AppendLine($"- _{n}_"));
        }

        if (plan is not null)
        {
            sb.AppendLine().AppendLine("## 4. Task decomposition").AppendLine();
            sb.AppendLine("| Task | Title | Depends on | Files | Risk |").AppendLine("|---|---|---|---|---|");
            foreach (var t in plan.Tasks)
                sb.AppendLine($"| `{t.Id}` | {t.Title} | {string.Join(", ", t.DependsOn)} | {t.Files.Count} | {t.Risk} |");
            sb.AppendLine().AppendLine($"Parallel groups: {string.Join(" → ", plan.ParallelGroups.Select(g => $"[{string.Join(", ", g)}]"))}. Critical path: {string.Join(" → ", plan.CriticalPath)}.");
        }

        sb.AppendLine().AppendLine("## 5. Orchestration").AppendLine();
        sb.AppendLine("```mermaid").Append(result.Graph.ToMermaid(id => result.Nodes.TryGetValue(id, out var s) ? s.Status : null)).AppendLine("```").AppendLine();
        sb.AppendLine("| Node | Stage | Agent used | Status | Executions | Revisions |").AppendLine("|---|---|---|---|---|---|");
        foreach (var node in result.Graph.Nodes)
        {
            var s = result.Nodes[node.Id];
            sb.AppendLine($"| `{node.Id}` | {node.Stage} | {s.AgentUsed} | {s.Status} | {s.Executions} | {s.Revisions} |");
        }
        sb.AppendLine().AppendLine("**Notable control-flow events**").AppendLine();
        string[] notable = ["wave.started", "agent.retry", "node.fallback", "remediation.started", "rollback.completed", "node.stale", "replan.retracted",
            "graph.mutated", "external.event", "approval.decided", "policy.finding", "node.revision", "run.safe-stop", "node.failed"];
        foreach (var e in events.Where(e => notable.Contains(e.Type)))
            sb.AppendLine($"- `#{e.Seq}` **{e.Type}** {(e.NodeId is null ? "" : $"[{e.NodeId}] ")}— {e.Actor}: {Shorten(e.Message, 220)}");

        sb.AppendLine().AppendLine("## 6. Validation").AppendLine();
        if (tests is not null)
            sb.AppendLine($"- Tests: **{tests.Passed}/{tests.Total} passed**, {tests.Failed} failed ({tests.DurationMs / 1000:F1}s).");
        if (security is not null)
            sb.AppendLine($"- Security review: {security.Findings.Count} findings, blocking: {security.Blocking}. Dependencies: {string.Join(", ", security.Dependencies)}.");
        var gateEvents = events.Where(e => e.Type.StartsWith("gate.")).ToList();
        sb.AppendLine($"- Gates evaluated: {gateEvents.Count} ({gateEvents.Count(e => e.Data.GetValueOrDefault("passed") == "False")} failed at some point).");
        if (trace is not null)
        {
            sb.AppendLine().AppendLine("| AC | Criterion | Verified by | ✔ |").AppendLine("|---|---|---|---|");
            foreach (var t in trace)
                sb.AppendLine($"| {t.AcceptanceCriterion} | {t.Criterion} | {string.Join(", ", t.Tests)} | {(t.Verified ? "yes" : "**no**")} |");
        }
        if (release is not null)
        {
            sb.AppendLine().AppendLine($"**Release {release.Version} checklist** (ready: {release.Ready})").AppendLine();
            release.Checklist.ToList().ForEach(c => sb.AppendLine($"- [{(c.Passed ? "x" : " ")}] {c.Item} — {c.Evidence}"));
            sb.AppendLine().AppendLine("**Rollback plan**").AppendLine();
            release.RollbackPlan.ToList().ForEach(r => sb.AppendLine($"- {r}"));
        }

        sb.AppendLine().AppendLine("## 7. Governance and decision lineage").AppendLine();
        sb.AppendLine("| Id | Node | Category | Decision | Actor | Rationale |").AppendLine("|---|---|---|---|---|---|");
        foreach (var d in result.Context.Decisions.All)
            sb.AppendLine($"| {d.Id} | {d.NodeId} | {d.Category} | {Cell(d.Summary)} | {d.Actor} | {Cell(Shorten(d.Rationale, 160))} |");

        sb.AppendLine().AppendLine("## 8. Reliability metrics").AppendLine();
        sb.AppendLine($"- Node attempt success rate: **{m.NodeSuccessRate:P1}** over {m.NodeExecutions} agent attempts");
        sb.AppendLine($"- MTTR: **{(m.MttrMs is null ? "n/a (no recovered failures)" : $"{m.MttrMs / 1000:F1}s")}** across {m.Recoveries.Count} recoveries ({string.Join(", ", m.Recoveries.Select(r => $"{r.NodeId} via {r.Mechanism}"))})");
        sb.AppendLine($"- Counters: {string.Join(", ", m.Counters.Select(c => $"{c.Key}={c.Value}"))}");
        sb.AppendLine().AppendLine("| Node | Stage | Agent | Duration | Status |").AppendLine("|---|---|---|---|---|");
        foreach (var t in m.Stages)
            sb.AppendLine($"| {t.NodeId} | {t.Stage} | {t.Agent} | {t.DurationMs / 1000:F1}s | {t.Status} |");

        if (design is not null)
        {
            sb.AppendLine().AppendLine("## 9. Risks and trade-offs").AppendLine();
            sb.AppendLine("| Id | Risk | L | I | Mitigation |").AppendLine("|---|---|---|---|---|");
            foreach (var r in design.Risks)
                sb.AppendLine($"| {r.Id} | {r.Description} | {r.Likelihood} | {r.Impact} | {r.Mitigation} |");
        }

        sb.AppendLine().AppendLine("## 10. Limitations of this run").AppendLine();
        sb.AppendLine("- Agents are deterministic and catalogue-driven: they recognise capabilities in the catalogue and apply reviewed recipes; they do not synthesise novel code. An LLM-backed agent can implement the same `IAgent` contract and is governed by the same gates, policies and approvals.");
        sb.AppendLine("- Human approvals in non-interactive mode come from the scenario script or the unattended provider (recorded as such in the audit log). Use `--interactive` for real sign-off.");
        sb.AppendLine($"- The sandbox (`workspace/`) is not promoted into the repository automatically; promotion is a human action.");
        return sb.ToString();
    }

    private static string Shorten(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    private static string Cell(string value) => value.Replace("|", "\\|").ReplaceLineEndings(" ");
}
