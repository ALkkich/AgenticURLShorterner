namespace Agentic.Orchestrator.Model;

public enum NodeStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped,
    /// <summary>Previously succeeded, but an input artifact changed upstream; must re-execute.</summary>
    Stale,
}

public enum RunStatus { Running, Succeeded, Failed, RolledBack, Stopped }

public enum RiskLevel { Low = 0, Medium = 1, High = 2, Critical = 3 }

public enum AgentOutcome
{
    Success,
    /// <summary>Retryable (timeouts, flaky tools, rate limits). Consumes the node's retry budget.</summary>
    TransientFailure,
    /// <summary>Not retryable with the same inputs. Triggers fallback / remediation / failure policy.</summary>
    PermanentFailure,
    /// <summary>The agent cannot proceed without human answers (clarification). Not counted as a failure.</summary>
    NeedsInput,
}

/// <summary>SDLC phases, used for reporting and stage-level metrics.</summary>
public static class Stages
{
    public const string Requirements = "Requirements";
    public const string Design = "Design";
    public const string Planning = "Planning";
    public const string Implementation = "Implementation";
    public const string Testing = "Testing";
    public const string Security = "Security";
    public const string Documentation = "Documentation";
    public const string Release = "Release";
}
