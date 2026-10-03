namespace Agentic.Orchestrator.Governance;

public enum ChangeKind { Create, Modify, Delete }

public sealed record FileChange(string Path, ChangeKind Kind, string? NewContent, string Description);

/// <summary>A proposed, not-yet-applied set of file edits. Agents propose; the engine's change control applies.</summary>
public sealed record ChangeSet(string Title, IReadOnlyList<FileChange> Changes)
{
    public bool IsEmpty => Changes.Count == 0;
}

public sealed record DiffStat(int FilesChanged, int LinesAdded, int LinesRemoved)
{
    public int TotalLines => LinesAdded + LinesRemoved;

    /// <summary>Multiset line diff: cheap, order-insensitive approximation adequate for change-size budgets.</summary>
    public static DiffStat Compute(ChangeSet changes, Func<string, string?> currentContent)
    {
        int added = 0, removed = 0;
        foreach (var change in changes.Changes)
        {
            var before = Lines(currentContent(change.Path));
            var after = change.Kind == ChangeKind.Delete ? [] : Lines(change.NewContent);
            var remaining = before.GroupBy(l => l).ToDictionary(g => g.Key, g => g.Count());
            foreach (var line in after)
            {
                if (remaining.TryGetValue(line, out var count) && count > 0)
                    remaining[line] = count - 1;
                else
                    added++;
            }
            removed += remaining.Values.Sum();
        }
        return new DiffStat(changes.Changes.Count, added, removed);
    }

    private static List<string> Lines(string? content) =>
        content is null ? [] : content.ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
}
