# Walkthrough

A guided tour of the prototype: what to run, what to look at, and where each capability is implemented.

## Overview

Agents do the engineering work, but they can only **propose**: artifacts, decisions, file changes, new tasks. A domain-agnostic engine runs them as a **dependency graph** in parallel waves. It checks every proposal against **policies**, stops at **human checkpoints** based on risk, **retries, falls back, remediates or rolls back** on failure, **re-plans** only what is affected when requirements change, and records everything in a **tamper-evident audit log** with full decision lineage. The system is demonstrated end to end on a URL shortener: greenfield, brownfield and an ambiguous request, each producing real code that compiles and passes its tests.

## Guided tour

| # | Look at | Command / file | What it shows |
|---|---|---|---|
| 1 | The request, as a person wrote it | [scenarios/ambiguous.json](../scenarios/ambiguous.json) | Unstructured, vague, and it changes halfway through |
| 2 | Run it with a human in the loop | `dotnet run --project src/Agentic.Cli -- run ambiguous --interactive` | You answer the clarifications and approvals (e.g. `Q-RATE` = `30 per minute`) |
| 3 | The live timeline | console output | `WAVE` = parallel batch; `POLICY` → `APPROVAL` for the `Program.cs` change; `UPSTREAM` → `STALE` → `REPLAN` → `ROLLBACK` when the requirement changes |
| 4 | The reviewable outcome | `.runs/<run>/ENGINEERING_SUMMARY.md` or [examples](examples/) | Ambiguities and resolutions, deferred scope, graph versions, AC→test traceability, decision lineage, metrics |
| 5 | The generated code | `.runs/<run>/workspace/src/UrlShortener.Api/RateLimiting.cs` | The clarification answer (30) became configuration in real, tested code |
| 6 | Failure handling | `dotnet run --project src/Agentic.Cli -- run greenfield` | Retry → fallback → compile defect → rollback → remediation → green; MTTR in the summary |
| 7 | Brownfield reasoning | [examples/brownfield-summary.md](examples/brownfield-summary.md) §2 | Root cause located at a file and line; an existing test that locks in the bug; ADR supersession |
| 8 | Integrity and reliability | `dotnet run --project src/Agentic.Cli -- verify-audit`, then `metrics` | Hash-chain verification; success rate, retries/rollbacks per run, MTTR, p50/p95 latency |
| 9 | Engineering quality | `dotnet test` and [ci.yml](../.github/workflows/ci.yml) | 64 tests; every control mechanism has its own test; CI runs all scenarios on every push |

## Requirement → implementation map

| Requirement | Where it lives |
|---|---|
| Requirement understanding, ambiguity | [RequirementsAnalystAgent](../src/Agentic.Agents/Agents/RequirementsAnalystAgent.cs), `AmbiguityRules` in [CapabilityCatalog](../src/Agentic.Agents/Knowledge/CapabilityCatalog.cs) |
| Task decomposition with dependencies | [DeliveryPlannerAgent](../src/Agentic.Agents/Agents/DeliveryPlannerAgent.cs) (closure, same-file ordering, parallel groups, critical path) |
| Codebase reasoning (brownfield) | [CodebaseAnalystAgent](../src/Agentic.Agents/Agents/CodebaseAnalystAgent.cs) |
| Explicit dependency graph, entry/exit gates | [WorkflowGraph](../src/Agentic.Orchestrator/Model/WorkflowGraph.cs), [Gates](../src/Agentic.Orchestrator/Gates/Gates.cs), [SdlcWorkflowFactory](../src/Agentic.Agents/Workflow/SdlcWorkflowFactory.cs) |
| Sequential + parallel paths with synchronisation | `ScheduleAsync` / `ReadyNodes` / `SelectNonConflicting` in [WorkflowRun](../src/Agentic.Orchestrator/Engine/WorkflowRun.cs) |
| Cross-stage context, decision lineage | [ArtifactStore](../src/Agentic.Orchestrator/Context/ArtifactStore.cs), [DecisionLog](../src/Agentic.Orchestrator/Context/DecisionLog.cs), read tracking in `AgentContext` |
| Human approval for high-impact actions | `CheckpointAsync` in [WorkflowRun.Node](../src/Agentic.Orchestrator/Engine/WorkflowRun.Node.cs), [Approvals](../src/Agentic.Orchestrator/Governance/Approvals.cs) |
| Bounded retries, fallback | `RunAgentWithRetriesAsync` in [WorkflowRun.Node](../src/Agentic.Orchestrator/Engine/WorkflowRun.Node.cs) |
| Rollback, remediation | `Rollback` in [WorkflowRun.Changes](../src/Agentic.Orchestrator/Engine/WorkflowRun.Changes.cs), `HandleFailure` in [WorkflowRun](../src/Agentic.Orchestrator/Engine/WorkflowRun.cs) |
| Safe stop | `ShouldStop` (Ctrl+C, STOP file, attempt and time budgets) |
| Policy guardrails: security, compliance, change control | [Policies](../src/Agentic.Orchestrator/Governance/Policies.cs), `ProposeChangesAsync` |
| Audit-grade observability | [AuditLog](../src/Agentic.Orchestrator/Observability/AuditLog.cs) (hash chain) |
| Reliability metrics | [RunMetrics](../src/Agentic.Orchestrator/Observability/RunMetrics.cs), `agentic metrics` |
| Dynamic re-planning | `OnArtifactChanged`, `RetractExpansion`, `TryApplyMutation` in [WorkflowRun](../src/Agentic.Orchestrator/Engine/WorkflowRun.cs) |
| Controlled autonomy | Read-only workspace for agents, `AutonomyPolicy`, mutation governance |
| Engineering outputs | Generated code and tests in the sandbox, OpenAPI contract, docs, release notes |
| Final engineering summary | [RunExporter](../src/Agentic.Agents/Reporting/RunExporter.cs) → `ENGINEERING_SUMMARY.md` |
