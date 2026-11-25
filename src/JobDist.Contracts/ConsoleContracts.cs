using System.ComponentModel.DataAnnotations;

namespace JobDist.Contracts;

// -----------------------------------------------------------------------------------------------------------------
// Contracts added for the web console (alerts, queue thresholds, reports, AI assistant, browser logs).
// API v1 rule: additive changes only. Clients must ignore unknown fields and tolerate unknown enum values.
// -----------------------------------------------------------------------------------------------------------------

/// <summary>Event that can raise an alert through MessageHub.</summary>
public enum AlertEventType
{
    /// <summary>Anomaly detection found something at or above <c>Anomaly:MinAlertSeverity</c>.</summary>
    AnomalyDetected = 0,
    /// <summary>A worker registered (or registered again).</summary>
    WorkerRegistered = 1,
    /// <summary>A worker unregistered itself or was removed by an administrator.</summary>
    WorkerUnregistered = 2,
    /// <summary>A worker stopped sending heartbeats without unregistering; its jobs were re-distributed.</summary>
    WorkerLost = 3,
    /// <summary>Another server instance became the primary (leader).</summary>
    LeaderChanged = 4,
    /// <summary>Pending jobs reached the warning percentage of a queue threshold.</summary>
    QueueNearLimit = 5,
    /// <summary>Pending jobs reached the limit of a queue threshold; new jobs of that type are rejected.</summary>
    QueueOverLimit = 6,
    /// <summary>Test message sent from the recipient screen.</summary>
    Test = 7
}

/// <summary>Alert severity (same values as anomaly severity).</summary>
public enum AlertSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2
}

/// <summary>Delivery state of one alert to one recipient.</summary>
public enum AlertSendStatus
{
    /// <summary>Waiting to be sent (or waiting for a retry).</summary>
    Pending = 0,
    /// <summary>Accepted by MessageHub.</summary>
    Sent = 1,
    /// <summary>Gave up: permanent error or too many attempts.</summary>
    Failed = 2,
    /// <summary>Not sent: recipient disabled or deleted, or MessageHub not configured.</summary>
    Skipped = 3
}

/// <summary>Queue usage compared with its threshold.</summary>
public enum QueueLevel
{
    Normal = 0,
    NearLimit = 1,
    OverLimit = 2
}

/// <summary>Create or update an alert recipient.</summary>
/// <param name="Name">Display name.</param>
/// <param name="Channel">MessageHub channel name, e.g. <c>Email</c>, <c>TeamsChat</c>, <c>Line</c>.</param>
/// <param name="Address">Address for the channel. On update, empty keeps the stored address.</param>
/// <param name="EventTypes">Events this recipient receives (at least one).</param>
/// <param name="MinSeverity">Lowest severity this recipient receives.</param>
/// <param name="Enabled">Receive alerts.</param>
/// <param name="Version">Required on update: the version that was read (optimistic concurrency).</param>
public sealed record AlertRecipientRequest(
    [Required, StringLength(200, MinimumLength = 1)] string Name,
    [Required, StringLength(50, MinimumLength = 1)] string Channel,
    [StringLength(512)] string? Address,
    [Required, MinLength(1)] IReadOnlyList<AlertEventType> EventTypes,
    AlertSeverity MinSeverity = AlertSeverity.Warning,
    bool Enabled = true,
    long? Version = null);

/// <summary>Alert recipient as returned by the API. The address is never returned in full.</summary>
public sealed record AlertRecipientResponse(
    Guid RecipientId,
    string Name,
    string Channel,
    string AddressMasked,
    IReadOnlyList<AlertEventType> EventTypes,
    AlertSeverity MinSeverity,
    bool Enabled,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string? UpdatedBy,
    long Version);

/// <summary>Result of sending one message through MessageHub.</summary>
/// <param name="Status">Sent, Failed or Skipped.</param>
/// <param name="CorrelationId">MessageHub message id when sent.</param>
/// <param name="Error">Short error text when not sent.</param>
public sealed record AlertSendResultResponse(AlertSendStatus Status, string? CorrelationId, string? Error);

/// <summary>One alert delivery (one row of the outbox).</summary>
public sealed record AlertLogResponse(
    long AlertLogId,
    DateTimeOffset CreatedUtc,
    AlertEventType EventType,
    AlertSeverity Severity,
    string Title,
    string Subject,
    Guid? RecipientId,
    string? RecipientName,
    string Channel,
    string AddressMasked,
    AlertSendStatus Status,
    int Attempts,
    DateTimeOffset? SentUtc,
    string? CorrelationId,
    string? Error,
    string? InstanceId);

/// <summary>Create or update a queue threshold.</summary>
/// <param name="JobType">Job type, or <c>*</c> for the total of all types.</param>
/// <param name="MaxPending">Pending jobs at which new jobs are rejected (1 - 10,000,000).</param>
/// <param name="WarnPercent">Percentage of <paramref name="MaxPending"/> that raises a near-limit alert (1-99).</param>
/// <param name="Enabled">Apply this threshold.</param>
/// <param name="Version">Required on update.</param>
public sealed record QueueThresholdRequest(
    [Required, StringLength(100, MinimumLength = 1), RegularExpression(@"^(\*|[A-Za-z0-9][A-Za-z0-9._\-]*)$")] string JobType,
    [Range(1, 10_000_000)] int MaxPending,
    [Range(1, 99)] int WarnPercent = 80,
    bool Enabled = true,
    long? Version = null);

/// <summary>Queue threshold.</summary>
public sealed record QueueThresholdResponse(
    Guid ThresholdId,
    string JobType,
    int MaxPending,
    int WarnPercent,
    bool Enabled,
    string? UpdatedBy,
    DateTimeOffset UpdatedUtc,
    long Version);

/// <summary>Current queue usage against one threshold.</summary>
public sealed record QueueStatusResponse(Guid ThresholdId, string JobType, int Pending, int MaxPending, double UsagePercent, QueueLevel Level, bool Enabled);

/// <summary>Job type known to the cluster (registered by an online worker or present in the queue).</summary>
public sealed record JobTypeInfoResponse(string JobType, int OnlineWorkers, int TotalConcurrency, int Pending);

/// <summary>One row of the job statistics report (null grouping fields are not grouped).</summary>
public sealed record JobStatisticsRowResponse(
    DateOnly? Day,
    string? JobType,
    Guid? WorkerId,
    string? WorkerName,
    int Submitted,
    int Succeeded,
    int Failed,
    int Cancelled,
    double SuccessRate,
    double AvgAttempts,
    double AvgDurationMs,
    double P95DurationMs,
    double AvgQueueWaitMs,
    double ThroughputPerHour);

/// <summary>Job statistics report.</summary>
public sealed record JobStatisticsReportResponse(
    DateOnly FromDate,
    DateOnly ToDate,
    string TimeZone,
    IReadOnlyList<string> GroupBy,
    IReadOnlyList<JobStatisticsRowResponse> Rows,
    JobStatisticsRowResponse Totals,
    DateTimeOffset GeneratedUtc);

/// <summary>One chat message (same shape as WebCommon.UI <c>BackendChatClient</c>).</summary>
public sealed record AgentChatMessage([Required, RegularExpression("^(user|assistant)$")] string Role, [Required, StringLength(4_000, MinimumLength = 1)] string Content);

/// <summary>Chat request from the web console.</summary>
public sealed record AgentChatRequest(
    [Required, MinLength(1), MaxLength(20)] IReadOnlyList<AgentChatMessage> Messages,
    [RegularExpression("^(th|en)$")] string Language = "th",
    [StringLength(500)] string? Page = null);

/// <summary>Chat reply.</summary>
public sealed record AgentChatResponse(string Reply);

/// <summary>Whether the AI assistant is configured.</summary>
public sealed record AgentStatusResponse(bool Enabled);

/// <summary>Log entry written in the browser.</summary>
public sealed record ClientLogEntry(
    Guid EntryId,
    DateTimeOffset TimestampUtc,
    [Required, RegularExpression("^(Debug|Information|Warning|Error|Critical)$")] string Level,
    [Required, StringLength(200)] string Category,
    [Required, StringLength(4_000)] string Message,
    [StringLength(500)] string? Page = null,
    [StringLength(8_000)] string? Exception = null,
    [StringLength(100)] string? CorrelationId = null);

/// <summary>Batch of browser log entries.</summary>
public sealed record ClientLogBatchRequest([Required, MinLength(1), MaxLength(50)] IReadOnlyList<ClientLogEntry> Entries);
