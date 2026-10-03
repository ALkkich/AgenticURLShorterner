using System.Text;
using Agentic.Orchestrator.Context;

namespace Agentic.Orchestrator.Workspace;

/// <summary>What agents can see of the sandbox. Agents never get write access; they propose ChangeSets.</summary>
public interface IReadOnlyWorkspace
{
    string Root { get; }
    bool Exists(string relativePath);
    string Read(string relativePath);
    IReadOnlyList<string> ListFiles();
}

/// <summary>
/// Sandboxed working copy. All paths are confined to <see cref="Root"/>; build output folders are
/// ignored for listing and state hashing.
/// </summary>
public sealed class FileSystemWorkspace : IReadOnlyWorkspace
{
    private static readonly string[] IgnoredSegments = ["bin", "obj", ".vs", "TestResults"];

    /// <summary>Extensions that affect build/test outcomes. Docs edits do not invalidate builds.</summary>
    private static readonly string[] BuildRelevantExtensions = [".cs", ".csproj", ".json", ".props", ".targets", ".sln"];

    public FileSystemWorkspace(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Resolve(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            throw new UnauthorizedAccessException($"Absolute paths are not allowed in the workspace: '{relativePath}'.");
        var full = Path.GetFullPath(Path.Combine(Root, relativePath));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"Path escapes the workspace sandbox: '{relativePath}'.");
        return full;
    }

    public bool Exists(string relativePath) => File.Exists(Resolve(relativePath));

    public string Read(string relativePath) => File.ReadAllText(Resolve(relativePath));

    public string? TryRead(string relativePath) => Exists(relativePath) ? Read(relativePath) : null;

    public void Write(string relativePath, string content)
    {
        var full = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
    }

    public void Delete(string relativePath)
    {
        var full = Resolve(relativePath);
        if (File.Exists(full))
            File.Delete(full);
    }

    public IReadOnlyList<string> ListFiles() =>
        Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Root, f).Replace('\\', '/'))
            .Where(p => !p.Split('/').Any(s => IgnoredSegments.Contains(s, StringComparer.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>Content hash over build-relevant files; published as the <c>workspace.state</c> artifact.</summary>
    public string ComputeBuildStateHash()
    {
        var manifest = new StringBuilder();
        foreach (var file in ListFiles().Where(f => BuildRelevantExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
            manifest.Append(file).Append(':').Append(Json.Sha256(Read(file).ReplaceLineEndings("\n"))).Append('\n');
        return Json.Sha256(manifest.ToString());
    }

    /// <summary>Seeds the sandbox from a source tree (brownfield baseline), excluding build outputs.</summary>
    public void SeedFrom(string sourceRoot, IEnumerable<string> relativeDirectories)
    {
        foreach (var dir in relativeDirectories)
        {
            var sourceDir = Path.Combine(sourceRoot, dir);
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceRoot, file).Replace('\\', '/');
                if (relative.Split('/').Any(s => IgnoredSegments.Contains(s, StringComparer.OrdinalIgnoreCase)))
                    continue;
                var target = Resolve(relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }
    }
}
