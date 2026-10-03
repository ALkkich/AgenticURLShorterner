using Agentic.Orchestrator.Governance;
using Agentic.Orchestrator.Observability;

namespace Agentic.Cli;

internal static class RepoLocator
{
    public static string Find()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "AgenticSdlc.sln")))
                    return dir.FullName;
        throw new InvalidOperationException("Run from inside the AgenticSdlc repository (AgenticSdlc.sln not found).");
    }
}

/// <summary>Prints the audit stream as a readable live timeline.</summary>
internal static class ConsoleProgress
{
    private static readonly object Gate = new();

    public static void Print(AuditEvent e)
    {
        var (color, label) = e.Type switch
        {
            "wave.started" => (ConsoleColor.DarkGray, "WAVE"),
            "node.started" => (ConsoleColor.Gray, "START"),
            "node.succeeded" => (ConsoleColor.Green, "DONE"),
            "node.failed" => (ConsoleColor.Red, "FAIL"),
            "agent.retry" => (ConsoleColor.Yellow, "RETRY"),
            "agent.failed" => (ConsoleColor.DarkYellow, "ERROR"),
            "node.fallback" => (ConsoleColor.Yellow, "FALLBACK"),
            "remediation.started" => (ConsoleColor.Magenta, "REMEDIATE"),
            "rollback.completed" => (ConsoleColor.Magenta, "ROLLBACK"),
            "node.stale" => (ConsoleColor.Cyan, "STALE"),
            "replan.retracted" => (ConsoleColor.Cyan, "REPLAN"),
            "graph.mutated" => (ConsoleColor.Cyan, "GRAPH"),
            "external.event" => (ConsoleColor.Cyan, "UPSTREAM"),
            "approval.decided" => (ConsoleColor.Blue, "APPROVAL"),
            "policy.finding" => (ConsoleColor.DarkYellow, "POLICY"),
            "gate.exit" or "gate.entry" when e.Data.GetValueOrDefault("passed") == "False" => (ConsoleColor.Red, "GATE"),
            "change.applied" => (ConsoleColor.DarkGreen, "CHANGE"),
            "node.revision" => (ConsoleColor.Yellow, "REVISE"),
            "run.safe-stop" => (ConsoleColor.Red, "STOP"),
            _ => (ConsoleColor.Black, ""),
        };
        if (label.Length == 0)
            return;

        lock (Gate)
        {
            Console.ForegroundColor = color;
            Console.Write($"{e.Timestamp:HH:mm:ss} {label,-9}");
            Console.ResetColor();
            var message = e.Message.Length > 150 ? e.Message[..150] + "…" : e.Message;
            Console.WriteLine($" {(e.NodeId is null ? "" : $"[{e.NodeId}] ")}{message}");
        }
    }
}

/// <summary>A real human in the loop: approvals and clarification answers from the console.</summary>
internal sealed class ConsoleApprovalProvider : IApprovalProvider
{
    public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        var approver = $"human:{Environment.UserName}";
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine($"── HUMAN CHECKPOINT [{request.Kind}] {request.Title} (risk {request.Risk}) ──");
        Console.ResetColor();
        Console.WriteLine(request.Summary);
        foreach (var line in request.Evidence)
            Console.WriteLine($"  • {line}");

        if (request.Questions.Count > 0)
        {
            var answers = new Dictionary<string, string>();
            foreach (var q in request.Questions)
            {
                Console.WriteLine($"\n{q.Id}: {q.Question}");
                if (q.Why is not null)
                    Console.WriteLine($"   why it matters: {q.Why}");
                if (q.Options is { Count: > 0 })
                    Console.WriteLine($"   options: {string.Join(" | ", q.Options)}");
                Console.Write($"   answer [{q.DefaultAnswer}]: ");
                var input = Console.ReadLine();
                answers[q.Id] = string.IsNullOrWhiteSpace(input) ? q.DefaultAnswer : input.Trim();
            }
            return Task.FromResult(new ApprovalDecision(true, approver, "Clarifications provided.", answers));
        }

        Console.Write("Approve? [y]es / [n]o: ");
        var approved = (Console.ReadLine() ?? "").Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
        string? comment = null;
        if (!approved)
        {
            Console.Write("Feedback for the agent: ");
            comment = Console.ReadLine();
        }
        return Task.FromResult(new ApprovalDecision(approved, approver, approved ? "Approved at console." : comment));
    }
}
