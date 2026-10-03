# Architecture

## 1. Goals and principles

1. **Agents execute; humans govern.** Agents can only *propose*: artifacts, decisions, file changes, graph changes. The engine decides what is applied, under policy, approvals and gates.
2. **Every effect is traceable.** Each artifact version records the inputs it was derived from. Each decision records its actor and rationale. Each event goes into a hash-chained audit log.
3. **Failure is a normal path.** Retries, fallback, remediation and rollback are bounded, and they are all observable in metrics.
4. **Non-linear but deterministic.** Parallel waves, dynamic graph expansion and re-planning on upstream change, all processed at a single synchronisation point so runs are reproducible.

## 2. Components

```
┌──────────────────────────── Agentic.Cli ────────────────────────────┐
│ run / metrics / verify-audit · console approvals · live timeline     │
└───────────────┬──────────────────────────────────────────────────────┘
                │
┌───────────────▼──────────── Agentic.Agents ─────────────────────────┐
│ SdlcWorkflowFactory (graph template + gates)   ScenarioRunner        │
│ Agents: requirements-analyst · codebase-analyst · solution-architect │
│         delivery-planner · implementer · build-runner · test-runner  │
│         security-reviewer · tech-writer (+docs-minimal) · release-mgr│
│ Knowledge: CapabilityCatalog · AmbiguityRules · RecipeBook           │
│ Reporting: EngineeringSummary · RunExporter                          │
└───────────────┬──────────────────────────────────────────────────────┘
                │ IAgent contract (no domain knowledge below this line)
┌───────────────▼──────────── Agentic.Orchestrator ───────────────────┐
│ Model:   StageNode · WorkflowGraph (versioned DAG, mutations)        │
│ Engine:  WorkflowEngine → WorkflowRun (scheduler, node lifecycle,    │
│          change control, compensation, re-planning, safe stop)       │
│ Context: ArtifactStore (versioned, lineage) · DecisionLog            │
│ Gates:   entry/exit gates                                            │
│ Governance: PolicyEngine (7 policies) · AutonomyPolicy · approvals   │
│ Observability: AuditLog (hash chain) · RunMetrics · ReliabilityReport│
│ Workspace: sandboxed FileSystemWorkspace · Tools: ProcessToolRunner  │
└──────────────────────────────────────────────────────────────────────┘
```

The orchestrator is **domain-agnostic**: it knows nothing about URL shorteners or C#. All SDLC knowledge lives in `Agentic.Agents`.

## 3. Orchestration model

### 3.1 Graph, nodes and gates
A run executes a `WorkflowGraph`: a versioned DAG of `StageNode`s. A node declares:

| Property | Purpose |
|---|---|
| `DependsOn` | Explicit dependency edges (validated acyclic) |
| `EntryGates` / `ExitGates` | Preconditions (e.g. required artifacts exist) and quality bars (tests green, every acceptance criterion verified, every FR traced to a component) |
| `Risk`, `RequiresApproval` | Drives human checkpoints |
| `Retry`, `FallbackAgent`, `Remediation`, `Timeout`, `MaxRevisions` | Bounded recovery |
| `Locks` | Resources (file paths) that must not be touched concurrently |
| `Tags`, `ExpandedBy` | Grouping for remediation; provenance of runtime-created nodes |

### 3.2 Scheduler: waves with a synchronisation barrier
```
loop:
  safe-stop check (Ctrl+C, STOP file, attempt budget, time budget)
  ready = nodes Pending|Stale whose *all ancestors* are Succeeded
  batch = greedy subset of ready with disjoint Locks
  retract previous expansions of any re-planning node in batch (with compensation)
  run batch in parallel (Task.WhenAll)                       ← parallel paths
  barrier: apply graph mutations, fire external events, handle failures (in order)
```
Readiness requires **all ancestors** to be settled, not just direct parents. Otherwise a node could run against outputs that a stale grandparent is about to replace. This bug was found in an end-to-end run and is covered by a regression test.

### 3.3 Node lifecycle
```
entry gates ─▶ agent attempt ─(transient)─▶ backoff retry ≤ N ─(exhausted/permanent)─▶ fallback agent
     │                │
     │           NeedsInput ─▶ clarification checkpoint ─▶ answers stored as artifact ─▶ re-run
     ▼
commit artifacts (lineage = inputs read) ─▶ change control ─▶ exit gates ─▶ checkpoint ─▶ Succeeded
                                               │                                │
                                     policy Block → fail              rejected → feedback → revision ≤ MaxRevisions
                                     RequiresApproval → human
```

### 3.4 Change control and rollback (saga)
Agents get a **read-only** view of the sandbox. Writes happen only through `ChangeSet` proposals:

1. **Policy evaluation** over the proposed diff: Block → rejected; RequiresApproval → human checkpoint at risk ≥ High.
2. **Atomic apply** under a lock. The prior content of each touched file is recorded as the change set's **compensation**.
3. The engine republishes `workspace.state`, a hash of the build-relevant files only, so doc edits don't invalidate builds.

`Rollback(nodes)` undoes those nodes' change sets in reverse order. It also undoes any **later** change set that touched the same files, since restoring older content would otherwise silently drop it. Every node whose changes were undone is rescheduled. This is used for remediation, rejection, re-planning, and full rollback on unrecoverable failure (run status `RolledBack`).

### 3.5 Non-linear control flow
| Mechanism | Trigger | Bound |
|---|---|---|
| Retry with exponential backoff | `TransientFailure`, timeout, unexpected exception | `RetryPolicy.MaxAttempts` |
| Fallback agent | Primary exhausted or permanently failed | one fallback |
| Revision | Human rejects output or change set (comment becomes `Feedback`) | `MaxRevisions` |
| Remediation | Build or test fails permanently → roll back and re-run all `implementation`-tagged nodes with the failure as feedback | `RemediationPolicy.MaxCycles` |
| Dynamic expansion | Planner returns a `GraphMutation` adding task nodes and edges | ≤ 50 nodes, governance rules below |
| Re-planning | An artifact changes; every succeeded node that consumed an older version becomes `Stale` (hash comparison, so unchanged content invalidates nothing). A stale planner first **retracts** its previous expansion (with compensation) | execution budget |

**Governance on graph mutations:** agents may add nodes and add edges (tighten ordering), but never remove nodes or loosen ordering. Only the engine retracts, and only nodes created by the node being re-planned. Mutations are validated atomically: no cycles, no unknown agents. Any agent-created node with risk ≥ High is forced behind a human checkpoint.

### 3.6 Cross-stage context and lineage
`ArtifactStore` is a versioned blackboard. `AgentContext.Read` records the exact version and hash of everything an agent reads. The engine stamps those references onto every output (`DerivedFrom`) and onto every decision (`BasedOn`). That gives three things:
- **Lineage:** "this plan v2 was derived from spec v2, which was derived from requirements.raw v2 written by the product owner".
- **Precise invalidation:** only consumers of the changed artifact re-run.
- **Idempotency:** writing identical content does not create a new version.

### 3.7 Human oversight and autonomy limits
| Checkpoint | When |
|---|---|
| Stage exit | `RequiresApproval` (design, release) or node risk ≥ Medium |
| Change set | A policy returns `RequiresApproval` (host config, deletions, change > 400 lines / 12 files, weak crypto) |
| Clarification | Agent returns `NeedsInput`; never auto-answered by policy |

`AutonomyPolicy(AutoApproveUpTo)` lets low-risk checkpoints pass as `policy:autonomy` (logged). Everything else goes to the `IApprovalProvider`: console (interactive), scripted (reproducible demos), or unattended (approves up to High and accepts defaults as assumptions). **Critical is never auto-approved by anyone but an explicit human.**

### 3.8 Policy guardrails
| Id | Category | Rule | Severity |
|---|---|---|---|
| SEC-001 | Security | Secrets (cloud keys, private keys, API keys, passwords) | Block |
| SEC-002 | Security | TLS validation bypass / weak RNG or hash in production code | Block / Approval |
| SEC-003 | Security | Package not on dependency allowlist | Block |
| SEC-004 | Security | Tool command not allowlisted, shell metacharacters, working dir outside sandbox | Block |
| CMP-001 | Compliance | Personal data (IP, referrer, email, user agent) written to logs | Block |
| CHG-001 | Change control | Protected paths (CI, prod config) / host and build config / deletions | Block / Approval |
| CHG-002 | Change control | Change budget exceeded | Approval |

Policies run at change time. The security-review stage re-runs the content policies over the **whole** tree as defence in depth. Tools run without a shell, with a timeout, inside the sandbox only.

### 3.9 Observability
- **Audit log:** JSONL, one event per action (`node.*`, `agent.*`, `gate.*`, `policy.finding`, `approval.*`, `change.applied`, `rollback.completed`, `node.stale`, `replan.retracted`, `graph.mutated`, `external.event`, `tool.executed`, `run.*`). Each event's SHA-256 covers its content and the previous hash, so `verify-audit` detects any edit, deletion or reordering.
- **Metrics per run:** node attempt success rate, retries, fallbacks, rollbacks, remediations, revisions, re-plans, stale invalidations, approvals (human, auto, rejected), policy findings and blocks, gate failures, per-stage durations, **end-to-end latency**, **MTTR** (a node's first failure → its eventual success, by mechanism).
- **Across runs:** `agentic metrics` reports run success rate, retries/rollbacks/remediations per run, mean MTTR, p50/p95 latency.

## 4. The SDLC agents

| Agent | Input → output | Notes |
|---|---|---|
| requirements-analyst | raw text → `RequirementSpec` | Capability triggers; vague-language rules → questions with defaults; answers become parameters, scope and deferrals; explicit later text supersedes earlier answers (recorded) |
| codebase-analyst | spec + workspace → `ImpactReport` | Symbol references, route discovery (incl. `MapGroup` prefixes), data flows, affected tests, blast radius → risk, **root cause located in source**; refuses a bug fix without one |
| solution-architect | spec + impact → `DesignSpec`, OpenAPI | Components, merged API contract (bug fixes replace responses, enhancements extend), ADRs (incl. supersession), risk register, FR→component traceability, review feedback folded in |
| delivery-planner | spec + design → `WorkPlan` + `GraphMutation` | Recipe closure, same-file ordering + file locks, parallel groups, critical path |
| implementer | task → `ChangeSet` | Blueprint composition (greenfield) or anchored edits (brownfield); **fails on baseline drift instead of guessing**; idempotent |
| build-runner / test-runner | workspace → reports | Real `dotnet build/test`; compiler errors are permanent (→ remediation), infrastructure errors transient (→ retry); TRX parsed into AC→test traceability |
| security-reviewer | workspace → `SecurityReport` | Whole-tree policy scan, dependency inventory, threats from risk register |
| tech-writer / docs-minimal | design → docs | API reference, design record, changelog; fallback writes a minimal reference and records documentation debt |
| release-manager | all evidence → readiness | Go/no-go checklist, semantic version, release notes, rollback plan; always behind a human checkpoint |

## 5. The target system (URL shortener)

Layered minimal API: `Api` (endpoints, contracts) → `LinkService` → `Domain` (model, validation, CSPRNG base62 codes) + ports `ILinkRepository`, `IClickAnalytics` with in-memory adapters. Key properties: atomic create (`TryAdd`) with bounded collision retry; RFC 7807 errors; options validated on start; `TimeProvider` injection; analytics as bounded aggregates; visitors pseudonymised with HMAC-SHA256; referrer reduced to host; liveness and readiness probes.

## 6. Key decisions

| Decision | Alternatives | Why |
|---|---|---|
| Deterministic, catalogue-driven agents | LLM agents | Reproducible runs and testable orchestration; the governance layer is what is being demonstrated. The `IAgent` contract is model-agnostic. |
| Agents propose, engine applies | Agents write files directly | Single choke point for policy, approval, compensation and audit |
| Wave scheduler with barrier | Fully event-driven scheduling | Deterministic ordering of mutations and failures; simpler to reason about and test. Cost: a slow node delays the next wave's start. |
| Hash-based staleness via read tracking | Re-run everything downstream | Re-runs only what consumed changed data; agents cannot forget to declare inputs |
| Per-file compensation with cascade | Whole-workspace snapshots | Correct with parallel writers; cheap |
| Remediation re-runs the whole implementation tag | Re-run only the failing task | Failure attribution from a compiler or test error is unreliable; whole-tag re-run is bounded and correct |
| Hash-chained JSONL audit | Database / external log service | Tamper-evident with zero infrastructure; can be shipped to a SIEM unchanged |

## 7. Risks and failure scenarios

| Scenario | Handling |
|---|---|
| Agent produces broken code | Build/test gates → rollback → remediation with feedback (bounded) → run fails and rolls back if still broken |
| Agent attempts something unsafe (secret, PII log, foreign package, shell injection, path escape) | Policy Block before apply; node fails; audit and decision recorded |
| Code changed since analysis | Implementer anchor check fails permanently ("refusing to guess") |
| Requirement changes mid-run | Stale propagation, retraction with compensation, re-plan; graph version increments |
| Infinite loop or runaway cost | Execution and time budgets → safe stop; bounded retries, revisions, remediations |
| Reviewer unavailable / rejects repeatedly | Revision budget → node fails → rollback |
| Audit tampering | Hash chain verification fails at the first altered event |

## 8. Limitations

- **No LLM.** Agents only cover capabilities in the catalogue. A request outside it fails explicitly ("no actionable capability"). Plugging in an LLM agent needs no orchestrator change, but it should add output-schema validation (already enforced by exit gates) and a prompt and response audit.
- **Simulated humans in non-interactive mode** (scripted or unattended, labelled in the audit log). `--interactive` provides real approvals.
- **No crash resume:** state and evidence are persisted, but replaying a run from a checkpoint is not implemented.
- **Single process:** the engine is not distributed; the per-wave barrier limits throughput for very wide graphs.
- **Promotion is manual:** the sandbox is never copied into the repository by the system; a human reviews the summary and workspace.
- **Service scope:** in-memory storage, no authentication, per-instance rate limits, static (no-DNS) destination checks. All appear in the generated risk registers and follow-ups.
