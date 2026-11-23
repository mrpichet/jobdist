using JobDist.Contracts;
using JobDist.Core.Domain;
using JobDist.Core.Services;
using JobDist.Server.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace JobDist.Server.Controllers;

/// <summary>
/// Worker protocol (implemented for other projects by <c>JobDist.Worker.Sdk</c>): register, heartbeat, long-poll for jobs,
/// report start / completion, unregister. Every call after registration carries <c>X-Worker-Token</c>.
/// </summary>
[ApiController]
[Route("api/workers")]
[Produces("application/json")]
public sealed class WorkersController(WorkerRegistryService registry, DispatchService dispatch, JobExecutionService execution, JobRiskService risk) : ControllerBase
{
    /// <summary>Registers (or re-registers) a worker and returns its secret token and the server's signing keys.</summary>
    /// <param name="request">Name, job types with concurrency, machine spec, encryption public keys.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("register")]
    [Authorize(Policy = Policies.Worker)]
    [EnableRateLimiting("register")]
    public async Task<ActionResult<RegisterWorkerResponse>> Register([FromBody] RegisterWorkerRequest request, CancellationToken ct) =>
        Ok(await registry.RegisterAsync(request, User.Identity!.Name!, ct));

    /// <summary>Unregisters a worker (graceful shutdown); jobs it holds are re-distributed immediately.</summary>
    [HttpDelete("{workerId:guid}")]
    [Authorize(Roles = Roles.Worker + "," + Roles.Admin)]
    [ServiceFilter<WorkerTokenFilter>]
    public async Task<IActionResult> Unregister(Guid workerId, CancellationToken ct)
    {
        await registry.UnregisterAsync(workerId, ct);
        return NoContent();
    }

    /// <summary>Heartbeat with load figures; returns pending signed commands (cancel / drain).</summary>
    [HttpPost("{workerId:guid}/heartbeat")]
    [Authorize(Policy = Policies.Worker)]
    [ServiceFilter<WorkerTokenFilter>]
    public async Task<ActionResult<HeartbeatResponse>> Heartbeat(Guid workerId, [FromBody] HeartbeatRequest request, CancellationToken ct) =>
        Ok(await registry.HeartbeatAsync(workerId, request, ct));

    /// <summary>Long-poll for signed job assignments (returns early when work arrives).</summary>
    [HttpPost("{workerId:guid}/poll")]
    [Authorize(Policy = Policies.Worker)]
    [ServiceFilter<WorkerTokenFilter>]
    public async Task<ActionResult<PollResponse>> Poll(Guid workerId, [FromBody] PollRequest request, CancellationToken ct) =>
        Ok(await dispatch.PollAsync(workerId, request, ct));

    /// <summary>Reports that an attempt started. Response <c>cancelRequested=true</c> means stop immediately.</summary>
    [HttpPost("{workerId:guid}/jobs/{jobId:guid}/started")]
    [Authorize(Policy = Policies.Worker)]
    [ServiceFilter<WorkerTokenFilter>]
    public async Task<IActionResult> Started(Guid workerId, Guid jobId, [FromBody] JobStartedRequest request, CancellationToken ct) =>
        Ok(new { cancelRequested = await execution.StartedAsync(workerId, jobId, request.AttemptId, ct) });

    /// <summary>Reports the end of an attempt; 409 when the job was already re-distributed (the worker drops the result).</summary>
    [HttpPost("{workerId:guid}/jobs/{jobId:guid}/completed")]
    [Authorize(Policy = Policies.Worker)]
    [ServiceFilter<WorkerTokenFilter>]
    [RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<ActionResult<JobStatusResponse>> Completed(Guid workerId, Guid jobId, [FromBody] JobCompletedRequest request, CancellationToken ct) =>
        Ok(await execution.CompletedAsync(workerId, jobId, request, ct));

    // ------------------------------------------------------------------------------------------ operator endpoints

    /// <summary>Lists workers.</summary>
    [HttpGet]
    [Authorize(Policy = Policies.Read)]
    public async Task<ActionResult<IReadOnlyList<WorkerResponse>>> List([FromQuery] bool includeUnregistered = false, CancellationToken ct = default) =>
        Ok(await registry.ListAsync(includeUnregistered, ct));

    /// <summary>Gets a worker.</summary>
    [HttpGet("{workerId:guid}")]
    [Authorize(Policy = Policies.Read)]
    public async Task<ActionResult<WorkerResponse>> Get(Guid workerId, CancellationToken ct) => Ok(await registry.GetAsync(workerId, ct));

    /// <summary>Drains a worker (no new jobs, current ones finish).</summary>
    [HttpPost("{workerId:guid}/drain")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Drain(Guid workerId, CancellationToken ct)
    {
        await registry.DrainAsync(workerId, ct);
        return Accepted();
    }

    /// <summary>Health risk of a worker.</summary>
    [HttpGet("{workerId:guid}/risk")]
    [Authorize(Policy = Policies.Read)]
    public async Task<ActionResult<RiskAssessmentDTO>> Risk(Guid workerId, CancellationToken ct) => Ok(await risk.AssessWorkerAsync(workerId, ct));
}
