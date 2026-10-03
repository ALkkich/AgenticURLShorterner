using System.Collections.Concurrent;
using Agentic.Orchestrator.Context;
using Agentic.Orchestrator.Governance;
using Agentic.Orchestrator.Model;
using Agentic.Orchestrator.Observability;
using Agentic.Orchestrator.Tools;
using Agentic.Orchestrator.Workspace;

namespace Agentic.Orchestrator.Agents;

/// <summary>
/// An agent performs one kind of SDLC work. It reads context, and returns artifacts, decisions,
/// proposed file changes and proposed graph changes. It never mutates shared state directly —
/// that is what keeps its autonomy bounded and every effect auditable.
/// </summary>
public interface IAgent
{
    string Name { get; }
    Task<AgentResult> ExecuteAsync(AgentContext context);
}

public sealed record ArtifactDraft(string Key, string Content, string ContentType);

public sealed class AgentResult
{
    private AgentResult(AgentOutcome outcome, string summary) => (Outcome, Summary) = (outcome, summary);

    public AgentOutcome Outcome { get; }
    public string Summary { get; }
    public List<ArtifactDraft> Artifacts { get; } = [];
    public List<DecisionDraft> Decisions { get; } = [];
    public List<ClarificationQuestion> Questions { get; } = [];
    public ChangeSet? Changes { get; private set; }
    public GraphMutation? Mutation { get; private set; }

    public static AgentResult Success(string summary) => new(AgentOutcome.Success, summary);
    public static AgentResult Transient(string error) => new(AgentOutcome.TransientFailure, error);
    public static AgentResult Permanent(string error) => new(AgentOutcome.PermanentFailure, error);

    public static AgentResult NeedsInput(string summary, IEnumerable<ClarificationQuestion> questions)
    {
        var result = new AgentResult(AgentOutcome.NeedsInput, summary);
        result.Questions.AddRange(questions);
        return result;
    }

    public AgentResult WithText(string key, string content, string contentType = "text/markdown")
    {
        Artifacts.Add(new ArtifactDraft(key, content, contentType));
        return this;
    }

    public AgentResult WithJson<T>(string key, T value)
    {
        Artifacts.Add(new ArtifactDraft(key, Json.Serialize(value), "application/json"));
        return this;
    }

    public AgentResult WithDecision(string category, string summary, string rationale, params string[] alternatives)
    {
        Decisions.Add(new DecisionDraft(category, summary, rationale, alternatives));
        return this;
    }

    public AgentResult WithChanges(ChangeSet changes)
    {
        Changes = changes;
        return this;
    }

    public AgentResult WithMutation(GraphMutation mutation)
    {
        Mutation = mutation;
        return this;
    }
}

public sealed class AgentRegistry
{
    private readonly Dictionary<string, IAgent> _agents = new(StringComparer.Ordinal);

    public AgentRegistry Register(IAgent agent)
    {
        _agents[agent.Name] = agent;
        return this;
    }

    public bool Contains(string name) => _agents.ContainsKey(name);

    public IAgent? Find(string name) => _agents.GetValueOrDefault(name);

    public IReadOnlyCollection<string> Names => _agents.Keys;
}

/// <summary>Shared, run-scoped state: the "blackboard" that carries context across stages.</summary>
public sealed class WorkflowContext(
    string runId,
    string scenario,
    string runDirectory,
    FileSystemWorkspace workspace,
    AuditLog audit,
    IReadOnlyDictionary<string, string>? settings = null,
    TimeProvider? time = null)
{
    public string RunId { get; } = runId;
    public string Scenario { get; } = scenario;
    public string RunDirectory { get; } = runDirectory;
    public FileSystemWorkspace Workspace { get; } = workspace;
    public AuditLog Audit { get; } = audit;
    public TimeProvider Time { get; } = time ?? TimeProvider.System;
    public IReadOnlyDictionary<string, string> Settings { get; } = settings ?? new Dictionary<string, string>();
    public ArtifactStore Artifacts { get; } = new(time);
    public DecisionLog Decisions { get; } = new(time);
    public RunMetrics Metrics { get; } = new(runId, scenario, time);
}

/// <summary>
/// The view of the world handed to an agent for one attempt. Every artifact read is recorded so the
/// engine can stamp lineage on the outputs and detect when those inputs later change.
/// </summary>
public sealed class AgentContext
{
    private readonly WorkflowContext _workflow;
    private readonly ConcurrentDictionary<string, ArtifactRef> _reads = new(StringComparer.Ordinal);

    internal AgentContext(WorkflowContext workflow, StageNode node, string agentName, int attempt,
        IReadOnlyList<string> feedback, IReadOnlyDictionary<string, string> answers, IToolRunner tools, CancellationToken cancellationToken)
    {
        _workflow = workflow;
        Node = node;
        AgentName = agentName;
        Attempt = attempt;
        Feedback = feedback;
        Answers = answers;
        Tools = tools;
        CancellationToken = cancellationToken;
    }

    public StageNode Node { get; }
    public string AgentName { get; }
    public int Attempt { get; }

    /// <summary>Reviewer comments and upstream failure reports from previous revisions/remediations.</summary>
    public IReadOnlyList<string> Feedback { get; }

    /// <summary>Human answers to this node's clarification questions.</summary>
    public IReadOnlyDictionary<string, string> Answers { get; }

    public IToolRunner Tools { get; }
    public CancellationToken CancellationToken { get; }
    public string RunId => _workflow.RunId;
    public IReadOnlyWorkspace Workspace => _workflow.Workspace;
    public IReadOnlyDictionary<string, string> Settings => _workflow.Settings;
    public IReadOnlyList<ArtifactRef> Inputs => _reads.Values.OrderBy(r => r.Key, StringComparer.Ordinal).ToList();

    public string? Setting(string key) => Settings.GetValueOrDefault(key);

    public T? Read<T>(string key) where T : class
    {
        var artifact = Track(key);
        return artifact is null ? null : Json.Deserialize<T>(artifact.Content);
    }

    public T Require<T>(string key) where T : class =>
        Read<T>(key) ?? throw new InvalidOperationException($"Required artifact '{key}' is missing.");

    public string? ReadText(string key) => Track(key)?.Content;

    /// <summary>Snapshot of the decision lineage so far (read-only).</summary>
    public IReadOnlyList<DecisionRecord> Decisions => _workflow.Decisions.All;

    /// <summary>All artifact versions so far (read-only, not lineage-tracked; for reporting agents).</summary>
    public IReadOnlyList<Artifact> AllArtifacts => _workflow.Artifacts.AllLatest();

    public void Log(string message) => _workflow.Audit.Append("agent.log", $"agent:{AgentName}", message, Node.Id);

    private Artifact? Track(string key)
    {
        var artifact = _workflow.Artifacts.Latest(key);
        if (artifact is not null)
            _reads[key] = artifact.Ref;
        return artifact;
    }
}
