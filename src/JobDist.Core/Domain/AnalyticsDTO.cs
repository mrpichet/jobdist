namespace JobDist.Core.Domain;

/// <summary>Aggregated history of one worker for one job type (feeds the ML model and risk scoring).</summary>
public sealed record PerformanceStatDTO(
    Guid WorkerId,
    string JobType,
    int Samples,
    int Successes,
    int Failures,
    double MeanDurationMs,
    double StdDevDurationMs,
    DateTimeOffset LastUpdatedUtc);

/// <summary>Prediction of how a worker would perform a job type.</summary>
/// <param name="ExpectedDurationMs">Predicted execution time.</param>
/// <param name="SuccessProbability">0-1.</param>
/// <param name="Confidence">0-1, grows with the number of observations.</param>
/// <param name="Samples">Observations for this worker/type.</param>
public sealed record PerformancePredictionDTO(double ExpectedDurationMs, double SuccessProbability, double Confidence, int Samples);

/// <summary>Kind of anomaly.</summary>
public enum AnomalyKind
{
    /// <summary>Job submissions far above the normal rate.</summary>
    SubmissionSpike = 1,
    /// <summary>Failure rate of a job type or worker far above normal.</summary>
    HighFailureRate = 2,
    /// <summary>Job durations far above history (z-score).</summary>
    SlowExecution = 3,
    /// <summary>A worker repeatedly drops off the network.</summary>
    WorkerFlapping = 4,
    /// <summary>Queue grows while no capable worker is online.</summary>
    QueueStarvation = 5,
    /// <summary>Many jobs re-distributed because workers were lost.</summary>
    FailoverStorm = 6,
    /// <summary>Repeated rejected / invalid worker reports (possible attack or broken worker).</summary>
    SuspiciousWorkerActivity = 7
}

/// <summary>Severity of an anomaly.</summary>
public enum AnomalySeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2
}

/// <summary>One detected anomaly.</summary>
public sealed record AnomalyDTO(
    AnomalyKind Kind,
    AnomalySeverity Severity,
    string Subject,
    string Description,
    double ObservedValue,
    double ExpectedValue,
    double Score,
    DateTimeOffset DetectedUtc);

/// <summary>Result of an anomaly scan.</summary>
public sealed record AnomalyReportDTO(DateTimeOffset FromUtc, DateTimeOffset ToUtc, IReadOnlyList<AnomalyDTO> Anomalies);

/// <summary>Explained factor of a risk score.</summary>
public sealed record RiskFactorDTO(string Factor, double Points, string Explanation);

/// <summary>Risk assessment (0-100).</summary>
public sealed record RiskAssessmentDTO(string Subject, double Score, string Level, IReadOnlyList<RiskFactorDTO> Factors, string Recommendation);

/// <summary>Candidate worker for a job with its score breakdown (explainable scheduling).</summary>
public sealed record WorkerRecommendationDTO(
    Guid WorkerId,
    string WorkerName,
    double Score,
    double LoadRatio,
    double ExpectedDurationMs,
    double SuccessProbability,
    string Reason);

/// <summary>Cluster overview for dashboards.</summary>
public sealed record ClusterSummaryDTO(
    int WorkersOnline,
    int WorkersDraining,
    int WorkersOffline,
    int TotalCapacity,
    int ActiveJobs,
    IReadOnlyDictionary<string, int> JobsByStatus,
    IReadOnlyDictionary<string, int> PendingByType,
    string? LeaderInstance,
    bool ThisInstanceIsLeader,
    DateTimeOffset GeneratedUtc);

/// <summary>Counters over a time window used by anomaly detection.</summary>
public sealed record WindowStatsDTO(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    IReadOnlyList<double> SubmissionsPerBucket,
    IReadOnlyList<TypeWindowStatDTO> ByType,
    IReadOnlyList<WorkerWindowStatDTO> ByWorker);

/// <summary>Per job type window statistics.</summary>
public sealed record TypeWindowStatDTO(string JobType, int Completed, int Failed, double MeanDurationMs, double HistoricalMeanMs, double HistoricalStdDevMs, int Pending, int CapableWorkersOnline);

/// <summary>Per worker window statistics.</summary>
public sealed record WorkerWindowStatDTO(Guid WorkerId, string Name, int Completed, int Failed, int Lost, int OfflineCount);
