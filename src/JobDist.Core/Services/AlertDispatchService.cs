using System.Security.Cryptography;
using System.Text.RegularExpressions;
using JobDist.Contracts;
using JobDist.Core.DAO;
using JobDist.Core.Domain;
using JobDist.Core.Observability;
using JobDist.Core.Options;
using JobDist.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobDist.Core.Services;

/// <summary>Result of one call to the message delivery service.</summary>
/// <param name="Status">Sent, Failed or Skipped.</param>
/// <param name="Retryable">Failed but worth retrying (timeout, network, 5xx, 429).</param>
/// <param name="CorrelationId">Message id from the delivery service.</param>
/// <param name="Error">Short error text (no secrets).</param>
/// <param name="CircuitOpen">Not attempted because the circuit breaker is open.</param>
public sealed record AlertDeliveryResult(AlertSendStatus Status, bool Retryable, string? CorrelationId, string? Error, bool CircuitOpen = false);

/// <summary>Sends one message to one address (implemented in Infrastructure by the MessageHub client).</summary>
public interface IAlertDeliveryClient
{
    /// <summary><c>true</c> when MessageHub is enabled and has a URL and API key.</summary>
    bool IsConfigured { get; }

    /// <summary>Sends one message. Never retries a POST inside the call (the outbox retries later).</summary>
    Task<AlertDeliveryResult> SendAsync(string channel, string address, string subject, string body, CancellationToken ct);

    /// <summary>Channels enabled on MessageHub (cached).</summary>
    Task<IReadOnlyList<string>> GetChannelsAsync(CancellationToken ct);
}

/// <summary>
/// Producer side of the alert outbox: turns one event into one outbox row per matching recipient. The (event key, recipient)
/// pair is unique, so publishing the same event twice, from one instance or several, queues it only once.
/// When no recipient matches, one history row with status Skipped is written so the event still shows on the alert history screen.
/// </summary>
public sealed class AlertDispatchService(
    IAlertRecipientDAO recipients,
    IAlertOutboxDAO outbox,
    IOptions<JobDistOptions> options,
    TimeProvider time,
    ILogger<AlertDispatchService> logger)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
    private volatile Tuple<DateTimeOffset, IReadOnlyList<AlertRecipientDTO>>? _cache;

    /// <summary>Drops the recipient cache (called after recipients change on this instance).</summary>
    public void InvalidateRecipients() => _cache = null;

    /// <summary>
    /// Queues an alert. Never throws for delivery problems; only a database error propagates.
    /// </summary>
    /// <param name="alert">Event.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Rows queued for delivery (0 when duplicate or no recipient).</returns>
    public async Task<int> PublishAsync(AlertEventDTO alert, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(alert);
        var now = time.GetUtcNow();

        // Step 1: recipients subscribed to this event type at this severity.
        var all = await GetRecipientsAsync(now, ct).ConfigureAwait(false);
        var matching = all.Where(r => r.Enabled && r.MinSeverity <= (int)alert.Severity && r.EventTypeList.Contains(alert.EventType)).ToList();
        logger.LogInformation("Alert {EventType} [{Severity}] {Subject}: {Title} → {Count} recipient(s).", alert.EventType, alert.Severity, alert.Subject, alert.Title, matching.Count);

        // Step 2: one row per recipient (or one Skipped history row).
        var rows = matching.Count == 0
            ? [NewRow(alert, Guid.Empty, null, "-", "-", now, AlertSendStatus.Skipped, "No recipient subscribed to this event.")]
            : matching.Select(r => NewRow(alert, r.RecipientId, r.Name, r.Channel, r.AddressMasked, now, AlertSendStatus.Pending, null)).ToList();

        // Step 3: idempotent insert.
        var inserted = await outbox.InsertIfAbsentAsync(rows, ct).ConfigureAwait(false);
        return matching.Count == 0 ? 0 : inserted;
    }

    /// <summary>Queues an alert and logs (never throws) any failure: an alert must not break the operation that raised it.</summary>
    public async Task TryPublishAsync(AlertEventDTO alert, CancellationToken ct)
    {
        try
        {
            await PublishAsync(alert, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not queue alert {EventKey}.", alert.EventKey);
        }
    }

    /// <summary>Builds an outbox row.</summary>
    internal AlertOutboxDTO NewRow(AlertEventDTO alert, Guid recipientId, string? recipientName, string channel, string addressMasked,
        DateTimeOffset now, AlertSendStatus status, string? error) => new()
    {
        EventKey = Truncate(alert.EventKey, 300),
        RecipientId = recipientId,
        RecipientName = recipientName,
        Channel = channel,
        AddressMasked = addressMasked,
        EventType = (int)alert.EventType,
        Severity = (int)alert.Severity,
        Subject = Truncate(alert.Subject, 300),
        Title = Truncate(alert.Title, 300),
        Body = alert.Body,
        OccurredUtc = alert.OccurredUtc.ToUniversalTime(),
        CreatedUtc = now,
        Status = (int)status,
        NextAttemptUtc = now,
        LastError = error,
        InstanceId = options.Value.InstanceId
    };

    private async Task<IReadOnlyList<AlertRecipientDTO>> GetRecipientsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var cached = _cache;
        if (cached is not null && now - cached.Item1 < CacheTtl)
        {
            return cached.Item2;
        }
        var list = await recipients.ListAsync(ct).ConfigureAwait(false);
        _cache = Tuple.Create(now, list);
        return list;
    }

    internal static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>
/// Consumer side of the alert outbox. Every server instance runs it: rows are claimed with row locks plus a lease, so each row
/// is sent by one instance. Retry policy: transient errors back off exponentially with jitter up to <see cref="AlertOptions.MaxAttempts"/>;
/// permanent errors (400/401/403/404) fail at once; an open circuit breaker postpones without counting an attempt.
/// MessageHub has no idempotency key, so a timeout after MessageHub accepted a message can deliver it twice (at-least-once);
/// the event key is appended to the message so recipients can see it is the same event.
/// </summary>
public sealed class AlertOutboxProcessor(
    IAlertOutboxDAO outbox,
    IAlertRecipientDAO recipients,
    IAlertDeliveryClient delivery,
    PayloadProtectionService protector,
    JobDistMetrics metrics,
    IOptions<AlertOptions> alertOptions,
    IOptions<JobDistOptions> options,
    TimeProvider time,
    ILogger<AlertOutboxProcessor> logger)
{
    /// <summary>
    /// Claims and delivers one batch.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Rows handled.</returns>
    public async Task<int> ProcessOnceAsync(CancellationToken ct)
    {
        var o = alertOptions.Value;
        var me = options.Value.InstanceId;
        var now = time.GetUtcNow();

        // Step 1: claim due rows (one pending row per subject + recipient → ordered delivery).
        var batch = await outbox.ClaimDueAsync(me, now, now.AddSeconds(o.OutboxLockSeconds), o.OutboxBatchSize, ct).ConfigureAwait(false);
        foreach (var row in batch)
        {
            ct.ThrowIfCancellationRequested();
            var update = await DeliverAsync(row, me, o, ct).ConfigureAwait(false);
            if (!await outbox.CompleteAsync(update, ct).ConfigureAwait(false))
            {
                logger.LogWarning("Outbox row {OutboxId} lock was lost before its result could be saved.", row.OutboxId);
            }
        }
        return batch.Count;
    }

    /// <summary>Lock owner prefix used by the broker relay while a row is in flight on the broker.</summary>
    public const string RelayPrefix = "relay:";

    /// <summary>
    /// Broker relay: reserves due rows for <paramref name="relayLock"/> so the consumer (on any instance) can claim them by id.
    /// A row whose message is lost is reserved again after the lock expires and re-published; the consumer ignores rows already handled.
    /// </summary>
    public Task<IReadOnlyList<AlertOutboxDTO>> ReserveForRelayAsync(TimeSpan relayLock, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return outbox.ClaimDueAsync(RelayPrefix + options.Value.InstanceId, now, now + relayLock, alertOptions.Value.OutboxBatchSize, ct);
    }

    /// <summary>
    /// Broker consumer: delivers one row by id (idempotent consumer - a duplicate or late message for a row that is already
    /// Sent / Failed / Skipped or held by another consumer does nothing).
    /// </summary>
    /// <returns><c>true</c> when this call handled the row.</returns>
    public async Task<bool> ProcessOneAsync(long outboxId, CancellationToken ct)
    {
        var o = alertOptions.Value;
        var me = options.Value.InstanceId;
        var now = time.GetUtcNow();
        var row = await outbox.ClaimByIdAsync(outboxId, me, RelayPrefix, now, now.AddSeconds(o.OutboxLockSeconds), ct).ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }
        var update = await DeliverAsync(row, me, o, ct).ConfigureAwait(false);
        return await outbox.CompleteAsync(update, ct).ConfigureAwait(false);
    }

    private async Task<AlertDeliveryUpdateDTO> DeliverAsync(AlertOutboxDTO row, string me, AlertOptions o, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        AlertDeliveryUpdateDTO Done(AlertSendStatus status, string? error, string? correlationId = null, AlertRecipientDTO? r = null) =>
            new(row.OutboxId, me, status, row.Attempts + (status == AlertSendStatus.Skipped ? 0 : 1), now, status == AlertSendStatus.Sent ? now : null,
                correlationId, error, r?.Name, r?.Channel, r?.AddressMasked);

        // Step 1: the recipient may have been disabled or deleted since the row was queued.
        var recipient = await recipients.GetAsync(row.RecipientId, ct).ConfigureAwait(false);
        if (recipient is null)
        {
            return Done(AlertSendStatus.Skipped, "Recipient was deleted.");
        }
        if (!recipient.Enabled)
        {
            return Done(AlertSendStatus.Skipped, "Recipient is disabled.", r: recipient);
        }
        if (!delivery.IsConfigured)
        {
            return Done(AlertSendStatus.Skipped, "MessageHub is not configured.", r: recipient);
        }

        // Step 2: decrypt the address (wrong or rotated key → permanent failure, the admin must re-enter it).
        string address;
        try
        {
            address = protector.Unprotect(recipient.RecipientId, recipient.AddressProtected);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return Done(AlertSendStatus.Failed, "Stored address cannot be decrypted; edit the recipient and enter the address again.", r: recipient);
        }

        // Step 3: send.
        var body = $"{row.Body}\n\n--\nEvent: {row.EventKey}\nOccurred: {row.OccurredUtc:yyyy-MM-dd HH:mm:ss} UTC";
        var result = await delivery.SendAsync(recipient.Channel, address, row.Title, body, ct).ConfigureAwait(false);
        metrics.AlertSent(result.Status == AlertSendStatus.Sent);

        // Step 4: decide the next state.
        if (result.Status == AlertSendStatus.Sent)
        {
            return Done(AlertSendStatus.Sent, null, result.CorrelationId, recipient);
        }
        if (result.CircuitOpen)
        {
            return new(row.OutboxId, me, AlertSendStatus.Pending, row.Attempts, now.AddSeconds(o.CircuitOpenDelaySeconds), null, null, result.Error,
                recipient.Name, recipient.Channel, recipient.AddressMasked);
        }
        var attempts = row.Attempts + 1;
        if (!result.Retryable || attempts >= o.MaxAttempts)
        {
            logger.LogWarning("Alert {OutboxId} to recipient {RecipientId} failed permanently after {Attempts} attempt(s): {Error}", row.OutboxId, row.RecipientId, attempts, result.Error);
            return Done(AlertSendStatus.Failed, result.Error, r: recipient);
        }
        return new(row.OutboxId, me, AlertSendStatus.Pending, attempts, now + Backoff(attempts, o), null, null, result.Error,
            recipient.Name, recipient.Channel, recipient.AddressMasked);
    }

    /// <summary>Retry delay: <c>min(max, base × 2^(attempts−1)) × jitter[0.8, 1.2]</c>.</summary>
    internal static TimeSpan Backoff(int attempts, AlertOptions o)
    {
        var raw = Math.Min(o.RetryMaxDelaySeconds, o.RetryBaseDelaySeconds * Math.Pow(2, Math.Max(0, attempts - 1)));
        var jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
        return TimeSpan.FromSeconds(raw * jitter);
    }
}

/// <summary>Validation and masking of recipient addresses per MessageHub channel.</summary>
public static partial class AlertAddress
{
    [GeneratedRegex(@"^[^@\s]{1,64}@[^@\s]{1,255}\.[^@\s]{2,}$")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"^\+[1-9]\d{6,14}$")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]{1,49}$")]
    private static partial Regex ChannelRegex();

    /// <summary>Checks channel name and address format.</summary>
    /// <exception cref="ValidationException">Invalid.</exception>
    public static void Validate(string channel, string address)
    {
        if (!ChannelRegex().IsMatch(channel))
        {
            throw new ValidationException("Channel name is invalid.");
        }
        if (string.IsNullOrWhiteSpace(address) || address.Length > 512 || address.Any(char.IsControl))
        {
            throw new ValidationException("Address is required (max 512 characters).");
        }
        var isEmailChannel = channel.Equals("Email", StringComparison.OrdinalIgnoreCase) || channel.Equals("TeamsEmail", StringComparison.OrdinalIgnoreCase);
        if (isEmailChannel && !EmailRegex().IsMatch(address))
        {
            throw new ValidationException("Address must be an e-mail address for this channel.");
        }
        if (channel.Equals("Sms", StringComparison.OrdinalIgnoreCase) && !PhoneRegex().IsMatch(address))
        {
            throw new ValidationException("Address must be a phone number in E.164 format, e.g. +66812345678.");
        }
    }

    /// <summary>Masks an address for display: e-mail keeps 2 characters and the domain, phone keeps the last 4 digits.</summary>
    public static string Mask(string address)
    {
        var at = address.IndexOf('@', StringComparison.Ordinal);
        if (at > 0)
        {
            return address[..Math.Min(2, at)] + new string('*', Math.Max(3, at - 2)) + address[at..];
        }
        if (address.StartsWith('+') && address.Length > 6)
        {
            return address[..3] + new string('*', address.Length - 7) + address[^4..];
        }
        return address.Length <= 8 ? new string('*', address.Length) : address[..3] + new string('*', address.Length - 6) + address[^3..];
    }
}
