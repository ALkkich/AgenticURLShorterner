using Agentic.Orchestrator.Agents;
using Agentic.Orchestrator.Engine;
using Agentic.Orchestrator.Governance;
using Agentic.Orchestrator.Model;
using Agentic.Orchestrator.Observability;
using Agentic.Orchestrator.Tools;
using Agentic.Orchestrator.Workspace;

namespace Agentic.Orchestrator.Tests;

public sealed class LambdaAgent(string name, Func<AgentContext, AgentResult> execute) : IAgent
{
    public string Name => name;
    public int Calls;

    public Task<AgentResult> ExecuteAsync(AgentContext context)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(execute(context));
    }
}

public sealed class FakeToolRunner : IToolRunner
{
    public Task<ToolResult> RunAsync(ToolInvocation invocation, CancellationToken cancellationToken) =>
        Task.FromResult(new ToolResult(0, "ok", TimeSpan.Zero, false));
}

/// <summary>Builds isolated runs over a temp workspace with zero retry backoff.</summary>
public sealed class EngineHarness : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agentic-tests", Guid.NewGuid().ToString("N"));

    public EngineHarness()
    {
        Workspace = new FileSystemWorkspace(Path.Combine(_root, "ws"));
        Audit = new AuditLog("test-run");
    }

    public AgentRegistry Agents { get; } = new();
    public FileSystemWorkspace Workspace { get; }
    public AuditLog Audit { get; }
    public List<ScriptedResponse> Script { get; } = [];
    public RunOptions Options { get; set; } = new() { BackoffScale = 0 };
    public PolicyEngine Policies { get; set; } = PolicyEngine.Default();

    public LambdaAgent Agent(string name, Func<AgentContext, AgentResult> execute)
    {
        var agent = new LambdaAgent(name, execute);
        Agents.Register(agent);
        return agent;
    }

    public static StageNode Node(string id, string agent, params string[] dependsOn) =>
        new() { Id = id, Stage = Stages.Implementation, Agent = agent, DependsOn = dependsOn };

    public Task<RunResult> RunAsync(params StageNode[] nodes) => RunAsync(new WorkflowGraph(nodes));

    public Task<RunResult> RunAsync(WorkflowGraph graph, IReadOnlyDictionary<string, string>? settings = null, Action<WorkflowContext>? seed = null)
    {
        var context = new WorkflowContext("test-run", "test", _root, Workspace, Audit, settings);
        seed?.Invoke(context);
        var approvals = new ScriptedApprovalProvider(Script, new UnattendedApprovalProvider());
        return new WorkflowEngine(Agents, Policies, approvals, new FakeToolRunner(), Options).RunAsync(graph, context);
    }

    public IEnumerable<string> EventsOfType(string type) => Audit.Events.Where(e => e.Type == type).Select(e => e.NodeId ?? e.Message);

    public void Dispose()
    {
        Audit.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
