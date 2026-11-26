using JobDist.Contracts;
using JobDist.Web.Services;

namespace JobDist.Web.Components;

/// <summary>Grid row of a job: text columns are localized so search, filter, group and export use the shown language.</summary>
public sealed record JobRow(JobStatusResponse Job, string? WorkerName)
{
    public Guid JobId => Job.JobId;
    public string ShortId => Job.JobId.ToString()[..8];
    public string JobType => Job.JobType;
    public JobStatus Status => Job.Status;
    public string StatusText => Fmt.Status(Job.Status);
    public int Priority => Job.Priority;
    public string AttemptText => $"{Job.Attempt}/{Job.MaxAttempts}";
    public string Worker => WorkerName ?? (Job.AssignedWorkerId is { } w ? w.ToString()[..8] : "");
    public DateTime Created => Job.CreatedUtc.LocalDateTime;
    public DateTime? Completed => Job.CompletedUtc?.LocalDateTime;
    public double? DurationMs => Job.LastDurationMs;
    public string DurationText => Fmt.Duration(Job.LastDurationMs);
    public string Tags => string.Join(", ", Job.Tags);
    public bool IsFinal => Fmt.IsFinal(Job.Status);
}

/// <summary>Grid row of a worker.</summary>
public sealed record WorkerRow(WorkerResponse Worker)
{
    public Guid WorkerId => Worker.WorkerId;
    public string Name => Worker.Name;
    public WorkerStatus Status => Worker.Status;
    public string StatusText => Fmt.Status(Worker.Status);
    public string LoadText => $"{Worker.ActiveJobs}/{Worker.MaxTotalConcurrency}";
    public double Cpu => Worker.CpuUsagePercent;
    public double Memory => Worker.MemoryUsagePercent;
    public DateTime LastHeartbeat => Worker.LastHeartbeatUtc.LocalDateTime;
    public int OfflineCount => Worker.OfflineCount;
    public string PqcText => Worker.SupportsPqc ? Localization.AppText.Get("Common.Yes") : Localization.AppText.Get("Common.No");
    public string Version => Worker.Version ?? "";
    public string Tags => string.Join(", ", Worker.Tags);
    public string JobTypes => string.Join(", ", Worker.Capabilities.Select(c => c.JobType));
    public bool CanAct => Worker.Status is WorkerStatus.Online or WorkerStatus.Draining or WorkerStatus.Offline;
}

/// <summary>Grid row of the alert history.</summary>
public sealed record AlertLogRow(AlertLogResponse Log)
{
    public long AlertLogId => Log.AlertLogId;
    public DateTime Created => Log.CreatedUtc.LocalDateTime;
    public string EventText => Fmt.Event(Log.EventType);
    public string SeverityText => Fmt.Severity(Log.Severity);
    public string Title => Log.Title;
    public string Subject => Log.Subject;
    public string Recipient => Log.RecipientName ?? "-";
    public string Channel => Log.Channel;
    public string Address => Log.AddressMasked;
    public AlertSendStatus Status => Log.Status;
    public string StatusText => Fmt.SendStatus(Log.Status);
    public int Attempts => Log.Attempts;
    public DateTime? Sent => Log.SentUtc?.LocalDateTime;
    public string Error => Log.Error ?? "";
    public string CorrelationId => Log.CorrelationId ?? "";
}

/// <summary>Grid row of an alert recipient.</summary>
public sealed record RecipientRow(AlertRecipientResponse Recipient)
{
    public Guid RecipientId => Recipient.RecipientId;
    public string Name => Recipient.Name;
    public string Channel => Recipient.Channel;
    public string Address => Recipient.AddressMasked;
    public string Events => string.Join(", ", Recipient.EventTypes.Select(Fmt.Event));
    public string MinSeverityText => Fmt.Severity(Recipient.MinSeverity);
    public bool Enabled => Recipient.Enabled;
    public string EnabledText => Localization.AppText.Get(Recipient.Enabled ? "Common.Enabled" : "Common.Disabled");
    public DateTime Updated => Recipient.UpdatedUtc.LocalDateTime;
    public string UpdatedBy => Recipient.UpdatedBy ?? "";
}

/// <summary>Grid row of a queue threshold with its current usage.</summary>
public sealed record ThresholdRow(QueueThresholdResponse Threshold, QueueStatusResponse? Usage)
{
    public Guid ThresholdId => Threshold.ThresholdId;
    public string JobType => Threshold.JobType == "*" ? Localization.AppText.Get("Threshold.AllTypes") : Threshold.JobType;
    public int MaxPending => Threshold.MaxPending;
    public int WarnPercent => Threshold.WarnPercent;
    public int Pending => Usage?.Pending ?? 0;
    public double UsagePercent => Usage?.UsagePercent ?? 0;
    public QueueLevel Level => Usage?.Level ?? QueueLevel.Normal;
    public string LevelText => Fmt.Level(Level);
    public bool Enabled => Threshold.Enabled;
    public string EnabledText => Localization.AppText.Get(Threshold.Enabled ? "Common.Enabled" : "Common.Disabled");
    public DateTime Updated => Threshold.UpdatedUtc.LocalDateTime;
    public string UpdatedBy => Threshold.UpdatedBy ?? "";
}

/// <summary>Row of the learned performance table (S11) with the worker name.</summary>
public sealed record PerformanceRow(PerformanceStat Stat, string WorkerName)
{
    public string Key => $"{Stat.WorkerId}:{Stat.JobType}";
    public string JobType => Stat.JobType;
    public string Worker => WorkerName;
    public int Samples => Stat.Samples;
    public int Successes => Stat.Successes;
    public int Failures => Stat.Failures;
    public double SuccessRate => Stat.Samples == 0 ? 0 : 100.0 * Stat.Successes / Stat.Samples;
    public double MeanMs => Stat.MeanDurationMs;
    public double StdDevMs => Stat.StdDevDurationMs;
    public DateTime Updated => Stat.LastUpdatedUtc.LocalDateTime;
}

/// <summary>Row of the anomaly table (S12).</summary>
public sealed record AnomalyRow(int Index, Anomaly Anomaly)
{
    public string SeverityText => Fmt.Severity(Anomaly.Severity);
    public string KindText => Localization.AppText.Get($"AnomalyKind.{Anomaly.Kind}");
    public string Subject => Anomaly.Subject;
    public string Description => Anomaly.Description;
    public double Observed => Anomaly.ObservedValue;
    public double Expected => Anomaly.ExpectedValue;
    public double Score => Anomaly.Score;
    public DateTime Detected => Anomaly.DetectedUtc.LocalDateTime;
}

/// <summary>Row of the job statistics report (X4).</summary>
public sealed record StatisticsRow(int Index, JobStatisticsRowResponse Row)
{
    public DateTime? Day => Row.Day?.ToDateTime(TimeOnly.MinValue);
    public string DayText => Row.Day?.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture) ?? "";
    public string JobType => Row.JobType ?? "";
    public string Worker => Row.WorkerName ?? (Row.WorkerId is { } w ? w.ToString()[..8] : "");
    public int Submitted => Row.Submitted;
    public int Succeeded => Row.Succeeded;
    public int Failed => Row.Failed;
    public int Cancelled => Row.Cancelled;
    public double SuccessRate => Row.SuccessRate;
    public double AvgAttempts => Row.AvgAttempts;
    public double AvgDurationMs => Row.AvgDurationMs;
    public double P95DurationMs => Row.P95DurationMs;
    public double AvgQueueWaitMs => Row.AvgQueueWaitMs;
    public double ThroughputPerHour => Row.ThroughputPerHour;
}

/// <summary>Row of a small "name: count" table (dashboard).</summary>
public sealed record CountRow(string Key, string Text, int Count, JobStatus? Status = null);

/// <summary>Row of a job's attempt history (S04).</summary>
public sealed record AttemptRow(JobAttemptResponse Attempt)
{
    public Guid AttemptId => Attempt.AttemptId;
    public int Number => Attempt.Attempt;
    public Guid WorkerId => Attempt.WorkerId;
    public string Worker => Attempt.WorkerName ?? Attempt.WorkerId.ToString()[..8];
    public DateTime Assigned => Attempt.AssignedUtc.LocalDateTime;
    public DateTime? Started => Attempt.StartedUtc?.LocalDateTime;
    public DateTime? Finished => Attempt.FinishedUtc?.LocalDateTime;
    public string QueueWaitText => Fmt.Duration(Attempt.QueueWaitMs);
    public string DurationText => Fmt.Duration(Attempt.DurationMs);
    public string OutcomeText => Fmt.Outcome(Attempt.Outcome);
    public double Score => Attempt.SchedulerScore;
    public string Error => Attempt.Error ?? "";
}

/// <summary>Row of a worker's capability list (S08).</summary>
public sealed record CapabilityRow(WorkerCapabilityRequest Capability)
{
    public string JobType => Capability.JobType;
    public int MaxConcurrency => Capability.MaxConcurrency;
    public double SpeedFactor => Capability.SpeedFactor;
}

/// <summary>Row of a risk assessment's factors.</summary>
public sealed record RiskFactorRow(int Index, RiskFactor Factor)
{
    public string Name => Factor.Factor;
    public double Points => Factor.Points;
    public string Explanation => Factor.Explanation;
}

/// <summary>Row of the worker recommendation table (S11).</summary>
public sealed record RecommendationRow(int Rank, WorkerRecommendation Recommendation)
{
    public Guid WorkerId => Recommendation.WorkerId;
    public string Worker => Recommendation.WorkerName;
    public double Score => Recommendation.Score;
    public double LoadPercent => Recommendation.LoadRatio * 100;
    public string ExpectedText => Fmt.Duration(Recommendation.ExpectedDurationMs);
    public double SuccessPercent => Recommendation.SuccessProbability * 100;
    public string Reason => Recommendation.Reason;
}

/// <summary>Row of the import column guide (X3).</summary>
public sealed record ImportColumnRow(string Header, string RequiredText, string Hint);

/// <summary>Row of the import result (X3).</summary>
public sealed record ImportResultRow(int Number, SubmitJobResponse Result)
{
    public Guid JobId => Result.JobId;
    public string ShortId => Result.JobId.ToString()[..8];
    public JobStatus Status => Result.Status;
    public string StatusText => Fmt.Status(Result.Status);
    public string DuplicateText => Localization.AppText.Get(Result.Duplicate ? "Common.Yes" : "Common.No");
}
