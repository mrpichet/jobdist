using JobDist.Contracts;
using JobDist.Core.Domain;
using JobDist.Core.Security;
using JobDist.Core.Services;
using JobDist.Server.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JobDist.Server.Controllers;

/// <summary>Cluster overview, ML statistics, worker recommendations and anomaly detection.</summary>
[ApiController]
[Route("api/admin")]
[Authorize(Policy = Policies.Read)]
[Produces("application/json")]
public sealed class AdminController(ClusterService cluster, SchedulerService scheduler, AnomalyDetectionService anomalies, AlertService alerts) : ControllerBase
{
    /// <summary>Cluster summary (workers, capacity, jobs by status, pending per type, HA leader).</summary>
    [HttpGet("summary")]
    public async Task<ActionResult<ClusterSummaryDTO>> Summary(CancellationToken ct) => Ok(await cluster.GetSummaryAsync(ct));

    /// <summary>What the ML model learned (per worker and job type).</summary>
    [HttpGet("performance")]
    public ActionResult<IReadOnlyList<PerformanceStatDTO>> Performance([FromQuery] string? jobType) => Ok(cluster.GetPerformance(jobType));

    /// <summary>Workers ranked for a job type with explanations.</summary>
    [HttpGet("recommendations")]
    public async Task<ActionResult<IReadOnlyList<WorkerRecommendationDTO>>> Recommend([FromQuery] string jobType, [FromQuery] int top = 5, CancellationToken ct = default) =>
        Ok(await scheduler.RecommendAsync(jobType, top, ct));

    /// <summary>Runs anomaly detection now.</summary>
    [HttpGet("anomalies")]
    public async Task<ActionResult<AnomalyReportDTO>> Anomalies([FromQuery] int windowMinutes = 15, CancellationToken ct = default) =>
        Ok(await anomalies.ScanAsync(windowMinutes, ct));

    /// <summary>Runs anomaly detection and sends an alert to admins through MessageHub immediately (ignores cooldown).</summary>
    [HttpPost("anomalies/alert")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<IReadOnlyList<AnomalyDTO>>> Alert([FromQuery] int windowMinutes = 15, CancellationToken ct = default)
    {
        var report = await anomalies.ScanAsync(windowMinutes, ct);
        return Ok(await alerts.NotifyAsync(report, force: true, ct));
    }
}

/// <summary>Public signing keys (workers verify assignments / commands with them).</summary>
[ApiController]
[Route("api/keys")]
[AllowAnonymous]
public sealed class KeysController(PqcSignatureService signer) : ControllerBase
{
    /// <summary>ML-DSA-65 + ECDSA-P384 public keys.</summary>
    [HttpGet]
    public ActionResult<ServerKeysResponse> Get() => Ok(signer.PublicKeys);
}
