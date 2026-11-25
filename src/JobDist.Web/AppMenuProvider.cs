using JobDist.Web.Localization;
using JobDist.Web.Security;
using WebCommon.UI.Navigation;

namespace JobDist.Web;

/// <summary>Side menu grouped by task: overview, jobs, workers, analysis, alerts.</summary>
public sealed class AppMenuProvider : IAppMenuProvider
{
    private static readonly IReadOnlyList<NavGroup> Menu =
    [
        new(AppText.Get("Menu.Overview"),
        [
            new NavItem { Title = AppText.Get("Menu.Dashboard"), Href = "", Icon = "home", MatchExact = true, Permission = AppPermissions.DashboardView }
        ]),
        new(AppText.Get("Menu.JobsGroup"),
        [
            new NavItem { Title = AppText.Get("Menu.Jobs"), Href = "jobs", Icon = "file-text", Permission = AppPermissions.JobView },
            new NavItem { Title = AppText.Get("Menu.SubmitJob"), Href = "jobs/new", Icon = "send", MatchExact = true, Permission = AppPermissions.JobCreate },
            new NavItem { Title = AppText.Get("Menu.ImportJobs"), Href = "jobs/import", Icon = "upload", MatchExact = true, Permission = AppPermissions.JobCreate }
        ]),
        new(AppText.Get("Menu.WorkersGroup"),
        [
            new NavItem { Title = AppText.Get("Menu.Workers"), Href = "workers", Icon = "monitor", Permission = AppPermissions.WorkerView }
        ]),
        new(AppText.Get("Menu.Analysis"),
        [
            new NavItem { Title = AppText.Get("Menu.Performance"), Href = "insights/performance", Icon = "bar-chart", Permission = AppPermissions.InsightView },
            new NavItem { Title = AppText.Get("Menu.Anomalies"), Href = "insights/anomalies", Icon = "alert-triangle", Permission = AppPermissions.InsightView },
            new NavItem { Title = AppText.Get("Menu.JobStatistics"), Href = "reports/job-statistics", Icon = "file-spreadsheet", Permission = AppPermissions.ReportView }
        ]),
        new(AppText.Get("Menu.AlertsGroup"),
        [
            new NavItem { Title = AppText.Get("Menu.AlertLogs"), Href = "alerts/logs", Icon = "inbox", Permission = AppPermissions.AlertView },
            new NavItem { Title = AppText.Get("Menu.Recipients"), Href = "alerts/recipients", Icon = "users", Permission = AppPermissions.AlertView },
            new NavItem { Title = AppText.Get("Menu.Thresholds"), Href = "alerts/queue-thresholds", Icon = "hourglass", Permission = AppPermissions.AlertView }
        ])
    ];

    /// <inheritdoc />
    public IReadOnlyList<NavGroup> GetMenu() => Menu;

    /// <summary>Main task button: submit a new job.</summary>
    public NavItem? PrimaryAction { get; } = new() { Title = AppText.Get("Menu.SubmitJob"), Href = "jobs/new", Icon = "plus", Permission = AppPermissions.JobCreate };
}
