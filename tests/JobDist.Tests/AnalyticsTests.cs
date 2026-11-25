using JobDist.Contracts;
using JobDist.Core.Domain;
using JobDist.Core.Observability;
using JobDist.Core.Options;
using JobDist.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JobDist.Tests;

/// <summary>SIMD math, retry policy, machine learning, anomaly detection, alerting and risk.</summary>
[TestClass]
public sealed class AnalyticsTests
{
    // ------------------------------------------------------------------------------------------------ SIMD / math

    [TestMethod]
    [DataRow(1)]
    [DataRow(7)]
    [DataRow(1000)]
    public void Simd_and_scalar_scores_are_identical(int n)
    {
        var rnd = new Random(42);
        double[] Make() => Enumerable.Range(0, n).Select(_ => rnd.NextDouble()).ToArray();
        var (load, speed, rel, exp, bonus) = (Make(), Make(), Make(), Make(), Make());
        var simd = new double[n];
        var scalar = new double[n];
        var w = (0.3, 0.4, 0.2, 0.1);

        ScoringMath.ComputeScores(load, speed, rel, exp, bonus, w, simd, useSimd: true);
        ScoringMath.ComputeScores(load, speed, rel, exp, bonus, w, scalar, useSimd: false);

        for (var i = 0; i < n; i++)
        {
            Assert.AreEqual(scalar[i], simd[i], 1e-12);
        }
    }

    [TestMethod]
    public void Mean_and_stddev_match_between_simd_and_scalar()
    {
        var values = Enumerable.Range(1, 1001).Select(i => (double)i).ToArray();

        var a = ScoringMath.MeanStdDev(values, true);
        var b = ScoringMath.MeanStdDev(values, false);

        Assert.AreEqual(501, a.Mean, 1e-9);
        Assert.AreEqual(b.Mean, a.Mean, 1e-9);
        Assert.AreEqual(b.StdDev, a.StdDev, 1e-6);
    }

    [TestMethod]
    public void ArgMax_ignores_ineligible_candidates()
    {
        Assert.AreEqual(2, ScoringMath.ArgMax([1, 9, 3], [true, false, true]));
        Assert.AreEqual(-1, ScoringMath.ArgMax([1, 2], [false, false]));
    }

    // ------------------------------------------------------------------------------------------------ retry policy

    private static RetryPolicyService Retry(bool countLoss = true) =>
        new(Options.Create(new JobDistOptions { RetryBaseDelaySeconds = 2, RetryMaxDelaySeconds = 60, CountWorkerLossAsAttempt = countLoss }));

    [TestMethod]
    public void Backoff_grows_exponentially_with_jitter_and_cap()
    {
        var policy = Retry();
        var rnd = new Random(1);
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            var expected = Math.Min(60, 2 * Math.Pow(2, attempt - 1));
            var d = policy.Backoff(attempt, rnd).TotalSeconds;
            Assert.IsTrue(d >= expected * 0.8 - 1e-9 && d <= Math.Min(60, expected * 1.2) + 1e-9, $"attempt {attempt}: {d}");
        }
    }

    [TestMethod]
    public void Retry_decisions_follow_the_policy()
    {
        var policy = Retry();
        var now = TestHost.Start;
        var job = new JobDTO { Attempt = 1, MaxAttempts = 3, Status = JobStatus.Running };

        Assert.AreEqual(JobStatus.Succeeded, policy.Decide(job, AttemptOutcome.Succeeded, true, now).FinalStatus);
        Assert.AreEqual(JobStatus.Failed, policy.Decide(job, AttemptOutcome.Failed, false, now).FinalStatus);
        var retry = policy.Decide(job, AttemptOutcome.TimedOut, true, now);
        Assert.IsTrue(retry.IsRetry);
        Assert.IsTrue(retry.NextAttemptUtc > now);

        var lost = policy.Decide(job, AttemptOutcome.WorkerLost, true, now);
        Assert.IsTrue(lost.IsRetry);
        Assert.AreEqual(now, lost.NextAttemptUtc, "Worker loss is re-distributed immediately.");

        var last = new JobDTO { Attempt = 3, MaxAttempts = 3, Status = JobStatus.Running };
        Assert.AreEqual(JobStatus.Failed, policy.Decide(last, AttemptOutcome.Failed, true, now).FinalStatus);
        Assert.AreEqual(JobStatus.Pending, policy.Decide(last, AttemptOutcome.Released, true, now).FinalStatus, "Release never consumes an attempt.");
        Assert.AreEqual(JobStatus.Pending, Retry(countLoss: false).Decide(last, AttemptOutcome.WorkerLost, true, now).FinalStatus);
        Assert.AreEqual(JobStatus.Cancelled, policy.Decide(new JobDTO { Attempt = 1, MaxAttempts = 3, Status = JobStatus.CancelRequested }, AttemptOutcome.Failed, true, now).FinalStatus);
    }

    // ------------------------------------------------------------------------------------------------ machine learning

    private static WorkerDTO Worker(string name, double benchmark = 1) => new()
    {
        WorkerId = Guid.NewGuid(), Name = name, Status = WorkerStatus.Online, MaxTotalConcurrency = 4,
        Capabilities = [new WorkerCapabilityDTO { JobType = "demo", MaxConcurrency = 4 }],
        Spec = new WorkerSpecDTO { CpuCores = 8, MemoryMb = 16_384, BenchmarkScore = benchmark }
    };

    [TestMethod]
    public void Model_learns_which_worker_is_faster_and_more_reliable()
    {
        var model = new PerformanceLearningService();
        var fast = Worker("fast");
        var slow = Worker("slow");
        for (var i = 0; i < 30; i++)
        {
            model.Observe(fast, "demo", 200, AttemptOutcome.Succeeded, 0.2, TestHost.Start);
            model.Observe(slow, "demo", 2_000, i % 3 == 0 ? AttemptOutcome.Failed : AttemptOutcome.Succeeded, 0.2, TestHost.Start);
        }

        var pf = model.Predict(fast, "demo", 0.2);
        var ps = model.Predict(slow, "demo", 0.2);

        Assert.IsLessThan(ps.ExpectedDurationMs, pf.ExpectedDurationMs);
        Assert.IsGreaterThan(ps.SuccessProbability, pf.SuccessProbability);
        Assert.IsGreaterThan(0.85, pf.Confidence);
        Assert.IsGreaterThan(model.ExplorationBonus(slow.WorkerId, "demo"), model.ExplorationBonus(Guid.NewGuid(), "demo"),
            "An unknown worker must get a larger exploration bonus than a well-known one.");
    }

    [TestMethod]
    public void Cold_start_uses_machine_spec_and_regression()
    {
        var model = new PerformanceLearningService();
        var strong = Worker("strong", benchmark: 4);
        var weak = Worker("weak", benchmark: 0.5);

        Assert.IsLessThan(model.Predict(weak, "demo", 0).ExpectedDurationMs, model.Predict(strong, "demo", 0).ExpectedDurationMs);

        // Train the regression on other workers: duration ∝ 1/benchmark. A brand-new worker gets a sensible estimate.
        for (var i = 0; i < 200; i++)
        {
            var w = Worker("w" + i, benchmark: 0.5 + i % 8);
            model.Observe(w, "demo", 4_000 / w.Spec.BenchmarkScore, AttemptOutcome.Succeeded, 0.1, TestHost.Start);
        }
        var fresh = Worker("fresh", benchmark: 8);
        var p = model.Predict(fresh, "demo", 0.1);
        Assert.AreEqual(0, p.Samples);
        Assert.IsLessThan(2_000, p.ExpectedDurationMs, $"Expected a fast estimate for a strong machine, got {p.ExpectedDurationMs:F0} ms");
    }

    [TestMethod]
    public async Task Scheduler_prefers_the_worker_the_model_learned_is_faster()
    {
        using var host = new TestHost(new() { ["JobDist:Weights:Exploration"] = "0" });
        var a = await host.RegisterAsync("a");
        var b = await host.RegisterAsync("b");
        var model = host.Get<PerformanceLearningService>();
        var workers = host.Get<JobDist.Core.DAO.IWorkerDAO>();
        var wa = (await workers.GetAsync(a.WorkerId, CancellationToken.None))!;
        var wb = (await workers.GetAsync(b.WorkerId, CancellationToken.None))!;
        for (var i = 0; i < 40; i++)
        {
            model.Observe(wa, "demo", 5_000, AttemptOutcome.Succeeded, 0, TestHost.Start);
            model.Observe(wb, "demo", 100, AttemptOutcome.Succeeded, 0, TestHost.Start);
        }

        var job = await host.SubmitAsync();
        await host.ScheduleAsync();

        Assert.AreEqual(job.JobId, (await host.PollAsync(b.WorkerId)).Assignments.Single().JobId);
        var ranking = await host.Scheduler.RecommendAsync("demo", 2, CancellationToken.None);
        Assert.AreEqual(b.WorkerId, ranking[0].WorkerId);
    }

    // ------------------------------------------------------------------------------------------------ anomaly detection

    private static AnomalyInputDTO Input(
        IReadOnlyList<double>? baseline = null, IReadOnlyList<double>? window = null, IReadOnlyList<JobAttemptDTO>? attempts = null,
        IReadOnlyList<WorkerDTO>? workers = null, IReadOnlyDictionary<string, int>? pending = null, IReadOnlyDictionary<string, int>? rejected = null) =>
        new(TestHost.Start.AddMinutes(-15), TestHost.Start, baseline ?? [], window ?? [], attempts ?? [], [], workers ?? [], pending ?? new Dictionary<string, int>(), rejected ?? new Dictionary<string, int>());

    private static JobAttemptDTO Attempt(Guid worker, AttemptOutcome outcome, int minute = 0) => new()
    {
        AttemptId = Guid.NewGuid(), JobId = Guid.NewGuid(), WorkerId = worker, JobType = "demo", Outcome = outcome,
        DurationMs = 100, FinishedUtc = TestHost.Start.AddMinutes(-minute)
    };

    [TestMethod]
    public void Healthy_cluster_has_no_anomalies()
    {
        var w = Worker("w");
        var attempts = Enumerable.Range(0, 50).Select(_ => Attempt(w.WorkerId, AttemptOutcome.Succeeded)).ToList();
        var baseline = Enumerable.Range(0, 60).Select(i => 10.0 + i % 3).ToList();

        var found = AnomalyDetectionService.Analyze(Input(baseline, [11, 10, 12], attempts, [w]), new AnomalyOptions(), true);

        Assert.IsEmpty(found);
    }

    [TestMethod]
    public void Detects_submission_spike()
    {
        var baseline = Enumerable.Range(0, 60).Select(i => 10.0 + i % 3).ToList();
        var found = AnomalyDetectionService.Analyze(Input(baseline, [80, 95, 90]), new AnomalyOptions(), true);
        Assert.AreEqual(AnomalyKind.SubmissionSpike, found.Single().Kind);
    }

    [TestMethod]
    public void Detects_high_failure_rate_flapping_and_failover_storm()
    {
        var bad = Worker("bad");
        var attempts = new List<JobAttemptDTO>();
        attempts.AddRange(Enumerable.Range(0, 12).Select(_ => Attempt(bad.WorkerId, AttemptOutcome.Failed)));
        attempts.AddRange(Enumerable.Range(0, 3).Select(_ => Attempt(bad.WorkerId, AttemptOutcome.Succeeded)));
        attempts.AddRange(Enumerable.Range(0, 24).Select(i => Attempt(bad.WorkerId, AttemptOutcome.WorkerLost, minute: i % 4)));

        var found = AnomalyDetectionService.Analyze(Input(attempts: attempts, workers: [bad]), new AnomalyOptions(), true);
        var kinds = found.Select(f => f.Kind).ToHashSet();

        Assert.Contains(AnomalyKind.HighFailureRate, kinds);
        Assert.Contains(AnomalyKind.WorkerFlapping, kinds);
        Assert.Contains(AnomalyKind.FailoverStorm, kinds);
        Assert.AreEqual(AnomalySeverity.Critical, found[0].Severity, "Results are ordered by severity.");
    }

    [TestMethod]
    public void Detects_queue_starvation_and_suspicious_reports()
    {
        var w = Worker("w");
        var found = AnomalyDetectionService.Analyze(
            Input(workers: [w], pending: new Dictionary<string, int> { ["video.encode"] = 4, ["demo"] = 3 }, rejected: new Dictionary<string, int> { [w.WorkerId.ToString()] = 60 }),
            new AnomalyOptions(), false);

        Assert.IsTrue(found.Any(f => f.Kind == AnomalyKind.QueueStarvation && f.Subject == "jobType:video.encode"));
        Assert.IsFalse(found.Any(f => f.Kind == AnomalyKind.QueueStarvation && f.Subject == "jobType:demo"));
        Assert.IsTrue(found.Any(f => f.Kind == AnomalyKind.SuspiciousWorkerActivity && f.Severity == AnomalySeverity.Critical));
    }

    // ------------------------------------------------------------------------------------------------ alerting

    [TestMethod]
    public async Task Alerts_respect_severity_filter_and_cooldown()
    {
        using var host = new TestHost();
        var alerts = new AlertService(host.Get<AlertDispatchService>(), host.Get<JobDistMetrics>(),
            Options.Create(new AnomalyOptions { AlertCooldownMinutes = 30 }), host.Time, NullLogger<AlertService>.Instance);
        var critical = new AnomalyDTO(AnomalyKind.FailoverStorm, AnomalySeverity.Critical, "cluster", "storm", 30, 20, 30, TestHost.Start);
        var info = new AnomalyDTO(AnomalyKind.SlowExecution, AnomalySeverity.Info, "jobType:x", "slow", 1, 1, 1, TestHost.Start);
        var report = new AnomalyReportDTO(TestHost.Start.AddMinutes(-15), TestHost.Start, [critical, info]);

        Assert.HasCount(1, await alerts.NotifyAsync(report, false, CancellationToken.None));
        Assert.IsEmpty(await alerts.NotifyAsync(report, false, CancellationToken.None), "Cooldown suppresses repeats.");
        host.Time.Advance(TimeSpan.FromMinutes(31));
        var later = report with { FromUtc = report.FromUtc.AddMinutes(31), ToUtc = report.ToUtc.AddMinutes(31) };
        Assert.HasCount(1, await alerts.NotifyAsync(later, false, CancellationToken.None));

        // Re-publishing the same scan (e.g. from a second instance) is deduplicated by the outbox event key.
        var other = new AlertService(host.Get<AlertDispatchService>(), host.Get<JobDistMetrics>(),
            Options.Create(new AnomalyOptions { AlertCooldownMinutes = 30 }), host.Time, NullLogger<AlertService>.Instance);
        Assert.HasCount(1, await other.NotifyAsync(later, false, CancellationToken.None));

        // Each sent alert is queued in the outbox (no recipient configured, so it is a Skipped history row).
        var (items, _) = await host.Get<JobDist.Core.DAO.IAlertOutboxDAO>().QueryAsync(
            new AlertOutboxQueryDTO(TestHost.Start.AddDays(-1), TestHost.Start.AddDays(1), AlertEventType.AnomalyDetected, null, null, 0, 50));
        Assert.HasCount(2, items);
        Assert.IsTrue(items.All(i => i.Title.Contains("Critical", StringComparison.Ordinal)));
    }

    // ------------------------------------------------------------------------------------------------ risk

    [TestMethod]
    public async Task Submission_risk_is_high_without_capable_worker_and_low_with_one()
    {
        using var host = new TestHost();
        var risk = host.Get<JobRiskService>();

        var before = await risk.AssessSubmissionAsync("demo", null, false, null, false, CancellationToken.None);
        await host.RegisterAsync("w1");
        await host.RegisterAsync("w2");
        var after = await risk.AssessSubmissionAsync("demo", null, false, null, false, CancellationToken.None);

        Assert.IsTrue(before.Factors.Any(f => f.Factor == "no_capable_worker"));
        Assert.IsLessThan(before.Score, after.Score);
    }
}
