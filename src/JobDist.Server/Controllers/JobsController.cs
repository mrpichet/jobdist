using JobDist.Contracts;
using JobDist.Core.Domain;
using JobDist.Core.Services;
using JobDist.Server.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace JobDist.Server.Controllers;

/// <summary>
/// Job submission and management API (referenced by other projects through <c>JobDist.Client</c>).
/// </summary>
[ApiController]
[Route("api/jobs")]
[Authorize(Policy = Policies.Read)]
[Produces("application/json")]
public sealed class JobsController(JobService jobs, JobRiskService risk) : ControllerBase
{
    /// <summary>
    /// Submits a job.
    /// </summary>
    /// <param name="request">Job type, payload, priority, optional target worker, retries, timeout, schedule time.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>201 with the job id (200 when the idempotency key matched an existing job).</returns>
    [HttpPost]
    [Authorize(Policy = Policies.Submit)]
    [EnableRateLimiting("submit")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    [ProducesResponseType<SubmitJobResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<SubmitJobResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SubmitJobResponse>> Submit([FromBody] SubmitJobRequest request, CancellationToken ct)
    {
        // Step 1: business layer (validation, idempotency, encryption of sensitive payloads).
        var result = await jobs.SubmitAsync(request, User.Identity!.Name!, ct);

        // Step 2: 201 + Location for new jobs, 200 for idempotent duplicates.
        return result.Duplicate ? Ok(result) : CreatedAtAction(nameof(Get), new { jobId = result.JobId }, result);
    }

    /// <summary>Submits up to 5,000 jobs in one request (bulk insert).</summary>
    /// <param name="request">Jobs.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>One result per job, same order.</returns>
    [HttpPost("batch")]
    [Authorize(Policy = Policies.Submit)]
    [EnableRateLimiting("submit")]
    [RequestSizeLimit(64 * 1024 * 1024)]
    public async Task<ActionResult<IReadOnlyList<SubmitJobResponse>>> SubmitBatch([FromBody] SubmitJobBatchRequest request, CancellationToken ct) =>
        Ok(await jobs.SubmitBatchAsync(request, User.Identity!.Name!, ct));

    /// <summary>Gets the status of a job.</summary>
    /// <param name="jobId">Job id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Job status.</returns>
    [HttpGet("{jobId:guid}")]
    public async Task<ActionResult<JobStatusResponse>> Get(Guid jobId, CancellationToken ct) => Ok(await jobs.GetAsync(jobId, ct));

    /// <summary>Lists jobs (newest first).</summary>
    /// <param name="status">Optional status filter.</param>
    /// <param name="jobType">Optional job type filter.</param>
    /// <param name="workerId">Optional assigned worker filter.</param>
    /// <param name="skip">Paging offset.</param>
    /// <param name="take">Page size (1-1000).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Page of jobs.</returns>
    [HttpGet]
    public async Task<ActionResult<PagedResponse<JobStatusResponse>>> List([FromQuery] JobStatus? status, [FromQuery] string? jobType, [FromQuery] Guid? workerId,
        [FromQuery] int skip = 0, [FromQuery] int take = 100, CancellationToken ct = default) =>
        Ok(await jobs.QueryAsync(new JobQueryDTO(status, jobType, workerId, skip, take), ct));

    /// <summary>Distribution history (attempts, workers, timings).</summary>
    [HttpGet("{jobId:guid}/attempts")]
    public async Task<ActionResult<IReadOnlyList<JobAttemptResponse>>> Attempts(Guid jobId, CancellationToken ct) => Ok(await jobs.GetAttemptsAsync(jobId, ct));

    /// <summary>Changes the priority of an unfinished job.</summary>
    /// <param name="jobId">Job id.</param>
    /// <param name="request">New priority 0-100.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Updated job.</returns>
    [HttpPatch("{jobId:guid}/priority")]
    [Authorize(Policy = Policies.Submit)]
    public async Task<ActionResult<JobStatusResponse>> ChangePriority(Guid jobId, [FromBody] ChangePriorityRequest request, CancellationToken ct) =>
        Ok(await jobs.ChangePriorityAsync(jobId, request.Priority, ct));

    /// <summary>
    /// Cancels a job. If a worker holds it, the server sends that worker a signed cancel command (status CancelRequested → Cancelled).
    /// </summary>
    [HttpPost("{jobId:guid}/cancel")]
    [Authorize(Policy = Policies.Submit)]
    public async Task<ActionResult<JobStatusResponse>> Cancel(Guid jobId, [FromBody] CancelJobRequest? request, CancellationToken ct) =>
        Ok(await jobs.CancelAsync(jobId, request?.Reason, ct));

    /// <summary>Risk (0-100) that the job fails / is delayed, with explained factors.</summary>
    [HttpGet("{jobId:guid}/risk")]
    public async Task<ActionResult<RiskAssessmentDTO>> Risk(Guid jobId, CancellationToken ct) => Ok(await risk.AssessJobAsync(jobId, ct));

    /// <summary>What-if risk of submitting a job type (nothing is created).</summary>
    [HttpGet("risk")]
    public async Task<ActionResult<RiskAssessmentDTO>> SubmissionRisk([FromQuery] string jobType, [FromQuery] Guid? targetWorkerId, [FromQuery] bool strictTarget = false,
        [FromQuery] int? timeoutSeconds = null, [FromQuery] bool sensitive = false, CancellationToken ct = default) =>
        Ok(await risk.AssessSubmissionAsync(jobType, targetWorkerId, strictTarget, timeoutSeconds, sensitive, ct));
}
