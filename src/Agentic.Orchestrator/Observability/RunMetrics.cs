using Agentic.Orchestrator.Context;
using Agentic.Orchestrator.Model;

namespace Agentic.Orchestrator.Observability;

public sealed record StageTiming(string NodeId, string Stage, string Agent, int Attempts, double DurationMs, NodeStatus Status);

public sealed record RecoveryEpisode(string NodeId, DateTimeOffset FailedAt, DateTimeOffset RecoveredAt, string Mechanism)
{
    public double TimeToRecoverMs => (RecoveredAt - FailedAt).TotalMilliseconds;
}

/// <summary>Reliability metrics for one run. Thread-safe counters; snapshot via <see cref="ToSnapshot"/>.</summary>
public sealed class RunMetrics(string runId, string scenario, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTimeOffset At, string Mechanism)> _openFailures = new(StringComparer.Ordinal);
    private readonly List<RecoveryEpisode> _recoveries = [];
    private readonly List<StageTiming> _timings = [];
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);
    private readonly DateTimeOffset _startedAt = (time ?? TimeProvider.System).GetUtcNow();

    public void Increment(string counter, int by = 1)
    {
        lock (_gate)
            _counters[counter] = _counters.GetValueOrDefault(counter) + by;
    }

    public int Get(string counter)
    {
        lock (_gate)
            return _counters.GetValueOrDefault(counter);
    }

    /// <summary>Opens a failure episode for the node (first failure wins; MTTR measures from there).</summary>
    public void FailureObserved(string nodeId, string mechanism)
    {
        lock (_gate)
            _openFailures.TryAdd(nodeId, (_time.GetUtcNow(), mechanism));
    }

    public void SuccessObserved(string nodeId)
    {
        lock (_gate)
        {
            if (_openFailures.Remove(nodeId, out var open))
                _recoveries.Add(new RecoveryEpisode(nodeId, open.At, _time.GetUtcNow(), open.Mechanism));
        }
    }

    public void RecordTiming(StageTiming timing)
    {
        lock (_gate)
            _timings.Add(timing);
    }

    public MetricsSnapshot ToSnapshot(RunStatus status)
    {
        lock (_gate)
        {
            var executions = _counters.GetValueOrDefault(Counters.NodeExecutions);
            var succeeded = _counters.GetValueOrDefault(Counters.NodeSucceeded);
            return new MetricsSnapshot(
                runId,
                scenario,
                status,
                _startedAt,
                (_time.GetUtcNow() - _startedAt).TotalMilliseconds,
                executions,
                executions == 0 ? 0 : Math.Round((double)succeeded / executions, 3),
                new SortedDictionary<string, int>(_counters, StringComparer.Ordinal),
                _recoveries.Count == 0 ? null : Math.Round(_recoveries.Average(r => r.TimeToRecoverMs), 1),
                _recoveries.ToList(),
                _openFailures.Keys.Order(StringComparer.Ordinal).ToList(),
                _timings.ToList());
        }
    }

    public static class Counters
    {
        public const string NodeExecutions = "node.executions";
        public const string NodeSucceeded = "node.succeeded";
        public const string NodeFailed = "node.failed";
        public const string Retries = "retries";
        public const string Fallbacks = "fallbacks";
        public const string Rollbacks = "rollbacks";
        public const string Remediations = "remediations";
        public const string Revisions = "revisions";
        public const string Replans = "replans";
        public const string StaleInvalidations = "stale.invalidations";
        public const string GraphMutations = "graph.mutations";
        public const string ApprovalsHuman = "approvals.human";
        public const string ApprovalsAuto = "approvals.auto";
        public const string ApprovalsRejected = "approvals.rejected";
        public const string PolicyFindings = "policy.findings";
        public const string PolicyBlocks = "policy.blocks";
        public const string GateFailures = "gate.failures";
    }
}

/// <param name="NodeSuccessRate">Successful node executions / all node executions (attempt-level reliability).</param>
/// <param name="MttrMs">Mean time from a node's first failure to its eventual success.</param>
public sealed record MetricsSnapshot(
    string RunId,
    string Scenario,
    RunStatus Status,
    DateTimeOffset StartedAt,
    double EndToEndLatencyMs,
    int NodeExecutions,
    double NodeSuccessRate,
    IReadOnlyDictionary<string, int> Counters,
    double? MttrMs,
    IReadOnlyList<RecoveryEpisode> Recoveries,
    IReadOnlyList<string> UnrecoveredFailures,
    IReadOnlyList<StageTiming> Stages);

/// <summary>Cross-run reliability view from the metrics history file (one snapshot per line).</summary>
public sealed record ReliabilityReport(
    int Runs,
    double RunSuccessRate,
    double RetriesPerRun,
    double RollbacksPerRun,
    double RemediationsPerRun,
    double? MeanMttrMs,
    double P50LatencyMs,
    double P95LatencyMs)
{
    public static ReliabilityReport From(IReadOnlyList<MetricsSnapshot> runs)
    {
        if (runs.Count == 0)
            return new ReliabilityReport(0, 0, 0, 0, 0, null, 0, 0);
        var latencies = runs.Select(r => r.EndToEndLatencyMs).Order().ToList();
        var mttrs = runs.Where(r => r.MttrMs is not null).Select(r => r.MttrMs!.Value).ToList();
        return new ReliabilityReport(
            runs.Count,
            Math.Round(runs.Count(r => r.Status == RunStatus.Succeeded) / (double)runs.Count, 3),
            Math.Round(runs.Average(r => r.Counters.GetValueOrDefault(RunMetrics.Counters.Retries)), 2),
            Math.Round(runs.Average(r => r.Counters.GetValueOrDefault(RunMetrics.Counters.Rollbacks)), 2),
            Math.Round(runs.Average(r => r.Counters.GetValueOrDefault(RunMetrics.Counters.Remediations)), 2),
            mttrs.Count == 0 ? null : Math.Round(mttrs.Average(), 1),
            Percentile(latencies, 0.50),
            Percentile(latencies, 0.95));
    }

    public static IReadOnlyList<MetricsSnapshot> ReadHistory(string path) =>
        File.Exists(path) ? File.ReadLines(path).Where(l => l.Length > 0).Select(Json.Deserialize<MetricsSnapshot>).ToList() : [];

    private static double Percentile(IReadOnlyList<double> sorted, double p) =>
        Math.Round(sorted[(int)Math.Clamp(Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)], 1);
}
