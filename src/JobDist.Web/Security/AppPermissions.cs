using WebCommon.UI.Models;

namespace JobDist.Web.Security;

/// <summary>
/// Permission ids of Application <c>jobdist</c> in AuthServer (claim <c>permissions</c>). Must match
/// <c>JobDist.Server.Security.Permissions</c>; the server checks every call again, the web only hides menus and buttons.
/// </summary>
public static class AppPermissions
{
    public const string DashboardView = "jobdist.dashboard.view";
    public const string JobView = "jobdist.job.view";
    public const string JobCreate = "jobdist.job.create";
    public const string JobEdit = "jobdist.job.edit";
    public const string JobCancel = "jobdist.job.cancel";
    public const string JobExport = "jobdist.job.export";
    public const string WorkerView = "jobdist.worker.view";
    public const string WorkerDrain = "jobdist.worker.drain";
    public const string WorkerUnregister = "jobdist.worker.unregister";
    public const string InsightView = "jobdist.insight.view";
    public const string AlertSend = "jobdist.alert.send";
    public const string AlertView = "jobdist.alert.view";
    public const string AlertManage = "jobdist.alert.manage";
    public const string QueueManage = "jobdist.queue.manage";
    public const string ReportView = "jobdist.report.view";
    public const string AgentUse = "jobdist.agent.use";

    /// <summary>Buttons of the job list (S02).</summary>
    public static readonly ListPermissions Jobs = new() { Create = JobCreate, View = JobView, Edit = JobEdit, Delete = JobCancel, Export = JobExport };

    /// <summary>Buttons of the worker list (S07); "delete" = unregister.</summary>
    public static readonly ListPermissions Workers = new() { View = WorkerView, Delete = WorkerUnregister, Export = WorkerView };

    /// <summary>Buttons of the alert recipient list (X2).</summary>
    public static readonly ListPermissions Recipients = new() { Create = AlertManage, View = AlertView, Edit = AlertManage, Delete = AlertManage, Export = AlertView };

    /// <summary>Buttons of the queue threshold list (X6).</summary>
    public static readonly ListPermissions Thresholds = new() { Create = QueueManage, View = AlertView, Edit = QueueManage, Delete = QueueManage, Export = AlertView };

    /// <summary>Buttons of the alert history (X1), read only.</summary>
    public static readonly ListPermissions AlertLogs = new() { View = AlertView, Export = AlertView };
}
