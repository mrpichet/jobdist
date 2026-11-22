using System.Collections.Concurrent;
using JobDist.Contracts;
using JobDist.Core.Domain;

namespace JobDist.Core.Services;

/// <summary>
/// Online machine-learning model that learns how fast and how reliably each worker runs each job type, used by the scheduler
/// to adapt distribution over time. Pure C# (no I/O), thread-safe, O(1) per observation and per prediction, so it can sit on
/// the hot path of a high-throughput scheduler.
/// <para>Model, per (worker, job type):</para>
/// <list type="bullet">
/// <item><b>Duration</b>: exponentially weighted mean/variance of log(duration) - adapts to drift (machine upgraded, noisy neighbour).</item>
/// <item><b>Reliability</b>: Beta-Bernoulli posterior of success (worker losses count as failures, so unstable nodes are avoided).</item>
/// </list>
/// <para>Per job type, a global linear regression (normalised LMS, trained online) predicts log(duration) from machine features
/// (benchmark, cores, GPU, declared speed, load). It gives a sensible <b>cold-start</b> estimate for new workers and is blended
/// with the worker's own history as observations accumulate (confidence = n / (n + k)).</para>
/// <para>Exploration: an Upper Confidence Bound bonus lets new / rarely used workers receive some work so the model keeps learning.</para>
/// </summary>
public sealed class PerformanceLearningService
{
    /// <summary>Default expected duration when nothing is known about a job type.</summary>
    public const double DefaultDurationMs = 1_000;

    private const int FeatureCount = 6;
    private const double PriorStrength = 3.0;      // k in confidence = n/(n+k)
    private const double SuccessPriorAlpha = 2.0;  // optimistic prior: new workers start at 2/3 success
    private const double SuccessPriorBeta = 1.0;
    private const double MinAlpha = 0.05;          // EWMA floor => remembers roughly the last 20 observations
    private const double LearningRate = 0.05;

    private readonly ConcurrentDictionary<(Guid Worker, string JobType), WorkerTypeStat> _stats = new();
    private readonly ConcurrentDictionary<string, TypeRegression> _regressions = new(StringComparer.OrdinalIgnoreCase);
    private long _totalObservations;

    /// <summary>Total observations learned (for the exploration term).</summary>
    public long TotalObservations => Interlocked.Read(ref _totalObservations);

    /// <summary>
    /// Learns from one finished attempt.
    /// </summary>
    /// <param name="worker">Worker that ran the attempt (spec and capability are model features).</param>
    /// <param name="jobType">Job type.</param>
    /// <param name="durationMs">Measured execution time (ignored when not positive).</param>
    /// <param name="outcome">Outcome; Succeeded is a success, Failed/TimedOut/WorkerLost are failures, Cancelled/Released are ignored.</param>
    /// <param name="loadAtAssign">Worker load ratio when the job was assigned.</param>
    /// <param name="atUtc">Observation time.</param>
    public void Observe(WorkerDTO worker, string jobType, double durationMs, AttemptOutcome outcome, double loadAtAssign, DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(worker);

        // Step 1: cancellations and voluntary releases say nothing about the worker's ability.
        if (outcome is AttemptOutcome.Cancelled or AttemptOutcome.Released or AttemptOutcome.InProgress)
        {
            return;
        }
        var success = outcome == AttemptOutcome.Succeeded;
        var stat = _stats.GetOrAdd((worker.WorkerId, jobType), _ => new WorkerTypeStat());

        // Step 2: update the per-worker statistics under a fine-grained lock (one lock per worker/type pair).
        lock (stat)
        {
            stat.Samples++;
            if (success)
            {
                stat.Successes++;
            }
            else
            {
                stat.Failures++;
            }

            // Step 2a: only successful runs describe the real duration (a timeout's duration is the timeout).
            if (success && durationMs > 0)
            {
                var y = Math.Log(durationMs);
                stat.DurationSamples++;
                var alpha = Math.Max(1.0 / stat.DurationSamples, MinAlpha);
                var delta = y - stat.MeanLog;
                stat.MeanLog += alpha * delta;
                stat.VarLog = (1 - alpha) * (stat.VarLog + alpha * delta * delta);
                stat.MeanLoad += alpha * (loadAtAssign - stat.MeanLoad);
            }
            stat.LastUpdatedUtc = atUtc;
        }

        // Step 3: train the global per-type regression with the same sample (cold-start model).
        if (success && durationMs > 0)
        {
            var regression = _regressions.GetOrAdd(jobType, _ => new TypeRegression());
            Span<double> x = stackalloc double[FeatureCount];
            BuildFeatures(worker, jobType, loadAtAssign, x);
            regression.Train(x, Math.Log(durationMs));
        }
        Interlocked.Increment(ref _totalObservations);
    }

    /// <summary>
    /// Predicts how a worker would perform a job type at a given load.
    /// </summary>
    /// <param name="worker">Candidate worker.</param>
    /// <param name="jobType">Job type.</param>
    /// <param name="loadRatio">Current load ratio of the worker (0-1).</param>
    /// <returns>Expected duration, success probability, confidence and sample count.</returns>
    public PerformancePredictionDTO Predict(WorkerDTO worker, string jobType, double loadRatio)
    {
        ArgumentNullException.ThrowIfNull(worker);

        // Step 1: cold-start estimate from the regression (or a spec-based heuristic until it has data).
        Span<double> x = stackalloc double[FeatureCount];
        BuildFeatures(worker, jobType, loadRatio, x);
        double priorLog;
        double loadCoefficient = 0.5;
        if (_regressions.TryGetValue(jobType, out var regression) && regression.Samples >= 5)
        {
            priorLog = regression.Predict(x);
            loadCoefficient = Math.Clamp(regression.LoadWeight, 0, 3);
        }
        else
        {
            priorLog = HeuristicLog(worker, jobType, loadRatio);
        }

        // Step 2: no worker history => prior only.
        if (!_stats.TryGetValue((worker.WorkerId, jobType), out var stat))
        {
            return new PerformancePredictionDTO(Math.Exp(priorLog), SuccessPriorAlpha / (SuccessPriorAlpha + SuccessPriorBeta), 0, 0);
        }

        // Step 3: blend the worker's own history (adjusted to the current load) with the prior by confidence.
        lock (stat)
        {
            var n = stat.DurationSamples;
            var ownLog = stat.MeanLog + loadCoefficient * (loadRatio - stat.MeanLoad);
            var blendedLog = n == 0 ? priorLog : (n * ownLog + PriorStrength * priorLog) / (n + PriorStrength);
            var success = (stat.Successes + SuccessPriorAlpha) / (stat.Samples + SuccessPriorAlpha + SuccessPriorBeta);
            var confidence = stat.Samples / (stat.Samples + PriorStrength);
            return new PerformancePredictionDTO(Math.Exp(blendedLog), success, confidence, stat.Samples);
        }
    }

    /// <summary>
    /// Upper Confidence Bound exploration bonus: large for workers with few observations of the type, shrinking as they are tried.
    /// </summary>
    /// <param name="workerId">Worker.</param>
    /// <param name="jobType">Job type.</param>
    /// <returns>Bonus in [0, 1].</returns>
    public double ExplorationBonus(Guid workerId, string jobType)
    {
        var n = _stats.TryGetValue((workerId, jobType), out var s) ? s.Samples : 0;
        return Math.Min(1.0, Math.Sqrt(Math.Log(TotalObservations + 2) / (n + 1)) / 3.0);
    }

    /// <summary>Snapshot of learned statistics (for dashboards, MCP tools and anomaly baselines).</summary>
    /// <returns>One entry per worker/type pair.</returns>
    public IReadOnlyList<PerformanceStatDTO> Snapshot()
    {
        var list = new List<PerformanceStatDTO>(_stats.Count);
        foreach (var ((worker, type), s) in _stats)
        {
            lock (s)
            {
                // Log-normal moments converted back to milliseconds.
                var mean = s.DurationSamples == 0 ? 0 : Math.Exp(s.MeanLog + s.VarLog / 2);
                var std = s.DurationSamples == 0 ? 0 : Math.Sqrt((Math.Exp(s.VarLog) - 1) * Math.Exp(2 * s.MeanLog + s.VarLog));
                list.Add(new PerformanceStatDTO(worker, type, s.Samples, s.Successes, s.Failures, mean, std, s.LastUpdatedUtc));
            }
        }
        return list;
    }

    /// <summary>Historical failure rate of a job type across all workers (null when no data).</summary>
    public double? FailureRate(string jobType)
    {
        long samples = 0, failures = 0;
        foreach (var ((_, type), s) in _stats)
        {
            if (!string.Equals(type, jobType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            lock (s)
            {
                samples += s.Samples;
                failures += s.Failures;
            }
        }
        return samples == 0 ? null : (double)failures / samples;
    }

    /// <summary>
    /// Warm-starts the model from stored history (called at start-up and periodically by the leader so every instance converges).
    /// </summary>
    /// <param name="attempts">Finished attempts, any order.</param>
    /// <param name="workers">Workers by id (needed for features); attempts of unknown workers are skipped.</param>
    /// <returns>Number of attempts learned.</returns>
    public int TrainFromHistory(IEnumerable<JobAttemptDTO> attempts, IReadOnlyDictionary<Guid, WorkerDTO> workers)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        ArgumentNullException.ThrowIfNull(workers);

        // Step 1: reset so replaying history does not double count.
        _stats.Clear();
        _regressions.Clear();
        Interlocked.Exchange(ref _totalObservations, 0);

        // Step 2: replay in chronological order so the EWMA ends at the most recent behaviour.
        var learned = 0;
        foreach (var a in attempts.Where(a => a.FinishedUtc is not null).OrderBy(a => a.FinishedUtc))
        {
            if (workers.TryGetValue(a.WorkerId, out var w))
            {
                Observe(w, a.JobType, a.DurationMs ?? 0, a.Outcome, a.WorkerLoadAtAssign, a.FinishedUtc!.Value);
                learned++;
            }
        }
        return learned;
    }

    /// <summary>Feature vector: [bias, -ln(benchmark), -ln(cores), -ln(speedFactor), load, hasGpu].</summary>
    private static void BuildFeatures(WorkerDTO worker, string jobType, double load, Span<double> x)
    {
        var speed = worker.CapabilityFor(jobType)?.SpeedFactor ?? 1.0;
        x[0] = 1.0;
        x[1] = -Math.Log(Math.Max(0.01, worker.Spec.BenchmarkScore));
        x[2] = -Math.Log(Math.Max(1, worker.Spec.CpuCores));
        x[3] = -Math.Log(Math.Max(0.01, speed));
        x[4] = Math.Clamp(load, 0, 1);
        x[5] = worker.Spec.GpuCount > 0 ? 1.0 : 0.0;
    }

    /// <summary>Spec-based heuristic used before the regression has data: faster/bigger machines are assumed quicker.</summary>
    private static double HeuristicLog(WorkerDTO worker, string jobType, double load)
    {
        var speed = worker.CapabilityFor(jobType)?.SpeedFactor ?? 1.0;
        var power = Math.Max(0.01, worker.Spec.BenchmarkScore) * Math.Max(0.01, speed) * (1 + 0.1 * Math.Log2(Math.Max(1, worker.Spec.CpuCores)));
        return Math.Log(DefaultDurationMs / power) + 0.5 * Math.Clamp(load, 0, 1);
    }

    /// <summary>Mutable statistics of one worker/type pair (guarded by locking the instance).</summary>
    private sealed class WorkerTypeStat
    {
        public int Samples;
        public int Successes;
        public int Failures;
        public int DurationSamples;
        public double MeanLog;
        public double VarLog;
        public double MeanLoad;
        public DateTimeOffset LastUpdatedUtc;
    }

    /// <summary>Online linear regression trained with normalised least-mean-squares (stable for any feature scale).</summary>
    private sealed class TypeRegression
    {
        private readonly double[] _w = new double[FeatureCount];
        private readonly Lock _gate = new();
        public int Samples;

        /// <summary>Coefficient of the load feature (how much load slows this job type down, in log space).</summary>
        public double LoadWeight
        {
            get
            {
                lock (_gate)
                {
                    return _w[4];
                }
            }
        }

        public void Train(ReadOnlySpan<double> x, double y)
        {
            lock (_gate)
            {
                // First sample: initialise the bias to the observation so the model is immediately useful.
                if (Samples == 0)
                {
                    _w[0] = y;
                }
                var error = y - Dot(x);
                double norm = 1;
                foreach (var v in x)
                {
                    norm += v * v;
                }
                for (var i = 0; i < FeatureCount; i++)
                {
                    _w[i] += LearningRate * error * x[i] / norm;
                }
                Samples++;
            }
        }

        public double Predict(ReadOnlySpan<double> x)
        {
            lock (_gate)
            {
                return Dot(x);
            }
        }

        private double Dot(ReadOnlySpan<double> x)
        {
            double s = 0;
            for (var i = 0; i < FeatureCount; i++)
            {
                s += _w[i] * x[i];
            }
            return s;
        }
    }
}
