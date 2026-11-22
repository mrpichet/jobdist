using JobDist.Contracts;
using JobDist.Core.DAO;
using JobDist.Core.Domain;
using JobDist.Core.Observability;
using JobDist.Core.Options;
using Microsoft.Extensions.Options;

namespace JobDist.Core.Services;

/// <summary>Everything the detector looks at, gathered once (keeps <see cref="AnomalyDetectionService.Analyze"/> pure and testable).</summary>
public sealed record AnomalyInputDTO(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    IReadOnlyList<double> BaselineSubmissionsPerMinute,
    IReadOnlyList<double> WindowSubmissionsPerMinute,
    IReadOnlyList<JobAttemptDTO> WindowAttempts,
    IReadOnlyList<PerformanceStatDTO> BaselineStats,
    IReadOnlyList<WorkerDTO> Workers,
    IReadOnlyDictionary<string, int> PendingByType,
    IReadOnlyDictionary<string, int> RejectedReports);

/// <summary>
/// Detects abnormal job distribution behaviour with robust statistics (z-scores against a baseline, rate thresholds):
/// submission spikes, high failure rates per job type and per worker, slow execution, flapping workers, fail-over storms,
/// queues without capable workers and suspicious worker reports. Results feed <see cref="AlertService"/>.
/// </summary>
public sealed class AnomalyDetectionService(
    IJobDAO jobs,
    IJobAttemptDAO attempts,
    IWorkerDAO workers,
    WorkerReportTracker tracker,
    JobDistMetrics metrics,
    IOptions<AnomalyOptions> anomalyOptions,
    IOptions<JobDistOptions> options,
    TimeProvider time)
{
    /// <summary>
    /// Scans the most recent window.
    /// </summary>
    /// <param name="windowMinutes">Window length (5-1440); null = configured default.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Report with every anomaly found.</returns>
    public async Task<AnomalyReportDTO> ScanAsync(int? windowMinutes, CancellationToken ct)
    {
        var a = anomalyOptions.Value;
        var window = TimeSpan.FromMinutes(Math.Clamp(windowMinutes ?? a.WindowMinutes, 5, 1440));
        var now = time.GetUtcNow();
        var from = now - window;
        var baselineFrom = now.AddHours(-a.BaselineHours);

        // Step 1: gather inputs (bounded queries).
        var submissions = await jobs.GetSubmissionTimesAsync(baselineFrom, now, 500_000, ct).ConfigureAwait(false);
        var windowAttempts = await attempts.GetFinishedSinceAsync(from, 100_000, ct).ConfigureAwait(false);
        var baselineStats = await attempts.GetStatsAsync(baselineFrom, ct).ConfigureAwait(false);
        var workerList = await workers.ListAsync(false, ct).ConfigureAwait(false);
        var pending = await jobs.PendingByTypeAsync(ct).ConfigureAwait(false);

        // Step 2: per-minute submission buckets for baseline (before the window) and the window itself.
        var baseline = Bucket(submissions.Where(t => t < from), baselineFrom, from);
        var current = Bucket(submissions.Where(t => t >= from), from, now);

        // Step 3: analyse.
        var input = new AnomalyInputDTO(from, now, baseline, current, windowAttempts, baselineStats, workerList, pending, tracker.CountsSince(from));
        var anomalies = Analyze(input, a, options.Value.UseSimd);
        foreach (var x in anomalies)
        {
            metrics.AnomalyDetected(x.Kind.ToString(), x.Severity.ToString());
        }
        return new AnomalyReportDTO(from, now, anomalies);
    }

    /// <summary>
    /// Pure analysis of gathered inputs.
    /// </summary>
    /// <param name="input">Window data.</param>
    /// <param name="o">Thresholds.</param>
    /// <param name="useSimd">Use SIMD statistics.</param>
    /// <returns>Anomalies, most severe first.</returns>
    public static IReadOnlyList<AnomalyDTO> Analyze(AnomalyInputDTO input, AnomalyOptions o, bool useSimd)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(o);
        var found = new List<AnomalyDTO>();
        var now = input.ToUtc;

        // Step 1: submission spike - mean rate in the window vs. baseline per-minute distribution.
        if (input.BaselineSubmissionsPerMinute.Count >= 10 && input.WindowSubmissionsPerMinute.Count > 0)
        {
            var (mean, std) = ScoringMath.MeanStdDev(input.BaselineSubmissionsPerMinute.ToArray(), useSimd);
            var observed = input.WindowSubmissionsPerMinute.Average();
            var z = ScoringMath.ZScore(observed, mean, std, 1.0);
            if (z >= o.ZScoreThreshold && observed >= 5)
            {
                found.Add(new AnomalyDTO(AnomalyKind.SubmissionSpike, z >= o.ZScoreThreshold * 2 ? AnomalySeverity.Critical : AnomalySeverity.Warning,
                    "cluster", $"Job submissions {observed:F1}/min vs normal {mean:F1}±{std:F1}/min (z={z:F1}).", observed, mean, z, now));
            }
        }

        // Step 2: per job type - failure rate and slow execution against history.
        var baselineByType = input.BaselineStats
            .GroupBy(s => s.JobType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Combine(g), StringComparer.OrdinalIgnoreCase);
        foreach (var g in input.WindowAttempts.Where(x => x.Outcome is not (AttemptOutcome.Cancelled or AttemptOutcome.Released or AttemptOutcome.InProgress))
                     .GroupBy(x => x.JobType, StringComparer.OrdinalIgnoreCase))
        {
            var total = g.Count();
            if (total < o.MinSamples)
            {
                continue;
            }
            var failures = g.Count(x => x.Outcome != AttemptOutcome.Succeeded);
            var rate = (double)failures / total;
            var expected = baselineByType.TryGetValue(g.Key, out var b) && b.Samples > 0 ? b.FailureRate : 0.0;
            if (rate >= o.FailureRateThreshold && rate > expected * 1.5)
            {
                found.Add(new AnomalyDTO(AnomalyKind.HighFailureRate, rate >= 0.75 ? AnomalySeverity.Critical : AnomalySeverity.Warning,
                    $"jobType:{g.Key}", $"{failures}/{total} attempts of '{g.Key}' failed ({rate:P0}, normal {expected:P0}).", rate, expected, rate - expected, now));
            }

            var durations = g.Where(x => x.Outcome == AttemptOutcome.Succeeded && x.DurationMs > 0).Select(x => x.DurationMs!.Value).ToArray();
            if (b is not null && b.Samples >= o.MinSamples && durations.Length >= Math.Max(3, o.MinSamples / 2))
            {
                var (meanNow, _) = ScoringMath.MeanStdDev(durations, useSimd);
                // Standard error of the window mean (n samples) gives a fair z-score for an average.
                var z = ScoringMath.ZScore(meanNow, b.MeanMs, b.StdMs / Math.Sqrt(durations.Length), 1.0);
                if (z >= o.ZScoreThreshold && meanNow > b.MeanMs * 1.2)
                {
                    found.Add(new AnomalyDTO(AnomalyKind.SlowExecution, z >= o.ZScoreThreshold * 2 ? AnomalySeverity.Critical : AnomalySeverity.Warning,
                        $"jobType:{g.Key}", $"'{g.Key}' takes {meanNow:F0} ms on average vs normal {b.MeanMs:F0} ms (z={z:F1}).", meanNow, b.MeanMs, z, now));
                }
            }
        }

        // Step 3: per worker - failure rate, flapping (repeated losses) and the cluster-wide fail-over storm.
        var names = input.Workers.ToDictionary(w => w.WorkerId, w => w.Name);
        var totalLost = 0;
        foreach (var g in input.WindowAttempts.GroupBy(x => x.WorkerId))
        {
            var name = names.GetValueOrDefault(g.Key, g.Key.ToString());
            var lost = g.Count(x => x.Outcome == AttemptOutcome.WorkerLost);
            totalLost += lost;
            var relevant = g.Count(x => x.Outcome is AttemptOutcome.Succeeded or AttemptOutcome.Failed or AttemptOutcome.TimedOut);
            var failed = g.Count(x => x.Outcome is AttemptOutcome.Failed or AttemptOutcome.TimedOut);
            if (relevant >= o.MinSamples && (double)failed / relevant >= o.FailureRateThreshold)
            {
                var rate = (double)failed / relevant;
                found.Add(new AnomalyDTO(AnomalyKind.HighFailureRate, rate >= 0.75 ? AnomalySeverity.Critical : AnomalySeverity.Warning,
                    $"worker:{name}", $"Worker {name} failed {failed}/{relevant} attempts ({rate:P0}).", rate, o.FailureRateThreshold, rate, now));
            }
            var lostEvents = g.Where(x => x.Outcome == AttemptOutcome.WorkerLost).Select(x => x.FinishedUtc).Distinct().Count();
            if (lostEvents >= o.FlappingThreshold)
            {
                found.Add(new AnomalyDTO(AnomalyKind.WorkerFlapping, AnomalySeverity.Warning, $"worker:{name}",
                    $"Worker {name} dropped off the network {lostEvents} times in the window ({lost} jobs re-distributed).", lostEvents, 0, lostEvents, now));
            }
        }
        if (totalLost >= o.FailoverStormThreshold)
        {
            found.Add(new AnomalyDTO(AnomalyKind.FailoverStorm, AnomalySeverity.Critical, "cluster",
                $"{totalLost} jobs were re-distributed because workers were lost (network or infrastructure problem?).", totalLost, o.FailoverStormThreshold, totalLost, now));
        }

        // Step 4: queue starvation - pending work that no online worker can run.
        foreach (var (type, count) in input.PendingByType)
        {
            if (count <= 0)
            {
                continue;
            }
            var capable = input.Workers.Count(w => w.Status == WorkerStatus.Online && w.CapabilityFor(type) is not null);
            if (capable == 0)
            {
                found.Add(new AnomalyDTO(AnomalyKind.QueueStarvation, AnomalySeverity.Critical, $"jobType:{type}",
                    $"{count} '{type}' jobs are waiting but no online worker can run this type.", count, 0, count, now));
            }
        }

        // Step 5: suspicious worker activity - many rejected / stale reports.
        foreach (var (subject, count) in input.RejectedReports)
        {
            if (count >= Math.Max(5, o.MinSamples))
            {
                var name = Guid.TryParse(subject, out var id) ? names.GetValueOrDefault(id, subject) : subject;
                found.Add(new AnomalyDTO(AnomalyKind.SuspiciousWorkerActivity, count >= o.MinSamples * 5 ? AnomalySeverity.Critical : AnomalySeverity.Warning,
                    $"worker:{name}", $"{count} rejected reports from {name} (stale attempts / invalid token). Broken worker or attack?", count, 0, count, now));
            }
        }
        return found.OrderByDescending(x => x.Severity).ThenByDescending(x => x.Score).ToList();
    }

    /// <summary>Counts timestamps into one-minute buckets between two times.</summary>
    internal static double[] Bucket(IEnumerable<DateTimeOffset> times, DateTimeOffset from, DateTimeOffset to)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling((to - from).TotalMinutes));
        var buckets = new double[minutes];
        foreach (var t in times)
        {
            var i = (int)((t - from).TotalMinutes);
            if (i >= 0 && i < minutes)
            {
                buckets[i]++;
            }
        }
        return buckets;
    }

    /// <summary>Pooled statistics of several worker/type groups.</summary>
    private static TypeBaseline Combine(IEnumerable<PerformanceStatDTO> stats)
    {
        var list = stats.ToList();
        var samples = list.Sum(s => s.Samples);
        var failures = list.Sum(s => s.Failures);
        var successes = list.Sum(s => s.Successes);
        if (successes == 0)
        {
            return new TypeBaseline(samples, samples == 0 ? 0 : (double)failures / samples, 0, 0);
        }

        // Pooled mean and variance (weights = successful samples, which carry the durations).
        var mean = list.Sum(s => s.MeanDurationMs * s.Successes) / successes;
        var variance = list.Sum(s => s.Successes * (s.StdDevDurationMs * s.StdDevDurationMs + Math.Pow(s.MeanDurationMs - mean, 2))) / successes;
        return new TypeBaseline(samples, samples == 0 ? 0 : (double)failures / samples, mean, Math.Sqrt(variance));
    }

    private sealed record TypeBaseline(int Samples, double FailureRate, double MeanMs, double StdMs);
}
