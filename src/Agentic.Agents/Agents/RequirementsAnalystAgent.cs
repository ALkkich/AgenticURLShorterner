using System.Text.RegularExpressions;
using Agentic.Agents.Knowledge;
using Agentic.Agents.Models;
using Agentic.Orchestrator.Agents;
using Agentic.Orchestrator.Context;
using Agentic.Orchestrator.Governance;

namespace Agentic.Agents.Agents;

/// <summary>
/// Turns free-text intent into a normalised, testable specification: capabilities, functional and
/// non-functional requirements, acceptance criteria (each mapped to verifying tests), assumptions
/// and explicit out-of-scope items. Vague language is never guessed at — it becomes clarification
/// questions with proposed defaults, answered by a human (or accepted defaults, recorded as assumptions).
/// </summary>
public sealed class RequirementsAnalystAgent : IAgent
{
    public string Name => "requirements-analyst";

    public Task<AgentResult> ExecuteAsync(AgentContext ctx)
    {
        var raw = ctx.ReadText(WellKnownArtifacts.RequirementsRaw);
        if (string.IsNullOrWhiteSpace(raw))
            return Task.FromResult(AgentResult.Permanent("No requirement text was provided."));

        var isGreenfield = ctx.Setting(SettingKeys.Baseline) == "empty";
        var explicitCaps = CapabilityCatalog.All
            .Where(c => c.Triggers.Any(t => Regex.IsMatch(raw, t, RegexOptions.IgnoreCase)))
            .ToList();

        var ambiguities = CapabilityCatalog.AmbiguityRules
            .Select(rule => (Rule: rule, Match: Regex.Match(raw, rule.Pattern, RegexOptions.IgnoreCase)))
            .Where(x => x.Match.Success)
            .ToList();
        var unanswered = ambiguities.SelectMany(a => a.Rule.Questions).Where(q => !ctx.Answers.ContainsKey(q.Id)).ToList();
        if (unanswered.Count > 0)
        {
            var terms = string.Join(", ", ambiguities.Select(a => $"\"{a.Match.Value}\""));
            return Task.FromResult(AgentResult.NeedsInput(
                $"Requirement contains unmeasurable language ({terms}); {unanswered.Count} decisions are needed before it can be specified.", unanswered));
        }

        var decisions = new List<DecisionDraft>();
        var assumptions = new List<string>
        {
            "In-memory storage is acceptable for this iteration; the repository port allows a durable store later.",
            "Authentication/authorisation is out of scope for this iteration (gateway responsibility).",
        };
        var outOfScope = new List<string>();
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["CreatePermitLimit"] = "20" };
        var resolutions = new List<Ambiguity>();

        // Scope: greenfield builds the core; brownfield only adds changes on top of existing capabilities.
        var selected = isGreenfield
            ? explicitCaps.Where(c => c.Kind == ChangeType.Greenfield).ToList()
            : explicitCaps.Where(c => c.Kind != ChangeType.Greenfield).ToList();
        if (isGreenfield)
            outOfScope.AddRange(explicitCaps.Where(c => c.Kind != ChangeType.Greenfield).Select(c => $"{c.Title} (enhancement; schedule after v1)"));
        else if (explicitCaps.Any(c => c.Kind == ChangeType.Greenfield))
            decisions.Add(new DecisionDraft(DecisionCategory.Scope, "Existing capabilities referenced as context only",
                $"Mentions of {string.Join(", ", explicitCaps.Where(c => c.Kind == ChangeType.Greenfield).Select(c => c.Id))} describe current behaviour, not new work."));

        ApplyAnswers(ctx, ambiguities.Select(a => a.Rule), selected, parameters, assumptions, outOfScope, resolutions, decisions, raw);

        if (selected.Count == 0)
            return Task.FromResult(AgentResult.Permanent("No actionable capability could be identified; the requirement must be rewritten by a human."));

        var spec = BuildSpec(raw, isGreenfield, selected, resolutions, assumptions, outOfScope, parameters);
        var result = AgentResult.Success($"{spec.Functional.Count} FRs, {spec.AcceptanceCriteria.Count} acceptance criteria across {spec.Capabilities.Count} capabilities; {resolutions.Count} ambiguities resolved.")
            .WithJson(ArtifactKeys.RequirementSpec, spec);
        result.Decisions.AddRange(decisions);
        result.WithDecision(DecisionCategory.Scope, $"Scope: {string.Join(", ", spec.Capabilities)}",
            isGreenfield ? "Capabilities explicitly requested for v1." : "Only the requested changes; existing behaviour must remain backward compatible.",
            outOfScope.ToArray());
        return Task.FromResult(result);
    }

    /// <summary>Translates human answers into scope, parameters, deferrals and recorded assumptions.</summary>
    private static void ApplyAnswers(AgentContext ctx, IEnumerable<AmbiguityRule> rules, List<CapabilityDefinition> selected,
        Dictionary<string, string> parameters, List<string> assumptions, List<string> outOfScope, List<Ambiguity> resolutions,
        List<DecisionDraft> decisions, string raw)
    {
        void Add(string capabilityId)
        {
            if (selected.All(c => c.Id != capabilityId))
                selected.Add(CapabilityCatalog.Get(capabilityId));
        }

        foreach (var rule in rules)
        {
            var superseded = new HashSet<string>(StringComparer.Ordinal);
            foreach (var question in rule.Questions)
            {
                var answer = ctx.Answers[question.Id];
                switch (question.Id)
                {
                    case "Q-ABUSE":
                        if (answer.Contains("rate", StringComparison.OrdinalIgnoreCase))
                            Add("rate-limit-creation");
                        if (answer.Contains("destination", StringComparison.OrdinalIgnoreCase))
                            Add("destination-safety");
                        break;
                    case "Q-RATE":
                        var match = Regex.Match(answer, @"\d+");
                        var limit = match.Success ? Math.Clamp(int.Parse(match.Value), 1, 100_000) : 20;
                        parameters["CreatePermitLimit"] = limit.ToString();
                        break;
                    case "Q-REDIRECT-LIMIT":
                        var explicitlyRequested = CapabilityCatalog.Get("rate-limit-redirects").Triggers.Any(t => Regex.IsMatch(raw, t, RegexOptions.IgnoreCase));
                        if (answer.StartsWith("y", StringComparison.OrdinalIgnoreCase))
                            Add("rate-limit-redirects");
                        else if (explicitlyRequested && superseded.Add(question.Id))
                            decisions.Add(new DecisionDraft(DecisionCategory.Replan, "Explicit requirement supersedes earlier clarification (redirect rate limiting now in scope)",
                                $"Earlier answer to {question.Id} was '{answer}', but the current requirement text explicitly asks for it."));
                        else
                            outOfScope.Add("Rate limiting of redirects (declined in clarification; viral-traffic risk).");
                        break;
                    case "Q-SCALE":
                        outOfScope.Add("Scalability/infrastructure work: no quantified target yet — follow-up with load testing.");
                        break;
                    case "Q-ANALYTICS":
                        if (answer.StartsWith("defer", StringComparison.OrdinalIgnoreCase))
                            outOfScope.Add("Analytics improvements: no concrete metric defined — follow-up with product.");
                        else
                            assumptions.Add($"Analytics request noted for product follow-up: {answer}");
                        break;
                }
                if (answer == question.DefaultAnswer && !superseded.Contains(question.Id))
                    assumptions.Add($"{question.Id}: proposed default accepted — {answer}.");
            }
            resolutions.Add(new Ambiguity(rule.Term, Regex.Match(raw, rule.Pattern, RegexOptions.IgnoreCase).Value,
                string.Join("; ", rule.Questions.Select(q =>
                    $"{q.Id}={ctx.Answers[q.Id]}{(superseded.Contains(q.Id) ? " (superseded by explicit requirement)" : "")}"))));
        }
    }

    private static RequirementSpec BuildSpec(string raw, bool isGreenfield, IReadOnlyList<CapabilityDefinition> caps,
        IReadOnlyList<Ambiguity> resolutions, List<string> assumptions, List<string> outOfScope, IReadOnlyDictionary<string, string> parameters)
    {
        var functional = new List<Requirement>();
        var acceptance = new List<AcceptanceCriterion>();
        var nonFunctional = new List<Requirement>();
        foreach (var cap in caps)
        {
            foreach (var text in cap.Functional)
            {
                var fr = new Requirement($"FR-{functional.Count + 1}", text, cap.Id, cap.Kind == ChangeType.BugFix ? "P0" : "P1");
                functional.Add(fr);
                if (fr.Text == cap.Functional[0])
                    foreach (var ac in cap.Acceptance)
                        acceptance.Add(new AcceptanceCriterion($"AC-{acceptance.Count + 1}", fr.Id, cap.Id, ac.Criterion, ac.VerifiedBy));
            }
            nonFunctional.AddRange(cap.NonFunctional.Select(n => new Requirement($"NFR-{nonFunctional.Count + 1}", n, cap.Id, "P1")));
        }
        nonFunctional.Add(new Requirement($"NFR-{nonFunctional.Count + 1}", "Every change ships with automated tests that verify its acceptance criteria.", "*", "P0"));
        nonFunctional.Add(new Requirement($"NFR-{nonFunctional.Count + 1}", "No secrets or personal data in code or logs (policy-enforced).", "*", "P0"));

        var title = isGreenfield ? "URL shortener v1" : string.Join(" + ", caps.Select(c => c.Title));
        var statement = isGreenfield
            ? $"Build a new URL shortener service that can: {string.Join("; ", caps.Select(c => c.Title.ToLowerInvariant()))}."
            : $"Change the existing URL shortener to: {string.Join("; ", caps.Select(c => c.Title.ToLowerInvariant()))}, without breaking existing clients.";

        return new RequirementSpec(title, statement, isGreenfield, caps.Select(c => c.Id).ToList(), functional, nonFunctional, acceptance,
            resolutions, assumptions.Distinct().ToList(), outOfScope.Distinct().ToList(), parameters);
    }
}

public static class SettingKeys
{
    /// <summary>"empty" (greenfield) or "reference" (brownfield, seeded from the repository).</summary>
    public const string Baseline = "baseline";
    public const string BaselineVersion = "baseline-version";
    public const string ReferenceRoot = "reference-root";
    public const string Faults = "faults";
    public const string InjectDefect = "inject-defect";
}
