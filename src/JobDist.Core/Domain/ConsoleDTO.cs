using JobDist.Contracts;

namespace JobDist.Core.Domain;

// Domain objects for the web console features: alert recipients, alert outbox, queue thresholds, idempotency records
// and report aggregates. Mutable classes so Dapper can map them directly; services own every write.

/// <summary>Alert recipient (table <c>alertrecipients</c>). The address is stored encrypted, plus a masked copy for display.</summary>
public sealed class AlertRecipientDTO
{
    public Guid RecipientId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    /// <summary>AES-256-GCM protected address (recipient id is the associated data).</summary>
    public string AddressProtected { get; set; } = string.Empty;
    public string AddressMasked { get; set; } = string.Empty;
    /// <summary>Comma-separated <see cref="AlertEventType"/> names.</summary>
    public string EventTypes { get; set; } = string.Empty;
    public int MinSeverity { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public long Version { get; set; }

    /// <summary>Parsed event types (unknown names are ignored).</summary>
    public IReadOnlyList<AlertEventType> EventTypeList =>
        EventTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Enum.TryParse<AlertEventType>(s, out var e) ? (AlertEventType?)e : null)
            .Where(e => e is not null).Select(e => e!.Value).ToList();

    /// <summary>Shallow copy.</summary>
    public AlertRecipientDTO Clone() => (AlertRecipientDTO)MemberwiseClone();
}

/// <summary>One alert for one recipient (table <c>alertoutbox</c>): the delivery queue and the alert history at the same time.</summary>
public sealed class AlertOutboxDTO
{
    public long OutboxId { get; set; }
    /// <summary>De-duplication key of the event; unique together with <see cref="RecipientId"/>.</summary>
    public string EventKey { get; set; } = string.Empty;
    /// <summary>Recipient, or <see cref="Guid.Empty"/> when no recipient matched (kept as history only).</summary>
    public Guid RecipientId { get; set; }
    public string? RecipientName { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string AddressMasked { get; set; } = string.Empty;
    public int EventType { get; set; }
    public int Severity { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset OccurredUtc { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public int Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptUtc { get; set; }
    public string? LockedBy { get; set; }
    public DateTimeOffset? LockedUntilUtc { get; set; }
    public DateTimeOffset? SentUtc { get; set; }
    public string? CorrelationId { get; set; }
    public string? LastError { get; set; }
    public string? InstanceId { get; set; }

    /// <summary>Shallow copy.</summary>
    public AlertOutboxDTO Clone() => (AlertOutboxDTO)MemberwiseClone();
}

/// <summary>Filter for the alert history.</summary>
public sealed record AlertOutboxQueryDTO(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    AlertEventType? EventType,
    AlertSendStatus? Status,
    Guid? RecipientId,
    int Skip,
    int Take);

/// <summary>Result of one delivery attempt, written back to the outbox row.</summary>
public sealed record AlertDeliveryUpdateDTO(
    long OutboxId,
    string LockedBy,
    AlertSendStatus Status,
    int Attempts,
    DateTimeOffset NextAttemptUtc,
    DateTimeOffset? SentUtc,
    string? CorrelationId,
    string? LastError,
    string? RecipientName,
    string? Channel,
    string? AddressMasked);

/// <summary>Event raised by a service, before it is split per recipient.</summary>
/// <param name="EventKey">De-duplication key, e.g. <c>WorkerLost:{workerId}:{offlineCount}</c>.</param>
/// <param name="EventType">Event type (recipients subscribe by type).</param>
/// <param name="Severity">Severity (recipients filter by minimum severity).</param>
/// <param name="Subject">What the event is about (worker name, job type, instance id); delivery is ordered per subject.</param>
/// <param name="Title">Message subject line.</param>
/// <param name="Body">Plain-text message.</param>
/// <param name="OccurredUtc">When it happened.</param>
public sealed record AlertEventDTO(string EventKey, AlertEventType EventType, AlertSeverity Severity, string Subject, string Title, string Body, DateTimeOffset OccurredUtc);

/// <summary>Queue threshold (table <c>queuethresholds</c>).</summary>
public sealed class QueueThresholdDTO
{
    public Guid ThresholdId { get; set; }
    public string JobType { get; set; } = "*";
    public int MaxPending { get; set; }
    public int WarnPercent { get; set; } = 80;
    public bool Enabled { get; set; } = true;
    public string? UpdatedBy { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public long Version { get; set; }

    /// <summary>Shallow copy.</summary>
    public QueueThresholdDTO Clone() => (QueueThresholdDTO)MemberwiseClone();
}

/// <summary>Stored response of a POST that carried an <c>Idempotency-Key</c> header (table <c>idempotencyrecords</c>).</summary>
public sealed class IdempotencyRecordDTO
{
    public string Principal { get; set; } = string.Empty;
    public string Route { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    /// <summary>Hex SHA-256 of the request body.</summary>
    public string RequestHash { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string? ResponseBody { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
}

/// <summary>Jobs created in one UTC hour.</summary>
public sealed class SubmittedBucketDTO
{
    public DateTime HourUtc { get; set; }
    public string JobType { get; set; } = string.Empty;
    public Guid? WorkerId { get; set; }
    public int JobCount { get; set; }
}

/// <summary>Jobs that reached a final state in one UTC hour.</summary>
public sealed class CompletedBucketDTO
{
    public DateTime HourUtc { get; set; }
    public string JobType { get; set; } = string.Empty;
    public Guid? WorkerId { get; set; }
    public int Status { get; set; }
    public int JobCount { get; set; }
    public long SumAttempts { get; set; }
}

/// <summary>Finished attempts in one UTC hour, split into logarithmic duration buckets (for an exact-enough P95 on both databases).</summary>
public sealed class AttemptBucketDTO
{
    public DateTime HourUtc { get; set; }
    public string JobType { get; set; } = string.Empty;
    public Guid WorkerId { get; set; }
    public int Outcome { get; set; }
    /// <summary><c>floor(ln(durationMs + 1) × 20)</c>; about 5 % resolution.</summary>
    public int DurationBucket { get; set; }
    public int AttemptCount { get; set; }
    public double SumDurationMs { get; set; }
    public double SumQueueWaitMs { get; set; }
}

/// <summary>Report filter in UTC.</summary>
public sealed record JobReportQueryDTO(DateTimeOffset FromUtc, DateTimeOffset ToUtc, string? JobType, Guid? WorkerId);
