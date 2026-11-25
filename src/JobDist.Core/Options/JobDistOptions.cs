using System.ComponentModel.DataAnnotations;

namespace JobDist.Core.Options;

/// <summary>Scheduler and cluster settings (section <c>JobDist</c>).</summary>
public sealed class JobDistOptions
{
    public const string SectionName = "JobDist";

    /// <summary>Unique id of this server instance (defaults to machine name + process id).</summary>
    public string InstanceId { get; set; } = $"{Environment.MachineName}-{Environment.ProcessId}";

    /// <summary>Default max attempts per job (first run + retries).</summary>
    [Range(1, 20)] public int DefaultMaxAttempts { get; set; } = 3;

    /// <summary>Default execution timeout per attempt.</summary>
    [Range(1, 86_400)] public int DefaultTimeoutSeconds { get; set; } = 300;

    /// <summary>Max payload size in bytes.</summary>
    [Range(1, 16_777_216)] public int MaxPayloadBytes { get; set; } = 1_048_576;

    /// <summary>Heartbeat interval workers should use.</summary>
    [Range(1, 300)] public int HeartbeatIntervalSeconds { get; set; } = 5;

    /// <summary>Heartbeat silence after which a worker is declared Offline and its jobs are re-distributed.</summary>
    [Range(3, 3600)] public int OfflineAfterSeconds { get; set; } = 20;

    /// <summary>Time a worker has to report "started" after an assignment before the job is re-distributed.</summary>
    [Range(5, 3600)] public int AssignmentStartTimeoutSeconds { get; set; } = 60;

    /// <summary>Extra lease time on top of the job timeout before a running job is considered lost.</summary>
    [Range(1, 3600)] public int LeaseGraceSeconds { get; set; } = 30;

    /// <summary>Base delay of the exponential retry back-off.</summary>
    [Range(0, 3600)] public double RetryBaseDelaySeconds { get; set; } = 2;

    /// <summary>Upper bound of the retry back-off.</summary>
    [Range(0, 86_400)] public double RetryMaxDelaySeconds { get; set; } = 300;

    /// <summary>Whether attempts lost because a worker disappeared count against <c>MaxAttempts</c>.</summary>
    public bool CountWorkerLossAsAttempt { get; set; } = true;

    /// <summary>Pending jobs examined per scheduling cycle.</summary>
    [Range(1, 100_000)] public int SchedulerBatchSize { get; set; } = 2_000;

    /// <summary>Delay between scheduling cycles when the queue is empty.</summary>
    [Range(10, 60_000)] public int SchedulerIdleDelayMs { get; set; } = 250;

    /// <summary>Priority points added per minute of waiting (anti-starvation aging).</summary>
    [Range(0, 100)] public double AgingPointsPerMinute { get; set; } = 1.0;

    /// <summary>Use SIMD (System.Numerics.Vector) for worker scoring and statistics.</summary>
    public bool UseSimd { get; set; } = true;

    /// <summary>Number of job types in a cycle above which per-type predictions are computed in parallel.</summary>
    [Range(1, 10_000)] public int ParallelThreshold { get; set; } = 4;

    /// <summary>Max degree of parallelism for CPU-bound work (0 = processor count).</summary>
    [Range(0, 1024)] public int MaxDegreeOfParallelism { get; set; }

    /// <summary>Weights of the scheduling score (normalised internally).</summary>
    public SchedulingWeights Weights { get; set; } = new();

    /// <summary>Base64 AES-256 key for encrypting sensitive payloads at rest. Empty = ephemeral key (dev only).</summary>
    public string? PayloadProtectionKey { get; set; }

    /// <summary>Keep terminal jobs this many days before housekeeping deletes them.</summary>
    [Range(1, 3650)] public int RetentionDays { get; set; } = 30;

    /// <summary>Leader lease time-to-live; a crashed leader is replaced after at most this long.</summary>
    [Range(3, 300)] public int LeaderLeaseSeconds { get; set; } = 15;

    /// <summary>Re-send unacknowledged worker commands after this many seconds.</summary>
    [Range(1, 3600)] public int CommandRedeliverSeconds { get; set; } = 15;

    /// <summary>Lowest worker SDK version allowed to register, e.g. <c>1.2.0</c>. Empty = no check.</summary>
    public string? MinWorkerSdkVersion { get; set; }
}

/// <summary>Relative weights of the worker score components.</summary>
public sealed class SchedulingWeights
{
    /// <summary>Prefer idle workers (1 - load ratio).</summary>
    [Range(0, 100)] public double Load { get; set; } = 0.35;
    /// <summary>Prefer fast workers (predicted duration from the ML model, spec and speed factor).</summary>
    [Range(0, 100)] public double Speed { get; set; } = 0.35;
    /// <summary>Prefer reliable workers (predicted success probability).</summary>
    [Range(0, 100)] public double Reliability { get; set; } = 0.25;
    /// <summary>Exploration bonus for workers with few observations (UCB).</summary>
    [Range(0, 100)] public double Exploration { get; set; } = 0.05;
    /// <summary>Bonus for the job's preferred (non-strict) target worker.</summary>
    [Range(0, 100)] public double PreferredWorkerBonus { get; set; } = 0.5;
    /// <summary>Penalty for the worker that failed the previous attempt.</summary>
    [Range(0, 100)] public double LastFailedPenalty { get; set; } = 0.3;
}

/// <summary>Anomaly detection and alerting settings (section <c>Anomaly</c>).</summary>
public sealed class AnomalyOptions
{
    public const string SectionName = "Anomaly";

    [Range(1, 1440)] public int ScanIntervalMinutes { get; set; } = 1;
    [Range(5, 1440)] public int WindowMinutes { get; set; } = 15;
    /// <summary>History used as the baseline.</summary>
    [Range(1, 720)] public int BaselineHours { get; set; } = 24;
    [Range(1, 20)] public double ZScoreThreshold { get; set; } = 3.0;
    [Range(0, 1)] public double FailureRateThreshold { get; set; } = 0.3;
    [Range(1, 100_000)] public int MinSamples { get; set; } = 10;
    [Range(1, 1000)] public int FlappingThreshold { get; set; } = 3;
    [Range(1, 100_000)] public int FailoverStormThreshold { get; set; } = 20;
    /// <summary>Same anomaly (kind + subject) is alerted at most once per cooldown.</summary>
    [Range(1, 1440)] public int AlertCooldownMinutes { get; set; } = 30;
    /// <summary>Only anomalies at or above this severity are sent to admins.</summary>
    public Domain.AnomalySeverity MinAlertSeverity { get; set; } = Domain.AnomalySeverity.Warning;
}
