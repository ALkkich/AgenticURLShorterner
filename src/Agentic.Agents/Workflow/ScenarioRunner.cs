using Agentic.Agents.Agents;
using Agentic.Agents.Reporting;
using Agentic.Orchestrator.Agents;
using Agentic.Orchestrator.Context;
using Agentic.Orchestrator.Engine;
using Agentic.Orchestrator.Governance;
using Agentic.Orchestrator.Model;
using Agentic.Orchestrator.Observability;
using Agentic.Orchestrator.Tools;
using Agentic.Orchestrator.Workspace;

namespace Agentic.Agents.Workflow;

public sealed record ScenarioDefinition
{
    public required string Name { get; init; }
    public required string Title { get; init; }
    public required string Kind { get; init; }

    /// <summary>"empty" (greenfield) or "reference" (seed the sandbox from the repository's service).</summary>
    public string Baseline { get; init; } = "reference";

    public string BaselineVersion { get; init; } = "1.0.0";
    public required string Requirement { get; init; }
    public string? Narrative { get; init; }

    /// <summary>Fault injection, e.g. "solution-architect:transient:1;tech-writer:permanent".</summary>
    public string? Faults { get; init; }

    /// <summary>Recipe id whose first implementation attempt carries a compile defect.</summary>
    public string? InjectDefect { get; init; }

    public List<ScriptedResponse> Approvals { get; init; } = [];
    public List<ExternalEvent> ExternalEvents { get; init; } = [];

    public static ScenarioDefinition Load(string path) => Json.Deserialize<ScenarioDefinition>(File.ReadAllText(path));
}

public sealed record ScenarioRunOptions
{
    public RiskLevel AutoApproveUpTo { get; init; } = RiskLevel.Low;
    public bool InjectFaults { get; init; } = true;
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(30);
}

public sealed record ScenarioRunOutcome(RunResult Result, string RunDirectory, string ReportPath);

/// <summary>Sets up an isolated run (sandbox, audit log, settings), executes it and exports evidence.</summary>
public static class ScenarioRunner
{
    public static readonly string[] ServiceDirectories = ["src/UrlShortener.Api", "tests/UrlShortener.Tests"];

    public static async Task<ScenarioRunOutcome> RunAsync(ScenarioDefinition scenario, string repoRoot, ScenarioRunOptions options,
        IApprovalProvider human, Action<AuditEvent>? onEvent, CancellationToken cancellationToken)
    {
        var runId = $"{scenario.Name}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        var runDir = Path.Combine(repoRoot, ".runs", runId);
        var workspace = new FileSystemWorkspace(Path.Combine(runDir, "workspace"));
        if (scenario.Baseline == "reference")
            workspace.SeedFrom(repoRoot, ServiceDirectories);

        using var audit = new AuditLog(runId, Path.Combine(runDir, "audit.jsonl"));
        if (onEvent is not null)
            audit.Appended += onEvent;

        var settings = new Dictionary<string, string>
        {
            [SettingKeys.Baseline] = scenario.Baseline,
            [SettingKeys.BaselineVersion] = scenario.BaselineVersion,
            [SettingKeys.ReferenceRoot] = repoRoot,
        };
        if (options.InjectFaults && scenario.InjectDefect is not null)
            settings[SettingKeys.InjectDefect] = scenario.InjectDefect;

        var context = new WorkflowContext(runId, scenario.Name, runDir, workspace, audit, settings);
        context.Artifacts.Put(WellKnownArtifacts.RequirementsRaw, scenario.Requirement, "text/plain", "human:requester");

        var engine = new WorkflowEngine(
            SdlcWorkflowFactory.CreateAgents(options.InjectFaults ? scenario.Faults : null),
            PolicyEngine.Default(),
            new ScriptedApprovalProvider(scenario.Approvals, human),
            new ProcessToolRunner(),
            new RunOptions
            {
                Autonomy = new AutonomyPolicy(options.AutoApproveUpTo),
                MaxDuration = options.MaxDuration,
                StopFilePath = Path.Combine(runDir, "STOP"),
                ExternalEvents = scenario.ExternalEvents,
            });

        var result = await engine.RunAsync(SdlcWorkflowFactory.Create(scenario.Baseline == "empty"), context, cancellationToken);
        var report = RunExporter.Export(scenario, result, runDir, repoRoot);
        return new ScenarioRunOutcome(result, runDir, report);
    }
}
