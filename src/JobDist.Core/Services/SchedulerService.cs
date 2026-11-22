using System.Diagnostics;
using JobDist.Contracts;
using JobDist.Core.DAO;
using JobDist.Core.Domain;
using JobDist.Core.Observability;
using JobDist.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobDist.Core.Services;

/// <summary>Result of planning one scheduling cycle.</summary>
/// <param name="Assignments">Decisions to persist.</param>
/// <param name="Unschedulable">Jobs that could not be placed, with the reason (no capable worker, capacity full...).</param>
public sealed record SchedulePlan(IReadOnlyList<AssignmentDTO> Assignments, IReadOnlyDictionary<Guid, string> Unschedulable);

/// <summary>
/// The distribution brain. Matches ready jobs to workers by:
/// <list type="number">
/// <item><b>Eligibility</b>: worker Online, registered the job type, free slot for the type and in total, the strict target
/// if one is set, and an encryption key when the payload is sensitive.</item>
/// <item><b>Order</b>: effective priority = priority + aging (waiting minutes × <c>AgingPointsPerMinute</c>) so low priority jobs
/// cannot starve, then oldest first.</item>
/// <item><b>Score</b> (higher wins) = load headroom + predicted speed (ML model × machine spec) + predicted reliability +
/// exploration bonus + preferred-worker bonus − penalty for the worker that failed the previous attempt.</item>
/// </list>
/// Planning is pure and in-memory (one DB read, one batched DB write per cycle) for high throughput; ML predictions per job type
/// are pre-computed in parallel and per-job scoring uses SIMD kernels (<see cref="ScoringMath"/>).
/// </summary>
public sealed class SchedulerService(
    IJobDAO jobs,
    IWorkerDAO workers,
    PerformanceLearningService model,
    WorkerSignalService signals,
    JobDistMetrics metrics,
    IOptions<JobDistOptions> options,
    TimeProvider time,
    ILogger<SchedulerService> logger)
{
    /// <summary>
    /// Runs one scheduling cycle: load ready jobs and online workers, plan, persist, wake the chosen workers.
    /// Called repeatedly by the leader instance.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of jobs assigned.</returns>
    public async Task<int> RunCycleAsync(CancellationToken ct)
    {
        using var activity = JobDistMetrics.ActivitySource.StartActivity("scheduler.cycle");
        var sw = Stopwatch.StartNew();
        var o = options.Value;
        var now = time.GetUtcNow();

        // Step 1: ready jobs (highest priority first). Nothing to do => cheap exit.
        var ready = await jobs.GetReadyPendingAsync(now, o.SchedulerBatchSize, ct).ConfigureAwait(false);
        if (ready.Count == 0)
        {
            return 0;
        }

        // Step 2: worker snapshot + live load (two queries regardless of cluster size).
        var online = (await workers.ListAsync(false, ct).ConfigureAwait(false)).Where(w => w.Status == WorkerStatus.Online).ToList();
        var load = await jobs.GetActiveLoadAsync(ct).ConfigureAwait(false);

        // Step 3: plan in memory.
        var plan = Plan(ready, online, load, now);

        // Step 4: persist atomically; lost races (job cancelled meanwhile...) are simply skipped.
        var applied = plan.Assignments.Count == 0
            ? []
            : await jobs.ApplyAssignmentsAsync(plan.Assignments, now, now.AddSeconds(o.AssignmentStartTimeoutSeconds), ct).ConfigureAwait(false);

        // Step 5: metrics and wake-ups.
        var byId = ready.ToDictionary(j => j.JobId);
        foreach (var a in applied)
        {
            var job = byId[a.JobId];
            var readySince = job.NextAttemptUtc > job.CreatedUtc ? job.NextAttemptUtc : job.CreatedUtc;
            metrics.JobAssigned(job.JobType, Math.Max(0, (now - readySince).TotalMilliseconds));
        }
        foreach (var workerId in applied.Select(a => a.WorkerId).Distinct())
        {
            signals.Notify(workerId);
        }
        metrics.SchedulerCycle(sw.Elapsed.TotalMilliseconds, applied.Count);
        activity?.SetTag("jobs.ready", ready.Count);
        activity?.SetTag("jobs.assigned", applied.Count);
        if (plan.Unschedulable.Count > 0)
        {
            logger.LogDebug("{Count} jobs could not be scheduled this cycle.", plan.Unschedulable.Count);
        }
        return applied.Count;
    }

    /// <summary>
    /// Pure planning function (no I/O): decides which worker runs which job.
    /// </summary>
    /// <param name="ready">Ready pending jobs.</param>
    /// <param name="onlineWorkers">Online workers.</param>
    /// <param name="activeLoad">Currently active jobs per worker/type.</param>
    /// <param name="now">Current time (for aging).</param>
    /// <returns>Assignments and the reasons for unscheduled jobs.</returns>
    public SchedulePlan Plan(IReadOnlyList<JobDTO> ready, IReadOnlyList<WorkerDTO> onlineWorkers, IReadOnlyList<WorkerLoadDTO> activeLoad, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ready);
        ArgumentNullException.ThrowIfNull(onlineWorkers);
        ArgumentNullException.ThrowIfNull(activeLoad);
        var o = options.Value;
        var assignments = new List<AssignmentDTO>();
        var unschedulable = new Dictionary<Guid, string>();

        // Step 1: index workers and build mutable capacity counters (struct-of-arrays for cache-friendly scoring).
        var workerIndex = new Dictionary<Guid, int>(onlineWorkers.Count);
        for (var i = 0; i < onlineWorkers.Count; i++)
        {
            workerIndex[onlineWorkers[i].WorkerId] = i;
        }
        var activeTotal = new int[onlineWorkers.Count];
        var activeByType = new Dictionary<(int Worker, string Type), int>();
        foreach (var l in activeLoad)
        {
            if (workerIndex.TryGetValue(l.WorkerId, out var wi))
            {
                activeTotal[wi] += l.Active;
                var key = (wi, l.JobType.ToUpperInvariant());
                activeByType[key] = activeByType.GetValueOrDefault(key) + l.Active;
            }
        }

        // Step 2: candidate lists per job type + ML predictions, computed once per type (in parallel for many types).
        var types = ready.Select(j => j.JobType.ToUpperInvariant()).Distinct().ToArray();
        var contexts = new TypeContext[types.Length];
        void Build(int t) => contexts[t] = BuildTypeContext(types[t], onlineWorkers, activeTotal);
        if (types.Length >= o.ParallelThreshold)
        {
            Parallel.For(0, types.Length, new ParallelOptions { MaxDegreeOfParallelism = o.MaxDegreeOfParallelism > 0 ? o.MaxDegreeOfParallelism : Environment.ProcessorCount }, Build);
        }
        else
        {
            for (var t = 0; t < types.Length; t++)
            {
                Build(t);
            }
        }
        var contextByType = new Dictionary<string, TypeContext>(StringComparer.Ordinal);
        for (var t = 0; t < types.Length; t++)
        {
            contextByType[types[t]] = contexts[t];
        }

        // Step 3: order jobs by effective priority (priority + aging), then by age.
        var ordered = ready
            .Select(j => (Job: j, Effective: EffectivePriority(j, now)))
            .OrderByDescending(x => x.Effective)
            .ThenBy(x => x.Job.CreatedUtc)
            .Select(x => x.Job);

        // Step 4: greedy assignment - each job gets the best eligible worker; counters update immediately so the next job
        // sees the new load (spreads a burst across the cluster instead of piling onto one machine).
        var w = Normalise(o.Weights);
        foreach (var job in ordered)
        {
            var ctx = contextByType[job.JobType.ToUpperInvariant()];
            if (ctx.Candidates.Length == 0)
            {
                unschedulable[job.JobId] = "no_capable_worker_online";
                continue;
            }
            if (ctx.Saturated)
            {
                unschedulable[job.JobId] = "capacity_full";
                continue;
            }

            var n = ctx.Candidates.Length;
            // Scratch buffers are allocated once per job type (no per-job allocation, no stackalloc in a loop).
            var loadArr = ctx.LoadBuffer;
            var bonus = ctx.BonusBuffer;
            var scores = ctx.ScoreBuffer;
            var eligible = ctx.EligibleBuffer;
            var anyEligible = false;
            var anyCapacity = false;

            // Step 4a: live load + eligibility per candidate.
            for (var c = 0; c < n; c++)
            {
                var wi = ctx.Candidates[c];
                var worker = onlineWorkers[wi];
                var typeActive = activeByType.GetValueOrDefault((wi, ctx.TypeKey));
                var hasSlot = typeActive < ctx.MaxConcurrency[c] && activeTotal[wi] < Math.Max(1, worker.MaxTotalConcurrency);
                anyCapacity |= hasSlot;
                loadArr[c] = LoadRatio(worker, activeTotal[wi]);
                var ok = hasSlot
                         && (!job.StrictTarget || job.TargetWorkerId == worker.WorkerId)
                         && (!job.Sensitive || !string.IsNullOrEmpty(worker.EcdhPublicKey));
                eligible[c] = ok;
                anyEligible |= ok;
                bonus[c] = (job.TargetWorkerId == worker.WorkerId ? w.Preferred : 0)
                           - (job.LastFailedWorkerId == worker.WorkerId ? w.LastFailedPenalty : 0);
            }
            if (!anyCapacity)
            {
                ctx.Saturated = true;
                unschedulable[job.JobId] = "capacity_full";
                continue;
            }
            if (!anyEligible)
            {
                unschedulable[job.JobId] = job.StrictTarget ? "target_worker_unavailable" : "no_eligible_worker";
                continue;
            }

            // Step 4b: SIMD scoring and arg-max.
            ScoringMath.ComputeScores(loadArr, ctx.Speed, ctx.Reliability, ctx.Exploration, bonus, (w.Load, w.Speed, w.Reliability, w.Exploration), scores, o.UseSimd);
            var best = ScoringMath.ArgMax(scores, eligible);
            var chosen = ctx.Candidates[best];

            // Step 4c: record the decision and update counters.
            assignments.Add(new AssignmentDTO(job.JobId, job.Version, onlineWorkers[chosen].WorkerId, Guid.CreateVersion7(), scores[best], loadArr[best]));
            activeTotal[chosen]++;
            var k = (chosen, ctx.TypeKey);
            activeByType[k] = activeByType.GetValueOrDefault(k) + 1;
        }
        return new SchedulePlan(assignments, unschedulable);
    }

    /// <summary>
    /// Ranks workers for a job type with an explanation (used by the API / MCP "recommend workers" tool and the UI).
    /// </summary>
    /// <param name="jobType">Job type.</param>
    /// <param name="top">Max results.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Ranked candidates, best first.</returns>
    public async Task<IReadOnlyList<WorkerRecommendationDTO>> RecommendAsync(string jobType, int top, CancellationToken ct)
    {
        // Step 1: snapshot.
        var online = (await workers.ListAsync(false, ct).ConfigureAwait(false)).Where(w => w.Status == WorkerStatus.Online).ToList();
        var load = await jobs.GetActiveLoadAsync(ct).ConfigureAwait(false);
        var totals = online.Select(w => load.Where(l => l.WorkerId == w.WorkerId).Sum(l => l.Active)).ToArray();

        // Step 2: same scoring as the scheduler (without capacity filtering, so users see why a worker is busy).
        var ctx = BuildTypeContext(jobType.ToUpperInvariant(), online, totals);
        var w = Normalise(options.Value.Weights);
        var result = new List<WorkerRecommendationDTO>(ctx.Candidates.Length);
        for (var c = 0; c < ctx.Candidates.Length; c++)
        {
            var worker = online[ctx.Candidates[c]];
            var loadRatio = LoadRatio(worker, totals[ctx.Candidates[c]]);
            var score = w.Load * (1 - loadRatio) + w.Speed * ctx.Speed[c] + w.Reliability * ctx.Reliability[c] + w.Exploration * ctx.Exploration[c];
            result.Add(new WorkerRecommendationDTO(worker.WorkerId, worker.Name, Math.Round(score, 4), Math.Round(loadRatio, 3),
                Math.Round(ctx.ExpectedMs[c], 1), Math.Round(ctx.Reliability[c], 3),
                $"load {loadRatio:P0}, expected {ctx.ExpectedMs[c]:F0} ms, success {ctx.Reliability[c]:P0}, {worker.Spec.CpuCores} cores, benchmark {worker.Spec.BenchmarkScore:F2}"));
        }
        return result.OrderByDescending(r => r.Score).Take(Math.Clamp(top, 1, 100)).ToList();
    }

    /// <summary>Priority plus aging bonus, capped so aging never exceeds +100.</summary>
    internal double EffectivePriority(JobDTO job, DateTimeOffset now)
    {
        var readySince = job.NextAttemptUtc > job.CreatedUtc ? job.NextAttemptUtc : job.CreatedUtc;
        var waitedMinutes = Math.Max(0, (now - readySince).TotalMinutes);
        return job.Priority + Math.Min(100, waitedMinutes * options.Value.AgingPointsPerMinute);
    }

    /// <summary>Load ratio blending slot usage (80%) and reported CPU (20%).</summary>
    private static double LoadRatio(WorkerDTO worker, int activeTotal) =>
        Math.Clamp(0.8 * activeTotal / Math.Max(1, worker.MaxTotalConcurrency) + 0.2 * worker.CpuUsagePercent / 100.0, 0, 1);

    /// <summary>Candidates and model predictions for one job type.</summary>
    private TypeContext BuildTypeContext(string typeKey, IReadOnlyList<WorkerDTO> online, int[] activeTotal)
    {
        // Step 1: workers that registered the type.
        var candidates = new List<int>();
        for (var i = 0; i < online.Count; i++)
        {
            if (online[i].CapabilityFor(typeKey) is not null)
            {
                candidates.Add(i);
            }
        }

        // Step 2: predictions.
        var n = candidates.Count;
        var ctx = new TypeContext(typeKey, [.. candidates], new int[n], new double[n], new double[n], new double[n], new double[n]);
        var fastest = double.MaxValue;
        for (var c = 0; c < n; c++)
        {
            var worker = online[candidates[c]];
            var cap = worker.CapabilityFor(typeKey)!;
            ctx.MaxConcurrency[c] = Math.Max(1, cap.MaxConcurrency);
            var prediction = model.Predict(worker, cap.JobType, LoadRatio(worker, activeTotal[candidates[c]]));
            ctx.ExpectedMs[c] = Math.Max(0.001, prediction.ExpectedDurationMs);
            ctx.Reliability[c] = prediction.SuccessProbability;
            ctx.Exploration[c] = model.ExplorationBonus(worker.WorkerId, cap.JobType);
            fastest = Math.Min(fastest, ctx.ExpectedMs[c]);
        }

        // Step 3: normalise speed so the fastest candidate scores 1.0.
        for (var c = 0; c < n; c++)
        {
            ctx.Speed[c] = fastest / ctx.ExpectedMs[c];
        }
        return ctx;
    }

    private static (double Load, double Speed, double Reliability, double Exploration, double Preferred, double LastFailedPenalty) Normalise(SchedulingWeights w)
    {
        var sum = w.Load + w.Speed + w.Reliability + w.Exploration;
        if (sum <= 0)
        {
            sum = 1;
        }
        return (w.Load / sum, w.Speed / sum, w.Reliability / sum, w.Exploration / sum, w.PreferredWorkerBonus, w.LastFailedPenalty);
    }

    /// <summary>Per-type working set for one cycle.</summary>
    private sealed record TypeContext(string TypeKey, int[] Candidates, int[] MaxConcurrency, double[] ExpectedMs, double[] Speed, double[] Reliability, double[] Exploration)
    {
        /// <summary>Set when no candidate has a free slot; later jobs of the type are skipped in O(1).</summary>
        public bool Saturated { get; set; }
        public double[] LoadBuffer { get; } = new double[Candidates.Length];
        public double[] BonusBuffer { get; } = new double[Candidates.Length];
        public double[] ScoreBuffer { get; } = new double[Candidates.Length];
        public bool[] EligibleBuffer { get; } = new bool[Candidates.Length];
    }
}
