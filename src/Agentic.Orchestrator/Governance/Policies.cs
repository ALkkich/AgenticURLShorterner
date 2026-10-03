using System.Text.RegularExpressions;
using Agentic.Orchestrator.Model;
using Agentic.Orchestrator.Tools;

namespace Agentic.Orchestrator.Governance;

public enum PolicyCategory { Security, Compliance, ChangeControl }

public enum PolicySeverity
{
    Info,
    Warning,
    /// <summary>Allowed only after an explicit human approval.</summary>
    RequiresApproval,
    /// <summary>Never allowed; the action is rejected and the node fails.</summary>
    Block,
}

public sealed record PolicyFinding(string PolicyId, PolicyCategory Category, PolicySeverity Severity, string Message, string? Path = null);

/// <summary>What a policy is asked to judge: a proposed change set or a tool invocation.</summary>
public sealed record PolicySubject(StageNode Node, ChangeSet? Changes, Func<string, string?> CurrentContent, ToolInvocation? Tool = null);

public interface IPolicy
{
    string Id { get; }
    PolicyCategory Category { get; }
    IEnumerable<PolicyFinding> Evaluate(PolicySubject subject);
}

public sealed class PolicyEngine(IEnumerable<IPolicy> policies)
{
    public IReadOnlyList<IPolicy> Policies { get; } = policies.ToList();

    public IReadOnlyList<PolicyFinding> Evaluate(PolicySubject subject) =>
        Policies.SelectMany(p => p.Evaluate(subject)).ToList();

    /// <summary>The guardrail set used by default for every run.</summary>
    public static PolicyEngine Default(PolicySettings? settings = null)
    {
        settings ??= new PolicySettings();
        return new PolicyEngine(
        [
            new SecretLeakPolicy(),
            new PiiLoggingPolicy(),
            new InsecureCodePolicy(),
            new ProtectedPathPolicy(settings.BlockedPaths, settings.ApprovalPaths),
            new DependencyAllowlistPolicy(settings.AllowedPackages),
            new ChangeBudgetPolicy(settings.MaxLinesWithoutApproval, settings.MaxFilesWithoutApproval),
            new CommandAllowlistPolicy(settings.AllowedCommands),
        ]);
    }
}

public sealed record PolicySettings
{
    public IReadOnlyList<string> BlockedPaths { get; init; } = [".github/", ".git/", "appsettings.Production.json", "secrets"];
    public IReadOnlyList<string> ApprovalPaths { get; init; } = [".csproj", "Directory.Build.props", "Program.cs"];
    public IReadOnlyList<string> AllowedPackages { get; init; } =
        ["Microsoft.AspNetCore.Mvc.Testing", "Microsoft.NET.Test.Sdk", "xunit", "xunit.runner.visualstudio", "coverlet.collector"];
    public int MaxLinesWithoutApproval { get; init; } = 400;
    public int MaxFilesWithoutApproval { get; init; } = 12;
    public IReadOnlyList<string> AllowedCommands { get; init; } = ["dotnet build", "dotnet test", "dotnet restore"];
}

internal static class ChangeScan
{
    public static IEnumerable<(FileChange Change, int Line, string Text)> AddedLines(PolicySubject subject)
    {
        if (subject.Changes is null)
            yield break;
        foreach (var change in subject.Changes.Changes.Where(c => c.Kind != ChangeKind.Delete && c.NewContent is not null))
        {
            var existing = (subject.CurrentContent(change.Path) ?? string.Empty).ReplaceLineEndings("\n").Split('\n').ToHashSet();
            var lines = change.NewContent!.ReplaceLineEndings("\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
                if (!existing.Contains(lines[i]))
                    yield return (change, i + 1, lines[i]);
        }
    }

    public static bool IsTestFile(string path) => path.Contains("Tests/", StringComparison.OrdinalIgnoreCase) || path.Contains(".Tests", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Security: credentials and keys must never be committed by an agent.</summary>
public sealed partial class SecretLeakPolicy : IPolicy
{
    public string Id => "SEC-001-secrets";
    public PolicyCategory Category => PolicyCategory.Security;

    [GeneratedRegex(@"AKIA[0-9A-Z]{16}|-----BEGIN [A-Z ]*PRIVATE KEY-----|sk-ant-[A-Za-z0-9_\-]{10,}|(?i)(password|pwd)\s*=\s*[^;""\s]{6,};|(?i)(api[_-]?key|client[_-]?secret)""?\s*[:=]\s*""[A-Za-z0-9_\-]{16,}""")]
    private static partial Regex SecretPattern();

    public IEnumerable<PolicyFinding> Evaluate(PolicySubject subject) =>
        ChangeScan.AddedLines(subject)
            .Where(l => SecretPattern().IsMatch(l.Text))
            .Select(l => new PolicyFinding(Id, Category, PolicySeverity.Block, $"Possible secret on line {l.Line}.", l.Change.Path));
}

/// <summary>Compliance: personal data (IPs, emails, raw referrers) must not reach logs.</summary>
public sealed partial class PiiLoggingPolicy : IPolicy
{
    public string Id => "CMP-001-pii-logging";
    public PolicyCategory Category => PolicyCategory.Compliance;

    [GeneratedRegex(@"\bLog(Trace|Debug|Information|Warning|Error|Critical)?\s*\(.*(RemoteIpAddress|Headers\.Referer|Email|UserAgent)")]
    private static partial Regex PiiLog();

    public IEnumerable<PolicyFinding> Evaluate(PolicySubject subject) =>
        ChangeScan.AddedLines(subject)
            .Where(l => PiiLog().IsMatch(l.Text))
            .Select(l => new PolicyFinding(Id, Category, PolicySeverity.Block, $"Personal data written to logs on line {l.Line}.", l.Change.Path));
}

/// <summary>Security: well-known insecure constructs in production code.</summary>
public sealed partial class InsecureCodePolicy : IPolicy
{
    public string Id => "SEC-002-insecure-code";
    public PolicyCategory Category => PolicyCategory.Security;

    [GeneratedRegex(@"DangerousAcceptAnyServerCertificateValidator|ServerCertificateCustomValidationCallback\s*=.*=>\s*true")]
    private static partial Regex TlsBypass();

    [GeneratedRegex(@"new\s+Random\s*\(|\bMD5\b|\bSHA1\b")]
    private static partial Regex WeakCrypto();

    public IEnumerable<PolicyFinding> Evaluate(PolicySubject subject)
    {
        foreach (var (change, line, text) in ChangeScan.AddedLines(subject))
        {
            if (TlsBypass().IsMatch(text))
                yield return new PolicyFinding(Id, Category, PolicySeverity.Block, $"TLS validation bypass on line {line}.", change.Path);
            else if (WeakCrypto().IsMatch(text) && !ChangeScan.IsTestFile(change.Path))
                yield return new PolicyFinding(Id, Category, PolicySeverity.RequiresApproval, $"Weak randomness/hash on line {line}.", change.Path);
        }
    }
}

/// <summary>Change control: some paths are off-limits to agents; others need a human.</summary>
public sealed class ProtectedPathPolicy(IReadOnlyList<string> blocked, IReadOnlyList<string> requiresApproval) : IPolicy
{
    public string Id => "CHG-001-protected-paths";
    public PolicyCategory Category => PolicyCategory.ChangeControl;

    public IEnumerable<PolicyFinding> Evaluate(PolicySubject subject)
    {
        if (subject.Changes is null)
            yield break;
        foreach (var change in subject.Changes.Changes)
        {
            var path = change.Path.Replace('\\', '/');
            if (path.Contains("..") || Path.IsPathRooted(path))
                yield return new PolicyFinding(Id, Category, PolicySeverity.Block, "Path escapes the workspace.", path);
            else if (blocked.Any(b => path.Contains(b, StringComparison.OrdinalIgnoreCase)))
                yield return new PolicyFinding(Id, Category, PolicySeverity.Block, "Path is protected from agent changes.", path);
            else if (change.Kind != ChangeKind.Create && requiresApproval.Any(a => path.EndsWith(a, StringComparison.OrdinalIgnoreCase)))
                yield return new PolicyFinding(Id, Category, PolicySeverity.RequiresApproval, "Modifies build/host configuration.", path);
            if (change.Kind == ChangeKind.Delete)
                yield return new PolicyFinding(Id, Category, PolicySeverity.RequiresApproval, "Deletes a file.", path);
        }
    }
}

/// <summary>Supply chain: new package references must be on the allowlist.</summary>
public sealed partial class DependencyAllowlistPolicy(IReadOnlyList<string> allowed) : IPolicy
{
    public string Id => "SEC-003-dependencies";
    public PolicyCategory Category => PolicyCategory.Security;

    [GeneratedRegex(@"<PackageReference\s+Include=""([^""]+)""")]
    private static partial Regex PackageReference();

    public IEnumerable<PolicyFinding> Evaluate(PolicySubject subject) =>
        ChangeScan.AddedLines(subject)
            .Select(l => (l.Change, Match: PackageReference().Match(l.Text)))
            .Where(x => x.Match.Success && !allowed.Contains(x.Match.Groups[1].Value, StringComparer.OrdinalIgnoreCase))
            .Select(x => new PolicyFinding(Id, Category, PolicySeverity.Block, $"Package '{x.Match.Groups[1].Value}' is not on the allowlist.", x.Change.Path));
}

/// <summary>Change control: large changes need a human reviewer (blast-radius limit).</summary>
public sealed class ChangeBudgetPolicy(int maxLines, int maxFiles) : IPolicy
{
    public string Id => "CHG-002-change-budget";
    public PolicyCategory Category => PolicyCategory.ChangeControl;

    public IEnumerable<PolicyFinding> Evaluate(PolicySubject subject)
    {
        if (subject.Changes is null)
            yield break;
        var stat = DiffStat.Compute(subject.Changes, subject.CurrentContent);
        if (stat.TotalLines > maxLines || stat.FilesChanged > maxFiles)
            yield return new PolicyFinding(Id, Category, PolicySeverity.RequiresApproval,
                $"Change of {stat.FilesChanged} files / {stat.TotalLines} lines exceeds the autonomous budget ({maxFiles} files / {maxLines} lines).");
    }
}

/// <summary>Security: agents may only execute allowlisted tool commands.</summary>
public sealed class CommandAllowlistPolicy(IReadOnlyList<string> allowed) : IPolicy
{
    public string Id => "SEC-004-commands";
    public PolicyCategory Category => PolicyCategory.Security;

    public IEnumerable<PolicyFinding> Evaluate(PolicySubject subject)
    {
        if (subject.Tool is not { } tool)
            yield break;
        var commandLine = $"{tool.FileName} {string.Join(' ', tool.Arguments)}";
        if (!allowed.Any(a => commandLine.StartsWith(a + " ", StringComparison.Ordinal) || commandLine == a))
            yield return new PolicyFinding(Id, Category, PolicySeverity.Block, $"Command not allowlisted: '{commandLine}'.");
        if (tool.Arguments.Any(a => a.Contains("&&") || a.Contains('|') || a.Contains(';')))
            yield return new PolicyFinding(Id, Category, PolicySeverity.Block, "Shell metacharacters in arguments.");
    }
}
