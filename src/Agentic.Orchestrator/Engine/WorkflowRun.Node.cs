using System.Diagnostics;
using Agentic.Orchestrator.Agents;
using Agentic.Orchestrator.Context;
using Agentic.Orchestrator.Gates;
using Agentic.Orchestrator.Governance;
using Agentic.Orchestrator.Model;
using Agentic.Orchestrator.Observability;
using Agentic.Orchestrator.Tools;
using Counters = Agentic.Orchestrator.Observability.RunMetrics.Counters;

namespace Agentic.Orchestrator.Engine;

internal sealed record NodeOutcome(bool Succeeded, string? Error, GraphMutation? Mutation)
{
    public static NodeOutcome Ok(GraphMutation? mutation) => new(true, null, mutation);
    public static NodeOutcome Fail(string error) => new(false, error, null);
}

internal sealed partial class WorkflowRun
{
    private readonly SemaphoreSlim _humanLock = new(1, 1);
    private readonly Dictionary<(string NodeId, string Kind), int> _checkpointCounts = [];

    /// <summary>
    /// Node lifecycle: entry gates → agent (bounded retries, timeout) → fallback agent → clarification
    /// loop → commit artifacts with lineage → change control → exit gates → human/policy checkpoint
    /// (rejection loops back with feedback, bounded by MaxRevisions).
    /// </summary>
    private async Task<NodeOutcome> ExecuteNodeAsync(StageNode node, CancellationToken cancellationToken)
    {
        var state = _states[node.Id];
        var stopwatch = Stopwatch.StartNew();
        lock (_stateLock)
        {
            state.Status = NodeStatus.Running;
            state.StartedAt = _ctx.Time.GetUtcNow();
            state.Executions++;
        }
        Audit.Append("node.started", "engine", $"{node.Stage}: {node.Description}".TrimEnd(' ', ':'), node.Id, new Dictionary<string, string>
        {
            ["agent"] = node.Agent,
            ["risk"] = node.Risk.ToString(),
            ["execution"] = state.Executions.ToString(),
            ["graphVersion"] = _graph.Version.ToString(),
        });

        var outcome = await RunLifecycleAsync(node, state, cancellationToken);

        lock (_stateLock)
        {
            state.CompletedAt = _ctx.Time.GetUtcNow();
            state.LastError = outcome.Error;
            // An input may have changed while this node was running; if so it is already stale.
            var staleInputs = outcome.Succeeded && state.Inputs.Any(i => _ctx.Artifacts.Latest(i.Key)?.Hash != i.Hash);
            state.Status = outcome.Succeeded ? (staleInputs ? NodeStatus.Stale : NodeStatus.Succeeded) : NodeStatus.Failed;
        }

        if (outcome.Succeeded)
            Metrics.SuccessObserved(node.Id);
        else
            Metrics.FailureObserved(node.Id, "node-failure");
        Metrics.RecordTiming(new StageTiming(node.Id, node.Stage, state.AgentUsed ?? node.Agent, state.Executions, stopwatch.Elapsed.TotalMilliseconds, state.Status));
        Audit.Append(outcome.Succeeded ? "node.succeeded" : "node.failed", "engine",
            outcome.Succeeded ? state.Summary ?? "ok" : Truncate(outcome.Error ?? "failed", 500), node.Id,
            new Dictionary<string, string> { ["durationMs"] = stopwatch.Elapsed.TotalMilliseconds.ToString("F0") });
        return outcome;
    }

    private async Task<NodeOutcome> RunLifecycleAsync(StageNode node, NodeState state, CancellationToken cancellationToken)
    {
        var gateContext = new GateContext(node, _ctx.Artifacts, _ctx.Settings);
        if (EvaluateGates(node, node.EntryGates, gateContext, "entry") is { } entryFailure)
            return NodeOutcome.Fail(entryFailure);

        var clarificationRounds = 0;
        while (true)
        {
            var (result, agentContext, error) = await RunAgentWithRetriesAsync(node, node.Agent, state, cancellationToken);
            if (result is null && node.FallbackAgent is { } fallback)
            {
                Metrics.Increment(Counters.Fallbacks);
                Audit.Append("node.fallback", "engine", $"Primary agent '{node.Agent}' failed ({Truncate(error ?? "", 200)}); switching to fallback '{fallback}'.", node.Id);
                _ctx.Decisions.Record(node.Id, "engine", new DecisionDraft(DecisionCategory.Recovery,
                    $"Fallback from '{node.Agent}' to '{fallback}'", error ?? "primary agent failed"));
                (result, agentContext, error) = await RunAgentWithRetriesAsync(node, fallback, state, cancellationToken);
            }
            if (result is null)
                return NodeOutcome.Fail(error ?? "agent failed");

            if (result.Outcome == AgentOutcome.NeedsInput)
            {
                if (++clarificationRounds > _options.MaxClarificationRounds)
                    return NodeOutcome.Fail("Clarification limit reached without a resolvable specification.");
                await ClarifyAsync(node, state, result, agentContext!, cancellationToken);
                continue;
            }

            var outputs = Commit(node, result, agentContext!);
            lock (_stateLock)
            {
                state.Inputs = agentContext!.Inputs;
                state.Outputs = outputs;
                state.Summary = result.Summary;
            }

            if (result.Changes is { IsEmpty: false } changes)
            {
                var change = await ProposeChangesAsync(node, changes, cancellationToken);
                if (change.Blocked)
                    return NodeOutcome.Fail($"Change set blocked by policy: {change.Reason}");
                if (change.Rejected)
                {
                    if (!TryScheduleRevision(node, state, $"Change set rejected: {change.Reason}"))
                        return NodeOutcome.Fail($"Change set rejected and revision budget exhausted: {change.Reason}");
                    continue;
                }
            }

            if (EvaluateGates(node, node.ExitGates, gateContext, "exit") is { } exitFailure)
                return NodeOutcome.Fail(exitFailure);

            if (node.RequiresApproval || node.Risk >= RiskLevel.Medium)
            {
                var evidence = outputs.Select(o => $"artifact {o.Key} v{o.Version} ({o.Hash[..12]})").ToList();
                var decision = await CheckpointAsync(node, ApprovalKind.StageExit, $"Approve output of '{node.Id}'", node.Risk,
                    result.Summary, evidence, [], cancellationToken);
                if (!decision.Approved)
                {
                    Rollback(new HashSet<string> { node.Id }, $"Reviewer rejected '{node.Id}'");
                    if (!TryScheduleRevision(node, state, decision.Comment ?? "Rejected without comment."))
                        return NodeOutcome.Fail($"Rejected by {decision.Approver} and revision budget exhausted: {decision.Comment}");
                    continue;
                }
            }

            return NodeOutcome.Ok(result.Mutation);
        }
    }

    private bool TryScheduleRevision(StageNode node, NodeState state, string feedback)
    {
        if (state.Revisions >= node.MaxRevisions)
            return false;
        lock (_stateLock)
        {
            state.Revisions++;
            state.Feedback.Add(feedback);
        }
        Metrics.Increment(Counters.Revisions);
        Metrics.FailureObserved(node.Id, "revision");
        Audit.Append("node.revision", "engine", $"Revision {state.Revisions}/{node.MaxRevisions}: {Truncate(feedback, 300)}", node.Id);
        return true;
    }

    private string? EvaluateGates(StageNode node, IReadOnlyList<IGate> gates, GateContext context, string kind)
    {
        foreach (var gate in gates)
        {
            GateResult result;
            try
            {
                result = gate.Evaluate(context);
            }
            catch (Exception ex)
            {
                result = GateResult.Fail($"gate threw {ex.GetType().Name}: {ex.Message}");
            }
            Audit.Append($"gate.{kind}", "engine", $"{gate.Name}: {(result.Passed ? "PASS" : "FAIL")} — {result.Reason}", node.Id,
                new Dictionary<string, string> { ["passed"] = result.Passed.ToString() });
            if (!result.Passed)
            {
                Metrics.Increment(Counters.GateFailures);
                return $"{kind} gate '{gate.Name}' failed: {result.Reason}";
            }
        }
        return null;
    }

    private async Task<(AgentResult? Result, AgentContext? Context, string? Error)> RunAgentWithRetriesAsync(
        StageNode node, string agentName, NodeState state, CancellationToken cancellationToken)
    {
        var agent = _agents.Find(agentName);
        if (agent is null)
            return (null, null, $"Agent '{agentName}' is not registered.");

        string? lastError = null;
        for (var attempt = 1; attempt <= node.Retry.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<string> feedback;
            Dictionary<string, string> answers;
            lock (_stateLock)
            {
                feedback = state.Feedback.ToList();
                answers = new Dictionary<string, string>(state.Answers);
                state.AgentUsed = agentName;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(node.Timeout);
            var context = new AgentContext(_ctx, node, agentName, attempt, feedback, answers, _governedTools, timeout.Token);
            Metrics.Increment(Counters.NodeExecutions);
            Audit.Append("agent.attempt", $"agent:{agentName}", $"Attempt {attempt}/{node.Retry.MaxAttempts}", node.Id);

            AgentResult result;
            try
            {
                result = await agent.ExecuteAsync(context).WaitAsync(node.Timeout, cancellationToken);
            }
            catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                result = AgentResult.Transient($"Timed out after {node.Timeout}.");
            }
            catch (Exception ex) when (ex is ToolPolicyViolationException or UnauthorizedAccessException)
            {
                result = AgentResult.Permanent($"Guardrail violation: {ex.Message}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = AgentResult.Transient($"{ex.GetType().Name}: {ex.Message}");
            }

            if (result.Outcome is AgentOutcome.Success or AgentOutcome.NeedsInput)
            {
                Metrics.Increment(Counters.NodeSucceeded);
                return (result, context, null);
            }

            Metrics.Increment(Counters.NodeFailed);
            Metrics.FailureObserved(node.Id, result.Outcome == AgentOutcome.TransientFailure ? "retry" : "fallback");
            lastError = result.Summary;
            Audit.Append("agent.failed", $"agent:{agentName}", $"{result.Outcome}: {Truncate(result.Summary, 500)}", node.Id);

            if (result.Outcome == AgentOutcome.PermanentFailure)
                return (null, null, lastError);
            if (attempt < node.Retry.MaxAttempts)
            {
                Metrics.Increment(Counters.Retries);
                var delay = node.Retry.BackoffFor(attempt) * _options.BackoffScale;
                Audit.Append("agent.retry", "engine", $"Transient failure; retrying in {delay.TotalMilliseconds:F0} ms.", node.Id);
                await Task.Delay(delay, cancellationToken);
            }
        }
        return (null, null, $"Retries exhausted ({node.Retry.MaxAttempts} attempts): {lastError}");
    }

    private IReadOnlyList<ArtifactRef> Commit(StageNode node, AgentResult result, AgentContext agentContext)
    {
        var producer = $"{node.Id}/{agentContext.AgentName}";
        var inputs = agentContext.Inputs;
        var outputs = result.Artifacts
            .Select(a => _ctx.Artifacts.Put(a.Key, a.Content, a.ContentType, producer, inputs).Ref)
            .ToList();
        foreach (var draft in result.Decisions)
            _ctx.Decisions.Record(node.Id, $"agent:{agentContext.AgentName}", draft, inputs);
        foreach (var output in outputs)
            Audit.Append("artifact.committed", $"agent:{agentContext.AgentName}", $"{output.Key} v{output.Version}", node.Id,
                new Dictionary<string, string> { ["hash"] = output.Hash[..12], ["derivedFrom"] = string.Join(",", inputs.Select(i => $"{i.Key}@v{i.Version}")) });
        return outputs;
    }

    private async Task ClarifyAsync(StageNode node, NodeState state, AgentResult result, AgentContext agentContext, CancellationToken cancellationToken)
    {
        var evidence = result.Questions.Select(q => $"{q.Id}: {q.Question} (default: {q.DefaultAnswer})").ToList();
        var decision = await CheckpointAsync(node, ApprovalKind.Clarification, "Clarify ambiguous requirements", RiskLevel.Medium,
            result.Summary, evidence, result.Questions, cancellationToken);
        var answers = decision.Answers ?? result.Questions.ToDictionary(q => q.Id, q => q.DefaultAnswer);

        lock (_stateLock)
            foreach (var (id, answer) in answers)
                state.Answers[id] = answer;

        _ctx.Artifacts.PutJson($"clarifications.{node.Id}", answers, decision.Approver, agentContext.Inputs);
        foreach (var question in result.Questions)
        {
            var answer = answers.GetValueOrDefault(question.Id, question.DefaultAnswer);
            var usedDefault = answer == question.DefaultAnswer;
            _ctx.Decisions.Record(node.Id, decision.Approver, new DecisionDraft(
                usedDefault ? DecisionCategory.Assumption : DecisionCategory.Scope,
                $"{question.Id}: {answer}",
                usedDefault ? $"Proposed default accepted for: {question.Question}" : $"Human answer to: {question.Question}"));
        }
    }

    private async Task<ApprovalDecision> CheckpointAsync(StageNode node, string kind, string title, RiskLevel risk, string summary,
        IReadOnlyList<string> evidence, IReadOnlyList<ClarificationQuestion> questions, CancellationToken cancellationToken)
    {
        int occurrence;
        lock (_checkpointCounts)
            occurrence = _checkpointCounts[(node.Id, kind)] = _checkpointCounts.GetValueOrDefault((node.Id, kind)) + 1;

        var request = new ApprovalRequest(_ctx.RunId, node.Id, kind, title, risk, summary, evidence, questions) { Occurrence = occurrence };
        Audit.Append("approval.requested", "engine", $"{title} [{kind}, risk {risk}]", node.Id);

        ApprovalDecision decision;
        if (_options.Autonomy.CanAutoApprove(request, node.RequiresApproval))
        {
            decision = new ApprovalDecision(true, "policy:autonomy", $"Risk {risk} is within the autonomous limit ({_options.Autonomy.AutoApproveUpTo}).");
            Metrics.Increment(Counters.ApprovalsAuto);
        }
        else
        {
            await _humanLock.WaitAsync(cancellationToken);
            try
            {
                decision = await _approvals.RequestAsync(request, cancellationToken);
            }
            finally
            {
                _humanLock.Release();
            }
            Metrics.Increment(Counters.ApprovalsHuman);
        }

        if (!decision.Approved)
            Metrics.Increment(Counters.ApprovalsRejected);
        var note = decision.Comment
            ?? (decision.Answers is { Count: > 0 } answers ? "answers: " + string.Join("; ", answers.Select(a => $"{a.Key}={a.Value}")) : "");
        Audit.Append("approval.decided", decision.Approver, $"{(decision.Approved ? "APPROVED" : "REJECTED")}: {note}", node.Id,
            new Dictionary<string, string> { ["kind"] = kind, ["occurrence"] = occurrence.ToString(), ["approved"] = decision.Approved.ToString() });
        if (kind != ApprovalKind.Clarification)
            _ctx.Decisions.Record(node.Id, decision.Approver, new DecisionDraft(DecisionCategory.Approval,
                $"{(decision.Approved ? "Approved" : "Rejected")}: {title}", decision.Comment ?? string.Empty));
        return decision;
    }
}
