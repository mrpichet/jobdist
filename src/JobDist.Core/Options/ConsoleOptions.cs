using System.ComponentModel.DataAnnotations;

namespace JobDist.Core.Options;

/// <summary>Alert delivery settings (section <c>Alerts</c>). MessageHub connection settings live in <c>Alerts:MessageHub</c>.</summary>
public sealed class AlertOptions
{
    public const string SectionName = "Alerts";

    /// <summary>Outbox rows claimed per cycle by each instance.</summary>
    [Range(1, 1_000)] public int OutboxBatchSize { get; set; } = 50;

    /// <summary>Delay between outbox cycles when nothing was due.</summary>
    [Range(1, 300)] public int OutboxPollSeconds { get; set; } = 2;

    /// <summary>How long a claimed row stays locked by one instance.</summary>
    [Range(10, 3600)] public int OutboxLockSeconds { get; set; } = 60;

    /// <summary>Attempts before a row is marked Failed.</summary>
    [Range(1, 100)] public int MaxAttempts { get; set; } = 10;

    /// <summary>Base delay of the retry back-off: <c>min(MaxDelay, Base × 2^(attempts−1)) × jitter[0.8, 1.2]</c>.</summary>
    [Range(1, 3600)] public double RetryBaseDelaySeconds { get; set; } = 5;

    /// <summary>Upper bound of the retry back-off.</summary>
    [Range(1, 86_400)] public double RetryMaxDelaySeconds { get; set; } = 900;

    /// <summary>Delay used while the MessageHub circuit breaker is open (not counted as an attempt).</summary>
    [Range(1, 3600)] public int CircuitOpenDelaySeconds { get; set; } = 30;

    /// <summary>Keep finished alert history this many days.</summary>
    [Range(1, 3650)] public int RetentionDays { get; set; } = 90;

    /// <summary>Interval of the queue-threshold check that raises near-limit / over-limit alerts (leader only).</summary>
    [Range(5, 3600)] public int QueueCheckSeconds { get; set; } = 15;

    /// <summary>The same queue alert (type + job type) is repeated at most once per this many minutes while the condition lasts.</summary>
    [Range(1, 1440)] public int QueueAlertCooldownMinutes { get; set; } = 30;

    /// <summary>Pending counts used to admit new jobs are cached this long (milliseconds).</summary>
    [Range(0, 60_000)] public int PendingCacheMilliseconds { get; set; } = 2_000;
}

/// <summary>How alerts travel from the outbox to MessageHub (section <c>Messaging</c>).</summary>
public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    /// <summary><c>Database</c> (default): every instance reads the outbox table and calls MessageHub.</summary>
    public MessagingTransport Transport { get; set; } = MessagingTransport.Database;
}

/// <summary>Alert transport.</summary>
public enum MessagingTransport
{
    /// <summary>Outbox table polled by <c>AlertOutboxWorker</c> (no extra infrastructure).</summary>
    Database = 0,

    /// <summary>
    /// Transactional outbox + RabbitMQ: the relay publishes due outbox rows to RabbitMQ (persistent, publisher confirms) and a
    /// consumer on any instance delivers them to MessageHub; settings in <c>Messaging:RabbitMq</c>.
    /// </summary>
    RabbitMq = 1
}
