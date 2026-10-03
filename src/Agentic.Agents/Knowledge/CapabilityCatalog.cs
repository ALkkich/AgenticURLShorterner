using Agentic.Agents.Models;
using Agentic.Orchestrator.Governance;

namespace Agentic.Agents.Knowledge;

public sealed record AcceptanceTemplate(string Criterion, IReadOnlyList<string> VerifiedBy);

public sealed record RootCauseRule(string Pattern, string Explanation);

/// <summary>
/// A unit of product capability the agents know how to specify, design, implement and verify.
/// The catalog is the agents' domain knowledge; an LLM-backed agent would replace the
/// trigger matching and recipes with generation, but keep the same contracts and gates.
/// </summary>
public sealed record CapabilityDefinition
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required ChangeType Kind { get; init; }

    /// <summary>Regexes that indicate an explicit request for this capability.</summary>
    public IReadOnlyList<string> Triggers { get; init; } = [];

    public IReadOnlyList<string> Functional { get; init; } = [];
    public IReadOnlyList<AcceptanceTemplate> Acceptance { get; init; } = [];
    public IReadOnlyList<string> NonFunctional { get; init; } = [];
    public required Component Component { get; init; }
    public IReadOnlyList<Endpoint> Endpoints { get; init; } = [];
    public Adr? Adr { get; init; }
    public IReadOnlyList<RiskItem> Risks { get; init; } = [];
    public IReadOnlyList<string> GreenfieldRecipes { get; init; } = [];
    public IReadOnlyList<string> Recipes { get; init; } = [];

    /// <summary>Code symbols this capability touches (drives brownfield impact analysis).</summary>
    public IReadOnlyList<string> Touchpoints { get; init; } = [];

    public IReadOnlyList<string> DataFlows { get; init; } = [];
    public RootCauseRule? RootCause { get; init; }
    public IReadOnlyList<string> ReleaseNotes { get; init; } = [];
}

/// <summary>Vague language and the questions that turn it into an engineering decision.</summary>
public sealed record AmbiguityRule(string Pattern, string Term, IReadOnlyList<ClarificationQuestion> Questions);

public static class CapabilityCatalog
{
    private static readonly Dictionary<string, string> Created = new() { ["201"] = "LinkResponse", ["400"] = "ValidationProblem", ["409"] = "Problem (alias taken)" };

    public static readonly IReadOnlyList<CapabilityDefinition> All =
    [
        new()
        {
            Id = "link-creation", Title = "Create short links", Kind = ChangeType.Greenfield,
            Triggers = [@"\bshorten", @"short (code|link|url)"],
            Functional = ["Accept an absolute http(s) URL and return a unique short code and short URL."],
            Acceptance =
            [
                new("A valid http(s) URL returns 201 with code, short URL and Location header.", ["Create_returns_201_with_location_and_short_url", "Create_generates_a_code_and_stamps_creation_time"]),
                new("Relative, non-http(s), credential-bearing or oversized URLs are rejected with 400 problem+json.", ["Create_with_invalid_url_returns_validation_problem", "Rejects_missing_relative_non_http_or_credentialed_urls", "Rejects_urls_over_the_length_limit"]),
                new("Code collisions are retried a bounded number of times, then fail safely.", ["Create_retries_on_code_collision", "Create_fails_safely_when_collision_retries_are_exhausted"]),
                new("Generated codes are unpredictable base62.", ["Generates_codes_of_requested_length_from_base62_alphabet", "Generated_codes_are_not_trivially_repeated"]),
            ],
            NonFunctional = ["Codes must not be enumerable (CSPRNG).", "Create is atomic under concurrency (no duplicate codes)."],
            Component = new("LinkService + Domain", "Validation, code allocation, link lifecycle", ["src/UrlShortener.Api/Services/LinkService.cs", "src/UrlShortener.Api/Domain/"]),
            Endpoints = [new("POST", "/api/links", "Create a short link", "CreateLinkRequest", Created)],
            Adr = new("ADR-001", "Random base62 codes with bounded collision retry",
                "7-char codes from a CSPRNG over base62; insert with atomic TryAdd; retry ≤ N times on collision.",
                "Unpredictable codes prevent enumeration of other users' links; 62^7 ≈ 3.5e12 keeps collisions rare.",
                ["Hash of URL (deterministic, but predictable and leaks existence)", "Sequential id + base62 (enumerable)"],
                "Same URL shortened twice yields two codes (no dedupe)."),
            Risks = [new("R-COL", "Code-space collisions under high volume", "Low", "Medium", "Bounded retry + 503 with alerting; widen CodeLength via config.")],
            GreenfieldRecipes = ["gf-scaffold", "gf-domain", "gf-storage", "gf-service", "gf-api"],
            Touchpoints = ["LinkService", "CreateLinkCommand", "LinkValidator"],
            DataFlows = ["POST /api/links → LinkEndpoints.CreateLink → LinkService.Create → LinkValidator → ILinkRepository.TryAdd"],
        },
        new()
        {
            Id = "redirect", Title = "Redirect short links", Kind = ChangeType.Greenfield,
            Triggers = [@"redirect", @"visit(ing)? the short"],
            Functional = ["Resolve a short code and redirect the client to the destination URL, recording a click."],
            Acceptance =
            [
                new("A known code redirects to its destination.", ["Redirect_sends_client_to_target"]),
                new("Unknown codes return 404.", ["Unknown_code_returns_404"]),
            ],
            NonFunctional = ["Redirect path does no I/O beyond one store lookup (latency-critical)."],
            Component = new("Redirect endpoint", "Hot path: resolve + record + redirect", ["src/UrlShortener.Api/Api/LinkEndpoints.cs"]),
            Endpoints = [new("GET", "/{code}", "Redirect to destination", null, new Dictionary<string, string> { ["301"] = "Location header", ["404"] = "Not found" })],
            Adr = new("ADR-002", "Permanent (301) redirects",
                "Respond 301 Moved Permanently.", "Cacheable by browsers and CDNs, minimising load on the redirect path.",
                ["302 Found (uncached, every click reaches the service)"], "Repeat clicks served from browser cache are not observed by analytics."),
            GreenfieldRecipes = ["gf-api"],
            Touchpoints = ["RedirectToTarget", "Resolve"],
            DataFlows = ["GET /{code} → LinkService.Resolve → ILinkRepository.Find → LinkService.RecordClick → IClickAnalytics.Record"],
        },
        new()
        {
            Id = "custom-alias", Title = "Custom aliases", Kind = ChangeType.Greenfield,
            Triggers = [@"custom alias", @"vanity", @"alias"],
            Functional = ["Allow an optional custom alias instead of a generated code."],
            Acceptance =
            [
                new("A taken alias is rejected with 409 and the original link is unchanged.", ["Duplicate_alias_returns_409", "Create_rejects_duplicate_custom_alias"]),
                new("Malformed or reserved aliases are rejected.", ["Rejects_malformed_or_reserved_aliases", "Accepts_well_formed_aliases"]),
            ],
            Component = new("LinkValidator", "Alias syntax and reserved words", ["src/UrlShortener.Api/Domain/LinkValidator.cs"]),
            GreenfieldRecipes = ["gf-domain"],
            Touchpoints = ["ValidateAlias", "ReservedAliases"],
        },
        new()
        {
            Id = "click-analytics", Title = "Click analytics", Kind = ChangeType.Greenfield,
            Triggers = [@"analytics", @"\bstats\b", @"click(s)? (count|per)", @"unique visitors"],
            Functional = ["Expose per-link statistics: total clicks, unique visitors, last click, clicks per UTC day, top referrer hosts."],
            Acceptance =
            [
                new("Stats aggregate totals, unique visitors, per-day counts and top referrers.", ["Aggregates_totals_unique_visitors_days_and_referrers", "Stats_reflect_redirects_with_referrer_host_only"]),
                new("No raw IPs or full referrer URLs are stored (privacy).", ["Stats_reflect_redirects_with_referrer_host_only"]),
                new("Recording is safe under concurrent clicks and memory is bounded.", ["Concurrent_recording_loses_no_clicks", "Caps_distinct_referrers_to_bound_memory"]),
            ],
            NonFunctional = ["Visitors pseudonymised with salted HMAC; referrer reduced to host.", "Per-link memory is bounded."],
            Component = new("Analytics", "Aggregated counters per link", ["src/UrlShortener.Api/Analytics/", "src/UrlShortener.Api/Services/VisitorHasher.cs"]),
            Endpoints = [new("GET", "/api/links/{code}/stats", "Link statistics", null, new Dictionary<string, string> { ["200"] = "LinkStats", ["404"] = "Not found" })],
            Adr = new("ADR-003", "Aggregate counters instead of raw click events",
                "Maintain per-link counters; hash visitor IPs with HMAC-SHA256 + secret salt; keep referrer host only.",
                "Bounded memory and no click-level personal data trail (privacy by design).",
                ["Append-only event log + batch aggregation (better for ad-hoc analytics, needs retention policy)"],
                "New metrics cannot be computed retroactively."),
            Risks = [new("R-PII", "Analytics could capture personal data", "Medium", "High", "HMAC visitor hash, referrer host only, compliance policy blocks PII in logs.")],
            GreenfieldRecipes = ["gf-analytics", "gf-service"],
            Touchpoints = ["IClickAnalytics", "ClickEvent", "LinkStats", "RecordClick"],
        },
        new()
        {
            Id = "link-management", Title = "Link lookup and deletion", Kind = ChangeType.Greenfield,
            Triggers = [@"\bdelete", @"look ?up", @"metadata", @"manage"],
            Functional = ["Get link metadata by code.", "Delete a link and its analytics."],
            Acceptance = [new("Deleting a link makes it unreachable and removes its analytics.", ["Delete_makes_link_unreachable", "Delete_removes_link_and_its_analytics"])],
            Component = new("Links API", "Metadata and lifecycle endpoints", ["src/UrlShortener.Api/Api/"]),
            Endpoints =
            [
                new("GET", "/api/links/{code}", "Link metadata", null, new Dictionary<string, string> { ["200"] = "LinkResponse", ["404"] = "Not found" }),
                new("DELETE", "/api/links/{code}", "Delete link", null, new Dictionary<string, string> { ["204"] = "Deleted", ["404"] = "Not found" }),
            ],
            Risks = [new("R-AUTHZ", "No authentication: anyone can delete any link", "High", "High", "Out of scope for prototype; gateway auth (API keys/OAuth) before production.")],
            GreenfieldRecipes = ["gf-api"],
        },
        new()
        {
            Id = "health-probes", Title = "Health probes", Kind = ChangeType.Greenfield,
            Triggers = [@"health", @"reliab", @"probe"],
            Functional = ["Expose liveness and readiness endpoints."],
            Acceptance = [new("Liveness and readiness endpoints return 200 when healthy.", ["Health_endpoints_report_healthy"])],
            NonFunctional = ["Errors returned as RFC 7807 problem details; no stack traces."],
            Component = new("Hosting", "Health checks, problem details, options validation on start", ["src/UrlShortener.Api/Program.cs"]),
            Endpoints =
            [
                new("GET", "/health/live", "Liveness", null, new Dictionary<string, string> { ["200"] = "Healthy" }),
                new("GET", "/health/ready", "Readiness", null, new Dictionary<string, string> { ["200"] = "Healthy" }),
            ],
            GreenfieldRecipes = ["gf-scaffold", "gf-api"],
        },

        // ---- Brownfield ----
        new()
        {
            Id = "fix-redirect-caching", Title = "Fix analytics undercount caused by cached redirects", Kind = ChangeType.BugFix,
            Triggers = [@"undercount", @"repeat (click|visit)", @"clicks? (are|is)? ?(not|n't) (counted|recorded)"],
            Functional = ["Every redirect must reach the service so that each click is counted."],
            Acceptance =
            [
                new("Redirects are temporary (302) and marked Cache-Control: no-store.", ["Redirect_is_temporary_and_not_cacheable", "Redirect_sends_client_to_target"]),
                new("Repeat clicks from the same client are each counted.", ["Repeat_clicks_are_each_counted"]),
            ],
            Component = new("Redirect endpoint", "Hot path: resolve + record + redirect", ["src/UrlShortener.Api/Api/LinkEndpoints.cs"]),
            Endpoints = [new("GET", "/{code}", "Redirect to destination", null, new Dictionary<string, string> { ["302"] = "Location header, Cache-Control: no-store", ["404"] = "Not found" }, "301 → 302 + no-store")],
            Adr = new("ADR-004", "Temporary, non-cacheable redirects (supersedes ADR-002)",
                "Respond 302 Found with Cache-Control: no-store.",
                "Analytics accuracy is a product requirement; 301s are cached indefinitely by browsers so repeat clicks never reach the service.",
                ["307 Temporary Redirect (method-preserving; unnecessary for GET)", "Keep 301 and estimate repeats (inaccurate)"],
                "Higher redirect traffic hits the service; acceptable at current scale, revisit with edge caching of lookups."),
            Risks = [new("R-LOAD", "More requests reach the redirect path once caching stops", "Medium", "Low", "Redirect path is O(1) in-memory; monitor p95 latency.")],
            Recipes = ["bf-redirect-caching"],
            Touchpoints = ["RedirectToTarget", "Redirect_sends_client_to_target"],
            DataFlows = ["Browser → GET /{code} (now uncached on every click) → RecordClick"],
            RootCause = new(@"permanent:\s*true", "Redirects are issued as 301 Moved Permanently; browsers cache them, so repeat clicks never reach the service and are not counted."),
            ReleaseNotes = ["Fixed: repeat clicks were undercounted because redirects were cached by browsers (now 302 + no-store)."],
        },
        new()
        {
            Id = "link-expiration", Title = "Link expiration", Kind = ChangeType.Enhancement,
            Triggers = [@"expir", @"\bttl\b", @"time-limited"],
            Functional = ["Allow an optional expiry timestamp at creation.", "Expired links stop redirecting and report 410 Gone."],
            Acceptance =
            [
                new("A future expiresAt is accepted and returned; a past one is rejected with 400.", ["Create_accepts_future_expiry", "Create_rejects_expiry_in_the_past"]),
                new("An expired link returns 410 Gone and the click is not counted.", ["Expired_link_returns_410_and_is_not_counted"]),
                new("Links without expiry keep working indefinitely (backward compatible).", ["Link_without_expiry_never_expires"]),
                new("Metadata exposes expiresAt.", ["Metadata_includes_expiry"]),
            ],
            NonFunctional = ["Backward compatible: expiresAt is optional in requests and responses.", "Expiry evaluated with the injected TimeProvider (testable, UTC)."],
            Component = new("ShortLink + LinkService", "Expiry on the domain model, evaluated on resolve", ["src/UrlShortener.Api/Domain/ShortLink.cs", "src/UrlShortener.Api/Services/LinkService.cs"]),
            Endpoints =
            [
                new("POST", "/api/links", "Create a short link", "CreateLinkRequest { url, customAlias?, expiresAt? }", Created, "adds optional expiresAt"),
                new("GET", "/{code}", "Redirect to destination", null, new Dictionary<string, string> { ["302"] = "Location", ["404"] = "Not found", ["410"] = "Expired" }, "adds 410 Gone"),
            ],
            Adr = new("ADR-005", "Lazy expiry evaluated on read",
                "Store expiresAt on the link; Resolve() returns Expired when now ≥ expiresAt; API maps to 410 Gone.",
                "No background job or store-specific TTL needed; deterministic and testable with TimeProvider.",
                ["Store-level TTL (Redis EXPIRE) — couples domain to store", "Background purge job — extra moving part"],
                "Expired links occupy storage until a future cleanup job; alias stays reserved after expiry."),
            Risks = [new("R-CLOCK", "Clock skew between instances could flip expiry", "Low", "Low", "UTC TimeProvider; expiry granularity is seconds.")],
            Recipes = ["bf-expiry-domain", "bf-expiry-service", "bf-expiry-api", "bf-expiry-tests"],
            Touchpoints = ["ShortLink", "CreateLinkCommand", "Resolve", "CreateLinkRequest", "LinkResponse", "RedirectToTarget"],
            DataFlows = ["POST /api/links {expiresAt} → LinkService.Create (validate future) → ShortLink.ExpiresAt", "GET /{code} → LinkService.Resolve → IsExpiredAt(now) → 410 | 302"],
            ReleaseNotes = ["Added: optional `expiresAt` on link creation; expired links return 410 Gone."],
        },

        // ---- Abuse / safety (reachable from the ambiguous scenario) ----
        new()
        {
            Id = "destination-safety", Title = "Block internal destinations", Kind = ChangeType.Enhancement,
            Triggers = [@"(internal|private|loopback) (host|address|ip|destination)", @"\bssrf\b"],
            Functional = ["Reject destination URLs pointing at loopback, private, link-local or internal hosts."],
            Acceptance =
            [
                new("Loopback, private, link-local, metadata and *.internal destinations are rejected.", ["Rejects_internal_destinations", "Create_with_internal_destination_returns_400"]),
                new("Public destinations are still accepted.", ["Accepts_public_destinations"]),
            ],
            NonFunctional = ["No DNS resolution on the create path (latency, DNS-rebinding TOCTOU)."],
            Component = new("LinkValidator", "Destination safety rules", ["src/UrlShortener.Api/Domain/LinkValidator.cs"]),
            Adr = new("ADR-006", "Static destination checks without DNS resolution",
                "Reject IP literals in private/loopback/link-local/CGNAT ranges and localhost/.local/.internal names at creation.",
                "Removes the most common pivot (shortened links to intranet/metadata endpoints) with zero latency cost.",
                ["Resolve DNS at create time (rebinding TOCTOU, latency)", "Third-party URL reputation API (cost, availability; follow-up)"],
                "Public hostnames resolving to private IPs are not caught; egress controls remain the defence in depth."),
            Risks = [new("R-DNS", "Hostname that resolves to a private IP bypasses the check", "Low", "Medium", "Document; recommend reputation service + egress proxy as follow-up.")],
            Recipes = ["ab-destination-safety"],
            Touchpoints = ["ValidateTargetUrl", "LinkValidator"],
            ReleaseNotes = ["Security: destinations on internal, loopback and private networks are rejected."],
        },
        new()
        {
            Id = "rate-limit-creation", Title = "Rate limit link creation", Kind = ChangeType.Enhancement,
            Triggers = [@"rate[- ]?limit\w* (link )?creat", @"throttl\w* creat"],
            Functional = ["Limit link creation per client; excess requests receive 429 with Retry-After."],
            Acceptance = [new("Creating links beyond the per-client limit returns 429 with Retry-After.", ["Create_beyond_limit_returns_429_with_retry_after"])],
            NonFunctional = ["Limits are configuration-driven (RateLimiting section).", "Partitioned per client address."],
            Component = new("Rate limiting", "ASP.NET Core fixed-window limiter policies", ["src/UrlShortener.Api/RateLimiting.cs", "src/UrlShortener.Api/Program.cs"]),
            Endpoints = [new("POST", "/api/links", "Create a short link", "CreateLinkRequest", new Dictionary<string, string>(Created) { ["429"] = "Too many requests (Retry-After)" }, "adds 429")],
            Adr = new("ADR-007", "In-process fixed-window rate limiting per client",
                "Use Microsoft.AspNetCore.RateLimiting with a per-IP fixed window; limits bound from configuration.",
                "Built into the framework (no new dependency); simple to reason about; config-tunable without redeploy.",
                ["Token bucket (smoother bursts; more tuning)", "Gateway/WAF rate limiting (preferred long-term, outside this repo)"],
                "Per-instance counters (N instances ⇒ N× limit); behind a proxy ForwardedHeaders must be configured."),
            Risks = [new("R-NAT", "Clients behind shared NAT share a limit", "Medium", "Low", "Generous default; move to authenticated keys when auth lands.")],
            Recipes = ["ab-rate-limit-creation"],
            Touchpoints = ["MapPost", "AddUrlShortener", "Program"],
            ReleaseNotes = ["Security: link creation is rate limited per client (configurable)."],
        },
        new()
        {
            Id = "rate-limit-redirects", Title = "Rate limit redirects", Kind = ChangeType.Enhancement,
            Triggers = [@"rate[- ]?limit\w*[^.]*redirect", @"redirects? (too|as well)", @"scrapers?"],
            Functional = ["Limit redirect requests per client with a generous budget."],
            Acceptance = [new("Redirect floods from one client receive 429.", ["Redirects_beyond_limit_return_429"])],
            Component = new("Rate limiting", "Redirect policy", ["src/UrlShortener.Api/Api/LinkEndpoints.cs"]),
            Endpoints = [new("GET", "/{code}", "Redirect to destination", null, new Dictionary<string, string> { ["302"] = "Location", ["404"] = "Not found", ["429"] = "Too many requests" }, "adds 429")],
            Risks = [new("R-VIRAL", "Legitimate viral traffic from one NAT could be throttled", "Medium", "Medium", "High default (600/min); monitor 429 rate; tune via config.")],
            Recipes = ["ab-rate-limit-redirects"],
            Touchpoints = ["RedirectToTarget"],
            ReleaseNotes = ["Security: redirects are rate limited per client to curb scraping."],
        },
    ];

    public static readonly IReadOnlyList<AmbiguityRule> AmbiguityRules =
    [
        new(@"\b(safer|more secure|security|abuse|abusive|spam)\b", "safer / abuse",
        [
            new("Q-ABUSE", "Which abuse controls are in scope for this iteration?", "rate limiting and destination checks",
                ["rate limiting and destination checks", "rate limiting only", "destination checks only"],
                "\"Safer\" has no measurable definition; each control has different trade-offs."),
            new("Q-RATE", "What link-creation limit per client is acceptable (per minute)?", "20 per minute", null,
                "Too low blocks legitimate batch users; too high does not stop spam."),
            new("Q-REDIRECT-LIMIT", "Should redirects also be rate limited? (risk: throttling legitimate viral traffic)", "no", ["yes", "no"],
                "Redirects are the product's hot path."),
        ]),
        new(@"(more|higher|heavy) (traffic|load)|\bscal(e|able|ability)\b|\bfaster\b|performance", "more traffic",
        [
            new("Q-SCALE", "Is there a quantified load/latency target, and is infrastructure change in scope now?",
                "no target yet; defer infrastructure changes to a follow-up with load testing",
                ["no target yet; defer infrastructure changes to a follow-up with load testing"],
                "\"More traffic\" cannot be verified without a number; scaling the store is an architectural change."),
        ]),
        new(@"\bbetter\b|\bimprove(d)?\b", "better / improved",
        [
            new("Q-ANALYTICS", "What concrete analytics improvement is needed (metric, audience, retention)?",
                "defer: no concrete metric defined; keep current analytics", null,
                "Unspecified improvements cannot be accepted or tested."),
        ]),
    ];

    public static CapabilityDefinition Get(string id) => All.First(c => c.Id == id);
}
