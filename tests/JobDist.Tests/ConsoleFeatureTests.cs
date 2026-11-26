using System.Security.Claims;
using Common.AspNetCore.Authentication;
using JobDist.Contracts;
using JobDist.Core.DAO;
using JobDist.Core.Domain;
using JobDist.Core.Services;
using JobDist.Server.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace JobDist.Tests;

/// <summary>Queue limits, alert outbox delivery, idempotency records, the statistics report and the console access policy.</summary>
[TestClass]
public sealed class ConsoleFeatureTests
{
    // ------------------------------------------------------------------------------------------------ queue admission

    [TestMethod]
    public async Task Queue_over_limit_rejects_new_jobs_and_queues_one_alert()
    {
        using var host = new TestHost(new() { ["Alerts:PendingCacheMilliseconds"] = "0" });
        await host.Get<QueueThresholdService>().CreateAsync(new QueueThresholdRequest("demo", 2, 50), "tester", CancellationToken.None);

        await host.SubmitAsync();
        await host.SubmitAsync();
        var ex = await Assert.ThrowsExactlyAsync<QueueFullException>(() => host.SubmitAsync());
        Assert.AreEqual("demo", ex.JobType);
        Assert.AreEqual(2, ex.Pending);
        Assert.AreEqual(2, ex.MaxPending);
        await Assert.ThrowsExactlyAsync<QueueFullException>(() => host.SubmitAsync());

        // Other job types are not limited by a per-type threshold.
        await host.SubmitAsync(type: "other");

        // Two rejections in the same cooldown window → one alert event.
        var alerts = await Logs(host, AlertEventType.QueueOverLimit);
        Assert.HasCount(1, alerts);
        StringAssert.Contains(alerts[0].Body, "rejected");
    }

    [TestMethod]
    public async Task Batch_that_would_overflow_the_queue_is_rejected_as_a_whole()
    {
        using var host = new TestHost(new() { ["Alerts:PendingCacheMilliseconds"] = "0" });
        await host.Get<QueueThresholdService>().CreateAsync(new QueueThresholdRequest("*", 3, 50), "tester", CancellationToken.None);
        var batch = Enumerable.Range(0, 4).Select(_ => new SubmitJobRequest("demo", "{}")).ToList();

        await Assert.ThrowsExactlyAsync<QueueFullException>(() => host.Jobs.SubmitBatchAsync(new SubmitJobBatchRequest(batch), "tester", CancellationToken.None));
        var page = await host.Jobs.QueryAsync(new JobQueryDTO(), CancellationToken.None);
        Assert.AreEqual(0, page.Total, "Nothing of a rejected batch is stored.");
    }

    [TestMethod]
    public async Task Periodic_check_raises_near_limit_alert_and_reports_usage()
    {
        using var host = new TestHost(new() { ["Alerts:PendingCacheMilliseconds"] = "0" });
        var thresholds = host.Get<QueueThresholdService>();
        await thresholds.CreateAsync(new QueueThresholdRequest("demo", 10, 50), "tester", CancellationToken.None);
        for (var i = 0; i < 6; i++)
        {
            await host.SubmitAsync();
        }

        Assert.AreEqual(1, await thresholds.CheckAndAlertAsync(CancellationToken.None));
        var status = (await thresholds.GetStatusAsync(CancellationToken.None)).Single();
        Assert.AreEqual(QueueLevel.NearLimit, status.Level);
        Assert.AreEqual(60.0, status.UsagePercent);
        Assert.HasCount(1, await Logs(host, AlertEventType.QueueNearLimit));
    }

    // ------------------------------------------------------------------------------------------------ alert outbox

    private sealed class FakeDelivery : IAlertDeliveryClient
    {
        public Queue<AlertDeliveryResult> Results { get; } = new();
        public List<(string Channel, string Address, string Subject)> Sent { get; } = [];
        public bool IsConfigured => true;

        public Task<AlertDeliveryResult> SendAsync(string channel, string address, string subject, string body, CancellationToken ct)
        {
            Sent.Add((channel, address, subject));
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : new AlertDeliveryResult(AlertSendStatus.Sent, false, "m-1", null));
        }

        public Task<IReadOnlyList<string>> GetChannelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["Email"]);
    }

    private static TestHost HostWithDelivery(FakeDelivery delivery) =>
        new(new() { ["Alerts:PendingCacheMilliseconds"] = "0" }, s => s.AddSingleton<IAlertDeliveryClient>(delivery));

    private static AlertEventDTO Event(string key, DateTimeOffset at) =>
        new(key, AlertEventType.QueueOverLimit, AlertSeverity.Critical, "queue:demo", "Queue over limit", "body", at);

    [TestMethod]
    public async Task Outbox_publish_is_idempotent_and_delivers_with_decrypted_address()
    {
        var delivery = new FakeDelivery();
        using var host = HostWithDelivery(delivery);
        await host.Get<AlertRecipientService>().CreateAsync(
            new AlertRecipientRequest("Ops", "Email", "ops@example.com", [AlertEventType.QueueOverLimit], AlertSeverity.Warning), "tester", CancellationToken.None);
        var dispatch = host.Get<AlertDispatchService>();

        Assert.AreEqual(1, await dispatch.PublishAsync(Event("k1", TestHost.Start), CancellationToken.None));
        Assert.AreEqual(0, await dispatch.PublishAsync(Event("k1", TestHost.Start), CancellationToken.None), "Same event key is queued once.");
        Assert.AreEqual(1, await host.Get<AlertOutboxProcessor>().ProcessOnceAsync(CancellationToken.None));

        Assert.HasCount(1, delivery.Sent);
        Assert.AreEqual("ops@example.com", delivery.Sent[0].Address);
        var log = (await Logs(host, AlertEventType.QueueOverLimit)).Single();
        Assert.AreEqual(AlertSendStatus.Sent, (AlertSendStatus)log.Status);
        Assert.AreNotEqual("ops@example.com", log.AddressMasked, "History keeps only the masked address.");
    }

    [TestMethod]
    public async Task Outbox_retries_transient_errors_with_backoff_and_fails_permanent_ones_at_once()
    {
        var delivery = new FakeDelivery();
        delivery.Results.Enqueue(new AlertDeliveryResult(AlertSendStatus.Failed, true, null, "timeout"));
        delivery.Results.Enqueue(new AlertDeliveryResult(AlertSendStatus.Failed, false, null, "400 bad address"));
        using var host = HostWithDelivery(delivery);
        await host.Get<AlertRecipientService>().CreateAsync(
            new AlertRecipientRequest("Ops", "Email", "ops@example.com", [AlertEventType.QueueOverLimit], AlertSeverity.Warning), "tester", CancellationToken.None);
        await host.Get<AlertDispatchService>().PublishAsync(Event("k2", TestHost.Start), CancellationToken.None);
        var processor = host.Get<AlertOutboxProcessor>();

        Assert.AreEqual(1, await processor.ProcessOnceAsync(CancellationToken.None));
        Assert.AreEqual(0, await processor.ProcessOnceAsync(CancellationToken.None), "Not due again before the backoff delay.");
        var row = (await Logs(host, AlertEventType.QueueOverLimit)).Single();
        Assert.AreEqual(AlertSendStatus.Pending, (AlertSendStatus)row.Status);
        Assert.AreEqual(1, row.Attempts);

        host.Time.Advance(TimeSpan.FromHours(1));
        Assert.AreEqual(1, await processor.ProcessOnceAsync(CancellationToken.None));
        row = (await Logs(host, AlertEventType.QueueOverLimit)).Single();
        Assert.AreEqual(AlertSendStatus.Failed, (AlertSendStatus)row.Status);
        Assert.AreEqual(2, row.Attempts);
        Assert.HasCount(2, delivery.Sent);
    }

    // ------------------------------------------------------------------------------------------------ idempotency records

    [TestMethod]
    public async Task Idempotency_record_replays_same_body_rejects_other_body_and_expires()
    {
        using var host = new TestHost();
        var svc = host.Get<IdempotencyService>();
        var hash = IdempotencyService.Hash("{\"a\":1}"u8);

        Assert.IsNull(await svc.FindAsync("ApiKey:ci", "POST /api/v1/jobs", "key-1", hash, CancellationToken.None));
        await svc.SaveAsync("ApiKey:ci", "POST /api/v1/jobs", "key-1", hash, 200, "{\"jobId\":\"x\"}", CancellationToken.None);
        await svc.SaveAsync("ApiKey:ci", "POST /api/v1/jobs", "key-1", hash, 200, "{\"jobId\":\"y\"}", CancellationToken.None);

        var stored = await svc.FindAsync("ApiKey:ci", "POST /api/v1/jobs", "key-1", hash, CancellationToken.None);
        Assert.AreEqual("{\"jobId\":\"x\"}", stored?.ResponseBody, "First response wins.");
        await Assert.ThrowsExactlyAsync<IdempotencyMismatchException>(() =>
            svc.FindAsync("ApiKey:ci", "POST /api/v1/jobs", "key-1", IdempotencyService.Hash("{\"a\":2}"u8), CancellationToken.None));
        Assert.IsNull(await svc.FindAsync("ApiKey:other", "POST /api/v1/jobs", "key-1", hash, CancellationToken.None), "Keys are per caller.");

        host.Time.Advance(IdempotencyService.Lifetime + TimeSpan.FromMinutes(1));
        Assert.IsNull(await svc.FindAsync("ApiKey:ci", "POST /api/v1/jobs", "key-1", hash, CancellationToken.None));
    }

    // ------------------------------------------------------------------------------------------------ report

    [TestMethod]
    public async Task Statistics_report_counts_outcomes_by_job_type()
    {
        using var host = new TestHost();
        var worker = await host.RegisterAsync("w1");
        var ok = await host.SubmitAsync();
        var bad = await host.SubmitAsync(maxAttempts: 1);
        await host.ScheduleAsync();
        var poll = await host.PollAsync(worker.WorkerId);
        Assert.HasCount(2, poll.Assignments);
        foreach (var a in poll.Assignments)
        {
            await host.Execution.StartedAsync(a.WorkerId, a.JobId, a.AttemptId, CancellationToken.None);
        }
        await host.CompleteAsync(poll.Assignments.Single(a => a.JobId == ok.JobId), AttemptOutcome.Succeeded, 200);
        await host.CompleteAsync(poll.Assignments.Single(a => a.JobId == bad.JobId), AttemptOutcome.Failed, 400, retryable: false);

        var day = DateOnly.FromDateTime(TestHost.Start.UtcDateTime);
        var report = await host.Get<JobReportService>().GetAsync(day, day, null, null, "jobType", "UTC", CancellationToken.None);

        var row = report.Rows.Single();
        Assert.AreEqual("demo", row.JobType);
        Assert.AreEqual(2, row.Submitted);
        Assert.AreEqual(1, row.Succeeded);
        Assert.AreEqual(1, row.Failed);
        Assert.AreEqual(50.0, row.SuccessRate, 0.01);
        Assert.AreEqual(200.0, row.AvgDurationMs, 1.0, "Durations count successful attempts only.");
        Assert.AreEqual(200.0, row.P95DurationMs, 200 * 0.06, "P95 comes from a histogram with ~5% buckets.");
        Assert.AreEqual(2, report.Totals.Submitted);
    }

    [TestMethod]
    public async Task Statistics_report_validates_range_group_and_time_zone()
    {
        using var host = new TestHost();
        var report = host.Get<JobReportService>();
        var day = DateOnly.FromDateTime(TestHost.Start.UtcDateTime);

        await Assert.ThrowsExactlyAsync<ValidationException>(() => report.GetAsync(day, day.AddDays(-1), null, null, null, null, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => report.GetAsync(day, day, null, null, "month", null, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => report.GetAsync(day, day, null, null, null, "Mars/Base", CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => report.GetAsync(day.AddDays(-400), day, null, null, null, null, CancellationToken.None));
    }

    // ------------------------------------------------------------------------------------------------ access policy

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "JobDist.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static async Task<bool> AllowedAsync(ClaimsIdentity identity, string permission, string roleSet, string environment = "Development")
    {
        var requirement = new JobDistAccessRequirement(permission.Split('|'), roleSet);
        var handler = new JobDistAccessHandler(new Env(environment), Options.Create(new AuthServerOptions { AdminRole = "admin" }));
        var context = new AuthorizationHandlerContext([requirement], new ClaimsPrincipal(identity), null);
        await handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static ClaimsIdentity ApiKey(string role) =>
        new([new Claim(ClaimTypes.Name, "ci"), new Claim(ClaimTypes.Role, role)], ApiKeyAuthenticationHandler.SchemeName);

    private static ClaimsIdentity User(params Claim[] claims) =>
        new([new Claim(ClaimTypes.Name, "somchai"), .. claims], AuthServerAuthenticationHandler.SchemeName);

    [TestMethod]
    public async Task Access_policy_accepts_permission_or_api_key_role()
    {
        // AuthServer user: needs the permission (or one of several), or the AuthServer admin role.
        Assert.IsTrue(await AllowedAsync(User(new Claim(AuthServerTokenValidator.PermissionClaim, Permissions.JobView)), Permissions.JobView, RoleSets.Read));
        Assert.IsTrue(await AllowedAsync(User(new Claim(AuthServerTokenValidator.PermissionClaim, Permissions.JobCreate)), Permissions.JobView + "|" + Permissions.JobCreate, RoleSets.Read));
        Assert.IsFalse(await AllowedAsync(User(new Claim(AuthServerTokenValidator.PermissionClaim, Permissions.JobView)), Permissions.JobCancel, RoleSets.Submit));
        Assert.IsTrue(await AllowedAsync(User(new Claim(ClaimTypes.Role, "admin")), Permissions.QueueManage, RoleSets.None));
        Assert.IsTrue(await AllowedAsync(User(new Claim(AuthServerTokenValidator.ScopeClaim, Permissions.ReportView)), Permissions.ReportView, RoleSets.None));

        // API key: needs a role from the role set; a permission name in a role claim does not help.
        Assert.IsTrue(await AllowedAsync(ApiKey(Roles.Submitter), Permissions.JobCreate, RoleSets.Submit));
        Assert.IsFalse(await AllowedAsync(ApiKey(Roles.Viewer), Permissions.JobCreate, RoleSets.Submit));
        Assert.IsFalse(await AllowedAsync(ApiKey(Roles.Admin), Permissions.AgentUse, RoleSets.None));
    }

    [TestMethod]
    public async Task Console_read_endpoints_need_admin_key_in_production()
    {
        Assert.IsTrue(await AllowedAsync(ApiKey(Roles.Viewer), Permissions.ReportView, RoleSets.ConsoleRead, "Development"));
        Assert.IsFalse(await AllowedAsync(ApiKey(Roles.Viewer), Permissions.ReportView, RoleSets.ConsoleRead, "Production"));
        Assert.IsTrue(await AllowedAsync(ApiKey(Roles.Admin), Permissions.ReportView, RoleSets.ConsoleRead, "Production"));
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private static async Task<IReadOnlyList<AlertOutboxDTO>> Logs(TestHost host, AlertEventType type)
    {
        var (items, _) = await host.Get<IAlertOutboxDAO>().QueryAsync(
            new AlertOutboxQueryDTO(TestHost.Start.AddDays(-1), TestHost.Start.AddDays(2), type, null, null, 0, 100));
        return items;
    }
}

/// <summary>Version-change support: broker message types and API client tagging.</summary>
[TestClass]
public sealed class VersioningTests
{
    [TestMethod]
    public void Alert_queue_accepts_known_and_legacy_types_only()
    {
        Assert.IsTrue(JobDist.Infrastructure.Messaging.AlertMessageTypes.IsSupported("jobdist.alert.v1"));
        Assert.IsTrue(JobDist.Infrastructure.Messaging.AlertMessageTypes.IsSupported(null), "Messages published before types existed are v1.");
        Assert.IsFalse(JobDist.Infrastructure.Messaging.AlertMessageTypes.IsSupported("jobdist.alert.v2"));
        Assert.AreEqual("jobdist.alert.v1", JobDist.Infrastructure.Messaging.AlertMessageTypes.Current);
    }

    [TestMethod]
    public void Api_client_tag_is_sdk_token_web_or_other()
    {
        Assert.AreEqual("JobDist.Client/2.0.0", ApiUsageMiddleware.Client("JobDist.Client/2.0.0"));
        Assert.AreEqual("JobDist.Worker.Sdk/1.4.2", ApiUsageMiddleware.Client("JobDist.Worker.Sdk/1.4.2 dotnet/10"));
        Assert.AreEqual("web", ApiUsageMiddleware.Client("Mozilla/5.0 (X11; Linux x86_64) Chrome/140"));
        Assert.AreEqual("other", ApiUsageMiddleware.Client("curl/8.5"));
        Assert.AreEqual("other", ApiUsageMiddleware.Client(""));
    }
}

/// <summary>JobDist as a client of AuthServer and MessageHub: versioned paths, problem codes, deprecation headers.</summary>
[TestClass]
public sealed class UpstreamContractTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class CountingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public int Warnings { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                Warnings++;
            }
        }
    }

    private static (JobDist.Infrastructure.Alerts.MessageHubDeliveryClient Client, StubHandler Handler, CountingLogger<JobDist.Infrastructure.Http.UpstreamVersionMonitor> MonitorLog, Microsoft.Extensions.Time.Testing.FakeTimeProvider Time)
        Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(TestHost.Start);
        var monitorLog = new CountingLogger<JobDist.Infrastructure.Http.UpstreamVersionMonitor>();
        var options = new JobDist.Infrastructure.Alerts.MessageHubAlertOptions { Enabled = true, BaseUrl = "https://messagehub.example.com", ApiKey = "test-key" };
        var client = new JobDist.Infrastructure.Alerts.MessageHubDeliveryClient(new StubFactory(handler), new Monitor<JobDist.Infrastructure.Alerts.MessageHubAlertOptions>(options), time,
            new JobDist.Infrastructure.Http.UpstreamVersionMonitor(time, monitorLog), Microsoft.Extensions.Logging.Abstractions.NullLogger<JobDist.Infrastructure.Alerts.MessageHubDeliveryClient>.Instance);
        return (client, handler, monitorLog, time);
    }

    private static HttpResponseMessage Json(System.Net.HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    [TestMethod]
    public async Task MessageHub_send_uses_v1_path_and_maps_delivery_status()
    {
        var (client, handler, _, _) = Create(_ => Json(System.Net.HttpStatusCode.OK, "{\"correlationId\":\"c-1\",\"status\":\"Sent\",\"unknownField\":1}"));

        var result = await client.SendAsync("Email", "ops@example.com", "subject", "body", CancellationToken.None);

        Assert.AreEqual(AlertSendStatus.Sent, result.Status);
        Assert.AreEqual("c-1", result.CorrelationId);
        Assert.AreEqual("https://messagehub.example.com/api/v1/messages", handler.Requests.Single().RequestUri!.ToString());
    }

    [TestMethod]
    public async Task MessageHub_problem_code_is_kept_and_4xx_is_not_retried()
    {
        var (client, _, _, _) = Create(_ => Json(System.Net.HttpStatusCode.BadRequest, "{\"status\":400,\"title\":\"x\",\"code\":\"VALIDATION_FAILED\"}"));

        var result = await client.SendAsync("Email", "ops@example.com", "subject", "body", CancellationToken.None);

        Assert.AreEqual(AlertSendStatus.Failed, result.Status);
        Assert.IsFalse(result.Retryable);
        StringAssert.Contains(result.Error, "VALIDATION_FAILED");
    }

    [TestMethod]
    public async Task Deprecation_headers_are_logged_once_a_day()
    {
        var (client, _, monitorLog, time) = Create(_ =>
        {
            var r = Json(System.Net.HttpStatusCode.OK, "{\"status\":\"Sent\"}");
            r.Headers.TryAddWithoutValidation("Deprecation", "@1764028800");
            r.Headers.TryAddWithoutValidation("Sunset", "Mon, 31 May 2027 00:00:00 GMT");
            r.Headers.TryAddWithoutValidation("Link", "</api/v2/messages>; rel=\"successor-version\"");
            return r;
        });

        await client.SendAsync("Email", "a@example.com", "s", "b", CancellationToken.None);
        await client.SendAsync("Email", "a@example.com", "s", "b", CancellationToken.None);
        Assert.AreEqual(1, monitorLog.Warnings);
        time.Advance(TimeSpan.FromDays(1));
        await client.SendAsync("Email", "a@example.com", "s", "b", CancellationToken.None);
        Assert.AreEqual(2, monitorLog.Warnings);
    }
}
