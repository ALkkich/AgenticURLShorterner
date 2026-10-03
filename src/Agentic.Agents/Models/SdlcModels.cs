using Agentic.Orchestrator.Model;

namespace Agentic.Agents.Models;

/// <summary>Well-known artifact keys exchanged between SDLC stages (the inter-agent contract).</summary>
public static class ArtifactKeys
{
    public const string RequirementSpec = "requirements.spec";
    public const string ImpactReport = "analysis.impact";
    public const string Design = "design.spec";
    public const string ApiContract = "design.openapi";
    public const string WorkPlan = "plan.work";
    public const string BuildReport = "build.report";
    public const string TestReport = "test.report";
    public const string Traceability = "test.traceability";
    public const string SecurityReport = "security.report";
    public const string DocsManifest = "docs.manifest";
    public const string ReleaseReadiness = "release.readiness";
    public const string ReleaseNotes = "release.notes";
    public static string TaskResult(string taskId) => $"impl.{taskId}";
}

public enum ChangeType { Greenfield, Enhancement, BugFix }

public sealed record Requirement(string Id, string Text, string CapabilityId, string Priority);

public sealed record AcceptanceCriterion(string Id, string RequirementId, string CapabilityId, string Criterion, IReadOnlyList<string> VerifiedBy);

public sealed record Ambiguity(string Term, string Excerpt, string Resolution);

public sealed record RequirementSpec(
    string Title,
    string ProblemStatement,
    bool IsGreenfield,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<Requirement> Functional,
    IReadOnlyList<Requirement> NonFunctional,
    IReadOnlyList<AcceptanceCriterion> AcceptanceCriteria,
    IReadOnlyList<Ambiguity> Ambiguities,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> OutOfScope,
    IReadOnlyDictionary<string, string> Parameters);

public sealed record ImpactedFile(string Path, string Reason, IReadOnlyList<string> Symbols);

public sealed record RootCause(string Capability, string Location, string Explanation);

public sealed record ImpactReport(
    IReadOnlyList<ImpactedFile> ImpactedFiles,
    IReadOnlyList<string> ExistingEndpoints,
    IReadOnlyList<string> ImpactedEndpoints,
    IReadOnlyList<string> DataFlows,
    IReadOnlyList<string> AffectedTests,
    IReadOnlyList<RootCause> RootCauses,
    int BlastRadius,
    RiskLevel Risk);

public sealed record Component(string Name, string Responsibility, IReadOnlyList<string> Paths);

public sealed record Endpoint(string Method, string Path, string Summary, string? Request, IReadOnlyDictionary<string, string> Responses, string? Change = null);

public sealed record Adr(string Id, string Title, string Decision, string Rationale, IReadOnlyList<string> Alternatives, string Consequences);

public sealed record RiskItem(string Id, string Description, string Likelihood, string Impact, string Mitigation);

public sealed record DesignSpec(
    string Summary,
    IReadOnlyList<Component> Components,
    IReadOnlyList<Endpoint> Endpoints,
    IReadOnlyList<Adr> Decisions,
    IReadOnlyList<RiskItem> Risks,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Traceability,
    IReadOnlyList<string> ReviewNotes);

public sealed record WorkTask(
    string Id,
    string Title,
    string RecipeId,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> Files,
    RiskLevel Risk);

public sealed record WorkPlan(IReadOnlyList<WorkTask> Tasks, IReadOnlyList<string> CriticalPath, IReadOnlyList<IReadOnlyList<string>> ParallelGroups);

public sealed record TestCaseResult(string Name, string Outcome, double DurationMs, string? Message);

public sealed record TestReport(int Total, int Passed, int Failed, int Skipped, double DurationMs, IReadOnlyList<TestCaseResult> Failures, IReadOnlyList<string> PassedTests);

public sealed record TraceLink(string AcceptanceCriterion, string Criterion, IReadOnlyList<string> Tests, bool Verified);

public sealed record SecurityFinding(string Rule, string Severity, string Path, string Message);

public sealed record SecurityReport(IReadOnlyList<SecurityFinding> Findings, IReadOnlyList<string> Dependencies, IReadOnlyList<string> Threats, bool Blocking);

public sealed record ChecklistItem(string Item, bool Passed, string Evidence);

public sealed record ReleaseReadiness(string Version, bool Ready, IReadOnlyList<ChecklistItem> Checklist, IReadOnlyList<string> RollbackPlan, IReadOnlyList<string> ChangedFiles);
