using System.Text;

namespace Agentic.Orchestrator.Model;

/// <summary>
/// Agent-proposed change to the workflow graph (dynamic planning). Mutations may add nodes and
/// add dependencies (tighten ordering); they can never remove nodes or loosen ordering — only the
/// engine retracts nodes, and only those created by the node being re-planned.
/// </summary>
public sealed record GraphMutation(
    IReadOnlyList<StageNode> AddNodes,
    IReadOnlyList<(string NodeId, string DependsOn)> AddDependencies,
    string Reason);

public sealed class GraphValidationException(string message) : Exception(message);

/// <summary>Versioned DAG of <see cref="StageNode"/>s. Mutated only by the engine's scheduler thread.</summary>
public sealed class WorkflowGraph
{
    private readonly Dictionary<string, StageNode> _nodes = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    public WorkflowGraph(IEnumerable<StageNode> nodes)
    {
        foreach (var node in nodes)
            AddInternal(node);
        Validate();
    }

    public int Version { get; private set; } = 1;

    public IReadOnlyList<StageNode> Nodes => _order.Select(id => _nodes[id]).ToList();

    public StageNode this[string id] => _nodes[id];

    public bool Contains(string id) => _nodes.ContainsKey(id);

    public IEnumerable<StageNode> Dependents(string id) => _nodes.Values.Where(n => n.DependsOn.Contains(id));

    /// <summary>Transitive dependents, excluding <paramref name="id"/> itself.</summary>
    public IReadOnlySet<string> Descendants(string id)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>([id]);
        while (stack.Count > 0)
            foreach (var dependent in Dependents(stack.Pop()))
                if (seen.Add(dependent.Id))
                    stack.Push(dependent.Id);
        return seen;
    }

    /// <summary>Transitive dependencies, excluding <paramref name="id"/> itself.</summary>
    public IReadOnlySet<string> Ancestors(string id)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(_nodes[id].DependsOn);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (seen.Add(current))
                foreach (var dep in _nodes[current].DependsOn)
                    stack.Push(dep);
        }
        return seen;
    }

    /// <summary>Validates and applies an agent mutation atomically. Returns ids of added nodes.</summary>
    public IReadOnlyList<string> Apply(GraphMutation mutation, string mutatingNodeId)
    {
        var candidate = new Dictionary<string, StageNode>(_nodes, StringComparer.Ordinal);
        var added = new List<string>();
        foreach (var node in mutation.AddNodes)
        {
            if (candidate.ContainsKey(node.Id))
                throw new GraphValidationException($"Mutation from '{mutatingNodeId}' re-defines existing node '{node.Id}'.");
            candidate[node.Id] = node with { ExpandedBy = mutatingNodeId };
            added.Add(node.Id);
        }
        foreach (var (nodeId, dependsOn) in mutation.AddDependencies)
        {
            if (!candidate.TryGetValue(nodeId, out var target))
                throw new GraphValidationException($"Mutation adds a dependency to unknown node '{nodeId}'.");
            if (!target.DependsOn.Contains(dependsOn))
                candidate[nodeId] = target with { DependsOn = [.. target.DependsOn, dependsOn] };
        }

        EnsureValid(candidate);
        foreach (var id in added)
            _order.Add(id);
        foreach (var (id, node) in candidate)
            _nodes[id] = node;
        Version++;
        return added;
    }

    /// <summary>Removes nodes created by <paramref name="expandedBy"/> and edges pointing at them.</summary>
    public IReadOnlyList<string> Retract(string expandedBy)
    {
        var removed = _nodes.Values.Where(n => n.ExpandedBy == expandedBy).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        if (removed.Count == 0)
            return [];

        foreach (var id in removed)
        {
            _nodes.Remove(id);
            _order.Remove(id);
        }
        foreach (var node in _nodes.Values.ToList())
            if (node.DependsOn.Any(removed.Contains))
                _nodes[node.Id] = node with { DependsOn = node.DependsOn.Where(d => !removed.Contains(d)).ToList() };

        Version++;
        return removed.ToList();
    }

    public void Validate() => EnsureValid(_nodes);

    /// <summary>Kahn levelisation: nodes in the same level have no ordering constraint between them.</summary>
    public IReadOnlyList<IReadOnlyList<string>> TopologicalLevels() => Levels(_nodes);

    public string ToMermaid(Func<string, NodeStatus?>? statusOf = null)
    {
        var sb = new StringBuilder("flowchart TD\n");
        foreach (var node in Nodes)
        {
            var status = statusOf?.Invoke(node.Id);
            var shape = node.RequiresApproval ? $"{{{{\"{node.Id}<br/>{node.Agent}\"}}}}" : $"[\"{node.Id}<br/>{node.Agent}\"]";
            sb.AppendLine($"    {Safe(node.Id)}{shape}");
            if (status is not null)
                sb.AppendLine($"    class {Safe(node.Id)} {status.ToString()!.ToLowerInvariant()}");
        }
        foreach (var node in Nodes)
            foreach (var dep in node.DependsOn)
                sb.AppendLine($"    {Safe(dep)} --> {Safe(node.Id)}");
        sb.AppendLine("    classDef succeeded fill:#d4edda,stroke:#2e7d32");
        sb.AppendLine("    classDef failed fill:#f8d7da,stroke:#c62828");
        sb.AppendLine("    classDef pending fill:#eeeeee,stroke:#999");
        sb.AppendLine("    classDef stale fill:#fff3cd,stroke:#f9a825");
        sb.AppendLine("    classDef skipped fill:#eeeeee,stroke:#999,stroke-dasharray: 4");
        return sb.ToString();

        static string Safe(string id) => id.Replace(':', '_').Replace('-', '_').Replace('.', '_');
    }

    private void AddInternal(StageNode node)
    {
        if (!_nodes.TryAdd(node.Id, node))
            throw new GraphValidationException($"Duplicate node id '{node.Id}'.");
        _order.Add(node.Id);
    }

    private static void EnsureValid(IReadOnlyDictionary<string, StageNode> nodes)
    {
        foreach (var node in nodes.Values)
            foreach (var dep in node.DependsOn)
                if (!nodes.ContainsKey(dep))
                    throw new GraphValidationException($"Node '{node.Id}' depends on unknown node '{dep}'.");

        var levelled = Levels(nodes).Sum(l => l.Count);
        if (levelled != nodes.Count)
            throw new GraphValidationException("Workflow graph contains a cycle.");
    }

    private static List<IReadOnlyList<string>> Levels(IReadOnlyDictionary<string, StageNode> nodes)
    {
        var inDegree = nodes.Values.ToDictionary(n => n.Id, n => n.DependsOn.Distinct().Count(), StringComparer.Ordinal);
        var levels = new List<IReadOnlyList<string>>();
        var current = inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
        while (current.Count > 0)
        {
            levels.Add(current);
            var next = new List<string>();
            foreach (var id in current)
                foreach (var dependent in nodes.Values.Where(n => n.DependsOn.Contains(id)))
                    if (--inDegree[dependent.Id] == 0)
                        next.Add(dependent.Id);
            current = next.Order(StringComparer.Ordinal).ToList();
        }
        return levels;
    }
}
