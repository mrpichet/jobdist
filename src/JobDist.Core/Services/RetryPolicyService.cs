using JobDist.Contracts;
using JobDist.Core.Domain;
using JobDist.Core.Options;
using Microsoft.Extensions.Options;

namespace JobDist.Core.Services;

/// <summary>What happens to a job after an attempt ended.</summary>
/// <param name="FinalStatus">Status to store (Pending means "retry").</param>
/// <param name="NextAttemptUtc">Earliest time of the retry (only when retrying).</param>
/// <param name="CountsAsAttempt">Whether the ended attempt consumes one of <c>MaxAttempts</c>.</param>
/// <param name="Reason">Short machine-readable reason for metrics / logs.</param>
public sealed record RetryDecision(JobStatus FinalStatus, DateTimeOffset? NextAttemptUtc, bool CountsAsAttempt, string Reason)
{
    /// <summary><c>true</c> when the job goes back to the queue.</summary>
    public bool IsRetry => FinalStatus == JobStatus.Pending;
}

/// <summary>
/// Pure retry policy: decides between success, final failure, cancellation and a retry with exponential back-off + jitter.
/// <list type="bullet">
/// <item>Worker lost / released → re-distributed immediately (the job did nothing wrong, another worker should take it).</item>
/// <item>Retryable failure or timeout → back-off <c>base * 2^(attempt-1)</c> capped at <c>RetryMaxDelaySeconds</c>, ±20% jitter
/// (spreads retries so a failing dependency is not hammered by synchronised retries).</item>
/// <item>Non-retryable failure or attempts exhausted → Failed.</item>
/// </list>
/// </summary>
public sealed class RetryPolicyService(IOptions<JobDistOptions> options)
{
    /// <summary>
    /// Decides the next state of a job.
    /// </summary>
    /// <param name="job">Job as stored (Attempt already counts the attempt that just ended).</param>
    /// <param name="outcome">Outcome of the attempt.</param>
    /// <param name="retryable">Worker's hint that the failure is transient.</param>
    /// <param name="now">Current time.</param>
    /// <param name="random">Random source for jitter (injectable for deterministic tests).</param>
    /// <returns>Decision.</returns>
    public RetryDecision Decide(JobDTO job, AttemptOutcome outcome, bool retryable, DateTimeOffset now, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        var o = options.Value;

        // Step 1: terminal outcomes that never retry.
        if (outcome == AttemptOutcome.Succeeded)
        {
            return new RetryDecision(JobStatus.Succeeded, null, true, "succeeded");
        }
        if (outcome == AttemptOutcome.Cancelled || job.Status == JobStatus.CancelRequested)
        {
            return new RetryDecision(JobStatus.Cancelled, null, true, "cancelled");
        }

        // Step 2: infrastructure failures (worker lost / released) - redistribute immediately.
        if (outcome is AttemptOutcome.WorkerLost or AttemptOutcome.Released)
        {
            var counts = outcome == AttemptOutcome.WorkerLost && o.CountWorkerLossAsAttempt;
            var used = counts ? job.Attempt : job.Attempt - 1;
            return used >= job.MaxAttempts
                ? new RetryDecision(JobStatus.Failed, null, counts, "attempts_exhausted")
                : new RetryDecision(JobStatus.Pending, now, counts, outcome == AttemptOutcome.WorkerLost ? "worker_lost" : "released");
        }

        // Step 3: business failure the worker marked as permanent.
        if (outcome == AttemptOutcome.Failed && !retryable)
        {
            return new RetryDecision(JobStatus.Failed, null, true, "non_retryable");
        }

        // Step 4: transient failure / timeout - retry while attempts remain.
        if (job.Attempt >= job.MaxAttempts)
        {
            return new RetryDecision(JobStatus.Failed, null, true, "attempts_exhausted");
        }
        return new RetryDecision(JobStatus.Pending, now + Backoff(job.Attempt, random), true, outcome == AttemptOutcome.TimedOut ? "timeout" : "failed");
    }

    /// <summary>
    /// Exponential back-off with ±20% jitter.
    /// </summary>
    /// <param name="attempt">Attempts made so far (1 = first attempt failed).</param>
    /// <param name="random">Random source.</param>
    /// <returns>Delay before the next attempt.</returns>
    public TimeSpan Backoff(int attempt, Random? random = null)
    {
        var o = options.Value;
        var exponent = Math.Clamp(attempt - 1, 0, 30);
        var seconds = Math.Min(o.RetryMaxDelaySeconds, o.RetryBaseDelaySeconds * Math.Pow(2, exponent));
        var jitter = 0.8 + (random ?? Random.Shared).NextDouble() * 0.4;
        return TimeSpan.FromSeconds(Math.Min(o.RetryMaxDelaySeconds, seconds * jitter));
    }
}
