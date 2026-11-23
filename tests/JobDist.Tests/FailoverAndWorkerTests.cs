using JobDist.Contracts;
using JobDist.Core.Security;
using JobDist.Core.Services;

namespace JobDist.Tests;

/// <summary>Worker registration, authentication, network loss and re-distribution.</summary>
[TestClass]
public sealed class FailoverAndWorkerTests
{
    [TestMethod]
    public async Task Lost_worker_jobs_are_redistributed_to_another_worker()
    {
        using var host = new TestHost();
        var w1 = await host.RegisterAsync("w1");
        var job = await host.SubmitAsync();
        var a = await host.DeliverAsync(w1.WorkerId, job.JobId);

        // w2 joins later and keeps sending heartbeats; w1 goes silent (network loss).
        host.Time.Advance(TimeSpan.FromSeconds(15));
        var w2 = await host.RegisterAsync("w2");
        host.Time.Advance(TimeSpan.FromSeconds(10));
        await host.Registry.HeartbeatAsync(w2.WorkerId, new HeartbeatRequest(), CancellationToken.None);

        var (lost, released) = await host.Failover.RunOnceAsync(CancellationToken.None);
        Assert.AreEqual(1, lost);
        Assert.AreEqual(1, released);
        Assert.AreEqual(WorkerStatus.Offline, (await host.Registry.GetAsync(w1.WorkerId, CancellationToken.None)).Status);

        await host.ScheduleAsync();
        var moved = (await host.PollAsync(w2.WorkerId)).Assignments.Single();
        Assert.AreEqual(job.JobId, moved.JobId);
        Assert.AreEqual(2, moved.Attempt);

        // The late report of the lost worker is rejected as stale (no double completion).
        await Assert.ThrowsExactlyAsync<ConflictException>(() => host.CompleteAsync(a, AttemptOutcome.Succeeded));
    }

    [TestMethod]
    public async Task Assigned_job_never_started_is_released_after_start_timeout()
    {
        using var host = new TestHost();
        var w1 = await host.RegisterAsync("w1");
        var job = await host.SubmitAsync();
        await host.ScheduleAsync();

        // Worker never polls / starts, but keeps its heartbeat alive.
        host.Time.Advance(TimeSpan.FromSeconds(15));
        await host.Registry.HeartbeatAsync(w1.WorkerId, new HeartbeatRequest(), CancellationToken.None);
        host.Time.Advance(TimeSpan.FromSeconds(15));
        await host.Registry.HeartbeatAsync(w1.WorkerId, new HeartbeatRequest(), CancellationToken.None);
        host.Time.Advance(TimeSpan.FromSeconds(15));
        await host.Registry.HeartbeatAsync(w1.WorkerId, new HeartbeatRequest(), CancellationToken.None);
        host.Time.Advance(TimeSpan.FromSeconds(20));
        await host.Registry.HeartbeatAsync(w1.WorkerId, new HeartbeatRequest(), CancellationToken.None);

        var (_, released) = await host.Failover.RunOnceAsync(CancellationToken.None);

        Assert.AreEqual(1, released);
        Assert.AreEqual(JobStatus.Pending, (await host.Jobs.GetAsync(job.JobId, CancellationToken.None)).Status);
    }

    [TestMethod]
    public async Task Unregister_releases_held_jobs_without_consuming_an_attempt()
    {
        using var host = new TestHost();
        var w1 = await host.RegisterAsync("w1");
        var job = await host.SubmitAsync(maxAttempts: 1);
        await host.DeliverAsync(w1.WorkerId, job.JobId);

        await host.Registry.UnregisterAsync(w1.WorkerId, CancellationToken.None);

        var status = await host.Jobs.GetAsync(job.JobId, CancellationToken.None);
        Assert.AreEqual(JobStatus.Pending, status.Status, "A graceful release must not burn the only attempt.");
    }

    [TestMethod]
    public async Task Worker_token_is_required_and_only_its_hash_is_stored()
    {
        using var host = new TestHost();
        var w = await host.RegisterAsync("w1");

        await host.Registry.AuthenticateAsync(w.WorkerId, w.WorkerToken, CancellationToken.None);
        await Assert.ThrowsExactlyAsync<ForbiddenException>(() => host.Registry.AuthenticateAsync(w.WorkerId, "wrong", CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ForbiddenException>(() => host.Registry.AuthenticateAsync(Guid.NewGuid(), w.WorkerToken, CancellationToken.None));

        var stored = await host.Get<JobDist.Core.DAO.IWorkerDAO>().GetAsync(w.WorkerId, CancellationToken.None);
        CollectionAssert.AreEqual(WorkerTokenService.Hash(w.WorkerToken), stored!.TokenHash);
    }

    [TestMethod]
    public async Task Re_registering_the_same_name_keeps_the_worker_id_and_rotates_the_token()
    {
        using var host = new TestHost();
        var first = await host.RegisterAsync("node-a");
        var second = await host.RegisterAsync("node-a");

        Assert.AreEqual(first.WorkerId, second.WorkerId);
        Assert.AreNotEqual(first.WorkerToken, second.WorkerToken);
        await Assert.ThrowsExactlyAsync<ForbiddenException>(() => host.Registry.AuthenticateAsync(first.WorkerId, first.WorkerToken, CancellationToken.None));
    }

    [TestMethod]
    public async Task Draining_worker_gets_no_new_jobs()
    {
        using var host = new TestHost();
        var w = await host.RegisterAsync("w1");
        await host.Registry.DrainAsync(w.WorkerId, CancellationToken.None);
        await host.SubmitAsync();

        Assert.AreEqual(0, await host.ScheduleAsync());
        var hb = await host.Registry.HeartbeatAsync(w.WorkerId, new HeartbeatRequest(), CancellationToken.None);
        Assert.AreEqual(WorkerStatus.Draining, hb.Status);
    }

    [TestMethod]
    public void Token_service_issues_unique_tokens_and_verifies_in_constant_time()
    {
        var (token, hash) = WorkerTokenService.Issue();
        var (other, _) = WorkerTokenService.Issue();

        Assert.AreNotEqual(token, other);
        Assert.IsTrue(WorkerTokenService.Verify(token, hash));
        Assert.IsFalse(WorkerTokenService.Verify(other, hash));
        Assert.IsFalse(WorkerTokenService.Verify(null, hash));
    }
}
