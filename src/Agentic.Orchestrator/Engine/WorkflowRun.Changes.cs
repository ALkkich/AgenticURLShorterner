using Agentic.Orchestrator.Context;
using Agentic.Orchestrator.Governance;
using Agentic.Orchestrator.Model;
using Counters = Agentic.Orchestrator.Observability.RunMetrics.Counters;

namespace Agentic.Orchestrator.Engine;

internal sealed record ChangeOutcome(bool Applied, bool Blocked, bool Rejected, string Reason);

/// <summary>A change set that was applied, with the prior content of each file (its compensation).</summary>
internal sealed record AppliedChange(long Seq, string NodeId, string Title, IReadOnlyDictionary<string, string?> Before, DiffStat Stat);

internal sealed partial class WorkflowRun
{
    private readonly SemaphoreSlim _changeLock = new(1, 1);
    private readonly List<AppliedChange> _applied = [];
    private long _changeSeq;

    private bool HasAppliedChanges
    {
        get { lock (_applied) return _applied.Count > 0; }
    }

    /// <summary>
    /// Change control: policy evaluation â†’ (approval if required) â†’ atomic apply with recorded
    /// compensation. Agents never touch the workspace directly.
    /// </summary>
    private async Task<ChangeOutcome> ProposeChangesAsync(StageNode node, ChangeSet changes, CancellationToken cancellationToken)
    {
        await _changeLock.WaitAsync(cancellationToken);
        try
        {
            var workspace = _ctx.Workspace;
            var findings = _policies.Evaluate(new PolicySubject(node, changes, workspace.TryRead));
            var stat = DiffStat.Compute(changes, workspace.TryRead);
            foreach (var finding in findings)
            {
                Metrics.Increment(Counters.PolicyFindings);
                Audit.Append("policy.finding", $"policy:{finding.PolicyId}", $"[{finding.Severity}] {finding.Message}", node.Id,
                    new Dictionary<string, string> { ["category"] = finding.Category.ToString(), ["path"] = finding.Path ?? "" });
            }

            var blocking = findings.Where(f => f.Severity == PolicySeverity.Block).ToList();
            if (blocking.Count > 0)
            {
                Metrics.Increment(Counters.PolicyBlocks);
                var reason = string.Join("; ", blocking.Select(f => $"{f.PolicyId}: {f.Message} {f.Path}"));
                _ctx.Decisions.Record(node.Id, "policy", new DecisionDraft(DecisionCategory.Policy, $"Blocked change set '{changes.Title}'", reason));
                return new ChangeOutcome(false, true, false, reason);
            }

            var needsApproval = findings.Where(f => f.Severity == PolicySeverity.RequiresApproval).ToList();
            if (needsApproval.Count > 0)
            {
                var evidence = needsApproval.Select(f => $"{f.PolicyId}: {f.Message} {f.Path}".Trim())
                    .Append($"diff: {stat.FilesChanged} files, +{stat.LinesAdded}/-{stat.LinesRemoved}")
                    .Concat(changes.Changes.Select(c => $"{c.Kind} {c.Path} â€” {c.Description}"))
                    .ToList();
                var risk = node.Risk < RiskLevel.High ? RiskLevel.High : node.Risk;
                var decision = await CheckpointAsync(node, ApprovalKind.ChangeSet, $"Approve change set '{changes.Title}'", risk,
                    "Policy requires human approval before applying this change.", evidence, [], cancellationToken);
                if (!decision.Approved)
                    return new ChangeOutcome(false, false, true, decision.Comment ?? "rejected");
            }

            var before = changes.Changes.Select(c => c.Path).Distinct().ToDictionary(p => p, workspace.TryRead);
            try
            {
                foreach (var change in changes.Changes)
                {
                    if (change.Kind == ChangeKind.Delete)
                        workspace.Delete(change.Path);
                    else
                        workspace.Write(change.Path, change.NewContent ?? string.Empty);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Restore(before);
                return new ChangeOutcome(false, true, false, $"Apply failed and was reverted: {ex.Message}");
            }

            lock (_applied)
                _applied.Add(new AppliedChange(++_changeSeq, node.Id, changes.Title, before, stat));
            Audit.Append("change.applied", $"agent:{node.Agent}", changes.Title, node.Id, new Dictionary<string, string>
            {
                ["files"] = string.Join(",", before.Keys),
                ["linesAdded"] = stat.LinesAdded.ToString(),
                ["linesRemoved"] = stat.LinesRemoved.ToString(),
            });
            PublishWorkspaceState();
            return new ChangeOutcome(true, false, false, "applied");
        }
        finally
        {
            _changeLock.Release();
        }
    }

    /// <summary>
    /// Compensates the change sets of <paramref name="nodeIds"/> in reverse order. Any later change set
    /// that touched an affected file is undone too (otherwise restoring old content would silently drop
    /// it); the ids of every node whose changes were undone are returned so callers can re-schedule them.
    /// </summary>
    private IReadOnlySet<string> Rollback(IReadOnlySet<string> nodeIds, string reason)
    {
        List<AppliedChange> undo;
        lock (_applied)
        {
            undo = [];
            var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _applied)
            {
                if (nodeIds.Contains(entry.NodeId) || entry.Before.Keys.Any(touched.Contains))
                {
                    undo.Add(entry);
                    touched.UnionWith(entry.Before.Keys);
                }
            }
            if (undo.Count == 0)
                return new HashSet<string>();
            foreach (var entry in Enumerable.Reverse(undo))
            {
                Restore(entry.Before);
                _applied.Remove(entry);
            }
        }

        Metrics.Increment(Counters.Rollbacks);
        var undoneNodes = undo.Select(u => u.NodeId).ToHashSet(StringComparer.Ordinal);
        Audit.Append("rollback.completed", "engine", reason, data: new Dictionary<string, string>
        {
            ["changeSets"] = string.Join(",", undo.Select(u => $"#{u.Seq}:{u.NodeId}")),
            ["files"] = string.Join(",", undo.SelectMany(u => u.Before.Keys).Distinct()),
        });
        PublishWorkspaceState();
        return undoneNodes;
    }

    private void Restore(IReadOnlyDictionary<string, string?> before)
    {
        foreach (var (path, content) in before)
        {
            if (content is null)
                _ctx.Workspace.Delete(path);
            else
                _ctx.Workspace.Write(path, content);
        }
    }

    /// <summary>Publishes the build-relevant workspace hash so build/test nodes go stale when code changes.</summary>
    private void PublishWorkspaceState()
    {
        var hash = _ctx.Workspace.ComputeBuildStateHash();
        _ctx.Artifacts.Put(WellKnownArtifacts.WorkspaceState, hash, "text/plain", "engine:change-control");
    }
}
