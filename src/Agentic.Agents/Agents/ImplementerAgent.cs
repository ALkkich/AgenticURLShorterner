using Agentic.Agents.Knowledge;
using Agentic.Agents.Models;
using Agentic.Orchestrator.Agents;
using Agentic.Orchestrator.Governance;

namespace Agentic.Agents.Agents;

/// <summary>
/// Executes one planned task by turning its recipe into a proposed <see cref="ChangeSet"/>. It never
/// writes files itself; change control applies (or blocks) the proposal. Anchored edits fail loudly on
/// drift instead of guessing, and are idempotent when the edit is already present.
/// </summary>
public sealed class ImplementerAgent : IAgent
{
    public string Name => "implementer";

    public Task<AgentResult> ExecuteAsync(AgentContext ctx)
    {
        var recipe = RecipeBook.Get(ctx.Node.Parameters["recipe"]);
        var spec = ctx.Require<RequirementSpec>(ArtifactKeys.RequirementSpec);
        ctx.Read<WorkPlan>(ArtifactKeys.WorkPlan); // read for lineage: a new plan makes this task stale

        var pending = new Dictionary<string, string>(StringComparer.Ordinal);
        string? Current(string path) =>
            pending.TryGetValue(path, out var content) ? content : ctx.Workspace.Exists(path) ? ctx.Workspace.Read(path) : null;

        foreach (var file in recipe.BlueprintFiles ?? [])
        {
            var root = ctx.Setting(SettingKeys.ReferenceRoot);
            if (root is null)
                return Task.FromResult(AgentResult.Permanent("The reference-root setting is required for blueprint recipes."));
            var source = Path.Combine(root, file);
            if (!File.Exists(source))
                return Task.FromResult(AgentResult.Permanent($"Blueprint component missing: {file}"));
            pending[file] = File.ReadAllText(source);
        }

        foreach (var edit in recipe.Edits)
        {
            var content = Substitute(edit.Content, spec.Parameters);
            if (edit.Find is null)
            {
                pending[edit.Path] = content;
                continue;
            }

            var current = Current(edit.Path);
            if (current is null)
                return Task.FromResult(AgentResult.Permanent($"Baseline drift: '{edit.Path}' does not exist. Re-analysis required."));
            var newline = current.Contains("\r\n") ? "\r\n" : "\n";
            var text = current.ReplaceLineEndings("\n");
            var find = edit.Find.ReplaceLineEndings("\n");
            var replacement = content.ReplaceLineEndings("\n");
            var occurrences = Occurrences(text, find);
            if (occurrences == 0 && text.Contains(replacement, StringComparison.Ordinal))
                continue; // already applied (idempotent re-run)
            if (occurrences != 1)
                return Task.FromResult(AgentResult.Permanent(
                    $"Baseline drift in '{edit.Path}': anchor found {occurrences} times (expected 1). The code changed since impact analysis; refusing to guess."));
            pending[edit.Path] = text.Replace(find, replacement).ReplaceLineEndings(newline);
        }

        if (ctx.Feedback.Count > 0)
            ctx.Log($"Re-implementing with feedback: {ctx.Feedback[^1]}");
        else if (ctx.Setting(SettingKeys.InjectDefect) == recipe.Id)
            InjectDefect(pending, ctx);

        var changes = pending
            .Where(p => !ctx.Workspace.Exists(p.Key) || ctx.Workspace.Read(p.Key) != p.Value)
            .Select(p => new FileChange(p.Key, ctx.Workspace.Exists(p.Key) ? ChangeKind.Modify : ChangeKind.Create, p.Value, recipe.Title))
            .ToList();

        return Task.FromResult(AgentResult.Success($"{recipe.Title}: {changes.Count} file(s) proposed.")
            .WithJson(ArtifactKeys.TaskResult(recipe.Id), new { recipe = recipe.Id, files = changes.Select(c => new { c.Path, c.Kind }) })
            .WithChanges(new ChangeSet(recipe.Title, changes)));
    }

    /// <summary>Fault injection: simulates a generation defect that only the build can catch.</summary>
    private static void InjectDefect(Dictionary<string, string> pending, AgentContext ctx)
    {
        var target = pending.Keys.FirstOrDefault(k => k.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
        if (target is null)
            return;
        pending[target] += "\n// [fault-injection] simulated generation defect\ninternal sealed class InjectedDefect { int Broken => \"not an int\"; }\n";
        ctx.Log($"[fault-injection] Injected a compile defect into {target}.");
    }

    private static string Substitute(string content, IReadOnlyDictionary<string, string> parameters) =>
        parameters.Aggregate(content, (text, p) => text.Replace("{{" + p.Key + "}}", p.Value, StringComparison.Ordinal));

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
