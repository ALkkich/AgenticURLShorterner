using Agentic.Agents.Knowledge;
using Agentic.Agents.Models;
using Agentic.Agents.Workflow;
using Agentic.Orchestrator.Agents;
using Agentic.Orchestrator.Context;
using Agentic.Orchestrator.Model;

namespace Agentic.Agents.Agents;

/// <summary>
/// Decomposes the design into implementation tasks with explicit dependencies, then proposes a graph
/// mutation that inserts one node per task between planning and verification. Tasks touching the
/// same file are ordered (deterministic edits) and also lock that file (no concurrent writers);
/// independent tasks run in parallel.
/// </summary>
public sealed class DeliveryPlannerAgent : IAgent
{
    public string Name => "delivery-planner";

    public Task<AgentResult> ExecuteAsync(AgentContext ctx)
    {
        var spec = ctx.Require<RequirementSpec>(ArtifactKeys.RequirementSpec);
        ctx.Require<DesignSpec>(ArtifactKeys.Design);
        var caps = spec.Capabilities.Select(CapabilityCatalog.Get).ToList();

        var wanted = new HashSet<string>(caps.SelectMany(c => spec.IsGreenfield ? c.GreenfieldRecipes : c.Recipes), StringComparer.Ordinal);
        if (spec.IsGreenfield)
            wanted.UnionWith(RecipeBook.GreenfieldAlways);
        var closure = Close(wanted);
        var ordered = RecipeBook.All.Where(r => closure.Contains(r.Id)).ToList();

        var tasks = new List<WorkTask>();
        foreach (var recipe in ordered)
        {
            var deps = new HashSet<string>(recipe.DependsOn.Where(closure.Contains), StringComparer.Ordinal);
            // Same-file tasks are applied in catalogue order so anchors are deterministic.
            foreach (var earlier in tasks)
                if (earlier.Files.Intersect(recipe.Files, StringComparer.OrdinalIgnoreCase).Any())
                    deps.Add(earlier.RecipeId);
            tasks.Add(new WorkTask(
                TaskId(recipe.Id), recipe.Title, recipe.Id,
                caps.Where(c => c.Recipes.Contains(recipe.Id) || c.GreenfieldRecipes.Contains(recipe.Id)).Select(c => c.Id).ToList(),
                deps.Select(TaskId).Order(StringComparer.Ordinal).ToList(),
                recipe.Files,
                recipe.Risk));
        }

        var groups = Levels(tasks);
        var plan = new WorkPlan(tasks, CriticalPath(tasks), groups);
        var mutation = new GraphMutation(
            tasks.Select(t => new StageNode
            {
                Id = t.Id,
                Stage = Stages.Implementation,
                Agent = "implementer",
                Description = t.Title,
                DependsOn = [NodeIds.Plan, .. t.DependsOn],
                Tags = [NodeIds.ImplementationTag],
                Locks = t.Files,
                Risk = t.Risk,
                Parameters = new Dictionary<string, string> { ["recipe"] = t.RecipeId },
            }).ToList(),
            tasks.SelectMany(t => new[] { (NodeIds.Build, t.Id), (NodeIds.SecurityReview, t.Id) }).ToList(),
            $"Inserted {tasks.Count} implementation tasks in {groups.Count} dependency levels (max parallelism {groups.Max(g => g.Count)}).");

        return Task.FromResult(AgentResult.Success($"{tasks.Count} tasks; critical path {plan.CriticalPath.Count}; parallel groups: {string.Join(" | ", groups.Select(g => string.Join(", ", g)))}.")
            .WithJson(ArtifactKeys.WorkPlan, plan)
            .WithDecision(DecisionCategory.Plan, $"Plan of {tasks.Count} tasks", "Recipes selected from capabilities, closed over dependencies, ordered by shared files; tests are mandatory tasks.")
            .WithMutation(mutation));
    }

    public static string TaskId(string recipeId) => $"impl:{recipeId}";

    private static HashSet<string> Close(HashSet<string> wanted)
    {
        var closure = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(wanted);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (closure.Add(id))
                foreach (var dep in RecipeBook.Get(id).DependsOn)
                    stack.Push(dep);
        }
        return closure;
    }

    private static List<IReadOnlyList<string>> Levels(IReadOnlyList<WorkTask> tasks)
    {
        var level = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var task in tasks) // tasks are already topologically ordered
            level[task.Id] = task.DependsOn.Count == 0 ? 0 : task.DependsOn.Max(d => level[d]) + 1;
        return level.GroupBy(l => l.Value).OrderBy(g => g.Key).Select(g => (IReadOnlyList<string>)g.Select(x => x.Key).ToList()).ToList();
    }

    private static List<string> CriticalPath(IReadOnlyList<WorkTask> tasks)
    {
        var best = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            var longest = task.DependsOn.Select(d => best[d]).OrderByDescending(p => p.Count).FirstOrDefault() ?? [];
            best[task.Id] = [.. longest, task.Id];
        }
        return best.Values.OrderByDescending(p => p.Count).FirstOrDefault() ?? [];
    }
}
