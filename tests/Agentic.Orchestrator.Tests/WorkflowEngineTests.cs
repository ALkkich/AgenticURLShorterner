using Agentic.Orchestrator.Agents;
using Agentic.Orchestrator.Context;
using Agentic.Orchestrator.Engine;
using Agentic.Orchestrator.Gates;
using Agentic.Orchestrator.Governance;
using Agentic.Orchestrator.Model;
using Agentic.Orchestrator.Observability;
using Counters = Agentic.Orchestrator.Observability.RunMetrics.Counters;

namespace Agentic.Orchestrator.Tests;

public class WorkflowEngineTests : IDisposable
{
    private readonly EngineHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static AgentResult Ok(string summary = "ok") => AgentResult.Success(summary);

    [Fact]
    public async Task Independent_branches_run_in_the_same_wave_and_join_waits_for_both()
    {
        _h.Agent("noop", _ => Ok());

        var result = await _h.RunAsync(
            EngineHarness.Node("start", "noop"),
            EngineHarness.Node("left", "noop", "start"),
            EngineHarness.Node("right", "noop", "start"),
            EngineHarness.Node("join", "noop", "left", "right"));

        Assert.Equal(RunStatus.Succeeded, result.Status);
        var waves = _h.Audit.Events.Where(e => e.Type == "wave.started").Select(e => e.Message).ToList();
        Assert.Equal(["Wave 1: start", "Wave 2: left, right", "Wave 3: join"], waves);
    }

    [Fact]
    public async Task Nodes_sharing_a_lock_are_serialised()
    {
        _h.Agent("noop", _ => Ok());
        var a = EngineHarness.Node("a", "noop") with { Locks = ["src/File.cs"] };
        var b = EngineHarness.Node("b", "noop") with { Locks = ["src/File.cs"] };

        var result = await _h.RunAsync(a, b);

        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal(2, _h.Audit.Events.Count(e => e.Type == "wave.started"));
    }

    [Fact]
    public async Task Transient_failures_are_retried_within_budget_and_recovery_time_is_measured()
    {
        var flaky = _h.Agent("flaky", ctx => ctx.Attempt < 3 ? AgentResult.Transient("timeout talking to tool") : Ok());

        var result = await _h.RunAsync(EngineHarness.Node("n", "flaky"));

        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal(3, flaky.Calls);
        Assert.Equal(2, result.Metrics.Counters[Counters.Retries]);
        Assert.Single(result.Metrics.Recoveries);
        Assert.NotNull(result.Metrics.MttrMs);
    }

    [Fact]
    public async Task Exhausted_retries_switch_to_the_fallback_agent()
    {
        _h.Agent("primary", _ => AgentResult.Transient("unavailable"));
        var fallback = _h.Agent("secondary", _ => Ok("degraded but valid"));
        var node = EngineHarness.Node("n", "primary") with { Retry = new RetryPolicy(2, TimeSpan.Zero), FallbackAgent = "secondary" };

        var result = await _h.RunAsync(node);

        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal(1, fallback.Calls);
        Assert.Equal("secondary", result.Nodes["n"].AgentUsed);
        Assert.Equal(1, result.Metrics.Counters[Counters.Fallbacks]);
    }

    [Fact]
    public async Task Unrecoverable_failure_rolls_back_every_applied_change()
    {
        _h.Workspace.Write("src/App.cs", "original");
        _h.Agent("writer", _ => Ok().WithChanges(new ChangeSet("edit", [new FileChange("src/App.cs", ChangeKind.Modify, "changed", "edit")])));
        _h.Agent("broken", _ => AgentResult.Permanent("tests failed"));

        var result = await _h.RunAsync(EngineHarness.Node("impl", "writer"), EngineHarness.Node("test", "broken", "impl"));

        Assert.Equal(RunStatus.RolledBack, result.Status);
        Assert.Equal("original", _h.Workspace.Read("src/App.cs"));
    }

    [Fact]
    public async Task Failing_test_triggers_bounded_remediation_of_implementation_with_feedback()
    {
        _h.Agent("impl", ctx => Ok().WithChanges(new ChangeSet("code",
            [new FileChange("src/App.cs", ChangeKind.Create, ctx.Feedback.Count == 0 ? "bug" : "fixed", "impl")])));
        _h.Agent("tester", ctx => _h.Workspace.Read("src/App.cs") == "fixed" ? Ok() : AgentResult.Permanent("1 test failed"));
        var impl = EngineHarness.Node("impl", "impl") with { Tags = ["implementation"] };
        var test = EngineHarness.Node("test", "tester", "impl") with { Remediation = new RemediationPolicy("implementation", 2) };

        var result = await _h.RunAsync(impl, test);

        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal(1, result.Metrics.Counters[Counters.Remediations]);
        Assert.Contains("Remediation required", result.Nodes["impl"].Feedback.Single());
        Assert.Contains(result.Metrics.Recoveries, r => r.NodeId == "test");
    }

    [Fact]
    public async Task Remediation_is_bounded()
    {
        _h.Agent("impl", _ => Ok());
        _h.Agent("tester", _ => AgentResult.Permanent("still failing"));
        var impl = EngineHarness.Node("impl", "impl") with { Tags = ["implementation"] };
        var test = EngineHarness.Node("test", "tester", "impl") with { Remediation = new RemediationPolicy("implementation", 2) };

        var result = await _h.RunAsync(impl, test);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(2, result.Metrics.Counters[Counters.Remediations]);
    }

    [Fact]
    public async Task Policy_blocks_change_sets_containing_secrets()
    {
        _h.Agent("leaky", _ => Ok().WithChanges(new ChangeSet("config",
            [new FileChange("src/Config.cs", ChangeKind.Create, "var apiKey = \"sk-ant-abcdefghijklmnop1234\";", "adds key")])));

        var result = await _h.RunAsync(EngineHarness.Node("n", "leaky"));

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.False(_h.Workspace.Exists("src/Config.cs"));
        Assert.Contains("SEC-001", result.Reason);
    }

    [Fact]
    public async Task Policy_requiring_approval_routes_change_to_a_human_and_rejection_triggers_revision()
    {
        _h.Workspace.Write("src/Program.cs", "old");
        _h.Script.Add(new ScriptedResponse("n", ApprovalKind.ChangeSet, 1, false, "reviewer", "Use the options pattern instead", null));
        _h.Agent("editor", ctx => Ok().WithChanges(new ChangeSet("host",
            [new FileChange("src/Program.cs", ChangeKind.Modify, ctx.Feedback.Count == 0 ? "v1" : "v2-options", "host change")])));

        var result = await _h.RunAsync(EngineHarness.Node("n", "editor"));

        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal("v2-options", _h.Workspace.Read("src/Program.cs"));
        Assert.Equal(1, result.Nodes["n"].Revisions);
        Assert.Equal(1, result.Metrics.Counters[Counters.ApprovalsRejected]);
    }

    [Fact]
    public async Task Stage_rejection_beyond_revision_budget_fails_the_run()
    {
        _h.Agent("designer", _ => Ok());
        for (var i = 1; i <= 3; i++)
            _h.Script.Add(new ScriptedResponse("design", ApprovalKind.StageExit, i, false, "architect", "not good enough", null));
        var node = EngineHarness.Node("design", "designer") with { RequiresApproval = true, MaxRevisions = 2 };

        var result = await _h.RunAsync(node);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(2, result.Nodes["design"].Revisions);
    }

    [Fact]
    public async Task Low_risk_nodes_never_reach_humans_and_critical_nodes_are_never_auto_approved()
    {
        _h.Options = _h.Options with { Autonomy = new AutonomyPolicy(RiskLevel.High) };
        _h.Agent("noop", _ => Ok());

        var result = await _h.RunAsync(
            EngineHarness.Node("low", "noop"),
            EngineHarness.Node("high", "noop", "low") with { Risk = RiskLevel.High },
            EngineHarness.Node("promote", "noop", "high") with { Risk = RiskLevel.Critical });

        // 'high' is auto-approved by policy; 'promote' goes to the (unattended) human, which refuses Critical.
        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(1, result.Metrics.Counters[Counters.ApprovalsAuto]);
        Assert.Equal(NodeStatus.Failed, result.Nodes["promote"].Status);
    }

    [Fact]
    public async Task Upstream_artifact_change_invalidates_and_re_executes_only_affected_nodes()
    {
        var spec = _h.Agent("spec", ctx => Ok().WithText("spec", ctx.ReadText(WellKnownArtifacts.RequirementsRaw)!.ToUpperInvariant()));
        var design = _h.Agent("design", ctx => Ok().WithText("design", "design for " + ctx.ReadText("spec")));
        var other = _h.Agent("other", _ => Ok());
        _h.Options = _h.Options with
        {
            ExternalEvents = [new ExternalEvent("design", WellKnownArtifacts.RequirementsRaw, "v2 requirement", "human:po", "Scope changed")],
        };
        var graph = new WorkflowGraph([EngineHarness.Node("spec", "spec"), EngineHarness.Node("design", "design", "spec"), EngineHarness.Node("other", "other", "design")]);
        var seed = SeedRequirement(graph, "v1 requirement");

        var result = await seed;

        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal(2, spec.Calls);
        Assert.Equal(2, design.Calls);
        Assert.Equal(1, other.Calls); // 'other' consumed no changed artifact
        Assert.Equal("design for V2 REQUIREMENT", result.Context.Artifacts.Latest("design")!.Content);
        Assert.Equal(2, result.Context.Artifacts.Latest("design")!.Version);
    }

    [Fact]
    public async Task Pending_descendants_wait_while_a_stale_ancestor_re_executes()
    {
        var specVersionSeenByVerify = new List<string>();
        _h.Agent("spec", ctx => Ok().WithText("spec", ctx.ReadText(WellKnownArtifacts.RequirementsRaw)!));
        _h.Agent("build", ctx => Ok().WithText("binary", "built from " + ctx.ReadText("spec")));
        _h.Agent("verify", ctx => { specVersionSeenByVerify.Add(ctx.ReadText("spec")!); ctx.ReadText("binary"); return Ok(); });
        _h.Options = _h.Options with
        {
            ExternalEvents = [new ExternalEvent("build", WellKnownArtifacts.RequirementsRaw, "v2", "human:po", "changed")],
        };
        var graph = new WorkflowGraph([EngineHarness.Node("spec", "spec"), EngineHarness.Node("build", "build", "spec"), EngineHarness.Node("verify", "verify", "build")]);

        var result = await SeedRequirement(graph, "v1");

        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal(["v2"], specVersionSeenByVerify); // never ran against the half-updated state
    }

    [Fact]
    public async Task Planner_mutation_expands_graph_and_is_retracted_and_compensated_on_replan()
    {
        var planCalls = 0;
        _h.Agent("planner", ctx =>
        {
            planCalls++;
            var version = ctx.ReadText(WellKnownArtifacts.RequirementsRaw)!;
            return Ok().WithText("plan", version).WithMutation(new GraphMutation(
                [EngineHarness.Node($"task-{version}", "worker", "plan") with { Tags = ["implementation"] }],
                [("verify", $"task-{version}")], "expand tasks"));
        });
        _h.Agent("worker", ctx => Ok().WithChanges(new ChangeSet("work",
            [new FileChange($"src/{ctx.Node.Id}.cs", ChangeKind.Create, "class X {}", "task output")])));
        _h.Agent("verify", ctx => { ctx.ReadText(WellKnownArtifacts.WorkspaceState); return Ok(); });
        _h.Options = _h.Options with
        {
            ExternalEvents = [new ExternalEvent("verify", WellKnownArtifacts.RequirementsRaw, "v2", "human:po", "Requirement changed after build")],
        };
        var graph = new WorkflowGraph([EngineHarness.Node("plan", "planner"), EngineHarness.Node("verify", "verify", "plan")]);

        var result = await SeedRequirement(graph, "v1");

        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal(2, planCalls);
        Assert.False(result.Graph.Contains("task-v1"));
        Assert.True(result.Graph.Contains("task-v2"));
        Assert.False(_h.Workspace.Exists("src/task-v1.cs")); // compensated
        Assert.True(_h.Workspace.Exists("src/task-v2.cs"));
        Assert.Contains("replan.retracted", _h.Audit.Events.Select(e => e.Type));
    }

    [Fact]
    public async Task Mutation_that_introduces_a_cycle_is_rejected_by_governance()
    {
        _h.Agent("noop", _ => Ok());
        _h.Agent("rogue", _ => Ok().WithMutation(new GraphMutation(
            [EngineHarness.Node("x", "noop", "after")], [("after", "x")], "creates a cycle")));

        var result = await _h.RunAsync(EngineHarness.Node("plan", "rogue"), EngineHarness.Node("after", "noop", "plan"));

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Contains("cycle", result.Reason);
    }

    [Fact]
    public async Task Agent_created_high_risk_nodes_are_forced_behind_a_human_checkpoint()
    {
        _h.Agent("noop", _ => Ok());
        _h.Agent("planner", _ => Ok().WithMutation(new GraphMutation(
            [EngineHarness.Node("deploy", "noop", "plan") with { Risk = RiskLevel.High }], [], "adds deploy")));

        var result = await _h.RunAsync(EngineHarness.Node("plan", "planner"));

        Assert.True(result.Graph["deploy"].RequiresApproval);
        Assert.Equal(1, result.Metrics.Counters[Counters.ApprovalsHuman]);
    }

    [Fact]
    public async Task Execution_budget_triggers_safe_stop()
    {
        _h.Options = _h.Options with { MaxNodeExecutions = 2 };
        _h.Agent("noop", _ => Ok());

        var result = await _h.RunAsync(EngineHarness.Node("a", "noop"), EngineHarness.Node("b", "noop", "a"), EngineHarness.Node("c", "noop", "b"));

        Assert.Equal(RunStatus.Stopped, result.Status);
        Assert.Equal(NodeStatus.Pending, result.Nodes["c"].Status);
    }

    [Fact]
    public async Task Kill_switch_file_stops_the_run_before_the_next_wave()
    {
        var stopFile = Path.Combine(_h.Workspace.Root, "..", "STOP");
        _h.Options = _h.Options with { StopFilePath = stopFile };
        _h.Agent("noop", _ => Ok());
        _h.Agent("stopper", _ => { File.WriteAllText(stopFile, "stop"); return Ok(); });

        var result = await _h.RunAsync(EngineHarness.Node("a", "stopper"), EngineHarness.Node("b", "noop", "a"));

        Assert.Equal(RunStatus.Stopped, result.Status);
        Assert.Equal(NodeStatus.Succeeded, result.Nodes["a"].Status);
        Assert.Equal(NodeStatus.Pending, result.Nodes["b"].Status);
    }

    [Fact]
    public async Task Entry_gate_failure_prevents_agent_execution()
    {
        var agent = _h.Agent("noop", _ => Ok());
        var node = EngineHarness.Node("n", "noop") with { EntryGates = [Gate.RequiresArtifacts("missing.artifact")] };

        var result = await _h.RunAsync(node);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(0, agent.Calls);
    }

    [Fact]
    public async Task Clarification_answers_are_fed_back_and_recorded_as_decisions()
    {
        _h.Script.Add(new ScriptedResponse("req", ApprovalKind.Clarification, 1, true, "po", null, new() { ["Q1"] = "100 rps" }));
        _h.Agent("analyst", ctx => ctx.Answers.Count == 0
            ? AgentResult.NeedsInput("ambiguous", [new ClarificationQuestion("Q1", "Target load?", "50 rps"), new ClarificationQuestion("Q2", "Auth?", "none")])
            : Ok().WithText("spec", $"{ctx.Answers["Q1"]}/{ctx.Answers["Q2"]}"));

        var result = await _h.RunAsync(EngineHarness.Node("req", "analyst"));

        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal("100 rps/none", result.Context.Artifacts.Latest("spec")!.Content);
        var decisions = result.Context.Decisions.All;
        Assert.Contains(decisions, d => d.Category == DecisionCategory.Scope && d.Summary == "Q1: 100 rps");
        Assert.Contains(decisions, d => d.Category == DecisionCategory.Assumption && d.Summary == "Q2: none");
    }

    [Fact]
    public async Task Artifacts_record_lineage_of_the_inputs_they_were_derived_from()
    {
        _h.Agent("a", _ => Ok().WithText("first", "1"));
        _h.Agent("b", ctx => Ok().WithText("second", ctx.ReadText("first") + "2"));

        var result = await _h.RunAsync(EngineHarness.Node("a", "a"), EngineHarness.Node("b", "b", "a"));

        var second = result.Context.Artifacts.Latest("second")!;
        Assert.Equal("b/b", second.ProducedBy);
        Assert.Equal("first", Assert.Single(second.DerivedFrom).Key);
    }

    [Fact]
    public async Task Audit_log_is_hash_chained_and_tamper_evident()
    {
        _h.Agent("noop", _ => Ok());
        await _h.RunAsync(EngineHarness.Node("a", "noop"));
        var events = _h.Audit.Events.ToList();

        Assert.True(AuditLog.Verify(events).Valid);

        events[2] = events[2] with { Message = "edited after the fact" };
        var (valid, brokenAt) = AuditLog.Verify(events);
        Assert.False(valid);
        Assert.Equal(3, brokenAt);
    }

    /// <summary>The requester's text is the root artifact of every run.</summary>
    private Task<RunResult> SeedRequirement(WorkflowGraph graph, string text) =>
        _h.RunAsync(graph, seed: c => c.Artifacts.Put(WellKnownArtifacts.RequirementsRaw, text, "text/plain", "human:requester"));
}
