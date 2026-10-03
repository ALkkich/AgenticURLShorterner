using Agentic.Orchestrator.Model;

namespace Agentic.Orchestrator.Governance;

public static class ApprovalKind
{
    public const string StageExit = "stage-exit";
    public const string ChangeSet = "change-set";
    public const string Clarification = "clarification";
}

public sealed record ClarificationQuestion(string Id, string Question, string DefaultAnswer, IReadOnlyList<string>? Options = null, string? Why = null);

public sealed record ApprovalRequest(
    string RunId,
    string NodeId,
    string Kind,
    string Title,
    RiskLevel Risk,
    string Summary,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<ClarificationQuestion> Questions)
{
    /// <summary>1-based count of checkpoints of this kind already raised for this node (for scripted responses).</summary>
    public int Occurrence { get; init; } = 1;
}

public sealed record ApprovalDecision(bool Approved, string Approver, string? Comment = null, IReadOnlyDictionary<string, string>? Answers = null);

/// <summary>Source of human judgement (console, scripted reviewer, ticketing integration…).</summary>
public interface IApprovalProvider
{
    Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Autonomy boundary: decides which checkpoints an agent may pass on its own (recorded as a
/// policy approval) and which must go to a human. Critical actions are never auto-approved.
/// </summary>
public sealed record AutonomyPolicy(RiskLevel AutoApproveUpTo = RiskLevel.Low)
{
    public bool CanAutoApprove(ApprovalRequest request, bool nodeRequiresApproval) =>
        request.Kind != ApprovalKind.Clarification
        && !nodeRequiresApproval
        && request.Risk <= AutoApproveUpTo
        && request.Risk < RiskLevel.Critical;
}

/// <summary>Non-interactive human stand-in: approves up to a risk ceiling and answers clarifications with defaults.</summary>
public sealed class UnattendedApprovalProvider(RiskLevel approveUpTo = RiskLevel.High) : IApprovalProvider
{
    public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        if (request.Questions.Count > 0)
            return Task.FromResult(new ApprovalDecision(true, "human:unattended(defaults)", "Accepted proposed defaults; recorded as assumptions.",
                request.Questions.ToDictionary(q => q.Id, q => q.DefaultAnswer)));

        var approved = request.Risk <= approveUpTo && request.Risk < RiskLevel.Critical;
        return Task.FromResult(new ApprovalDecision(approved, "human:unattended",
            approved ? $"Auto-approved in unattended mode (risk {request.Risk} ≤ {approveUpTo})." : $"Refused: {request.Risk} actions need an interactive approver."));
    }
}

public sealed record ScriptedResponse(string NodeId, string Kind, int Occurrence, bool Approved, string Approver, string? Comment, Dictionary<string, string>? Answers);

/// <summary>Replays reviewer decisions from a scenario file (reproducible demos and tests); defers otherwise.</summary>
public sealed class ScriptedApprovalProvider(IEnumerable<ScriptedResponse> script, IApprovalProvider fallback) : IApprovalProvider
{
    private readonly List<ScriptedResponse> _script = script.ToList();

    public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        var match = _script.FirstOrDefault(s => s.NodeId == request.NodeId && s.Kind == request.Kind && s.Occurrence == request.Occurrence);
        if (match is null)
            return fallback.RequestAsync(request, cancellationToken);

        var answers = match.Answers;
        if (request.Questions.Count > 0)
        {
            answers = request.Questions.ToDictionary(q => q.Id, q => q.DefaultAnswer);
            foreach (var (id, answer) in match.Answers ?? [])
                answers[id] = answer;
        }
        return Task.FromResult(new ApprovalDecision(match.Approved, $"human:{match.Approver}", match.Comment, answers));
    }
}
