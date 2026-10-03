using Agentic.Agents.Workflow;
using Agentic.Cli;
using Agentic.Orchestrator.Governance;
using Agentic.Orchestrator.Model;
using Agentic.Orchestrator.Observability;

var repoRoot = RepoLocator.Find();
if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
    return Usage();

switch (args[0])
{
    case "run":
        return await RunAsync(args.Skip(1).ToArray());
    case "scenarios":
        foreach (var file in Directory.GetFiles(Path.Combine(repoRoot, "scenarios"), "*.json").Order())
        {
            var s = ScenarioDefinition.Load(file);
            Console.WriteLine($"{s.Name,-12} {s.Kind,-11} {s.Title}");
        }
        return 0;
    case "metrics":
        var history = ReliabilityReport.ReadHistory(Path.Combine(repoRoot, ".runs", "metrics-history.jsonl"));
        var report = ReliabilityReport.From(history);
        Console.WriteLine($"Runs: {report.Runs}   run success rate: {report.RunSuccessRate:P0}");
        Console.WriteLine($"Retries/run: {report.RetriesPerRun}   rollbacks/run: {report.RollbacksPerRun}   remediations/run: {report.RemediationsPerRun}");
        Console.WriteLine($"Mean MTTR: {(report.MeanMttrMs is null ? "n/a" : $"{report.MeanMttrMs / 1000:F1}s")}   latency p50: {report.P50LatencyMs / 1000:F1}s   p95: {report.P95LatencyMs / 1000:F1}s");
        foreach (var run in history.TakeLast(10))
            Console.WriteLine($"  {run.RunId,-34} {run.Status,-10} {run.EndToEndLatencyMs / 1000,6:F1}s  retries={run.Counters.GetValueOrDefault("retries")} rollbacks={run.Counters.GetValueOrDefault("rollbacks")}");
        return 0;
    case "verify-audit":
        var runDir = args.Length > 1 ? Path.GetFullPath(args[1]) : LatestRun(repoRoot);
        var events = AuditLog.ReadFile(Path.Combine(runDir, "audit.jsonl"));
        var (valid, brokenAt) = AuditLog.Verify(events);
        Console.WriteLine(valid ? $"Audit chain intact: {events.Count} events ({runDir})" : $"AUDIT CHAIN BROKEN at event #{brokenAt}");
        return valid ? 0 : 2;
    default:
        return Usage();
}

async Task<int> RunAsync(string[] runArgs)
{
    if (runArgs.Length == 0)
        return Usage();
    var path = File.Exists(runArgs[0]) ? runArgs[0] : Path.Combine(repoRoot, "scenarios", $"{runArgs[0]}.json");
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"Scenario not found: {runArgs[0]}");
        return 1;
    }
    var scenario = ScenarioDefinition.Load(path);
    var interactive = runArgs.Contains("--interactive");
    var autonomy = Option(runArgs, "--autonomy") is { } a ? Enum.Parse<RiskLevel>(a, ignoreCase: true) : RiskLevel.Low;
    var options = new ScenarioRunOptions
    {
        AutoApproveUpTo = autonomy,
        InjectFaults = !runArgs.Contains("--no-faults"),
        MaxDuration = TimeSpan.FromMinutes(int.Parse(Option(runArgs, "--max-minutes") ?? "30")),
    };
    IApprovalProvider human = interactive ? new ConsoleApprovalProvider() : new UnattendedApprovalProvider(RiskLevel.High);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Console.WriteLine("\n[safe-stop] Cancellation requested; finishing the current wave…");
        cts.Cancel();
    };

    Console.WriteLine($"▶ {scenario.Title}  ({scenario.Kind}; autonomy ≤ {autonomy}; approvals: {(interactive ? "interactive" : "scripted/unattended")}; faults: {(options.InjectFaults ? "on" : "off")})");
    var outcome = await ScenarioRunner.RunAsync(scenario, repoRoot, options, human, ConsoleProgress.Print, cts.Token);
    var m = outcome.Result.Metrics;
    Console.WriteLine();
    Console.WriteLine($"■ {outcome.Result.Status}: {outcome.Result.Reason}");
    Console.WriteLine($"  latency {m.EndToEndLatencyMs / 1000:F1}s | attempts {m.NodeExecutions} | success rate {m.NodeSuccessRate:P0} | MTTR {(m.MttrMs is null ? "n/a" : $"{m.MttrMs / 1000:F1}s")} | {string.Join(" ", m.Counters.Where(c => c.Key is "retries" or "fallbacks" or "rollbacks" or "remediations" or "replans" or "revisions").Select(c => $"{c.Key}={c.Value}"))}");
    Console.WriteLine($"  summary:   {outcome.ReportPath}");
    Console.WriteLine($"  workspace: {Path.Combine(outcome.RunDirectory, "workspace")}");
    Console.WriteLine($"  audit:     {Path.Combine(outcome.RunDirectory, "audit.jsonl")}");
    return outcome.Result.Status == RunStatus.Succeeded ? 0 : 3;
}

static string? Option(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string LatestRun(string repoRoot) =>
    new DirectoryInfo(Path.Combine(repoRoot, ".runs")).GetDirectories().OrderByDescending(d => d.CreationTimeUtc).First().FullName;

static int Usage()
{
    Console.WriteLine("""
        Agentic SDLC orchestrator — URL shortener

        Usage:
          agentic scenarios                         List scenarios
          agentic run <scenario|file.json> [options]
              --interactive                         Ask a human (console) for approvals and clarifications
              --autonomy low|medium|high            Highest risk an agent may self-approve (default: low)
              --no-faults                           Disable the scenario's fault injection
              --max-minutes N                       Safe-stop time budget (default: 30)
          agentic metrics                           Cross-run reliability metrics
          agentic verify-audit [runDir]             Verify the hash chain of a run's audit log

        Safe stop: Ctrl+C, or create a file named STOP in the run directory.
        """);
    return 1;
}
