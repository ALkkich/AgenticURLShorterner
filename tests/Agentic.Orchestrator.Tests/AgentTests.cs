using Agentic.Agents.Agents;
using Agentic.Agents.Models;
using Agentic.Agents.Workflow;
using Agentic.Orchestrator.Context;
using Agentic.Orchestrator.Model;

namespace Agentic.Orchestrator.Tests;

/// <summary>SDLC agents exercised through the engine (no dotnet build/test processes involved).</summary>
public class AgentTests : IDisposable
{
    private readonly EngineHarness _h = new();

    public AgentTests()
    {
        foreach (var agent in new Orchestrator.Agents.IAgent[]
                 { new RequirementsAnalystAgent(), new CodebaseAnalystAgent(), new SolutionArchitectAgent(), new DeliveryPlannerAgent(), new ImplementerAgent() })
            _h.Agents.Register(agent);
        _h.Agent("noop", _ => Orchestrator.Agents.AgentResult.Success("ok"));
    }

    public void Dispose() => _h.Dispose();

    private static StageNode Requirements => new() { Id = "requirements", Stage = Stages.Requirements, Agent = "requirements-analyst" };

    private Task<Engine.RunResult> Run(string baseline, string requirement, params StageNode[] nodes) =>
        _h.RunAsync(new WorkflowGraph(nodes), new Dictionary<string, string> { [SettingKeys.Baseline] = baseline },
            c => c.Artifacts.Put(WellKnownArtifacts.RequirementsRaw, requirement, "text/plain", "human:test"));

    [Fact]
    public async Task Vague_requirement_is_turned_into_clarifications_and_deferred_scope()
    {
        var result = await Run("reference", "Make the shortener safer and able to handle more traffic.", Requirements);

        var spec = result.Context.Artifacts.LatestJson<RequirementSpec>(ArtifactKeys.RequirementSpec)!;
        Assert.Equal(["rate-limit-creation", "destination-safety"], spec.Capabilities);
        Assert.Equal("20", spec.Parameters["CreatePermitLimit"]);
        Assert.Contains(spec.OutOfScope, o => o.StartsWith("Scalability"));
        Assert.Contains(result.Context.Decisions.All, d => d.Category == DecisionCategory.Assumption && d.Summary.StartsWith("Q-SCALE"));
    }

    [Fact]
    public async Task Clear_brownfield_request_needs_no_clarification_and_ignores_existing_capabilities()
    {
        var result = await Run("reference", "Analytics undercount repeat clicks. Links should support an expiry time.", Requirements);

        var spec = result.Context.Artifacts.LatestJson<RequirementSpec>(ArtifactKeys.RequirementSpec)!;
        Assert.Equal(["fix-redirect-caching", "link-expiration"], spec.Capabilities);
        Assert.Empty(spec.Ambiguities);
        Assert.All(spec.Functional, fr => Assert.Contains(spec.AcceptanceCriteria, ac => ac.CapabilityId == fr.CapabilityId));
    }

    [Fact]
    public async Task Greenfield_scope_excludes_enhancements_and_logs_them_as_follow_ups()
    {
        var result = await Run("empty", "Shorten URLs, redirect visitors, show click analytics. Later links should expire.", Requirements);

        var spec = result.Context.Artifacts.LatestJson<RequirementSpec>(ArtifactKeys.RequirementSpec)!;
        Assert.Contains("link-creation", spec.Capabilities);
        Assert.DoesNotContain("link-expiration", spec.Capabilities);
        Assert.Contains(spec.OutOfScope, o => o.StartsWith("Link expiration"));
    }

    [Fact]
    public async Task Planner_orders_tasks_that_share_a_file_and_wires_them_before_verification()
    {
        var result = await Run("reference", "Analytics undercount repeat clicks. Links should support an expiry time.",
            Requirements,
            new StageNode { Id = NodeIds.Design, Stage = Stages.Design, Agent = "solution-architect", DependsOn = ["requirements"] },
            new StageNode { Id = NodeIds.Plan, Stage = Stages.Planning, Agent = "delivery-planner", DependsOn = [NodeIds.Design] },
            new StageNode { Id = NodeIds.Build, Stage = Stages.Testing, Agent = "noop", DependsOn = [NodeIds.Plan] },
            new StageNode { Id = NodeIds.SecurityReview, Stage = Stages.Security, Agent = "noop", DependsOn = [NodeIds.Plan] });

        var plan = result.Context.Artifacts.LatestJson<WorkPlan>(ArtifactKeys.WorkPlan)!;
        var api = plan.Tasks.Single(t => t.RecipeId == "bf-expiry-api");
        Assert.Contains("impl:bf-redirect-caching", api.DependsOn); // both edit LinkEndpoints.cs
        Assert.Contains("impl:bf-expiry-service", api.DependsOn);
        Assert.Contains(plan.ParallelGroups[0], id => id == "impl:bf-redirect-caching");
        Assert.Contains(plan.ParallelGroups[0], id => id == "impl:bf-expiry-domain");
        Assert.All(plan.Tasks, t => Assert.Contains(t.Id, result.Graph[NodeIds.Build].DependsOn));
        // Implementation nodes failed (empty workspace = drift), which is expected here; the plan and graph shape are what matter.
    }

    [Fact]
    public async Task Implementer_refuses_to_guess_when_the_code_drifted_from_the_analysed_baseline()
    {
        _h.Workspace.Write("src/UrlShortener.Api/Domain/ShortLink.cs", "public sealed record ShortLink(string Code, string Somebody, bool ChangedThis);");
        var spec = new RequirementSpec("t", "t", false, ["link-expiration"], [], [], [], [], [], [], new Dictionary<string, string>());
        var node = new StageNode { Id = "impl", Stage = Stages.Implementation, Agent = "implementer", Parameters = new Dictionary<string, string> { ["recipe"] = "bf-expiry-domain" } };

        var result = await _h.RunAsync(new WorkflowGraph([node]), seed: c => c.Artifacts.PutJson(ArtifactKeys.RequirementSpec, spec, "test"));

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Contains("Baseline drift", result.Reason);
        Assert.Contains("ChangedThis", _h.Workspace.Read("src/UrlShortener.Api/Domain/ShortLink.cs"));
    }

    [Fact]
    public async Task Bug_fix_without_a_located_root_cause_is_not_allowed_to_proceed()
    {
        _h.Workspace.Write("src/UrlShortener.Api/Api/LinkEndpoints.cs", "class LinkEndpoints { void RedirectToTarget() { /* already 302 */ } }");

        var result = await Run("reference", "Analytics undercount repeat clicks.",
            Requirements, new StageNode { Id = "impact", Stage = Stages.Design, Agent = "codebase-analyst", DependsOn = ["requirements"] });

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Contains("root cause", result.Reason);
    }
}
