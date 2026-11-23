using System.Collections.Concurrent;
using System.Text;
using JobDist.Core.Domain;
using JobDist.Core.Observability;
using JobDist.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobDist.Core.Services;

/// <summary>Alert for administrators.</summary>
/// <param name="Title">Short subject.</param>
/// <param name="Body">Plain-text details.</param>
/// <param name="Severity">Highest severity included.</param>
public sealed record AdminAlertDTO(string Title, string Body, AnomalySeverity Severity);

/// <summary>Delivers admin alerts (implemented in Infrastructure by the mrpichet/messagehub API client).</summary>
public interface IAlertSender
{
    /// <summary>Sends an alert.</summary>
    /// <returns><c>true</c> when the alert was accepted by the delivery service.</returns>
    Task<bool> SendAsync(AdminAlertDTO alert, CancellationToken ct);
}

/// <summary>
/// Turns anomaly reports into admin alerts: filters by severity, suppresses repeats of the same anomaly within a cooldown
/// (no alert storms), groups everything of one scan into a single message and sends it through <see cref="IAlertSender"/>.
/// </summary>
public sealed class AlertService(IAlertSender sender, JobDistMetrics metrics, IOptions<AnomalyOptions> options, TimeProvider time, ILogger<AlertService> logger)
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
        var alert = new AdminAlertDTO($"[JobDist][{top}] {fresh.Count} anomaly(ies): {fresh[0].Kind}", body.ToString(), top);

        // Step 3: send; remember cooldown only on success (a failed send is retried on the next scan).
        bool ok;
        try
        {
            ok = await sender.SendAsync(alert, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Sending admin alert failed.");
            ok = false;
        }
        metrics.AlertSent(ok);
        if (!ok)
        {
            return [];
        }
        foreach (var a in fresh)
        {
            _lastSent[Key(a)] = now;
        }
        return fresh;
    }

    private static string Key(AnomalyDTO a) => $"{a.Kind}|{a.Subject}";
}
