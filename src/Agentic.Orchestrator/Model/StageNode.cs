using Agentic.Orchestrator.Gates;

namespace Agentic.Orchestrator.Model;

/// <param name="MaxAttempts">Total attempts including the first (bounded retries).</param>
public sealed record RetryPolicy(int MaxAttempts, TimeSpan InitialBackoff, double BackoffMultiplier = 2.0)
{
    public static readonly RetryPolicy Default = new(3, TimeSpan.FromMilliseconds(250));
    public static readonly RetryPolicy None = new(1, TimeSpan.Zero);

    public TimeSpan BackoffFor(int attempt) =>
        TimeSpan.FromMilliseconds(InitialBackoff.TotalMilliseconds * Math.Pow(BackoffMultiplier, attempt - 1));
}

/// <summary>
/// On permanent failure of the owning node, roll back and re-run every node tagged
/// <paramref name="TargetTag"/> with the failure as feedback, at most <paramref name="MaxCycles"/> times.
/// This is the non-linear "test failed → fix implementation" loop, kept bounded.
/// </summary>
public sealed record RemediationPolicy(string TargetTag, int MaxCycles);

/// <summary>Immutable definition of one unit of work in the workflow graph.</summary>
public sealed record StageNode
{
    public required string Id { get; init; }
    public required string Stage { get; init; }
    public required string Agent { get; init; }
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<string> DependsOn { get; init; } = [];
    public IReadOnlyList<IGate> EntryGates { get; init; } = [];
    public IReadOnlyList<IGate> ExitGates { get; init; } = [];
    public RiskLevel Risk { get; init; } = RiskLevel.Low;

    /// <summary>Forces a human checkpoint regardless of risk-based autonomy settings.</summary>
    public bool RequiresApproval { get; init; }

    public RetryPolicy Retry { get; init; } = RetryPolicy.Default;
    public string? FallbackAgent { get; init; }
    public RemediationPolicy? Remediation { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Max re-executions after a human rejects the node's output with feedback.</summary>
    public int MaxRevisions { get; init; } = 2;

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Named resources (e.g. file paths). Nodes sharing a lock never run concurrently.</summary>
    public IReadOnlyList<string> Locks { get; init; } = [];

    /// <summary>Id of the node whose graph mutation created this node (null for template nodes).</summary>
    public string? ExpandedBy { get; init; }

    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();

    public bool NeedsCheckpoint(RiskLevel autoApproveUpTo) => RequiresApproval || Risk > autoApproveUpTo;
}
