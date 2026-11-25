namespace JobDist.Web.Services;

// Client copies of the analytics DTOs returned by /api/admin/* and */risk (server types live in JobDist.Core, not in Contracts).

/// <summary><c>GET /api/admin/summary</c>.</summary>
public sealed record ClusterSummary(int WorkersOnline, int WorkersDraining, int WorkersOffline, int TotalCapacity, int ActiveJobs,
    Dictionary<string, int> JobsByStatus, Dictionary<string, int> PendingByType, string? LeaderInstance, bool ThisInstanceIsLeader, DateTimeOffset GeneratedUtc);

/// <summary>One factor of a risk score.</summary>
public sealed record RiskFactor(string Factor, double Points, string Explanation);

/// <summary>Risk assessment (0-100).</summary>
public sealed record RiskAssessment(string Subject, double Score, string Level, List<RiskFactor> Factors, string Recommendation);

/// <summary>Detected anomaly.</summary>
public sealed record Anomaly(string Kind, string Severity, string Subject, string Description, double ObservedValue, double ExpectedValue, double Score, DateTimeOffset DetectedUtc);

/// <summary>Anomaly report for a window.</summary>
public sealed record AnomalyReport(DateTimeOffset FromUtc, DateTimeOffset ToUtc, List<Anomaly> Anomalies);

/// <summary>Learned performance of one worker for one job type.</summary>
public sealed record PerformanceStat(Guid WorkerId, string JobType, int Samples, int Successes, int Failures, double MeanDurationMs, double StdDevDurationMs, DateTimeOffset LastUpdatedUtc);

/// <summary>Worker recommended for a job type.</summary>
public sealed record WorkerRecommendation(Guid WorkerId, string WorkerName, double Score, double LoadRatio, double ExpectedDurationMs, double SuccessProbability, string Reason);
