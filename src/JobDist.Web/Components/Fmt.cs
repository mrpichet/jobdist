using System.Globalization;
using JobDist.Contracts;
using JobDist.Web.Localization;
using WebCommon.UI.Models;

namespace JobDist.Web.Components;

/// <summary>Display helpers: local times, durations and status texts in the current language.</summary>
public static class Fmt
{
    /// <summary>Local date and time (dd/MM/yyyy HH:mm:ss) or "-".</summary>
    public static string Time(DateTimeOffset? value) => value is { } v ? v.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture) : "-";

    /// <summary>Duration from milliseconds: 850 ms, 12.3 s, 4.5 min, 2.1 h.</summary>
    public static string Duration(double? ms) => ms switch
    {
        null => "-",
        < 1_000 => $"{ms.Value:N0} ms",
        < 60_000 => $"{ms.Value / 1_000:N1} s",
        < 3_600_000 => $"{ms.Value / 60_000:N1} min",
        _ => $"{ms.Value / 3_600_000:N1} h"
    };

    /// <summary>Percent with one decimal.</summary>
    public static string Percent(double value) => value.ToString("N1", CultureInfo.CurrentCulture) + " %";

    /// <summary>Short GUID (first 8 characters) for tables.</summary>
    public static string Short(Guid? id) => id is { } g ? g.ToString()[..8] : "-";

    public static string Status(JobStatus s) => AppText.Get($"JobStatus.{s}");
    public static string Status(WorkerStatus s) => AppText.Get($"WorkerStatus.{s}");
    public static string Outcome(AttemptOutcome o) => AppText.Get($"Outcome.{o}");
    public static string Event(AlertEventType e) => AppText.Get($"AlertEvent.{e}");
    public static string Severity(AlertSeverity s) => AppText.Get($"Severity.{s}");
    public static string Severity(string s) => AppText.Get($"Severity.{s}");
    public static string SendStatus(AlertSendStatus s) => AppText.Get($"SendStatus.{s}");
    public static string Level(QueueLevel l) => AppText.Get($"QueueLevel.{l}");

    public static AlertLevel Level(JobStatus s) => s switch
    {
        JobStatus.Succeeded => AlertLevel.Success,
        JobStatus.Failed => AlertLevel.Danger,
        JobStatus.CancelRequested or JobStatus.Cancelled => AlertLevel.Warning,
        _ => AlertLevel.Info
    };

    public static string Icon(JobStatus s) => s switch
    {
        JobStatus.Pending => "hourglass",
        JobStatus.Assigned or JobStatus.Running => "play",
        JobStatus.Succeeded => "check-circle",
        JobStatus.Failed => "x-circle",
        _ => "pause"
    };

    public static AlertLevel Level(WorkerStatus s) => s switch
    {
        WorkerStatus.Online => AlertLevel.Success,
        WorkerStatus.Draining => AlertLevel.Warning,
        WorkerStatus.Offline => AlertLevel.Danger,
        _ => AlertLevel.Info
    };

    public static string Icon(WorkerStatus s) => s switch
    {
        WorkerStatus.Online => "check-circle",
        WorkerStatus.Draining => "pause",
        WorkerStatus.Offline => "wifi-off",
        _ => "x"
    };

    public static AlertLevel Level(AlertSendStatus s) => s switch
    {
        AlertSendStatus.Sent => AlertLevel.Success,
        AlertSendStatus.Failed => AlertLevel.Danger,
        AlertSendStatus.Skipped => AlertLevel.Warning,
        _ => AlertLevel.Info
    };

    public static AlertLevel SeverityLevel(string severity) => severity switch
    {
        "Critical" => AlertLevel.Danger,
        "Warning" => AlertLevel.Warning,
        _ => AlertLevel.Info
    };

    public static AlertLevel RiskLevel(string level) => level switch
    {
        "Critical" or "High" => AlertLevel.Danger,
        "Medium" => AlertLevel.Warning,
        _ => AlertLevel.Success
    };

    /// <summary>Final status (no more changes).</summary>
    public static bool IsFinal(JobStatus s) => s is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled;
}
