using Agentic.Orchestrator.Agents;
using Agentic.Orchestrator.Context;
using Agentic.Orchestrator.Governance;
using Agentic.Orchestrator.Model;
using Agentic.Orchestrator.Observability;
using Agentic.Orchestrator.Tools;
using Counters = Agentic.Orchestrator.Observability.RunMetrics.Counters;

namespace Agentic.Orchestrator.Engine;

/// <summary>
/// One run of a workflow graph. The scheduler runs in waves: every node whose dependencies have
/// succeeded is eligible; eligible nodes without conflicting locks execute in parallel; the wave is a
/// synchronisation barrier after which graph mutations, external events and failures are processed
/// on the scheduler thread only. Node state changes are guarded by <see cref="_stateLock"/>.
/// </summary>
internal sealed partial class WorkflowRun
{
    private readonly AgentRegistry _agents;
    private readonly PolicyEngine _policies;
    private readonly IApprovalProvider _approvals;
    private readonly IToolRunner _governedTools;
    private readonly RunOptions _options;
    private readonly WorkflowGraph _graph;
    private readonly WorkflowContext _ctx;
    private readonly Dictionary<string, NodeState> _states = new(StringComparer.Ordinal);
    private readonly object _stateLock = new();
    private readonly HashSet<ExternalEvent> _firedEvents = [];
    private readonly DateTimeOffset _startedAt;
    private RunStatus _status = RunStatus.Running;
    private string? _reason;

    public WorkflowRun(AgentRegistry agents, PolicyEngine policies, IApprovalProvider approvals, IToolRunner tools,
        RunOptions options, WorkflowGraph graph, WorkflowContext context)
    {
        _agents = agents;
        _policies = policies;
        _approvals = approvals;
        _options = options;
        _graph = graph;
        _ctx = context;
        _governedTools = new GovernedToolRunner(tools, this);
        _startedAt = context.Time.GetUtcNow();
        foreach (var node in graph.Nodes)
            _states[node.Id] = new NodeState();
        _ctx.Artifacts.Changed += OnArtifactChanged;
    }

    private AuditLog Audit => _ctx.Audit;
    private RunMetrics Metrics => _ctx.Metrics;

    public async Task<RunResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        Audit.Append("run.started", "engine", $"Run started for scenario '{_ctx.Scenario}' with {_graph.Nodes.Count} nodes.", data: new Dictionary<string, string>
        {
            ["graphVersion"] = _graph.Version.ToString(),
            ["autoApproveUpTo"] = _options.Autonomy.AutoApproveUpTo.ToString(),
            ["maxNodeExecutions"] = _options.MaxNodeExecutions.ToString(),
            ["maxDuration"] = _options.MaxDuration.ToString(),
        });
        PublishWorkspaceState();

        try
        {
            await ScheduleAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Finish(RunStatus.Stopped, "Cancelled by operator; stopped at a safe boundary (applied changes are atomic per change set).");
        }
        finally
        {
            _ctx.Artifacts.Changed -= OnArtifactChanged;
        }

        Audit.Append("run.completed", "engine", $"Run {_status}: {_reason}", data: new Dictionary<string, string>
        {
            ["status"] = _status.ToString(),
            ["graphVersion"] = _graph.Version.ToString(),
            ["durationMs"] = (_ctx.Time.GetUtcNow() - _startedAt).TotalMilliseconds.ToString("F0"),
        });

        lock (_stateLock)
            return new RunResult(_status, _reason, _graph, new Dictionary<string, NodeState>(_states), Metrics.ToSnapshot(_status), _ctx);
    }

    private async Task ScheduleAsync(CancellationToken cancellationToken)
    {
        var wave = 0;
        while (_status == RunStatus.Running)
        {
            if (ShouldStop(cancellationToken, out var stopReason))
            {
                Audit.Append("run.safe-stop", "engine", stopReason);
                Finish(RunStatus.Stopped, stopReason);
                return;
            }

            var ready = ReadyNodes();
            if (ready.Count == 0)
            {
                CompleteOrDeadlock();
                return;
            }

            var batch = SelectNonConflicting(ready);
            foreach (var node in batch.Where(n => _graph.Nodes.Any(x => x.ExpandedBy == n.Id)))
                RetractExpansion(node.Id, $"'{node.Id}' is re-planning; its previous expansion is retracted.");

            wave++;
            Audit.Append("wave.started", "engine", $"Wave {wave}: {string.Join(", ", batch.Select(n => n.Id))}", data: new Dictionary<string, string>
            {
                ["parallelism"] = batch.Count.ToString(),
                ["deferredByLocks"] = string.Join(",", ready.Except(batch).Select(n => n.Id)),
            });

            var outcomes = await Task.WhenAll(batch.Select(n => ExecuteNodeAsync(n, cancellationToken)));

            // Synchronisation barrier: process results in a deterministic order on the scheduler thread.
            foreach (var (node, outcome) in batch.Zip(outcomes))
            {
                if (outcome.Succeeded)
                {
                    if (outcome.Mutation is not null && !TryApplyMutation(node, outcome.Mutation))
                        return;
                    FireExternalEvents(node.Id);
                }
            }

            foreach (var (node, outcome) in batch.Zip(outcomes).Where(x => !x.Second.Succeeded))
            {
                if (!HandleFailure(node, outcome.Error ?? "unknown failure"))
                    return;
            }
        }
    }

    /// <summary>
    /// A node is ready when every ancestor — not just its direct dependencies — has settled. Otherwise a
    /// node whose parent is still "succeeded" could run against outputs that a stale grandparent is
    /// about to change (wasted work, false failures).
    /// </summary>
    private List<StageNode> ReadyNodes()
    {
        lock (_stateLock)
        {
            return _graph.Nodes
                .Where(n => _states[n.Id].Status is NodeStatus.Pending or NodeStatus.Stale)
                .Where(n => _graph.Ancestors(n.Id).All(d => _states[d].Status is NodeStatus.Succeeded or NodeStatus.Skipped))
                .ToList();
        }
    }

    /// <summary>Greedy selection so that no two nodes in a wave share a lock (e.g. the same source file).</summary>
    private static List<StageNode> SelectNonConflicting(IReadOnlyList<StageNode> ready)
    {
        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batch = new List<StageNode>();
        foreach (var node in ready)
        {
            if (node.Locks.Any(held.Contains))
                continue;
            held.UnionWith(node.Locks);
            batch.Add(node);
        }
        return batch;
    }

    private void CompleteOrDeadlock()
    {
        lock (_stateLock)
        {
            if (_states.Values.All(s => s.Status is NodeStatus.Succeeded or NodeStatus.Skipped))
            {
                Finish(RunStatus.Succeeded, "All nodes completed and all gates passed.");
                return;
            }
            var blocked = _states.Where(s => s.Value.Status != NodeStatus.Succeeded).Select(s => $"{s.Key}={s.Value.Status}");
            Finish(RunStatus.Failed, $"No runnable nodes remain: {string.Join(", ", blocked)}");
        }
    }

    private bool ShouldStop(CancellationToken cancellationToken, out string reason)
    {
        cancellationToken.ThrowIfCancellationRequested();
        reason = string.Empty;
        if (_options.StopFilePath is { } stopFile && File.Exists(stopFile))
            reason = $"Kill switch engaged ({Path.GetFileName(stopFile)} present).";
        else if (Metrics.Get(Counters.NodeExecutions) >= _options.MaxNodeExecutions)
            reason = $"Execution budget exhausted ({_options.MaxNodeExecutions} agent attempts).";
        else if (_ctx.Time.GetUtcNow() - _startedAt > _options.MaxDuration)
            reason = $"Time budget exhausted ({_options.MaxDuration}).";
        return reason.Length > 0;
    }

    private void Finish(RunStatus status, string reason)
    {
        _status = status;
        _reason = reason;
    }

    private void SetStatus(string nodeId, NodeStatus status)
    {
        lock (_stateLock)
            _states[nodeId].Status = status;
    }

    /// <summary>
    /// Dynamic re-planning trigger: any succeeded node that consumed an older version of a changed
    /// artifact becomes Stale and will re-execute. Unchanged content never invalidates (hash-based).
    /// </summary>
    private void OnArtifactChanged(Artifact current, Artifact? previous)
    {
        if (previous is null)
            return;
        List<string> invalidated;
        lock (_stateLock)
        {
            invalidated = _states
                .Where(s => s.Value.Status == NodeStatus.Succeeded && s.Value.Inputs.Any(i => i.Key == current.Key && i.Hash != current.Hash))
                .Select(s => s.Key)
                .ToList();
            foreach (var id in invalidated)
                _states[id].Status = NodeStatus.Stale;
        }
        foreach (var id in invalidated)
        {
            Metrics.Increment(Counters.StaleInvalidations);
            Audit.Append("node.stale", "engine", $"Input '{current.Key}' changed (v{previous.Version}→v{current.Version}); node will re-execute.", id,
                new Dictionary<string, string> { ["artifact"] = current.Key, ["fromHash"] = previous.Hash[..12], ["toHash"] = current.Hash[..12], ["changedBy"] = current.ProducedBy });
        }
    }

    private void FireExternalEvents(string completedNodeId)
    {
        foreach (var external in _options.ExternalEvents.Where(e => e.AfterNodeId == completedNodeId && !_firedEvents.Contains(e)))
        {
            _firedEvents.Add(external);
            Audit.Append("external.event", external.Actor, external.Reason, completedNodeId,
                new Dictionary<string, string> { ["artifact"] = external.ArtifactKey });
            _ctx.Decisions.Record(null, external.Actor, new DecisionDraft(DecisionCategory.Replan,
                $"Upstream change to '{external.ArtifactKey}'", external.Reason));
            _ctx.Artifacts.Put(external.ArtifactKey, external.Content, "text/plain", external.Actor);
        }
    }

    private bool TryApplyMutation(StageNode node, GraphMutation mutation)
    {
        try
        {
            if (mutation.AddNodes.Count > _options.MaxNodesPerMutation)
                throw new GraphValidationException($"Mutation adds {mutation.AddNodes.Count} nodes (limit {_options.MaxNodesPerMutation}).");
            var unknownAgent = mutation.AddNodes.FirstOrDefault(n => !_agents.Contains(n.Agent));
            if (unknownAgent is not null)
                throw new GraphValidationException($"Node '{unknownAgent.Id}' uses unregistered agent '{unknownAgent.Agent}'.");

            // Governance: agents cannot create unsupervised high-risk work.
            var governed = mutation with
            {
                AddNodes = mutation.AddNodes.Select(n => n.Risk >= RiskLevel.High ? n with { RequiresApproval = true } : n).ToList(),
            };
            var added = _graph.Apply(governed, node.Id);
            lock (_stateLock)
                foreach (var id in added)
                    _states[id] = new NodeState();

            Metrics.Increment(Counters.GraphMutations);
            Audit.Append("graph.mutated", $"agent:{node.Agent}", mutation.Reason, node.Id, new Dictionary<string, string>
            {
                ["graphVersion"] = _graph.Version.ToString(),
                ["addedNodes"] = string.Join(",", added),
                ["addedEdges"] = string.Join(",", mutation.AddDependencies.Select(e => $"{e.DependsOn}->{e.NodeId}")),
            });
            _ctx.Decisions.Record(node.Id, $"agent:{node.Agent}", new DecisionDraft(DecisionCategory.Plan,
                $"Expanded workflow graph to v{_graph.Version} with {added.Count} nodes", mutation.Reason), _states[node.Id].Outputs);
            return true;
        }
        catch (GraphValidationException ex)
        {
            Metrics.Increment(Counters.PolicyBlocks);
            Audit.Append("graph.mutation-rejected", "engine", ex.Message, node.Id);
            SetStatus(node.Id, NodeStatus.Failed);
            Finish(RunStatus.Failed, $"Governance rejected graph mutation from '{node.Id}': {ex.Message}");
            RollbackAllIfConfigured();
            return false;
        }
    }

    /// <summary>Retracts nodes a re-planning node created earlier, compensating their applied changes.</summary>
    private void RetractExpansion(string nodeId, string reason)
    {
        var expanded = _graph.Nodes.Where(n => n.ExpandedBy == nodeId).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var undone = Rollback(expanded, reason);
        var removed = _graph.Retract(nodeId);
        lock (_stateLock)
        {
            foreach (var id in removed)
                _states.Remove(id);
            foreach (var id in undone.Where(_states.ContainsKey))
                _states[id].Status = NodeStatus.Pending;
        }
        Metrics.Increment(Counters.Replans);
        Audit.Append("replan.retracted", "engine", reason, nodeId, new Dictionary<string, string>
        {
            ["removedNodes"] = string.Join(",", removed),
            ["graphVersion"] = _graph.Version.ToString(),
        });
        _ctx.Decisions.Record(nodeId, "engine", new DecisionDraft(DecisionCategory.Replan,
            $"Retracted {removed.Count} planned nodes and compensated their changes", reason));
    }

    /// <summary>Returns true if the run can continue (failure was remediated).</summary>
    private bool HandleFailure(StageNode node, string error)
    {
        var state = _states[node.Id];
        if (node.Remediation is { } remediation && state.RemediationCycles < remediation.MaxCycles)
        {
            var targets = _graph.Nodes.Where(n => n.Tags.Contains(remediation.TargetTag)).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            if (targets.Count > 0)
            {
                state.RemediationCycles++;
                Metrics.Increment(Counters.Remediations);
                var undone = Rollback(targets, $"Remediation cycle {state.RemediationCycles} for '{node.Id}'");
                var reset = new HashSet<string>(targets, StringComparer.Ordinal) { node.Id };
                reset.UnionWith(undone);
                foreach (var target in targets)
                    reset.UnionWith(_graph.Descendants(target));

                lock (_stateLock)
                {
                    foreach (var id in reset.Where(_states.ContainsKey))
                        _states[id].Status = NodeStatus.Pending;
                    foreach (var id in targets)
                        _states[id].Feedback.Add($"Remediation required: '{node.Id}' failed with: {error}");
                }
                Audit.Append("remediation.started", "engine",
                    $"'{node.Id}' failed; rolling back and re-running [{string.Join(", ", targets)}] with failure feedback (cycle {state.RemediationCycles}/{remediation.MaxCycles}).",
                    node.Id, new Dictionary<string, string> { ["error"] = Truncate(error, 400) });
                _ctx.Decisions.Record(node.Id, "engine", new DecisionDraft(DecisionCategory.Recovery,
                    $"Remediation cycle {state.RemediationCycles}: re-run '{remediation.TargetTag}' nodes",
                    $"Bounded remediation policy ({remediation.MaxCycles} cycles) for failure: {Truncate(error, 200)}",
                    ["halt immediately", "skip failing gate"]));
                return true;
            }
        }

        Audit.Append("run.failing", "engine", $"Unrecoverable failure in '{node.Id}': {Truncate(error, 400)}", node.Id);
        Finish(RunStatus.Failed, $"'{node.Id}' failed: {Truncate(error, 300)}");
        RollbackAllIfConfigured();
        return false;
    }

    private void RollbackAllIfConfigured()
    {
        if (!_options.RollbackOnFailure || !HasAppliedChanges)
            return;
        var undone = Rollback(_graph.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal), "Unrecoverable failure: restoring the workspace baseline.");
        if (undone.Count > 0)
            _status = RunStatus.RolledBack;
    }

    internal static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    private sealed class GovernedToolRunner(IToolRunner inner, WorkflowRun run) : IToolRunner
    {
        public async Task<ToolResult> RunAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            var workspaceRoot = run._ctx.Workspace.Root;
            var workingDirectory = Path.GetFullPath(invocation.WorkingDirectory);
            if (!workingDirectory.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
                throw new ToolPolicyViolationException($"Tool working directory '{workingDirectory}' is outside the workspace sandbox.");

            var probe = new StageNode { Id = "tool", Stage = "Tool", Agent = "tool" };
            var findings = run._policies.Evaluate(new PolicySubject(probe, null, _ => null, invocation));
            var commandLine = $"{invocation.FileName} {string.Join(' ', invocation.Arguments)}";
            if (findings.FirstOrDefault(f => f.Severity == PolicySeverity.Block) is { } block)
            {
                run.Metrics.Increment(Counters.PolicyBlocks);
                run.Audit.Append("tool.blocked", "policy", $"{block.PolicyId}: {block.Message}");
                throw new ToolPolicyViolationException(block.Message);
            }

            var result = await inner.RunAsync(invocation with { WorkingDirectory = workingDirectory }, cancellationToken);
            run.Audit.Append("tool.executed", "engine", commandLine, data: new Dictionary<string, string>
            {
                ["exitCode"] = result.ExitCode.ToString(),
                ["durationMs"] = result.Duration.TotalMilliseconds.ToString("F0"),
                ["timedOut"] = result.TimedOut.ToString(),
            });
            return result;
        }
    }
}
