namespace Agentic.Orchestrator.Context;

public static class DecisionCategory
{
    public const string Scope = "scope";
    public const string Assumption = "assumption";
    public const string Design = "design";
    public const string Plan = "plan";
    public const string Approval = "approval";
    public const string Replan = "replan";
    public const string Policy = "policy";
    public const string Recovery = "recovery";
}

/// <summary>Agent-side description of a decision; the engine stamps identity, actor and lineage.</summary>
public sealed record DecisionDraft(string Category, string Summary, string Rationale, IReadOnlyList<string>? Alternatives = null);

/// <summary>One entry of the decision lineage: what was decided, by whom, why, based on which inputs.</summary>
public sealed record DecisionRecord(
    string Id,
    string? NodeId,
    string Category,
    string Summary,
    string Rationale,
    IReadOnlyList<string> Alternatives,
    string Actor,
    DateTimeOffset At,
    IReadOnlyList<ArtifactRef> BasedOn);

public sealed class DecisionLog(TimeProvider? time = null)
{
    private readonly List<DecisionRecord> _records = [];
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public DecisionRecord Record(string? nodeId, string actor, DecisionDraft draft, IReadOnlyList<ArtifactRef>? basedOn = null)
    {
        lock (_records)
        {
            var record = new DecisionRecord($"D{_records.Count + 1:000}", nodeId, draft.Category, draft.Summary, draft.Rationale,
                draft.Alternatives ?? [], actor, _time.GetUtcNow(), basedOn ?? []);
            _records.Add(record);
            return record;
        }
    }

    public IReadOnlyList<DecisionRecord> All
    {
        get { lock (_records) return _records.ToList(); }
    }
}
