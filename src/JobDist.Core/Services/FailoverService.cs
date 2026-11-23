using JobDist.Contracts;
using JobDist.Core.DAO;
using JobDist.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobDist.Core.Services;

/// <summary>
/// Detects lost workers and stuck jobs and re-distributes their work (workers run on unstable networks):
/// <list type="bullet">
/// <item>Worker heartbeat older than <c>OfflineAfterSeconds</c> → worker Offline (OfflineCount++), every job it held is released
/// as <c>WorkerLost</c> and goes back to the queue for another worker (subject to MaxAttempts).</item>
/// <item>Assigned but never started before the start timeout (delivery lost) → released as <c>WorkerLost</c>.</item>
/// <item>Running past timeout + grace → <c>TimedOut</c> (retry with back-off).</item>
/// <item>Cancel requested but the worker never acknowledged → Cancelled.</item>
/// </list>
/// Runs on the leader only.
/// </summary>
public sealed class FailoverService(
    IWorkerDAO workers,
    IJobDAO jobs,
    JobExecutionService execution,
    IOptions<JobDistOptions> options,
    TimeProvider time,
    ILogger<FailoverService> logger)
{
    /// <summary>
    /// Runs one detection pass.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>(workers marked offline, jobs released).</returns>
    public async Task<(int WorkersLost, int JobsReleased)> RunOnceAsync(CancellationToken ct)
    {
        var o = options.Value;
        var now = time.GetUtcNow();
        int lost = 0, released = 0;

        // Step 1: workers that stopped sending heartbeats.
        var stale = await workers.GetStaleAsync(now.AddSeconds(-o.OfflineAfterSeconds), ct).ConfigureAwait(false);
        foreach (var worker in stale)
        {
            // Conditional transition: only the instance that flips the status releases the jobs.
            if (!await workers.SetStatusAsync(worker.WorkerId, WorkerStatus.Offline, true, worker.Status, ct).ConfigureAwait(false))
            {
                continue;
            }
            lost++;
            var held = await jobs.GetHeldByWorkerAsync(worker.WorkerId, ct).ConfigureAwait(false);
            foreach (var job in held)
            {
                if (await TryReleaseAsync(job, job.Status == Contracts.JobStatus.CancelRequested ? AttemptOutcome.Cancelled : AttemptOutcome.WorkerLost,
                        $"Worker {worker.Name} lost (no heartbeat for {o.OfflineAfterSeconds}s)", ct).ConfigureAwait(false))
                {
                    released++;
                }
            }
            logger.LogWarning("Worker {Name} ({WorkerId}) is offline; {Count} jobs re-distributed.", worker.Name, worker.WorkerId, held.Count);
        }

        // Step 2: jobs whose lease expired although the worker still heartbeats (lost delivery, hung handler).
        var expired = await jobs.GetExpiredLeasesAsync(now, 1_000, ct).ConfigureAwait(false);
        foreach (var job in expired)
        {
            var outcome = job.Status switch
            {
                Contracts.JobStatus.Running => AttemptOutcome.TimedOut,
                Contracts.JobStatus.CancelRequested => AttemptOutcome.Cancelled,
                _ => AttemptOutcome.WorkerLost
            };
            if (await TryReleaseAsync(job, outcome, outcome == AttemptOutcome.TimedOut ? "Lease expired (timeout)" : "Assignment not started in time", ct).ConfigureAwait(false))
            {
                released++;
            }
        }
        return (lost, released);
    }

    /// <summary>Releases a job, ignoring races where the worker reported just in time.</summary>
    private async Task<bool> TryReleaseAsync(Domain.JobDTO job, AttemptOutcome outcome, string reason, CancellationToken ct)
    {
        try
        {
            await execution.ReleaseAsync(job, outcome, reason, ct).ConfigureAwait(false);
            return true;
        }
        catch (ConflictException)
        {
            return false;
        }
    }
}
