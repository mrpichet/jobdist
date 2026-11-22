using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace JobDist.Core.Observability;

/// <summary>
/// Server metrics (System.Diagnostics.Metrics). Exported over OTLP by the service defaults to the .NET Aspire dashboard.
/// Instruments are created once through <see cref="IMeterFactory"/>; recording is lock-free and allocation-free.
/// </summary>
public sealed class JobDistMetrics
{
    /// <summary>Meter name used for OpenTelemetry registration.</summary>
    public const string MeterName = "JobDist.Server";

    /// <summary>Activity source for distributed tracing of scheduling.</summary>
    public static readonly ActivitySource ActivitySource = new(MeterName);

    private readonly Counter<long> _submitted;
    private readonly Counter<long> _assigned;
    private readonly Counter<long> _completed;
    private readonly Counter<long> _retried;
    private readonly Counter<long> _failovers;
    private readonly Counter<long> _cancelled;
    private readonly Counter<long> _anomalies;
    private readonly Counter<long> _alerts;
    private readonly Counter<long> _rejectedReports;
    private readonly Counter<long> _signatures;
    private readonly Histogram<double> _duration;
    private readonly Histogram<double> _queueWait;
    private readonly Histogram<double> _cycle;
    private readonly Histogram<double> _score;
    private Func<long> _queueDepth = () => 0;
    private Func<long> _workersOnline = () => 0;
    private Func<int> _isLeader = () => 0;

    /// <summary>Creates all instruments.</summary>
    /// <param name="meterFactory">DI meter factory.</param>
    public JobDistMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _submitted = meter.CreateCounter<long>("jobdist.jobs.submitted", "{job}", "Jobs submitted.");
        _assigned = meter.CreateCounter<long>("jobdist.jobs.assigned", "{job}", "Jobs assigned to workers.");
        _completed = meter.CreateCounter<long>("jobdist.jobs.completed", "{job}", "Attempts finished, tagged by outcome.");
        _retried = meter.CreateCounter<long>("jobdist.jobs.retried", "{job}", "Jobs re-queued for another attempt.");
        _failovers = meter.CreateCounter<long>("jobdist.failovers", "{job}", "Jobs re-distributed because a worker was lost.");
        _cancelled = meter.CreateCounter<long>("jobdist.jobs.cancelled", "{job}", "Jobs cancelled.");
        _anomalies = meter.CreateCounter<long>("jobdist.anomalies.detected", "{anomaly}", "Anomalies detected.");
        _alerts = meter.CreateCounter<long>("jobdist.alerts.sent", "{alert}", "Alerts sent to administrators.");
        _rejectedReports = meter.CreateCounter<long>("jobdist.worker.reports.rejected", "{report}", "Stale or invalid worker reports.");
        _signatures = meter.CreateCounter<long>("jobdist.signatures.created", "{signature}", "Signatures created, by algorithm.");
        _duration = meter.CreateHistogram<double>("jobdist.job.duration", "ms", "Execution time reported by workers.");
        _queueWait = meter.CreateHistogram<double>("jobdist.job.queue_wait", "ms", "Time from ready to assignment.");
        _cycle = meter.CreateHistogram<double>("jobdist.scheduler.cycle.duration", "ms", "Duration of one scheduling cycle.");
        _score = meter.CreateHistogram<double>("jobdist.risk.score", "{score}", "Risk scores calculated.");
        meter.CreateObservableGauge("jobdist.queue.depth", () => _queueDepth(), "{job}", "Pending jobs.");
        meter.CreateObservableGauge("jobdist.workers.online", () => _workersOnline(), "{worker}", "Online workers.");
        meter.CreateObservableGauge("jobdist.leader", () => _isLeader(), "{bool}", "1 when this instance is the scheduling leader.");
    }

    /// <summary>Registers callbacks for the observable gauges.</summary>
    /// <param name="queueDepth">Pending job count.</param>
    /// <param name="workersOnline">Online worker count.</param>
    /// <param name="isLeader">1 when leader.</param>
    public void BindGauges(Func<long> queueDepth, Func<long> workersOnline, Func<int> isLeader)
    {
        _queueDepth = queueDepth;
        _workersOnline = workersOnline;
        _isLeader = isLeader;
    }

    public void JobSubmitted(string jobType, int count = 1) => _submitted.Add(count, new KeyValuePair<string, object?>("job.type", jobType));
    public void JobAssigned(string jobType, double queueWaitMs)
    {
        var tag = new KeyValuePair<string, object?>("job.type", jobType);
        _assigned.Add(1, tag);
        _queueWait.Record(queueWaitMs, tag);
    }
    public void AttemptCompleted(string jobType, string outcome, double durationMs)
    {
        var type = new KeyValuePair<string, object?>("job.type", jobType);
        var result = new KeyValuePair<string, object?>("outcome", outcome);
        _completed.Add(1, type, result);
        if (durationMs > 0)
        {
            _duration.Record(durationMs, type, result);
        }
    }
    public void JobRetried(string jobType, string reason) => _retried.Add(1, new KeyValuePair<string, object?>("job.type", jobType), new KeyValuePair<string, object?>("reason", reason));
    public void Failover(int jobs) => _failovers.Add(jobs);
    public void JobCancelled(string jobType) => _cancelled.Add(1, new KeyValuePair<string, object?>("job.type", jobType));
    public void AnomalyDetected(string kind, string severity) => _anomalies.Add(1, new KeyValuePair<string, object?>("kind", kind), new KeyValuePair<string, object?>("severity", severity));
    public void AlertSent(bool success) => _alerts.Add(1, new KeyValuePair<string, object?>("success", success));
    public void WorkerReportRejected(string reason) => _rejectedReports.Add(1, new KeyValuePair<string, object?>("reason", reason));
    public void SignatureCreated(string algorithm) => _signatures.Add(1, new KeyValuePair<string, object?>("algorithm", algorithm));
    public void SchedulerCycle(double ms, int assigned) => _cycle.Record(ms, new KeyValuePair<string, object?>("assigned", assigned > 0));
    public void RiskScore(string subject, double score) => _score.Record(score, new KeyValuePair<string, object?>("subject", subject));
}
