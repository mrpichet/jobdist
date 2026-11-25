using System.Security.Cryptography;
using System.Text;
using JobDist.Contracts;
using JobDist.Core.DAO;
using JobDist.Core.Domain;
using JobDist.Core.Options;
using JobDist.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobDist.Core.Services;

/// <summary>New jobs rejected because a queue threshold is reached (HTTP 429 <c>queue_full</c>).</summary>
public sealed class QueueFullException(string jobType, int pending, int maxPending)
    : JobDistException($"Queue for job type '{jobType}' is full ({pending} pending, limit {maxPending}). Retry later.")
{
    public string JobType { get; } = jobType;
    public int Pending { get; } = pending;
    public int MaxPending { get; } = maxPending;
}

/// <summary>Same <c>Idempotency-Key</c> was used with a different request body (HTTP 422).</summary>
public sealed class IdempotencyMismatchException()
    : JobDistException("This Idempotency-Key was already used with a different request body.");

/// <summary>Admission check run before jobs are inserted.</summary>
public interface IQueueAdmission
{
    /// <summary>Throws <see cref="QueueFullException"/> when adding these jobs would pass an enabled threshold.</summary>
    /// <param name="additions">New jobs per job type.</param>
    /// <param name="ct">Cancellation token.</param>
    Task EnsureCapacityAsync(IReadOnlyDictionary<string, int> additions, CancellationToken ct);
}

/// <summary>Short-lived cache of pending counts per job type (admission runs at thousands of submissions per second).</summary>
public sealed class PendingQueueCache(IJobDAO jobs, IOptions<AlertOptions> options, TimeProvider time)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Tuple<DateTimeOffset, IReadOnlyDictionary<string, int>>? _cache;

    /// <summary>Pending jobs per type, at most <see cref="AlertOptions.PendingCacheMilliseconds"/> old.</summary>
    public async Task<IReadOnlyDictionary<string, int>> GetAsync(CancellationToken ct)
    {
        var ttl = TimeSpan.FromMilliseconds(options.Value.PendingCacheMilliseconds);
        var c = _cache;
        if (c is not null && time.GetUtcNow() - c.Item1 < ttl)
        {
            return c.Item2;
        }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            c = _cache;
            if (c is not null && time.GetUtcNow() - c.Item1 < ttl)
            {
                return c.Item2;
            }
            var fresh = await jobs.PendingByTypeAsync(ct).ConfigureAwait(false);
            _cache = Tuple.Create(time.GetUtcNow(), fresh);
            return fresh;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forces the next read to hit the database.</summary>
    public void Invalidate() => _cache = null;
}

/// <summary>Alert recipients: CRUD with optimistic concurrency, encrypted addresses, test message.</summary>
public sealed class AlertRecipientService(
    IAlertRecipientDAO recipients,
    IAlertOutboxDAO outbox,
    IAlertDeliveryClient delivery,
    AlertDispatchService dispatch,
    PayloadProtectionService protector,
    TimeProvider time,
    ILogger<AlertRecipientService> logger)
{
    /// <summary>All recipients.</summary>
    public async Task<IReadOnlyList<AlertRecipientResponse>> ListAsync(CancellationToken ct) =>
        (await recipients.ListAsync(ct).ConfigureAwait(false)).Select(ToResponse).ToList();

    /// <summary>One recipient.</summary>
    /// <exception cref="NotFoundException">Unknown id.</exception>
    public async Task<AlertRecipientResponse> GetAsync(Guid id, CancellationToken ct) => ToResponse(await LoadAsync(id, ct).ConfigureAwait(false));

    /// <summary>Creates a recipient.</summary>
    public async Task<AlertRecipientResponse> CreateAsync(AlertRecipientRequest request, string user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var address = request.Address?.Trim() ?? string.Empty;
        AlertAddress.Validate(request.Channel, address);
        var now = time.GetUtcNow();
        var r = new AlertRecipientDTO
        {
            RecipientId = Guid.NewGuid(), CreatedUtc = now, Version = 1
        };
        Apply(r, request, user, now);
        r.AddressProtected = protector.Protect(r.RecipientId, address);
        r.AddressMasked = AlertAddress.Mask(address);
        await recipients.InsertAsync(r, ct).ConfigureAwait(false);
        dispatch.InvalidateRecipients();
        logger.LogInformation("Alert recipient {RecipientId} created by {User}.", r.RecipientId, user);
        return ToResponse(r);
    }

    /// <summary>Updates a recipient. An empty address keeps the stored one.</summary>
    /// <exception cref="ConflictException">Version does not match (someone else saved first).</exception>
    public async Task<AlertRecipientResponse> UpdateAsync(Guid id, AlertRecipientRequest request, string user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var expected = request.Version ?? throw new ValidationException("version is required.");
        var r = await LoadAsync(id, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        Apply(r, request, user, now);
        if (!string.IsNullOrWhiteSpace(request.Address))
        {
            var address = request.Address.Trim();
            AlertAddress.Validate(request.Channel, address);
            r.AddressProtected = protector.Protect(r.RecipientId, address);
            r.AddressMasked = AlertAddress.Mask(address);
        }
        r.Version = expected + 1;
        if (!await recipients.TryUpdateAsync(r, expected, ct).ConfigureAwait(false))
        {
            throw new ConflictException("The recipient was changed by someone else. Reload and try again.");
        }
        dispatch.InvalidateRecipients();
        logger.LogInformation("Alert recipient {RecipientId} updated by {User}.", id, user);
        return ToResponse(r);
    }

    /// <summary>Deletes a recipient (idempotent: deleting a missing recipient succeeds).</summary>
    public async Task DeleteAsync(Guid id, long version, string user, CancellationToken ct)
    {
        var current = await recipients.GetAsync(id, ct).ConfigureAwait(false);
        if (current is null)
        {
            return;
        }
        if (!await recipients.TryDeleteAsync(id, version, ct).ConfigureAwait(false))
        {
            throw new ConflictException("The recipient was changed by someone else. Reload and try again.");
        }
        dispatch.InvalidateRecipients();
        logger.LogInformation("Alert recipient {RecipientId} deleted by {User}.", id, user);
    }

    /// <summary>Sends a test message now and records it in the alert history.</summary>
    public async Task<AlertSendResultResponse> TestAsync(Guid id, string user, CancellationToken ct)
    {
        var r = await LoadAsync(id, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        AlertDeliveryResult result;
        if (!delivery.IsConfigured)
        {
            result = new(AlertSendStatus.Skipped, false, null, "MessageHub is not configured.");
        }
        else
        {
            string address;
            try
            {
                address = protector.Unprotect(r.RecipientId, r.AddressProtected);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return new AlertSendResultResponse(AlertSendStatus.Failed, null, "Stored address cannot be decrypted; enter the address again.");
            }
            result = await delivery.SendAsync(r.Channel, address, "[JobDist] Test alert",
                $"Test message from JobDist sent by {user} at {now:yyyy-MM-dd HH:mm:ss} UTC.", ct).ConfigureAwait(false);
        }

        // History row (already final, never picked up by the outbox worker).
        var alert = new AlertEventDTO($"Test:{r.RecipientId}:{Guid.NewGuid():N}", AlertEventType.Test, AlertSeverity.Info, r.Name,
            "[JobDist] Test alert", $"Test message sent by {user}.", now);
        var row = dispatch.NewRow(alert, r.RecipientId, r.Name, r.Channel, r.AddressMasked, now, result.Status == AlertSendStatus.Sent ? AlertSendStatus.Sent : result.Status == AlertSendStatus.Skipped ? AlertSendStatus.Skipped : AlertSendStatus.Failed, result.Error);
        row.Attempts = result.Status == AlertSendStatus.Skipped ? 0 : 1;
        row.CorrelationId = result.CorrelationId;
        row.SentUtc = result.Status == AlertSendStatus.Sent ? now : null;
        await outbox.InsertIfAbsentAsync([row], ct).ConfigureAwait(false);
        var status = result.Status == AlertSendStatus.Pending ? AlertSendStatus.Failed : result.Status;
        return new AlertSendResultResponse(status, result.CorrelationId, result.Error);
    }

    /// <summary>Channels enabled on MessageHub; empty when MessageHub is not configured or unreachable.</summary>
    public Task<IReadOnlyList<string>> GetChannelsAsync(CancellationToken ct) => delivery.GetChannelsAsync(ct);

    /// <summary>Alert history.</summary>
    public async Task<PagedResponse<AlertLogResponse>> QueryLogsAsync(DateTimeOffset? fromUtc, DateTimeOffset? toUtc, AlertEventType? eventType,
        AlertSendStatus? status, Guid? recipientId, int skip, int take, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var to = (toUtc ?? now).ToUniversalTime();
        var from = (fromUtc ?? to.AddDays(-7)).ToUniversalTime();
        if (from > to)
        {
            throw new ValidationException("fromUtc must be before toUtc.");
        }
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 500);
        var (items, total) = await outbox.QueryAsync(new AlertOutboxQueryDTO(from, to, eventType, status, recipientId, skip, take), ct).ConfigureAwait(false);
        return new PagedResponse<AlertLogResponse>(items.Select(ToLog).ToList(), total, skip, take);
    }

    /// <summary>Outbox row → history entry.</summary>
    public static AlertLogResponse ToLog(AlertOutboxDTO o) => new(
        o.OutboxId, o.CreatedUtc, (AlertEventType)o.EventType, (AlertSeverity)o.Severity, o.Title, o.Subject,
        o.RecipientId == Guid.Empty ? null : o.RecipientId, o.RecipientName, o.Channel, o.AddressMasked, (AlertSendStatus)o.Status,
        o.Attempts, o.SentUtc, o.CorrelationId, o.LastError, o.InstanceId);

    private async Task<AlertRecipientDTO> LoadAsync(Guid id, CancellationToken ct) =>
        await recipients.GetAsync(id, ct).ConfigureAwait(false) ?? throw new NotFoundException($"Alert recipient {id} not found.");

    private static void Apply(AlertRecipientDTO r, AlertRecipientRequest request, string user, DateTimeOffset now)
    {
        if (request.EventTypes is null || request.EventTypes.Count == 0)
        {
            throw new ValidationException("Select at least one event type.");
        }
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200)
        {
            throw new ValidationException("Name is required (max 200 characters).");
        }
        r.Name = request.Name.Trim();
        r.Channel = request.Channel.Trim();
        r.EventTypes = string.Join(',', request.EventTypes.Distinct().Select(e => e.ToString()));
        r.MinSeverity = (int)request.MinSeverity;
        r.Enabled = request.Enabled;
        r.UpdatedUtc = now;
        r.UpdatedBy = user;
    }

    private static AlertRecipientResponse ToResponse(AlertRecipientDTO r) => new(
        r.RecipientId, r.Name, r.Channel, r.AddressMasked, r.EventTypeList, (AlertSeverity)r.MinSeverity, r.Enabled, r.CreatedUtc, r.UpdatedUtc, r.UpdatedBy, r.Version);
}

/// <summary>
/// Queue thresholds: CRUD, current usage, admission control for new jobs (Q17: alert and reject) and the periodic check
/// that raises near-limit / over-limit alerts. Threshold <c>*</c> applies to the total of all job types.
/// </summary>
public sealed class QueueThresholdService(
    IQueueThresholdDAO thresholds,
    PendingQueueCache pending,
    AlertDispatchService dispatch,
    IOptions<AlertOptions> options,
    TimeProvider time,
    ILogger<QueueThresholdService> logger) : IQueueAdmission
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);
    private volatile Tuple<DateTimeOffset, IReadOnlyList<QueueThresholdDTO>>? _cache;

    /// <summary>All thresholds.</summary>
    public async Task<IReadOnlyList<QueueThresholdResponse>> ListAsync(CancellationToken ct) =>
        (await thresholds.ListAsync(ct).ConfigureAwait(false)).Select(ToResponse).ToList();

    /// <summary>One threshold.</summary>
    public async Task<QueueThresholdResponse> GetAsync(Guid id, CancellationToken ct) => ToResponse(await LoadAsync(id, ct).ConfigureAwait(false));

    /// <summary>Creates a threshold.</summary>
    /// <exception cref="ConflictException">The job type already has one.</exception>
    public async Task<QueueThresholdResponse> CreateAsync(QueueThresholdRequest request, string user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        var t = new QueueThresholdDTO
        {
            ThresholdId = Guid.NewGuid(), JobType = request.JobType.Trim(), MaxPending = request.MaxPending, WarnPercent = request.WarnPercent,
            Enabled = request.Enabled, UpdatedBy = user, UpdatedUtc = time.GetUtcNow(), Version = 1
        };
        await thresholds.InsertAsync(t, ct).ConfigureAwait(false);
        _cache = null;
        logger.LogInformation("Queue threshold for {JobType} created by {User}: max {Max}, warn {Warn}%.", t.JobType, user, t.MaxPending, t.WarnPercent);
        return ToResponse(t);
    }

    /// <summary>Updates a threshold.</summary>
    public async Task<QueueThresholdResponse> UpdateAsync(Guid id, QueueThresholdRequest request, string user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        var expected = request.Version ?? throw new ValidationException("version is required.");
        var t = await LoadAsync(id, ct).ConfigureAwait(false);
        t.JobType = request.JobType.Trim();
        t.MaxPending = request.MaxPending;
        t.WarnPercent = request.WarnPercent;
        t.Enabled = request.Enabled;
        t.UpdatedBy = user;
        t.UpdatedUtc = time.GetUtcNow();
        t.Version = expected + 1;
        if (!await thresholds.TryUpdateAsync(t, expected, ct).ConfigureAwait(false))
        {
            throw new ConflictException("The threshold was changed by someone else. Reload and try again.");
        }
        _cache = null;
        logger.LogInformation("Queue threshold {ThresholdId} updated by {User}.", id, user);
        return ToResponse(t);
    }

    /// <summary>Deletes a threshold (idempotent).</summary>
    public async Task DeleteAsync(Guid id, long version, string user, CancellationToken ct)
    {
        if (await thresholds.GetAsync(id, ct).ConfigureAwait(false) is null)
        {
            return;
        }
        if (!await thresholds.TryDeleteAsync(id, version, ct).ConfigureAwait(false))
        {
            throw new ConflictException("The threshold was changed by someone else. Reload and try again.");
        }
        _cache = null;
        logger.LogInformation("Queue threshold {ThresholdId} deleted by {User}.", id, user);
    }

    /// <summary>Current usage of every threshold (enabled first).</summary>
    public async Task<IReadOnlyList<QueueStatusResponse>> GetStatusAsync(CancellationToken ct)
    {
        var list = await GetThresholdsAsync(ct).ConfigureAwait(false);
        var counts = await pending.GetAsync(ct).ConfigureAwait(false);
        return list.Select(t => Status(t, PendingFor(t.JobType, counts))).OrderByDescending(s => s.Enabled).ThenByDescending(s => s.UsagePercent).ToList();
    }

    /// <inheritdoc />
    public async Task EnsureCapacityAsync(IReadOnlyDictionary<string, int> additions, CancellationToken ct)
    {
        if (additions.Count == 0)
        {
            return;
        }
        var list = await GetThresholdsAsync(ct).ConfigureAwait(false);
        if (list.Count == 0 || !list.Any(t => t.Enabled))
        {
            return;
        }
        var counts = await pending.GetAsync(ct).ConfigureAwait(false);
        foreach (var t in list.Where(t => t.Enabled))
        {
            var adding = t.JobType == "*" ? additions.Values.Sum() : additions.Where(a => string.Equals(a.Key, t.JobType, StringComparison.OrdinalIgnoreCase)).Sum(a => a.Value);
            if (adding == 0)
            {
                continue;
            }
            var current = PendingFor(t.JobType, counts);
            if (current + adding > t.MaxPending)
            {
                await RaiseAsync(t, current, QueueLevel.OverLimit, ct).ConfigureAwait(false);
                throw new QueueFullException(t.JobType, current, t.MaxPending);
            }
        }
    }

    /// <summary>Checks every enabled threshold and raises alerts (leader only, every <see cref="AlertOptions.QueueCheckSeconds"/>).</summary>
    /// <returns>Thresholds at near-limit or over-limit.</returns>
    public async Task<int> CheckAndAlertAsync(CancellationToken ct)
    {
        _cache = null;
        pending.Invalidate();
        var raised = 0;
        foreach (var s in (await GetStatusAsync(ct).ConfigureAwait(false)).Where(s => s.Enabled && s.Level != QueueLevel.Normal))
        {
            var t = (await GetThresholdsAsync(ct).ConfigureAwait(false)).First(x => x.ThresholdId == s.ThresholdId);
            await RaiseAsync(t, s.Pending, s.Level, ct).ConfigureAwait(false);
            raised++;
        }
        return raised;
    }

    private async Task RaiseAsync(QueueThresholdDTO t, int current, QueueLevel level, CancellationToken ct)
    {
        // Event key repeats per cooldown window → one alert per window across all instances.
        var now = time.GetUtcNow();
        var window = now.ToUnixTimeSeconds() / (options.Value.QueueAlertCooldownMinutes * 60L);
        var type = level == QueueLevel.OverLimit ? AlertEventType.QueueOverLimit : AlertEventType.QueueNearLimit;
        var severity = level == QueueLevel.OverLimit ? AlertSeverity.Critical : AlertSeverity.Warning;
        var label = t.JobType == "*" ? "all job types" : t.JobType;
        var usage = t.MaxPending == 0 ? 0 : Math.Round(current * 100.0 / t.MaxPending, 1);
        var body = level == QueueLevel.OverLimit
            ? $"Pending jobs for {label}: {current} of {t.MaxPending} ({usage}%). New jobs of this type are rejected (HTTP 429) until the queue drains."
            : $"Pending jobs for {label}: {current} of {t.MaxPending} ({usage}%), at or above the warning level of {t.WarnPercent}%.";
        try
        {
            await dispatch.PublishAsync(new AlertEventDTO($"{type}:{t.JobType}:{window}", type, severity, $"queue:{t.JobType}",
                $"[JobDist][{severity}] Queue {(level == QueueLevel.OverLimit ? "over limit" : "near limit")}: {label}", body, now), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Rejecting the job matters more than the alert; the periodic check will try again.
            logger.LogError(ex, "Could not queue {Type} alert for {JobType}.", type, t.JobType);
        }
    }

    private static int PendingFor(string jobType, IReadOnlyDictionary<string, int> counts) =>
        jobType == "*" ? counts.Values.Sum() : counts.Where(c => string.Equals(c.Key, jobType, StringComparison.OrdinalIgnoreCase)).Sum(c => c.Value);

    private static QueueStatusResponse Status(QueueThresholdDTO t, int current)
    {
        var usage = t.MaxPending <= 0 ? 0 : Math.Round(current * 100.0 / t.MaxPending, 1);
        var level = current >= t.MaxPending ? QueueLevel.OverLimit : usage >= t.WarnPercent ? QueueLevel.NearLimit : QueueLevel.Normal;
        return new QueueStatusResponse(t.ThresholdId, t.JobType, current, t.MaxPending, usage, level, t.Enabled);
    }

    private async Task<IReadOnlyList<QueueThresholdDTO>> GetThresholdsAsync(CancellationToken ct)
    {
        var c = _cache;
        var now = time.GetUtcNow();
        if (c is not null && now - c.Item1 < CacheTtl)
        {
            return c.Item2;
        }
        var list = await thresholds.ListAsync(ct).ConfigureAwait(false);
        _cache = Tuple.Create(now, list);
        return list;
    }

    private async Task<QueueThresholdDTO> LoadAsync(Guid id, CancellationToken ct) =>
        await thresholds.GetAsync(id, ct).ConfigureAwait(false) ?? throw new NotFoundException($"Queue threshold {id} not found.");

    private static void Validate(QueueThresholdRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.JobType) || r.JobType.Length > 100)
        {
            throw new ValidationException("Job type is required (max 100 characters, * = all types).");
        }
        if (r.MaxPending is < 1 or > 10_000_000)
        {
            throw new ValidationException("maxPending must be 1 to 10,000,000.");
        }
        if (r.WarnPercent is < 1 or > 99)
        {
            throw new ValidationException("warnPercent must be 1 to 99.");
        }
    }

    private static QueueThresholdResponse ToResponse(QueueThresholdDTO t) =>
        new(t.ThresholdId, t.JobType, t.MaxPending, t.WarnPercent, t.Enabled, t.UpdatedBy, t.UpdatedUtc, t.Version);
}

/// <summary>Stored-response idempotency for POST requests that carry an <c>Idempotency-Key</c> header.</summary>
public sealed class IdempotencyService(IIdempotencyRecordDAO records, TimeProvider time)
{
    /// <summary>Records live 24 hours.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    /// <summary>Hex SHA-256 of a request body.</summary>
    public static string Hash(ReadOnlySpan<byte> body) => Convert.ToHexString(SHA256.HashData(body));

    /// <summary>
    /// Returns the stored response for this key, or null when the key is new.
    /// </summary>
    /// <exception cref="IdempotencyMismatchException">Same key, different body.</exception>
    public async Task<IdempotencyRecordDTO?> FindAsync(string principal, string route, string key, string requestHash, CancellationToken ct)
    {
        var r = await records.GetAsync(principal, route, key, time.GetUtcNow(), ct).ConfigureAwait(false);
        if (r is null)
        {
            return null;
        }
        return r.RequestHash == requestHash ? r : throw new IdempotencyMismatchException();
    }

    /// <summary>Stores the first response (a concurrent duplicate that loses the race is ignored).</summary>
    public Task SaveAsync(string principal, string route, string key, string requestHash, int statusCode, string? body, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return records.TryInsertAsync(new IdempotencyRecordDTO
        {
            Principal = principal, Route = route, IdempotencyKey = key, RequestHash = requestHash, StatusCode = statusCode,
            ResponseBody = body, CreatedUtc = now, ExpiresUtc = now + Lifetime
        }, ct);
    }

    /// <summary>Deletes expired records.</summary>
    public Task<int> PurgeAsync(CancellationToken ct) => records.PurgeAsync(time.GetUtcNow(), ct);
}

/// <summary>Job types known to the cluster (for the job type pickers of the console).</summary>
public sealed class JobCatalogService(IWorkerDAO workers, PendingQueueCache pending)
{
    /// <summary>Types registered by online / draining workers plus types that have pending jobs.</summary>
    public async Task<IReadOnlyList<JobTypeInfoResponse>> GetJobTypesAsync(CancellationToken ct)
    {
        var list = await workers.ListAsync(false, ct).ConfigureAwait(false);
        var online = list.Where(w => w.Status is WorkerStatus.Online or WorkerStatus.Draining).ToList();
        var counts = await pending.GetAsync(ct).ConfigureAwait(false);
        var types = online.SelectMany(w => w.Capabilities.Select(c => c.JobType)).Concat(counts.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
        return types.Select(t => new JobTypeInfoResponse(
                t,
                online.Count(w => w.Status == WorkerStatus.Online && w.Capabilities.Any(c => string.Equals(c.JobType, t, StringComparison.OrdinalIgnoreCase))),
                online.Where(w => w.Status == WorkerStatus.Online).SelectMany(w => w.Capabilities).Where(c => string.Equals(c.JobType, t, StringComparison.OrdinalIgnoreCase)).Sum(c => c.MaxConcurrency),
                counts.Where(c => string.Equals(c.Key, t, StringComparison.OrdinalIgnoreCase)).Sum(c => c.Value)))
            .OrderBy(t => t.JobType, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>
/// Job statistics report (X4). The database returns hourly UTC aggregates; this service regroups them by local day in the
/// requested time zone, job type and/or worker, and computes P95 from a logarithmic duration histogram (bucket width ≈ 5 %),
/// so SQL Server and PostgreSQL give the same result.
/// </summary>
public sealed class JobReportService(IJobReportDAO reports, IWorkerDAO workers, IOptions<JobDistOptions> options, TimeProvider time)
{
    private static readonly string[] AllowedGroups = ["day", "jobType", "worker"];

    /// <summary>Builds the report.</summary>
    /// <exception cref="ValidationException">Bad range, group or time zone.</exception>
    public async Task<JobStatisticsReportResponse> GetAsync(DateOnly fromDate, DateOnly toDate, string? jobType, Guid? workerId, string? groupBy, string? timeZone, CancellationToken ct)
    {
        // Step 1: validate.
        if (toDate < fromDate)
        {
            throw new ValidationException("toDate must not be before fromDate.");
        }
        var days = toDate.DayNumber - fromDate.DayNumber + 1;
        if (days > options.Value.RetentionDays)
        {
            throw new ValidationException($"The range can be at most {options.Value.RetentionDays} days (data retention).");
        }
        var groups = (groupBy ?? "day").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(g => AllowedGroups.FirstOrDefault(a => a.Equals(g, StringComparison.OrdinalIgnoreCase)) ?? throw new ValidationException($"Unknown groupBy '{g}'. Use day, jobType or worker."))
            .Distinct().ToList();
        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(timeZone) ? "Asia/Bangkok" : timeZone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ValidationException($"Unknown time zone '{timeZone}'.");
        }

        // Step 2: UTC range of the local days, then hourly aggregates.
        var fromUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(fromDate.ToDateTime(TimeOnly.MinValue), tz), TimeSpan.Zero);
        var toUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(toDate.AddDays(1).ToDateTime(TimeOnly.MinValue), tz), TimeSpan.Zero);
        var q = new JobReportQueryDTO(fromUtc, toUtc, string.IsNullOrWhiteSpace(jobType) ? null : jobType.Trim(), workerId);
        var submitted = await reports.GetSubmittedAsync(q, ct).ConfigureAwait(false);
        var completed = await reports.GetCompletedAsync(q, ct).ConfigureAwait(false);
        var attempts = await reports.GetAttemptsAsync(q, ct).ConfigureAwait(false);
        var names = (await workers.ListAsync(true, ct).ConfigureAwait(false)).ToDictionary(w => w.WorkerId, w => w.Name);

        // Step 3: regroup.
        bool byDay = groups.Contains("day"), byType = groups.Contains("jobType"), byWorker = groups.Contains("worker");
        (DateOnly?, string?, Guid?) Key(DateTime hourUtc, string type, Guid? worker) => (
            byDay ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(hourUtc, DateTimeKind.Utc), tz)) : null,
            byType ? type : null,
            byWorker ? worker : null);

        var acc = new Dictionary<(DateOnly?, string?, Guid?), Acc>();
        var total = new Acc();
        Acc For((DateOnly?, string?, Guid?) k) => acc.TryGetValue(k, out var a) ? a : acc[k] = new Acc();
        foreach (var s in submitted)
        {
            For(Key(s.HourUtc, s.JobType, s.WorkerId)).Submitted += s.JobCount;
            total.Submitted += s.JobCount;
        }
        foreach (var c in completed)
        {
            foreach (var a in new[] { For(Key(c.HourUtc, c.JobType, c.WorkerId)), total })
            {
                a.AddCompleted((JobStatus)c.Status, c.JobCount, c.SumAttempts);
            }
        }
        foreach (var b in attempts)
        {
            foreach (var a in new[] { For(Key(b.HourUtc, b.JobType, b.WorkerId)), total })
            {
                a.AddAttempts((AttemptOutcome)b.Outcome, b.DurationBucket, b.AttemptCount, b.SumDurationMs, b.SumQueueWaitMs);
            }
        }

        // Step 4: rows; throughput per hour of each group's span (one local day = 24 h, otherwise the whole range).
        var rangeHours = (toUtc - fromUtc).TotalHours;
        var rows = acc.Select(kv => kv.Value.ToRow(kv.Key.Item1, kv.Key.Item2, kv.Key.Item3,
                kv.Key.Item3 is { } id && names.TryGetValue(id, out var n) ? n : null, kv.Key.Item1 is null ? rangeHours : 24))
            .OrderBy(r => r.Day).ThenBy(r => r.JobType, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.WorkerName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new JobStatisticsReportResponse(fromDate, toDate, tz.Id, groups, rows, total.ToRow(null, null, null, null, rangeHours), time.GetUtcNow());
    }

    /// <summary>Accumulator of one report row.</summary>
    private sealed class Acc
    {
        public int Submitted;
        private int _succeeded, _failed, _cancelled;
        private long _sumAttempts;
        private int _finished;
        private int _okAttempts;
        private double _okDuration;
        private int _allAttempts;
        private double _queueWait;
        private readonly SortedDictionary<int, int> _histogram = [];

        public void AddCompleted(JobStatus status, int count, long sumAttempts)
        {
            switch (status)
            {
                case JobStatus.Succeeded: _succeeded += count; break;
                case JobStatus.Failed: _failed += count; break;
                case JobStatus.Cancelled: _cancelled += count; break;
            }
            _finished += count;
            _sumAttempts += sumAttempts;
        }

        public void AddAttempts(AttemptOutcome outcome, int bucket, int count, double sumDuration, double sumQueueWait)
        {
            _allAttempts += count;
            _queueWait += sumQueueWait;
            if (outcome == AttemptOutcome.Succeeded)
            {
                _okAttempts += count;
                _okDuration += sumDuration;
                _histogram[bucket] = _histogram.GetValueOrDefault(bucket) + count;
            }
        }

        public JobStatisticsRowResponse ToRow(DateOnly? day, string? jobType, Guid? workerId, string? workerName, double hours) => new(
            day, jobType, workerId, workerName, Submitted, _succeeded, _failed, _cancelled,
            _succeeded + _failed == 0 ? 0 : Math.Round(_succeeded * 100.0 / (_succeeded + _failed), 2),
            _finished == 0 ? 0 : Math.Round((double)_sumAttempts / _finished, 2),
            _okAttempts == 0 ? 0 : Math.Round(_okDuration / _okAttempts, 1),
            Math.Round(P95(), 1),
            _allAttempts == 0 ? 0 : Math.Round(_queueWait / _allAttempts, 1),
            hours <= 0 ? 0 : Math.Round(_succeeded / hours, 2));

        /// <summary>95th percentile from the log histogram: bucket b covers [e^(b/20) − 1, e^((b+1)/20) − 1); returns the bucket midpoint.</summary>
        private double P95()
        {
            var n = _histogram.Values.Sum();
            if (n == 0)
            {
                return 0;
            }
            var rank = (int)Math.Ceiling(0.95 * n);
            var seen = 0;
            foreach (var (bucket, count) in _histogram)
            {
                seen += count;
                if (seen >= rank)
                {
                    return Math.Exp((bucket + 0.5) / 20.0) - 1;
                }
            }
            return 0;
        }
    }
}
