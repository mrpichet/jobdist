using System.Collections.Concurrent;
using System.Text;
using JobDist.Contracts;
using JobDist.Core.Domain;
using JobDist.Core.Observability;
using JobDist.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobDist.Core.Services;

/// <summary>
/// Turns anomaly reports into admin alerts: filters by severity, suppresses repeats of the same anomaly within a cooldown
/// (no alert storms), groups everything of one scan into a single message and queues it in the alert outbox (event <c>AnomalyDetected</c>).
/// </summary>
public sealed class AlertService(AlertDispatchService dispatch, JobDistMetrics metrics, IOptions<AnomalyOptions> options, TimeProvider time, ILogger<AlertService> logger)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSent = new();

    /// <summary>
    /// Sends one alert for the new anomalies of a report.
    /// </summary>
    /// <param name="report">Scan result.</param>
    /// <param name="force">Ignore cooldown (manual "send now").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Anomalies included in the alert (empty when nothing was sent).</returns>
    public async Task<IReadOnlyList<AnomalyDTO>> NotifyAsync(AnomalyReportDTO report, bool force, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        var o = options.Value;
        var now = time.GetUtcNow();

        // Step 1: severity filter + cooldown per (kind, subject).
        var fresh = report.Anomalies
            .Where(a => a.Severity >= o.MinAlertSeverity)
            .Where(a => force || !_lastSent.TryGetValue(Key(a), out var last) || now - last >= TimeSpan.FromMinutes(o.AlertCooldownMinutes))
            .ToList();
        if (fresh.Count == 0)
        {
            return [];
        }

        // Step 2: compose one message.
        var top = fresh.Max(a => a.Severity);
        var body = new StringBuilder();
        body.AppendLine($"JobDist detected {fresh.Count} anomaly(ies) between {report.FromUtc:u} and {report.ToUtc:u}:");
        foreach (var a in fresh)
        {
            body.AppendLine($"- [{a.Severity}] {a.Kind} on {a.Subject}: {a.Description}");
        }
        var severity = (AlertSeverity)(int)top;
        var key = force
            ? $"AnomalyDetected:manual:{Guid.NewGuid():N}"
            : $"AnomalyDetected:{report.ToUtc.UtcDateTime:yyyyMMddHHmm}:{StableHash(string.Join('|', fresh.Select(Key).Order(StringComparer.Ordinal)))}";
        var alert = new AlertEventDTO(key, AlertEventType.AnomalyDetected, severity, "cluster:anomalies",
            $"[JobDist][{top}] {fresh.Count} anomaly(ies): {fresh[0].Kind}", body.ToString(), now);

        // Step 3: queue; remember cooldown only on success (a failed insert is retried on the next scan).
        try
        {
            await dispatch.PublishAsync(alert, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Queueing admin alert failed.");
            return [];
        }
        foreach (var a in fresh)
        {
            _lastSent[Key(a)] = now;
        }
        return fresh;
    }

    private static string Key(AnomalyDTO a) => $"{a.Kind}|{a.Subject}";

    // Same on every instance and restart (string.GetHashCode is randomised per process).
    private static string StableHash(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)), 0, 6);
}
