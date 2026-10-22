namespace FactoryPulse.Web.Content;

public static class BehindTheBuildContent
{
    public const string RepositoryUrl = "https://github.com/KeParsons12/FactoryPulse";
    public const string SnapshotDate = "October 22, 2026";

    public static IReadOnlyList<BuildMetric> ProjectMetrics { get; } =
    [
        new(
            "Calendar development span",
            "Oct 20–22, 2026",
            "Measured",
            "Git author dates from the first recorded commit through this case-study release. This is elapsed calendar time, not hours worked.",
            true),
        new(
            "Git commits",
            "14",
            "Measured",
            "Repository commit count through the Behind the Build release, measured with git rev-list --count HEAD.",
            true),
        new(
            "Completed milestones",
            "6",
            "Verified",
            "Six scoped delivery milestones are represented by named implementation commits in the repository history.",
            true),
        new(
            "Automated tests",
            "32",
            "Verified",
            "Passing xUnit tests reported by dotnet test on October 22, 2026. This is a test count, not a coverage percentage.",
            true),
        new(
            "Human development hours",
            "Not yet measured",
            "Unknown",
            "No reliable time-tracking record exists in the approved project artifacts. Commit timestamps are not treated as hours worked.",
            false),
        new(
            "AI tokens consumed",
            "Not yet measured",
            "Unknown",
            "Token totals are not stored in the repository and were not inferred from source size, prompts, or commit count.",
            false),
        new(
            "AI development cost",
            "Not yet measured",
            "Unknown",
            "No verified token-billing record is available. Subscription access is not presented as token-based API cost.",
            false),
        new(
            "Estimated time saved",
            "Not yet measured",
            "Unestimated",
            "A defensible traditional-development baseline was not recorded, so no speedup or savings claim is made.",
            false)
    ];

    public static IReadOnlyList<WorkflowStage> Workflow { get; } =
    [
        new("01", "Requirements", "Define the industrial problem, architecture boundaries, acceptance criteria, and a milestone small enough to verify."),
        new("02", "AI implementation", "Use Codex to produce focused code, components, tests, and documentation within the approved repository."),
        new("03", "Code review", "Inspect architecture, calculations, failure behavior, security assumptions, and the details AI can get subtly wrong."),
        new("04", "Testing", "Run automated tests and targeted integration checks, including real secure OPC UA client/server communication where recorded."),
        new("05", "Refinement", "Correct findings, strengthen reliability, improve the operator experience, document decisions, and verify again.")
    ];

    public static IReadOnlyList<BuildMilestone> Milestones { get; } =
    [
        new(
            1,
            "Live machine monitoring",
            new DateOnly(2026, 10, 20),
            "A responsive Blazor dashboard and thread-safe single-machine production simulator established the live monitoring foundation.",
            ["Blazor", "Background services", "Thread safety"],
            "14200d8",
            CommitUrl("14200d88cc27de9fc721eb6791a8e7a7fb8a4c98")),
        new(
            2,
            "Machine control & fault handling",
            new DateOnly(2026, 10, 21),
            "Validated state transitions, explicit operator controls, deterministic fault timing, and event history added industrial control behavior.",
            ["State machines", "TimeProvider", "Fault lifecycle"],
            "4930209",
            CommitUrl("4930209db17a210a424289be48ee332721ac57d8")),
        new(
            3,
            "SQLite persistence & reliability",
            new DateOnly(2026, 10, 21),
            "EF Core persistence evolved into a bounded, observable event pipeline that interlocks simulation changes when durability is unhealthy.",
            ["EF Core", "SQLite", "Bounded queues", "Health monitoring"],
            "86ecc63",
            CommitUrl("86ecc631f75cf7d4650cdfacec1f18e4373a5cb4")),
        new(
            4,
            "Production & downtime analytics",
            new DateOnly(2026, 10, 21),
            "Persisted events drive period-based production, fault, downtime, MTTR, and honestly qualified monitoring metrics.",
            ["SQL aggregation", "UTC intervals", "Manufacturing KPIs"],
            "9900416",
            CommitUrl("9900416aaa9799956cc12bde0094cf65b9924418")),
        new(
            5,
            "Secure OPC UA integration",
            new DateOnly(2026, 10, 21),
            "An independent server and subscribed read-only client exchange live machine data using explicit certificate trust and encrypted sessions.",
            ["OPC UA", "Subscriptions", "Basic256Sha256", "Stale detection"],
            "0016bce",
            CommitUrl("0016bce557ef1d7e9af70f3cf95c7d9250bfaa1e")),
        new(
            6,
            "One-command demo operations",
            new DateOnly(2026, 10, 22),
            "A coordinated launcher manages readiness, console controls, graceful shutdown, and Windows Job Object cleanup for both applications.",
            ["PowerShell", "Process ownership", "Windows Job Objects"],
            "3039b8c",
            CommitUrl("3039b8c51724895006b5afef80fcae88d982c8c8"))
    ];

    public static IReadOnlyList<EngineeringChallenge> Challenges { get; } =
    [
        new(
            "Event persistence reliability",
            "An asynchronous writer could let simulation activity outrun a slow or failed database, and an unbounded queue could grow without control.",
            "That creates a dangerous gap between what the machine appears to have done and what was durably recorded.",
            "Added a bounded queue, retries, visible persistence health, shutdown flushing, and an interlock that rejects state changes when events cannot be accepted.",
            "86ecc63",
            CommitUrl("86ecc631f75cf7d4650cdfacec1f18e4373a5cb4")),
        new(
            "Production analytics accuracy",
            "Whole-second SQL bucketing could move fractional timestamps across a boundary, and fault-free monitored time was labeled too broadly as availability.",
            "Boundary errors distort production totals; imprecise KPI language can mislead manufacturing decisions.",
            "Separated the exact-range total query, enforced half-open UTC buckets, added fractional-boundary tests, and renamed metrics to distinguish them from conventional availability and MTBF.",
            "6c60526",
            CommitUrl("6c605268f00d7ba392a11f2d0de5f1d9c30ac5f0")),
        new(
            "OPC UA trust & connection health",
            "Two independent processes needed secure discovery, explicit mutual certificate trust, typed subscriptions, and useful behavior through disconnects.",
            "Automatically trusting certificates or displaying stale values as live would undermine the industrial security story.",
            "Required Basic256Sha256 SignAndEncrypt, retained manual trust, validated status and types, timestamped good data, and implemented bounded reconnection with stale-state reporting.",
            "0016bce",
            CommitUrl("0016bce557ef1d7e9af70f3cf95c7d9250bfaa1e")),
        new(
            "Demo process ownership",
            "Starting two applications was inconvenient, while terminating only the launcher risked leaving servers holding ports in the background.",
            "Orphaned services make demos unreliable and tempt broad process-kill workarounds that can affect unrelated development work.",
            "Built readiness-aware orchestration, graceful Q/Ctrl+C shutdown, exact child-process tracking, and a private Windows Job Object that cleans only launcher-owned process trees.",
            "45f5de6",
            CommitUrl("45f5de66648d6cee331e219f522df2c816fbacaa"))
    ];

    public static IReadOnlyList<TechnologyItem> Technologies { get; } =
    [
        new("C# / .NET 10", "Core language and runtime for the web app, background simulation, OPC UA services, tests, and launcher."),
        new("ASP.NET Core Blazor", "Server-rendered, interactive operator dashboard and this engineering case study."),
        new("Entity Framework Core", "Migration-based data access and efficient historical event queries."),
        new("SQLite", "Local durable storage for production, state, fault, and monitoring-boundary events."),
        new("OPC UA", "Secure, subscribed industrial telemetry between independent simulator and client processes."),
        new("xUnit", "Deterministic unit and integration coverage for state, persistence, analytics, and telemetry conversion."),
        new("PowerShell / CMD", "One-command Windows entry point and developer-friendly build orchestration."),
        new("Windows Job Objects", "Process-tree ownership that prevents orphaned demo services after forced launcher exit."),
        new("Git / GitHub", "Incremental delivery history, reviewable corrections, and source distribution."),
        new("OpenAI Codex", "AI-assisted implementation under human-defined requirements, review, testing, and release decisions.")
    ];

    public static IReadOnlyList<BuildMetric> AiUsageMetrics { get; } =
    [
        new("AI development tool", "OpenAI Codex", "Declared", "The project was developed with Codex assistance; implementation remained guided and reviewed by the project owner.", true),
        new("Model history", "Not yet measured", "Unknown", "Exact model/session history is not part of the repository snapshot and is not inferred.", false),
        new("Input tokens", "Not yet measured", "Unknown", "No safely aggregated, project-only token record is available in the repository.", false),
        new("Cached input tokens", "Not yet measured", "Unknown", "Cache accounting is not recorded in project artifacts.", false),
        new("Output tokens", "Not yet measured", "Unknown", "Output-token totals were not recovered from private session data.", false),
        new("Development sessions", "Not yet measured", "Unknown", "Conversation count is not represented as a repository fact.", false)
    ];

    private static string CommitUrl(string hash) => $"{RepositoryUrl}/commit/{hash}";
}

public sealed record BuildMetric(
    string Label,
    string Value,
    string Status,
    string Methodology,
    bool IsMeasured);

public sealed record WorkflowStage(string Number, string Title, string Description);

public sealed record BuildMilestone(
    int Number,
    string Title,
    DateOnly CompletedOn,
    string Description,
    IReadOnlyList<string> Concepts,
    string Commit,
    string CommitUrl);

public sealed record EngineeringChallenge(
    string Title,
    string Problem,
    string Impact,
    string Resolution,
    string Commit,
    string CommitUrl);

public sealed record TechnologyItem(string Name, string Role);
