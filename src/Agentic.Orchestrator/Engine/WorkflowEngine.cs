using Agentic.Orchestrator.Agents;
using Agentic.Orchestrator.Context;
using Agentic.Orchestrator.Governance;
using Agentic.Orchestrator.Model;
using Agentic.Orchestrator.Observability;
using Agentic.Orchestrator.Tools;

namespace Agentic.Orchestrator.Engine;

/// <summary>An upstream change arriving from outside the run (e.g. the product owner edits the ticket).</summary>
public sealed record ExternalEvent(string AfterNodeId, string ArtifactKey, string Content, string Actor, string Reason);

public sealed record RunOptions
{
    public AutonomyPolicy Autonomy { get; init; } = new();

    /// <summary>Safe-stop budget: total agent attempts across the run (guards against livelock).</summary>
    public int MaxNodeExecutions { get; init; } = 200;

    /// <summary>Safe-stop budget: wall-clock limit for the whole run.</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>On unrecoverable failure, undo every applied change set (saga compensation).</summary>
    public bool RollbackOnFailure { get; init; } = true;

    /// <summary>Multiplier on retry backoff (0 in tests).</summary>
    public double BackoffScale { get; init; } = 1.0;

    /// <summary>Creating this file requests a safe stop at the next scheduling boundary (kill switch).</summary>
    public string? StopFilePath { get; init; }

    public int MaxClarificationRounds { get; init; } = 2;
    public int MaxNodesPerMutation { get; init; } = 50;
    public IReadOnlyList<ExternalEvent> ExternalEvents { get; init; } = [];
}

public sealed class NodeState
{
    public NodeStatus Status { get; internal set; } = NodeStatus.Pending;
    public int Executions { get; internal set; }
    public int Revisions { get; internal set; }
    public int RemediationCycles { get; internal set; }
    public string? AgentUsed { get; internal set; }
    public string? LastError { get; internal set; }
    public string? Summary { get; internal set; }
    public DateTimeOffset? StartedAt { get; internal set; }
    public DateTimeOffset? CompletedAt { get; internal set; }
    public List<string> Feedback { get; } = [];
    public Dictionary<string, string> Answers { get; } = new(StringComparer.Ordinal);
    public IReadOnlyList<ArtifactRef> Inputs { get; internal set; } = [];
    public IReadOnlyList<ArtifactRef> Outputs { get; internal set; } = [];
}

public sealed record RunResult(
    RunStatus Status,
    string? Reason,
    WorkflowGraph Graph,
    IReadOnlyDictionary<string, NodeState> Nodes,
    MetricsSnapshot Metrics,
    WorkflowContext Context);

/// <summary>Entry point. Each call to <see cref="RunAsync"/> executes one isolated, stateful run.</summary>
public sealed class WorkflowEngine(
    AgentRegistry agents,
    PolicyEngine policies,
    IApprovalProvider approvals,
    IToolRunner tools,
    RunOptions? options = null)
{
    public Task<RunResult> RunAsync(WorkflowGraph graph, WorkflowContext context, CancellationToken cancellationToken = default) =>
        new WorkflowRun(agents, policies, approvals, tools, options ?? new RunOptions(), graph, context).ExecuteAsync(cancellationToken);
}
