using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace JobDist.Worker.Sdk;

/// <summary>
/// Worker-side metrics (meter <c>JobDist.Worker</c>). Exported to the Aspire dashboard through OpenTelemetry/OTLP by the
/// host (see <c>JobDist.ServiceDefaults</c>). Instruments are lock-free and cheap on the hot path.
/// </summary>
public sealed class WorkerMetrics : IDisposable
{
    /// <summary>Meter name to enable in OpenTelemetry.</summary>
    public const string MeterName = "JobDist.Worker";

    private readonly Meter _meter;
    private readonly Counter<long> _received;
    private readonly Counter<long> _completed;
    private readonly Counter<long> _rejected;
    private readonly Counter<long> _reconnects;
    private readonly Counter<long> _reportsDropped;
    private readonly Histogram<double> _duration;
    private int _running;
    private int _outbox;

    /// <summary>Creates the instruments.</summary>
    /// <param name="meterFactory">Meter factory from DI (keeps meters per service provider, test friendly).</param>
    public WorkerMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        _meter = meterFactory.Create(MeterName);
        _received = _meter.CreateCounter<long>("jobdist.worker.jobs.received", description: "Assignments received.");
        _completed = _meter.CreateCounter<long>("jobdist.worker.jobs.completed", description: "Attempts finished, tagged by outcome.");
        _rejected = _meter.CreateCounter<long>("jobdist.worker.messages.rejected", description: "Assignments/commands rejected (bad signature, wrong worker, hash mismatch).");
        _reconnects = _meter.CreateCounter<long>("jobdist.worker.reregistrations", description: "Re-registrations after lost identity.");
        _reportsDropped = _meter.CreateCounter<long>("jobdist.worker.reports.dropped", description: "Completion reports dropped (stale or expired).");
        _duration = _meter.CreateHistogram<double>("jobdist.worker.job.duration", unit: "ms", description: "Handler execution time.");
        _meter.CreateObservableGauge("jobdist.worker.jobs.running", () => Volatile.Read(ref _running), description: "Jobs executing now.");
        _meter.CreateObservableGauge("jobdist.worker.outbox.size", () => Volatile.Read(ref _outbox), description: "Completion reports waiting to be delivered.");
    }

    /// <summary>Records a received assignment.</summary>
    /// <param name="jobType">Job type.</param>
    public void Received(string jobType) => _received.Add(1, new KeyValuePair<string, object?>("job.type", jobType));

    /// <summary>Records the end of an attempt.</summary>
    /// <param name="jobType">Job type.</param>
    /// <param name="outcome">Outcome name.</param>
    /// <param name="durationMs">Duration.</param>
    public void Completed(string jobType, string outcome, double durationMs)
    {
        var tags = new TagList { { "job.type", jobType }, { "outcome", outcome } };
        _completed.Add(1, tags);
        _duration.Record(durationMs, tags);
    }

    /// <summary>Records a rejected message.</summary>
    /// <param name="reason">Short reason.</param>
    public void Rejected(string reason) => _rejected.Add(1, new KeyValuePair<string, object?>("reason", reason));

    /// <summary>Records a re-registration.</summary>
    public void ReRegistered() => _reconnects.Add(1);

    /// <summary>Records a dropped report.</summary>
    /// <param name="reason">Short reason.</param>
    public void ReportDropped(string reason) => _reportsDropped.Add(1, new KeyValuePair<string, object?>("reason", reason));

    /// <summary>Adjusts the running gauge.</summary>
    /// <param name="delta">+1 / -1.</param>
    public void RunningDelta(int delta) => Interlocked.Add(ref _running, delta);

    /// <summary>Adjusts the outbox gauge.</summary>
    /// <param name="delta">+1 / -1.</param>
    public void OutboxDelta(int delta) => Interlocked.Add(ref _outbox, delta);

    /// <summary>Disposes the meter.</summary>
    public void Dispose() => _meter.Dispose();
}
