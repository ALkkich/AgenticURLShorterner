using Agentic.Orchestrator.Context;
using Agentic.Orchestrator.Model;

namespace Agentic.Orchestrator.Gates;

public sealed record GateResult(bool Passed, string Reason)
{
    public static GateResult Pass(string reason = "ok") => new(true, reason);
    public static GateResult Fail(string reason) => new(false, reason);
}

public sealed record GateContext(StageNode Node, ArtifactStore Artifacts, IReadOnlyDictionary<string, string> Settings);

/// <summary>Entry gates guard preconditions; exit gates guard the quality of what a node produced.</summary>
public interface IGate
{
    string Name { get; }
    GateResult Evaluate(GateContext context);
}

public static class Gate
{
    public static IGate Check(string name, Func<GateContext, GateResult> evaluate) => new DelegateGate(name, evaluate);

    /// <summary>Entry gate: the listed artifacts must exist (upstream contract satisfied).</summary>
    public static IGate RequiresArtifacts(params string[] keys) => Check($"requires[{string.Join(",", keys)}]", ctx =>
    {
        var missing = keys.Where(k => ctx.Artifacts.Latest(k) is null).ToList();
        return missing.Count == 0 ? GateResult.Pass() : GateResult.Fail($"missing artifacts: {string.Join(", ", missing)}");
    });

    /// <summary>Gate over a typed JSON artifact.</summary>
    public static IGate OnArtifact<T>(string name, string key, Func<T, GateResult> evaluate) where T : class =>
        Check(name, ctx => ctx.Artifacts.LatestJson<T>(key) is { } value ? evaluate(value) : GateResult.Fail($"artifact '{key}' not found"));

    private sealed class DelegateGate(string name, Func<GateContext, GateResult> evaluate) : IGate
    {
        public string Name => name;
        public GateResult Evaluate(GateContext context) => evaluate(context);
    }
}
