using Agentic.Agents.Agents;
using Agentic.Agents.Models;
using Agentic.Orchestrator.Agents;
using Agentic.Orchestrator.Context;
using Agentic.Orchestrator.Gates;
using Agentic.Orchestrator.Model;

namespace Agentic.Agents.Workflow;

public static class NodeIds
{
    public const string Requirements = "requirements";
    public const string ImpactAnalysis = "impact-analysis";
    public const string Design = "design";
    public const string Plan = "plan";
    public const string Docs = "docs";
    public const string Build = "build";
    public const string Test = "test";
    public const string SecurityReview = "security-review";
    public const string Release = "release";
    public const string ImplementationTag = "implementation";
}

/// <summary>
/// The SDLC workflow template. Implementation nodes are not in the template: the planner inserts
/// them at runtime (dynamic expansion). Shape:
/// <code>
/// requirements → [impact-analysis] → design ⟨human⟩ → plan → impl:* (parallel by deps/locks)
///                                       └→ docs ──────────────────────────────┐
///                                     plan → impl:* → build → test ───────────┼→ release ⟨human⟩
///                                     plan → impl:* → security-review ────────┘
/// </code>
/// </summary>
public static class SdlcWorkflowFactory
{
    public static WorkflowGraph Create(bool greenfield)
    {
        var nodes = new List<StageNode>
        {
            new()
            {
                Id = NodeIds.Requirements, Stage = Stages.Requirements, Agent = "requirements-analyst",
                Description = "Normalise intent into a testable specification",
                EntryGates = [Gate.RequiresArtifacts(WellKnownArtifacts.RequirementsRaw)],
                ExitGates =
                [
                    Gate.OnArtifact<RequirementSpec>("every-FR-has-acceptance-criteria", ArtifactKeys.RequirementSpec, s =>
                    {
                        var uncovered = s.Functional.Where(fr => s.AcceptanceCriteria.All(ac => ac.CapabilityId != fr.CapabilityId)).Select(fr => fr.Id).ToList();
                        return s.Functional.Count == 0 ? GateResult.Fail("no functional requirements")
                            : uncovered.Count > 0 ? GateResult.Fail($"FRs without acceptance criteria: {string.Join(", ", uncovered)}")
                            : GateResult.Pass($"{s.Functional.Count} FRs / {s.AcceptanceCriteria.Count} ACs");
                    }),
                ],
            },
        };

        if (!greenfield)
            nodes.Add(new()
            {
                Id = NodeIds.ImpactAnalysis, Stage = Stages.Design, Agent = "codebase-analyst",
                Description = "Impact and root-cause analysis on the existing codebase",
                DependsOn = [NodeIds.Requirements],
                ExitGates = [Gate.OnArtifact<ImpactReport>("impact-identified", ArtifactKeys.ImpactReport, r =>
                    r.ImpactedFiles.Count > 0 ? GateResult.Pass($"{r.ImpactedFiles.Count} files, risk {r.Risk}") : GateResult.Fail("no impacted files found"))],
            });

        nodes.AddRange(
        [
            new()
            {
                Id = NodeIds.Design, Stage = Stages.Design, Agent = "solution-architect",
                Description = "Architecture, API contract, ADRs and risks",
                DependsOn = greenfield ? [NodeIds.Requirements] : [NodeIds.Requirements, NodeIds.ImpactAnalysis],
                RequiresApproval = true, Risk = RiskLevel.Medium,
                ExitGates = [Gate.Check("requirements-traced-to-components", ctx =>
                {
                    var spec = ctx.Artifacts.LatestJson<RequirementSpec>(ArtifactKeys.RequirementSpec)!;
                    var design = ctx.Artifacts.LatestJson<DesignSpec>(ArtifactKeys.Design)!;
                    var untraced = spec.Functional.Where(fr => !design.Traceability.ContainsKey(fr.Id)).Select(fr => fr.Id).ToList();
                    return untraced.Count == 0 ? GateResult.Pass("all FRs traced") : GateResult.Fail($"untraced: {string.Join(", ", untraced)}");
                })],
            },
            new()
            {
                Id = NodeIds.Plan, Stage = Stages.Planning, Agent = "delivery-planner",
                Description = "Decompose into dependent tasks and expand the graph",
                DependsOn = [NodeIds.Design],
                ExitGates = [Gate.Check("every-capability-has-a-task", ctx =>
                {
                    var spec = ctx.Artifacts.LatestJson<RequirementSpec>(ArtifactKeys.RequirementSpec)!;
                    var plan = ctx.Artifacts.LatestJson<WorkPlan>(ArtifactKeys.WorkPlan)!;
                    var missing = spec.Capabilities.Where(c => plan.Tasks.All(t => !t.Capabilities.Contains(c))).ToList();
                    return missing.Count == 0 ? GateResult.Pass($"{plan.Tasks.Count} tasks") : GateResult.Fail($"no task for: {string.Join(", ", missing)}");
                })],
            },
            new()
            {
                Id = NodeIds.Docs, Stage = Stages.Documentation, Agent = "tech-writer", FallbackAgent = "docs-minimal",
                Description = "API reference, design record, changelog",
                DependsOn = [NodeIds.Design],
            },
            new()
            {
                Id = NodeIds.Build, Stage = Stages.Testing, Agent = "build-runner",
                Description = "Compile the workspace",
                DependsOn = [NodeIds.Plan],
                Retry = new RetryPolicy(2, TimeSpan.FromSeconds(2)),
                Remediation = new RemediationPolicy(NodeIds.ImplementationTag, 2),
            },
            new()
            {
                Id = NodeIds.Test, Stage = Stages.Testing, Agent = "test-runner",
                Description = "Unit + integration tests, acceptance traceability",
                DependsOn = [NodeIds.Build],
                Retry = new RetryPolicy(2, TimeSpan.FromSeconds(2)),
                Remediation = new RemediationPolicy(NodeIds.ImplementationTag, 2),
                ExitGates =
                [
                    Gate.OnArtifact<TestReport>("tests-green", ArtifactKeys.TestReport, r =>
                        r.Failed == 0 && r.Total > 0 ? GateResult.Pass($"{r.Passed}/{r.Total}") : GateResult.Fail($"{r.Failed} failed of {r.Total}")),
                    Gate.OnArtifact<List<TraceLink>>("acceptance-criteria-verified", ArtifactKeys.Traceability, t =>
                    {
                        var unverified = t.Where(l => !l.Verified).Select(l => l.AcceptanceCriterion).ToList();
                        return unverified.Count == 0 ? GateResult.Pass($"{t.Count} ACs verified") : GateResult.Fail($"unverified: {string.Join(", ", unverified)}");
                    }),
                ],
            },
            new()
            {
                Id = NodeIds.SecurityReview, Stage = Stages.Security, Agent = "security-reviewer",
                Description = "Whole-codebase security and compliance scan",
                DependsOn = [NodeIds.Plan],
            },
            new()
            {
                Id = NodeIds.Release, Stage = Stages.Release, Agent = "release-manager",
                Description = "Release readiness and go/no-go",
                DependsOn = [NodeIds.Test, NodeIds.SecurityReview, NodeIds.Docs],
                EntryGates = [Gate.RequiresArtifacts(ArtifactKeys.TestReport, ArtifactKeys.SecurityReport, ArtifactKeys.DocsManifest)],
                RequiresApproval = true, Risk = RiskLevel.High, Retry = RetryPolicy.None,
            },
        ]);
        return new WorkflowGraph(nodes);
    }

    public static AgentRegistry CreateAgents(string? faults)
    {
        var registry = new AgentRegistry();
        IAgent[] agents =
        [
            new RequirementsAnalystAgent(), new CodebaseAnalystAgent(), new SolutionArchitectAgent(), new DeliveryPlannerAgent(),
            new ImplementerAgent(), new BuildRunnerAgent(), new TestRunnerAgent(), new SecurityReviewerAgent(),
            new TechWriterAgent(), new MinimalDocsAgent(), new ReleaseManagerAgent(),
        ];
        foreach (var agent in agents)
            registry.Register(FaultInjectingAgent.Wrap(agent, faults));
        return registry;
    }
}
