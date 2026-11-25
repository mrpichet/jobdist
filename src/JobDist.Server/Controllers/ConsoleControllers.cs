using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using JobDist.Contracts;
using JobDist.Core.Services;
using JobDist.Server.Console;
using JobDist.Server.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace JobDist.Server.Controllers;

/// <summary>Alert recipients (X2), channels and event types.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/alerts")]
[Route("api/alerts")]
[Produces("application/json")]
public sealed class AlertRecipientsController(AlertRecipientService recipients, ILogger<AlertRecipientsController> logger) : ControllerBase
{
    /// <summary>All recipients (addresses masked).</summary>
    [HttpGet("recipients")]
    [JobDistAuthorize(Permissions.AlertView, RoleSets.ConsoleRead)]
    public async Task<ActionResult<IReadOnlyList<AlertRecipientResponse>>> List(CancellationToken ct) => Ok(await recipients.ListAsync(ct));

    /// <summary>One recipient.</summary>
    [HttpGet("recipients/{id:guid}")]
    [JobDistAuthorize(Permissions.AlertView, RoleSets.ConsoleRead)]
    public async Task<ActionResult<AlertRecipientResponse>> Get(Guid id, CancellationToken ct) => Ok(await recipients.GetAsync(id, ct));

    /// <summary>Creates a recipient (send header <c>Idempotency-Key</c> so a retry does not create a second one).</summary>
    [HttpPost("recipients")]
    [JobDistAuthorize(Permissions.AlertManage, RoleSets.ConsoleWrite)]
    [Idempotent]
    public async Task<ActionResult<AlertRecipientResponse>> Create([FromBody] AlertRecipientRequest request, CancellationToken ct)
    {
        var created = await recipients.CreateAsync(request, User.CallerName(), ct);
        Audit("alert.recipient.create", created.RecipientId);
        return CreatedAtAction(nameof(Get), new { id = created.RecipientId }, created);
    }

    /// <summary>Updates a recipient; <c>version</c> is required (409 when someone else saved first). Empty address keeps the stored one.</summary>
    [HttpPut("recipients/{id:guid}")]
    [JobDistAuthorize(Permissions.AlertManage, RoleSets.ConsoleWrite)]
    public async Task<ActionResult<AlertRecipientResponse>> Update(Guid id, [FromBody] AlertRecipientRequest request, CancellationToken ct)
    {
        var updated = await recipients.UpdateAsync(id, request, User.CallerName(), ct);
        Audit("alert.recipient.update", id);
        return Ok(updated);
    }

    /// <summary>Deletes a recipient.</summary>
    [HttpDelete("recipients/{id:guid}")]
    [JobDistAuthorize(Permissions.AlertManage, RoleSets.ConsoleWrite)]
    public async Task<IActionResult> Delete(Guid id, [FromQuery, Required] long version, CancellationToken ct)
    {
        await recipients.DeleteAsync(id, version, User.CallerName(), ct);
        Audit("alert.recipient.delete", id);
        return NoContent();
    }

    /// <summary>Sends a test message to the recipient now.</summary>
    [HttpPost("recipients/{id:guid}/test")]
    [JobDistAuthorize(Permissions.AlertManage, RoleSets.ConsoleWrite)]
    [Idempotent]
    public async Task<ActionResult<AlertSendResultResponse>> Test(Guid id, CancellationToken ct)
    {
        Audit("alert.recipient.test", id);
        return Ok(await recipients.TestAsync(id, User.CallerName(), ct));
    }

    /// <summary>Channels enabled on MessageHub (empty when MessageHub is not configured).</summary>
    [HttpGet("channels")]
    [JobDistAuthorize(Permissions.AlertView, RoleSets.ConsoleRead)]
    public async Task<ActionResult<IReadOnlyList<string>>> Channels(CancellationToken ct) => Ok(await recipients.GetChannelsAsync(ct));

    /// <summary>Alert event types.</summary>
    [HttpGet("event-types")]
    [JobDistAuthorize(Permissions.AlertView, RoleSets.ConsoleRead)]
    public ActionResult<IReadOnlyList<string>> EventTypes() => Ok(Enum.GetNames<AlertEventType>().Where(n => n != nameof(AlertEventType.Test)).ToList());

    /// <summary>Alert history (X1), newest first.</summary>
    [HttpGet("logs")]
    [JobDistAuthorize(Permissions.AlertView, RoleSets.ConsoleRead)]
    public async Task<ActionResult<PagedResponse<AlertLogResponse>>> Logs([FromQuery] DateTimeOffset? fromUtc, [FromQuery] DateTimeOffset? toUtc,
        [FromQuery] AlertEventType? eventType, [FromQuery] AlertSendStatus? status, [FromQuery] Guid? recipientId,
        [FromQuery] int skip = 0, [FromQuery] int take = 100, CancellationToken ct = default) =>
        Ok(await recipients.QueryLogsAsync(fromUtc, toUtc, eventType, status, recipientId, skip, take, ct));

    private void Audit(string action, Guid id) =>
        logger.LogInformation("Audit {Action} {EntityId} by {UserId} from {ClientIp}.", action, id, User.CallerId(), HttpContext.Connection.RemoteIpAddress);
}

/// <summary>Queue thresholds (X6) and current queue usage.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/alerts")]
[Route("api/alerts")]
[Produces("application/json")]
public sealed class QueueThresholdsController(QueueThresholdService thresholds, ILogger<QueueThresholdsController> logger) : ControllerBase
{
    /// <summary>All thresholds.</summary>
    [HttpGet("queue-thresholds")]
    [JobDistAuthorize(Permissions.AlertView, RoleSets.ConsoleRead)]
    public async Task<ActionResult<IReadOnlyList<QueueThresholdResponse>>> List(CancellationToken ct) => Ok(await thresholds.ListAsync(ct));

    /// <summary>One threshold.</summary>
    [HttpGet("queue-thresholds/{id:guid}")]
    [JobDistAuthorize(Permissions.AlertView, RoleSets.ConsoleRead)]
    public async Task<ActionResult<QueueThresholdResponse>> Get(Guid id, CancellationToken ct) => Ok(await thresholds.GetAsync(id, ct));

    /// <summary>Creates a threshold (409 when the job type already has one).</summary>
    [HttpPost("queue-thresholds")]
    [JobDistAuthorize(Permissions.QueueManage, RoleSets.ConsoleWrite)]
    [Idempotent]
    public async Task<ActionResult<QueueThresholdResponse>> Create([FromBody] QueueThresholdRequest request, CancellationToken ct)
    {
        var created = await thresholds.CreateAsync(request, User.CallerName(), ct);
        Audit("queue.threshold.create", created.ThresholdId);
        return CreatedAtAction(nameof(Get), new { id = created.ThresholdId }, created);
    }

    /// <summary>Updates a threshold (<c>version</c> required).</summary>
    [HttpPut("queue-thresholds/{id:guid}")]
    [JobDistAuthorize(Permissions.QueueManage, RoleSets.ConsoleWrite)]
    public async Task<ActionResult<QueueThresholdResponse>> Update(Guid id, [FromBody] QueueThresholdRequest request, CancellationToken ct)
    {
        var updated = await thresholds.UpdateAsync(id, request, User.CallerName(), ct);
        Audit("queue.threshold.update", id);
        return Ok(updated);
    }

    /// <summary>Deletes a threshold.</summary>
    [HttpDelete("queue-thresholds/{id:guid}")]
    [JobDistAuthorize(Permissions.QueueManage, RoleSets.ConsoleWrite)]
    public async Task<IActionResult> Delete(Guid id, [FromQuery, Required] long version, CancellationToken ct)
    {
        await thresholds.DeleteAsync(id, version, User.CallerName(), ct);
        Audit("queue.threshold.delete", id);
        return NoContent();
    }

    /// <summary>Current pending jobs against every threshold (dashboard).</summary>
    [HttpGet("queue-status")]
    [JobDistAuthorize(Permissions.DashboardView, RoleSets.ConsoleRead)]
    public async Task<ActionResult<IReadOnlyList<QueueStatusResponse>>> Status(CancellationToken ct) => Ok(await thresholds.GetStatusAsync(ct));

    private void Audit(string action, Guid id) =>
        logger.LogInformation("Audit {Action} {EntityId} by {UserId} from {ClientIp}.", action, id, User.CallerId(), HttpContext.Connection.RemoteIpAddress);
}

/// <summary>Reports (X4).</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/reports")]
[Route("api/reports")]
[Produces("application/json")]
public sealed class ReportsController(JobReportService reports) : ControllerBase
{
    /// <summary>Job statistics by day (in <paramref name="timeZone"/>), job type and/or worker.</summary>
    /// <param name="fromDate">First day (yyyy-MM-dd).</param>
    /// <param name="toDate">Last day (yyyy-MM-dd); range at most <c>JobDist:RetentionDays</c>.</param>
    /// <param name="jobType">Optional job type.</param>
    /// <param name="workerId">Optional worker.</param>
    /// <param name="groupBy"><c>day</c>, <c>jobType</c>, <c>worker</c> or a comma-separated combination.</param>
    /// <param name="timeZone">IANA time zone (default Asia/Bangkok).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("job-statistics")]
    [JobDistAuthorize(Permissions.ReportView, RoleSets.ConsoleRead)]
    public async Task<ActionResult<JobStatisticsReportResponse>> JobStatistics([FromQuery, Required] DateOnly fromDate, [FromQuery, Required] DateOnly toDate,
        [FromQuery] string? jobType, [FromQuery] Guid? workerId, [FromQuery] string groupBy = "day", [FromQuery] string timeZone = "Asia/Bangkok", CancellationToken ct = default) =>
        Ok(await reports.GetAsync(fromDate, toDate, jobType, workerId, groupBy, timeZone, ct));
}

/// <summary>AI assistant of the web console (X5); shape matches WebCommon.UI <c>BackendChatClient</c>.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/agent")]
[Route("api/agent")]
[Produces("application/json")]
public sealed class AgentChatController(AgentChatService chat) : ControllerBase
{
    /// <summary>Whether an AI provider is configured (anonymous: the web console reads it before sign-in to show or hide the assistant).</summary>
    [HttpGet("status")]
    [AllowAnonymous]
    public ActionResult<AgentStatusResponse> Status() => Ok(new AgentStatusResponse(chat.IsEnabled));

    /// <summary>Answers a chat with read-only JobDist tools filtered by the caller's permissions (503 when not configured).</summary>
    [HttpPost("chat")]
    [JobDistAuthorize(Permissions.AgentUse, RoleSets.None)]
    [EnableRateLimiting("agent")]
    public async Task<ActionResult<AgentChatResponse>> Chat([FromBody] AgentChatRequest request, CancellationToken ct)
    {
        if (!chat.IsEnabled)
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "AI assistant not configured", detail: "The installer has not configured AgentChat.");
        }
        return Ok(await chat.ChatAsync(request, User, HttpContext.RequestServices, ct));
    }
}

/// <summary>Browser log entries forwarded to LogServer (L3).</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/client-logs")]
[Route("api/client-logs")]
public sealed class ClientLogsController(ClientLogForwarder forwarder) : ControllerBase
{
    /// <summary>Accepts 1-50 entries; the server adds user id, client IP and user agent itself.</summary>
    [HttpPost]
    [Authorize]
    [EnableRateLimiting("client-logs")]
    [RequestSizeLimit(512 * 1024)]
    public IActionResult Post([FromBody] ClientLogBatchRequest request)
    {
        forwarder.Write(request, User.CallerId(), HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString());
        return Accepted();
    }
}
