using System.Text;
using JobDist.Contracts;
using JobDist.Contracts.Security;
using JobDist.Core.DAO;
using JobDist.Core.Domain;
using JobDist.Core.Observability;
using JobDist.Core.Security;
using Microsoft.Extensions.Logging;

namespace JobDist.Core.Services;

/// <summary>
/// Delivers assigned jobs to workers through long-polling. Each assignment is signed (ML-DSA-65 + ECDSA) and sensitive payloads
/// are re-encrypted for the receiving worker only (ML-KEM-768 + ECDH envelope, attempt id as associated data).
/// </summary>
public sealed class DispatchService(
    IJobDAO jobs,
    IWorkerDAO workers,
    WorkerRegistryService registry,
    PqcSignatureService signer,
    PayloadProtectionService protector,
    WorkerSignalService signals,
    JobDistMetrics metrics,
    TimeProvider time,
    ILogger<DispatchService> logger)
{
    /// <summary>How often a waiting poll re-checks the database (assignments made by another instance).</summary>
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Long-poll: returns as soon as jobs or commands are available, or after <c>WaitSeconds</c> with an empty result.
    /// </summary>
    /// <param name="workerId">Authenticated worker.</param>
    /// <param name="request">Max jobs and wait time.</param>
    /// <param name="ct">Request aborted token.</param>
    /// <returns>Signed assignments and commands.</returns>
    public async Task<PollResponse> PollAsync(Guid workerId, PollRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var max = Math.Clamp(request.MaxJobs, 1, 256);
        var deadline = time.GetUtcNow().AddSeconds(Math.Clamp(request.WaitSeconds, 0, 30));

        // Step 1: the worker must exist and be allowed to receive work.
        var worker = await workers.GetAsync(workerId, ct).ConfigureAwait(false);
        if (worker is null || worker.Status == WorkerStatus.Unregistered)
        {
            throw new NotFoundException("Worker is not registered.");
        }

        while (true)
        {
            // Step 2: commands first (cancellations must not wait behind new work), then undelivered assignments.
            var cmds = await registry.TakeCommandsAsync(workerId, ct).ConfigureAwait(false);
            var taken = await jobs.TakeUndeliveredAsync(workerId, max, time.GetUtcNow(), ct).ConfigureAwait(false);
            if (cmds.Count > 0 || taken.Count > 0)
            {
                var assignments = new List<JobAssignmentResponse>(taken.Count);
                foreach (var job in taken)
                {
                    assignments.Add(BuildAssignment(job, worker));
                }
                return new PollResponse(assignments, cmds);
            }

            // Step 3: nothing yet - wait for a signal from the scheduler or re-check shortly; give up at the deadline.
            var remaining = deadline - time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return new PollResponse([], []);
            }
            await signals.WaitAsync(workerId, remaining < RecheckInterval ? remaining : RecheckInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Builds and signs one assignment, encrypting the payload for the worker if the job is sensitive.</summary>
    internal JobAssignmentResponse BuildAssignment(JobDTO job, WorkerDTO worker)
    {
        // Step 1: resolve the plaintext payload (decrypt at-rest protection for sensitive jobs).
        string? plain = job.Sensitive && job.ProtectedPayload is not null ? protector.Unprotect(job.JobId, job.ProtectedPayload) : job.Payload;
        var attemptId = job.CurrentAttemptId ?? throw new InvalidOperationException("Assigned job without attempt id.");

        // Step 2: sensitive payload → PQC envelope bound to the attempt id; never sent in clear text.
        EncryptedPayload? envelope = null;
        if (job.Sensitive)
        {
            if (string.IsNullOrEmpty(worker.EcdhPublicKey))
            {
                throw new InvalidOperationException("Sensitive job assigned to a worker without an encryption key.");
            }
            envelope = PqcEnvelope.Seal(worker.EcdhPublicKey, worker.MlKemPublicKey, Encoding.UTF8.GetBytes(plain ?? string.Empty), attemptId.ToByteArray());
            plain = null;
        }

        // Step 3: sign the canonical digest (job, attempt, worker, type, timeout, time, payload hash).
        var unsigned = new JobAssignmentResponse(job.JobId, attemptId, worker.WorkerId, job.JobType, job.Attempt, job.Priority, job.TimeoutSeconds,
            time.GetUtcNow(), plain, envelope, job.PayloadSha256, PqcSignatureService.Unsigned);
        var signed = signer.Sign(unsigned);
        metrics.SignatureCreated(signed.Signature.Algorithm);
        logger.LogDebug("Delivering job {JobId} attempt {Attempt} to worker {Worker}.", job.JobId, job.Attempt, worker.Name);
        return signed;
    }
}
