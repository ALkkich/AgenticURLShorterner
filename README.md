# Agentic SDLC orchestrator — URL shortener

A working prototype of an **agentic software-engineering system** that turns a requirement into a reviewable engineering outcome. It covers specification, impact analysis, design, task plan, code changes, build, tests, security review, docs and release readiness. Agents act within defined autonomy limits, and humans own approvals and final quality.

The system builds and evolves a real **URL shortener service** (ASP.NET Core 8) through three scenarios: greenfield, brownfield and ambiguous. Every run executes real `dotnet build` / `dotnet test` in an isolated sandbox. It also produces a hash-chained audit log and an engineering summary for human sign-off.

```
requirement ─▶ requirements ─▶ [impact-analysis] ─▶ design ⟨human⟩ ─▶ plan ──▶ impl:* (inserted at runtime, parallel)
                                                        └─▶ docs ─────────────────────────────┐
                                                     plan ─▶ impl:* ─▶ build ─▶ test ─────────┼─▶ release ⟨human⟩
                                                     plan ─▶ impl:* ─▶ security-review ───────┘
```

**Start here:**
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): design, orchestration model, decisions, risks
- [docs/WALKTHROUGH.md](docs/WALKTHROUGH.md): guided tour and requirement → code map
- **Example run reports** (no setup needed): [greenfield](docs/examples/greenfield-summary.md) · [brownfield](docs/examples/brownfield-summary.md) · [ambiguous](docs/examples/ambiguous-summary.md)

## Quick start

Prerequisites: **.NET 8 SDK** (internet access for the first NuGet restore). No other dependencies, no API keys.

```powershell
cd AgenticSdlc
dotnet build
dotnet test                                            # 64 tests: service + orchestrator + agents

dotnet run --project src/Agentic.Cli -- scenarios      # list scenarios
dotnet run --project src/Agentic.Cli -- run greenfield
dotnet run --project src/Agentic.Cli -- run brownfield
dotnet run --project src/Agentic.Cli -- run ambiguous
dotnet run --project src/Agentic.Cli -- metrics        # cross-run reliability metrics
dotnet run --project src/Agentic.Cli -- verify-audit   # verify the latest run's audit hash chain
```

Useful options for `run`:

| Option | Effect |
|---|---|
| `--interactive` | You are the human: approve or reject checkpoints and answer clarifications at the console (rejections take feedback that is sent back to the agent). |
| `--autonomy low\|medium\|high` | Highest risk level an agent may self-approve (default `low`). `Critical` is never self-approved. |
| `--no-faults` | Disable the scenario's fault injection. |
| `--max-minutes N` | Time budget; the run safe-stops when exceeded. |

Safe stop at any time: **Ctrl+C**, or create a file named `STOP` in the run directory (kill switch). The run halts at the next scheduling boundary. Change sets are applied atomically, so the sandbox is never left half-edited.

Run the service itself: `dotnet run --project src/UrlShortener.Api`, then `POST /api/links {"url":"https://example.com"}`.

## What a run produces

Each run writes to `.runs/<scenario>-<timestamp>/`:

| File | Content |
|---|---|
| `ENGINEERING_SUMMARY.md` | The reviewable outcome: normalised requirement, ambiguities, assumptions, impact, design and ADRs, task DAG, orchestration graph (Mermaid), validation, traceability matrix, decision lineage, metrics, risks, limitations |
| `workspace/` | The sandboxed code the agents produced (it builds; its tests pass) |
| `audit.jsonl` | Append-only, hash-chained audit trail of every attempt, gate, policy finding, approval, change, rollback and re-plan |
| `artifacts/`, `lineage.json` | Every stage output; each version records which input versions it was derived from |
| `decisions.json` | Decision lineage: what was decided, by whom (agent, human, policy or engine), why, and the alternatives considered |
| `metrics.json`, `graph.mmd` | Run metrics; final workflow graph |

## The three scenarios

| Scenario | Input | What it demonstrates |
|---|---|---|
| **greenfield** | Clear request for a URL shortener v1; empty workspace | Decomposition into 7 dependent tasks inserted into the graph at runtime (domain ∥ analytics in parallel). Injected faults exercise recovery: transient architect failure → **bounded retry**; docs writer down → **fallback agent** (docs debt recorded); compile defect → build gate fails → **rollback** of all implementation changes → **remediation** with compiler feedback → green. Result: 36/36 tests, 13/13 acceptance criteria verified. |
| **brownfield** | Bug report ("analytics undercount repeat clicks") + feature (link expiry) on the existing service | **Root cause located in source** (`permanent: true` → 301s cached by browsers). Impact analysis covers files, symbols, endpoints, data flows, and an existing test that locks in the bug. ADR-004 supersedes ADR-002. The reviewer **rejects the first design** with feedback → bounded revision. Same-file tasks are ordered and locked; independent tasks run in parallel. Result: 43/43 tests, release 1.1.0. |
| **ambiguous** | "People are abusing the shortener. Make it safer, handle more traffic, and analytics should be better." | Vague terms become **clarification questions with proposed defaults** (product owner answers 30 req/min; the rest become recorded assumptions). Scale and analytics work are **explicitly deferred** rather than guessed. A `Program.cs` change is routed to a tech lead by **change-control policy**. After the first build the product owner **changes the requirement**: only affected nodes go stale, the old plan's tasks are retracted and their changes **compensated**, the graph is re-planned (v4), and the earlier clarification is formally superseded. Result: 50/50 tests. |

> **Seeded defect.** The v1 service in `src/UrlShortener.Api` intentionally ships with 301 redirects, a realistic, common shortener bug. The brownfield scenario finds and fixes it in the sandbox. The repository baseline is never modified by runs, so scenarios are repeatable.

## Repository layout

```
src/
  UrlShortener.Api/          Target system (minimal API; domain, storage port, analytics, services)
  Agentic.Orchestrator/      Domain-agnostic engine: graph, scheduler, gates, governance, observability
  Agentic.Agents/            SDLC agents, capability catalogue, change recipes, workflow template, reporting
  Agentic.Cli/               `agentic` command line (runs, metrics, audit verification, console approvals)
tests/
  UrlShortener.Tests/        36 unit + integration tests (WebApplicationFactory, controllable clock)
  Agentic.Orchestrator.Tests/ 22 engine tests (fake agents) + 6 agent tests
scenarios/                   greenfield.json, brownfield.json, ambiguous.json
docs/ARCHITECTURE.md         Design, orchestration model, decisions, risks, limitations
docs/WALKTHROUGH.md          Guided tour and requirement → code map
docs/examples/               Engineering summaries from real runs of the three scenarios
.github/workflows/ci.yml     CI: build (warnings as errors) → tests → all scenarios → audit verification
Directory.Build.props        Shared build settings (nullable, warnings as errors, code-style enforcement)
.editorconfig                Formatting and naming conventions
```

## Testing approach

- **Service:** unit tests for code generation, validation, collision handling, analytics aggregation (including concurrency and memory bounds). Integration tests drive the real HTTP pipeline with `WebApplicationFactory` and an injected `TimeProvider`.
- **Orchestrator:** each control mechanism has a dedicated test using deterministic fake agents: wave parallelism, lock serialisation, retry budget and MTTR, fallback, saga rollback, bounded remediation, policy block, policy-driven approval and revision, autonomy limits (Critical never auto-approved), stale invalidation, waiting on stale ancestors, graph expansion with retraction and compensation, governance rejection of cyclic mutations, forced checkpoints on agent-created high-risk nodes, execution budget, kill switch, entry gates, clarification lineage, artifact lineage, audit tamper detection.
- **Agents:** ambiguity handling, scope selection, same-file task ordering, refusal on code drift, refusal to fix a bug without a located root cause.
- **Continuous integration:** [.github/workflows/ci.yml](.github/workflows/ci.yml) runs the build, all tests and all three scenarios on every push, verifies every run's audit chain, and attaches the reports and audit logs to the workflow run.
- **End to end:** the three scenarios are the system tests. Inside each run, generated code is compiled and its test suite executed. The test gate requires every acceptance criterion to map to a test that actually ran and passed (parsed from TRX).

## Limitations and trade-offs (summary)

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#limitations) for detail.

- **Agents are deterministic, not LLM-based.** They reason over a capability catalogue and apply reviewed change recipes, so they cannot satisfy requirements outside the catalogue. They say so instead of guessing. This was a deliberate choice for reproducibility and testability in a governance prototype. An LLM agent would implement the same `IAgent` contract and be held to the same gates, policies, approvals and sandbox.
- Non-interactive approvals come from scenario scripts or an unattended stand-in, and are labelled as such in the audit log. `--interactive` puts a real human in the loop.
- Single-process engine with in-memory state; the audit log and artifacts are persisted, but runs cannot be resumed after a crash.
- The service uses in-memory storage, has no authentication, and rate limits per instance. These are documented as risks and follow-ups.
