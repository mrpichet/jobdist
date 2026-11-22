using System.Text;
using JobDist.Contracts;
using JobDist.Contracts.Security;
using JobDist.Core.Services;

namespace JobDist.Tests;

/// <summary>End-to-end business flows on the in-memory store: submit → schedule → poll → start → complete.</summary>
[TestClass]
public sealed class JobLifecycleTests
{
    [TestMethod]
    public async Task Job_runs_to_success_and_records_history()
    {
        using var host = new TestHost();
        var worker = await host.RegisterAsync("w1");
        var job = await host.SubmitAsync(payload: "{\"x\":1}");

        Assert.AreEqual(1, await host.ScheduleAsync());
        var poll = await host.PollAsync(worker.WorkerId);
        var a = poll.Assignments.Single();
        Assert.AreEqual(job.JobId, a.JobId);
        Assert.AreEqual("{\"x\":1}", a.Payload);
        Assert.IsFalse(await host.Execution.StartedAsync(worker.WorkerId, a.JobId, a.AttemptId, CancellationToken.None));

        var done = await host.CompleteAsync(a, AttemptOutcome.Succeeded, 250, result: "ok");

        Assert.AreEqual(JobStatus.Succeeded, done.Status);
        Assert.AreEqual("ok", done.Result);
        var history = await host.Jobs.GetAttemptsAsync(job.JobId, CancellationToken.None);
        Assert.HasCount(1, history);
        Assert.AreEqual(worker.WorkerId, history[0].WorkerId);
    }

    [TestMethod]
    public async Task Assignment_signature_verifies_with_published_keys_and_detects_tampering()
    {
        using var host = new TestHost();
        var worker = await host.RegisterAsync("w1");
        await host.SubmitAsync();
        await host.ScheduleAsync();
        var a = (await host.PollAsync(worker.WorkerId)).Assignments.Single();

        using var verifier = new HybridSignatureVerifier(worker.ServerKeys);
        Assert.IsTrue(verifier.Verify(CanonicalDigest.ForAssignment(a), a.Signature));

        // Replaying the assignment to another worker or changing the payload hash breaks the signature.
        Assert.IsFalse(verifier.Verify(CanonicalDigest.ForAssignment(a with { WorkerId = Guid.NewGuid() }), a.Signature));
        Assert.IsFalse(verifier.Verify(CanonicalDigest.ForAssignment(a with { PayloadSha256 = CanonicalDigest.PayloadHash("evil") }), a.Signature));
        Assert.IsFalse(verifier.Verify(CanonicalDigest.ForAssignment(a), a.Signature with { KeyId = "other" }));
    }

    [TestMethod]
    public async Task Sensitive_payload_is_encrypted_for_the_worker_and_bound_to_the_attempt()
    {
        using var host = new TestHost();
        using var keys = HybridKeyPair.Generate();
        var worker = await host.RegisterAsync("w1", keys: keys);
        await host.SubmitAsync(payload: "{\"secret\":\"s3cr3t\"}", sensitive: true);
        await host.ScheduleAsync();
        var a = (await host.PollAsync(worker.WorkerId)).Assignments.Single();

        Assert.IsNull(a.Payload, "Sensitive payload must never travel in clear text.");
        Assert.IsNotNull(a.EncryptedPayload);
        var plain = Encoding.UTF8.GetString(PqcEnvelope.Open(keys, a.EncryptedPayload, a.AttemptId.ToByteArray()));
        Assert.AreEqual("{\"secret\":\"s3cr3t\"}", plain);
        Assert.AreEqual(a.PayloadSha256, CanonicalDigest.PayloadHash(plain), ignoreCase: true);

        // Associated data = attempt id: the ciphertext cannot be re-used for another attempt.
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => PqcEnvelope.Open(keys, a.EncryptedPayload, Guid.NewGuid().ToByteArray()));
    }

    [TestMethod]
    public async Task Sensitive_job_is_not_given_to_a_worker_without_encryption_key()
    {
        using var host = new TestHost();
        await host.RegisterAsync("no-keys");
        await host.SubmitAsync(sensitive: true);

        Assert.AreEqual(0, await host.ScheduleAsync());
    }

    [TestMethod]
    public async Task Higher_priority_runs_first_when_capacity_is_short()
    {
        using var host = new TestHost();
        var worker = await host.RegisterAsync("w1", maxTotal: 1, caps: ("demo", 1));
        var low = await host.SubmitAsync(priority: 10);
        var high = await host.SubmitAsync(priority: 90);

        await host.ScheduleAsync();
        var a = (await host.PollAsync(worker.WorkerId)).Assignments.Single();

        Assert.AreEqual(high.JobId, a.JobId);
        Assert.AreEqual(JobStatus.Pending, (await host.Jobs.GetAsync(low.JobId, CancellationToken.None)).Status);
    }

    [TestMethod]
    public async Task Changing_priority_reorders_the_queue()
    {
        using var host = new TestHost();
        var worker = await host.RegisterAsync("w1", maxTotal: 1, caps: ("demo", 1));
        var first = await host.SubmitAsync(priority: 50);
        var second = await host.SubmitAsync(priority: 40);

        var changed = await host.Jobs.ChangePriorityAsync(second.JobId, 100, CancellationToken.None);
        await host.ScheduleAsync();

        Assert.AreEqual(100, changed.Priority);
        Assert.AreEqual(second.JobId, (await host.PollAsync(worker.WorkerId)).Assignments.Single().JobId);
        Assert.AreEqual(JobStatus.Pending, (await host.Jobs.GetAsync(first.JobId, CancellationToken.None)).Status);
    }

    [TestMethod]
    public async Task Strict_target_only_runs_on_that_worker()
    {
        using var host = new TestHost();
        var a = await host.RegisterAsync("a");
        var b = await host.RegisterAsync("b");
        var job = await host.SubmitAsync(target: b.WorkerId, strict: true);

        await host.ScheduleAsync();

        Assert.IsEmpty((await host.PollAsync(a.WorkerId)).Assignments);
        Assert.AreEqual(job.JobId, (await host.PollAsync(b.WorkerId)).Assignments.Single().JobId);
    }

    [TestMethod]
    public async Task Job_waits_when_no_worker_has_the_capability()
    {
        using var host = new TestHost();
        await host.RegisterAsync("w1", caps: ("image.resize", 2));
        var job = await host.SubmitAsync(type: "video.encode");

        Assert.AreEqual(0, await host.ScheduleAsync());
        Assert.AreEqual(JobStatus.Pending, (await host.Jobs.GetAsync(job.JobId, CancellationToken.None)).Status);
    }

    [TestMethod]
    public async Task Per_type_concurrency_limit_is_respected()
    {
        using var host = new TestHost();
        var worker = await host.RegisterAsync("w1", maxTotal: 10, caps: ("demo", 2));
        for (var i = 0; i < 5; i++)
        {
            await host.SubmitAsync();
        }

        Assert.AreEqual(2, await host.ScheduleAsync());
        Assert.HasCount(2, (await host.PollAsync(worker.WorkerId)).Assignments);
    }

    [TestMethod]
    public async Task Idempotency_key_prevents_duplicates()
    {
        using var host = new TestHost();
        var first = await host.SubmitAsync(idempotencyKey: "order-42");
        var again = await host.SubmitAsync(idempotencyKey: "order-42");

        Assert.IsFalse(first.Duplicate);
        Assert.IsTrue(again.Duplicate);
        Assert.AreEqual(first.JobId, again.JobId);
    }

    [TestMethod]
    public async Task Invalid_job_type_is_rejected()
    {
        using var host = new TestHost();
        await Assert.ThrowsExactlyAsync<ValidationException>(() => host.SubmitAsync(type: "bad type!"));
    }

    [TestMethod]
    public async Task Retryable_failure_is_retried_after_backoff()
    {
        using var host = new TestHost();
        var worker = await host.RegisterAsync("w1");
        var job = await host.SubmitAsync(maxAttempts: 3);
        var a = await host.DeliverAsync(worker.WorkerId, job.JobId);

        var afterFail = await host.CompleteAsync(a, AttemptOutcome.Failed, retryable: true);
        Assert.AreEqual(JobStatus.Pending, afterFail.Status);

        // Not ready during back-off (base 2 s ± 20 %).
        Assert.AreEqual(0, await host.ScheduleAsync());
        host.Time.Advance(TimeSpan.FromSeconds(3));
        Assert.AreEqual(1, await host.ScheduleAsync());
        var retry = (await host.PollAsync(worker.WorkerId)).Assignments.Single();
        Assert.AreEqual(2, retry.Attempt);
    }

    [TestMethod]
    public async Task Non_retryable_failure_fails_immediately()
    {
        using var host = new TestHost();
        var worker = await host.RegisterAsync("w1");
        var job = await host.SubmitAsync(maxAttempts: 5);
        var a = await host.DeliverAsync(worker.WorkerId, job.JobId);

        var result = await host.CompleteAsync(a, AttemptOutcome.Failed, retryable: false);

        Assert.AreEqual(JobStatus.Failed, result.Status);
    }

    [TestMethod]
    public async Task Job_fails_when_attempts_are_exhausted()
    {
        using var host = new TestHost();
        var worker = await host.RegisterAsync("w1");
        var job = await host.SubmitAsync(maxAttempts: 2);

        var a1 = await host.DeliverAsync(worker.WorkerId, job.JobId);
        await host.CompleteAsync(a1, AttemptOutcome.TimedOut);
        host.Time.Advance(TimeSpan.FromMinutes(1));
        var a2 = await host.DeliverAsync(worker.WorkerId, job.JobId);
        var final = await host.CompleteAsync(a2, AttemptOutcome.Failed);

        Assert.AreEqual(JobStatus.Failed, final.Status);
        Assert.AreEqual(2, final.Attempt);
    }

    [TestMethod]
    public async Task Cancel_of_running_job_sends_signed_command_to_worker()
    {
        using var host = new TestHost();
        var worker = await host.RegisterAsync("w1");
        var job = await host.SubmitAsync();
        var a = await host.DeliverAsync(worker.WorkerId, job.JobId);

        var cancelled = await host.Jobs.CancelAsync(job.JobId, "user request", CancellationToken.None);
        Assert.AreEqual(JobStatus.CancelRequested, cancelled.Status);

        var hb = await host.Registry.HeartbeatAsync(worker.WorkerId, new HeartbeatRequest([job.JobId]), CancellationToken.None);
        var command = hb.Commands.Single();
        Assert.AreEqual(WorkerCommandKind.CancelJob, command.Kind);
        Assert.AreEqual(job.JobId, command.JobId);
        using var verifier = new HybridSignatureVerifier(worker.ServerKeys);
        Assert.IsTrue(verifier.Verify(CanonicalDigest.ForCommand(command), command.Signature));

        var final = await host.CompleteAsync(a, AttemptOutcome.Cancelled);
        Assert.AreEqual(JobStatus.Cancelled, final.Status);
    }

    [TestMethod]
    public async Task Cancel_of_pending_job_is_immediate_and_final_jobs_cannot_be_changed()
    {
        using var host = new TestHost();
        var job = await host.SubmitAsync();

        var cancelled = await host.Jobs.CancelAsync(job.JobId, null, CancellationToken.None);

        Assert.AreEqual(JobStatus.Cancelled, cancelled.Status);
        await Assert.ThrowsExactlyAsync<ConflictException>(() => host.Jobs.ChangePriorityAsync(job.JobId, 99, CancellationToken.None));
    }

    [TestMethod]
    public async Task Unknown_job_returns_not_found()
    {
        using var host = new TestHost();
        await Assert.ThrowsExactlyAsync<NotFoundException>(() => host.Jobs.GetAsync(Guid.NewGuid(), CancellationToken.None));
    }
}
