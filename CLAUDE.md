# CLAUDE.md: JobDist

JobDist is a distributed job scheduler on .NET 10. One business layer is exposed three ways that another project can call:

1. **REST API** at `/api/v1/*` (alias `/api/*`; default dev URL `http://localhost:8080`).
2. **MCP server**: Streamable HTTP at `POST /mcp`, or stdio via `src/JobDist.McpServer`.
3. **.NET SDKs**: `JobDist.Client` (submit and track jobs) and `JobDist.Worker.Sdk` (run jobs as a worker).

This file is a summary of what can be called, with parameters and return values. The full generated reference
(every contract field with its doc comment) is [`docs/API_EXPORT.md`](docs/API_EXPORT.md), machine-readable copy
[`docs/api-export.json`](docs/api-export.json). Regenerate both with `dotnet run --project tools/JobDist.ApiExport -- docs`
after changing the API, then update this file if an endpoint, tool or SDK member was added, removed or changed.

## Calling conventions

- **Versioning**: routes are `api/v1/...`. The unversioned `api/...` alias is the same v1 endpoint (header `api-version: 1.0`
  or `?api-version=1.0` also work, unknown version = 400). New clients use `api/v1/`. A breaking change ships as v2 and replaces v1.
- **Auth, machines**: header `X-Api-Key: <key>`. Roles: `Admin`, `Submitter`, `Viewer`, `Worker`, `AiAgent`.
  Dev keys (from `src/JobDist.Server/appsettings.Development.json`, never use in production):
  `dev-admin-key-change-me`, `dev-submitter-key-change-me`, `dev-worker-key-change-me`, `dev-ai-agent-key-change-me`.
- **Auth, people (web console)**: `Authorization: Bearer <access token>` from AuthServer (Application `jobdist`, client
  `jobdist-web`), accepted when `AuthServer:Authority` is set. The web signs in through the same-origin proxy
  `/api/auth/*` and `/api/account/*`, which forwards to AuthServer.
- **Access rule** ("permission OR role"): each endpoint names a permission and an API-key role set. A Bearer token passes with
  the permission (or the AuthServer `admin` role); an API key passes with a role from the set.
  Role sets: Read = Viewer/Submitter/Admin; Submit = Submitter/Admin; Admin = Admin; WorkerOrAdmin = Worker/Admin;
  ConsoleRead = Admin in Production, otherwise Viewer/Submitter/Admin; ConsoleWrite = Admin; None = no API key (people only).
  Worker = Worker; AiAgent = AiAgent/Admin.
- **Permissions** (Application `jobdist` in AuthServer): `jobdist.dashboard.view`, `jobdist.job.view`, `jobdist.job.create`,
  `jobdist.job.edit`, `jobdist.job.cancel`, `jobdist.job.export`, `jobdist.worker.view`, `jobdist.worker.drain`,
  `jobdist.worker.unregister`, `jobdist.insight.view`, `jobdist.alert.send`, `jobdist.alert.view`, `jobdist.alert.manage`,
  `jobdist.queue.manage`, `jobdist.report.view`, `jobdist.agent.use`.
- **Idempotency**: `POST /api/jobs` and `/api/jobs/batch` dedupe on the body field `idempotencyKey`. The console create endpoints
  marked "idem" below accept header `Idempotency-Key` (1-100 chars, kept 24 h per caller + route): a retry with the same body
  replays the first response with `Idempotent-Replayed: true`; the same key with a different body gets 422.
- **Worker calls** on `/api/workers/{workerId}/...` also need `X-Worker-Token`, returned once by `POST /api/workers/register`.
- **JSON**: camelCase properties, enums as strings (`"Pending"`), times ISO-8601 UTC, ids are GUIDs.
- **Errors**: RFC 7807 problem details. 400 validation, 401/403 auth, 404 not found, 409 conflict (also optimistic concurrency:
  `version` does not match), 422 idempotency key reused with another body, 429 rate limited or queue full, 500 unexpected.
  Queue full: 429 with `Retry-After: 60` and extensions `error: "queue_full"`, `jobType`, `pending`, `maxPending`; a batch is
  rejected as a whole.
- **Rate limits** (per key): global 10,000 req/s (burst 20,000); submit 2,500/s (burst 5,000); register 120/min.
- **Limits**: payload and result max 1 MB, error text max 4,000 chars, batch max 5,000 jobs, priority 0-100 (default 50).

## REST endpoints

`?` = optional. Defaults in parentheses. Return types are listed under [Return types](#return-types) and [Contracts](#contracts).

### Jobs (`JobsController`)

| Verb | Route | Policy | Parameters | Returns |
|---|---|---|---|---|
| POST | `/api/jobs` | Submit | body `SubmitJobRequest` | `SubmitJobResponse` |
| POST | `/api/jobs/batch` | Submit | body `SubmitJobBatchRequest` (`{ jobs: SubmitJobRequest[] }`, 1-5,000) | `SubmitJobResponse[]` |
| GET | `/api/jobs/{jobId}` | Read | route `jobId` | `JobStatusResponse` |
| GET | `/api/jobs` | Read | query `status?: JobStatus`, `jobType?`, `workerId?: Guid`, `skip` (0), `take` (100) | `PagedResponse<JobStatusResponse>` (newest first) |
| GET | `/api/jobs/{jobId}/attempts` | Read | route `jobId` | `JobAttemptResponse[]` |
| PATCH | `/api/jobs/{jobId}/priority` | Submit | body `{ priority: int }` (0-100) | `JobStatusResponse` |
| POST | `/api/jobs/{jobId}/cancel` | Submit | body `{ reason?: string }` | `JobStatusResponse` (status `CancelRequested` while a worker holds it, then `Cancelled`) |
| GET | `/api/jobs/{jobId}/risk` | Read | route `jobId` | `RiskAssessmentDTO` |
| GET | `/api/jobs/risk` | Read | query `jobType`, `targetWorkerId?`, `strictTarget` (false), `timeoutSeconds?`, `sensitive` (false) | `RiskAssessmentDTO` (what-if, creates nothing) |
| GET | `/api/jobs/types` | Read | none | `JobTypeInfoResponse[]` (types of online workers plus types with pending jobs) |

Permissions: GET `jobdist.job.view`; POST `jobdist.job.create`; PATCH priority `jobdist.job.edit`; cancel `jobdist.job.cancel`;
`GET /api/jobs/risk` accepts `jobdist.job.create` or `jobdist.job.view`. Submit and batch return 429 `queue_full` over a queue threshold.

### Workers (`WorkersController`)

Worker-protocol endpoints (used by `JobDist.Worker.Sdk`; you normally do not call them by hand):

| Verb | Route | Policy | Parameters | Returns |
|---|---|---|---|---|
| POST | `/api/workers/register` | Worker | body `RegisterWorkerRequest` | `RegisterWorkerResponse` |
| DELETE | `/api/workers/{workerId}` | WorkerOrAdmin, `jobdist.worker.unregister` | route `workerId` | 204; held jobs are re-distributed |
| POST | `/api/workers/{workerId}/heartbeat` | Worker + token | body `HeartbeatRequest` | `HeartbeatResponse` |
| POST | `/api/workers/{workerId}/poll` | Worker + token | body `PollRequest` (`maxJobs` 1-256, `waitSeconds` 0-30) | `PollResponse` (long-poll) |
| POST | `/api/workers/{workerId}/jobs/{jobId}/started` | Worker + token | body `{ attemptId: Guid }` | `{ cancelRequested: bool }` |
| POST | `/api/workers/{workerId}/jobs/{jobId}/completed` | Worker + token | body `JobCompletedRequest` (max 2 MB) | `JobStatusResponse`; 409 if already re-distributed |

Operator endpoints:

| Verb | Route | Policy | Parameters | Returns |
|---|---|---|---|---|
| GET | `/api/workers` | Read | query `includeUnregistered` (false) | `WorkerResponse[]` |
| GET | `/api/workers/{workerId}` | Read | route `workerId` | `WorkerResponse` |
| POST | `/api/workers/{workerId}/drain` | Admin, `jobdist.worker.drain` | route `workerId` | 202 Accepted; no new jobs, current ones finish |
| GET | `/api/workers/{workerId}/risk` | Read | route `workerId` | `RiskAssessmentDTO` |

### Admin and analytics (`AdminController`)

| Verb | Route | Policy | Parameters | Returns |
|---|---|---|---|---|
| GET | `/api/admin/summary` | Read | none | `ClusterSummaryDTO` |
| GET | `/api/admin/performance` | Read | query `jobType?` | `PerformanceStatDTO[]` |
| GET | `/api/admin/recommendations` | Read | query `jobType`, `top` (5) | `WorkerRecommendationDTO[]` |
| GET | `/api/admin/anomalies` | Read | query `windowMinutes` (15) | `AnomalyReportDTO` |
| POST | `/api/admin/anomalies/alert` | Admin | query `windowMinutes` (15) | `AnomalyDTO[]`; also sends an alert through MessageHub now |
| GET | `/api/keys` | anonymous | none | `ServerKeysResponse` |

Permissions: summary `jobdist.dashboard.view`; performance, recommendations, anomalies `jobdist.insight.view`; alert `jobdist.alert.send`.
Worker reads use `jobdist.worker.view`.

### Alerts and queue limits (`AlertsController`, `QueueThresholdsController`)

Alerts go through a database outbox (one row per event and recipient, unique, so a repeat is queued once) and are delivered
to MessageHub with retry, back-off and a circuit breaker. With `Messaging:Transport=RabbitMq` the outbox rows travel through RabbitMQ.

| Verb | Route | Policy / permission | Parameters | Returns |
|---|---|---|---|---|
| GET | `/api/alerts/recipients` | ConsoleRead, `jobdist.alert.view` | none | `AlertRecipientResponse[]` |
| GET | `/api/alerts/recipients/{id}` | ConsoleRead, `jobdist.alert.view` | route `id` | `AlertRecipientResponse` |
| POST | `/api/alerts/recipients` (idem) | ConsoleWrite, `jobdist.alert.manage` | body `AlertRecipientRequest` | 201 `AlertRecipientResponse` |
| PUT | `/api/alerts/recipients/{id}` | ConsoleWrite, `jobdist.alert.manage` | body `AlertRecipientRequest` (`version` required; empty `address` keeps the stored one) | `AlertRecipientResponse`; 409 on version mismatch |
| DELETE | `/api/alerts/recipients/{id}` | ConsoleWrite, `jobdist.alert.manage` | query `version` | 204 |
| POST | `/api/alerts/recipients/{id}/test` (idem) | ConsoleWrite, `jobdist.alert.manage` | route `id` | `AlertSendResultResponse` |
| GET | `/api/alerts/channels` | ConsoleRead, `jobdist.alert.view` | none | `string[]` (channels enabled on MessageHub) |
| GET | `/api/alerts/event-types` | ConsoleRead, `jobdist.alert.view` | none | `string[]` (`AlertEventType` names) |
| GET | `/api/alerts/logs` | ConsoleRead, `jobdist.alert.view` | query `fromUtc?`, `toUtc?`, `eventType?`, `status?`, `recipientId?`, `skip` (0), `take` (100, max 500) | `PagedResponse<AlertLogResponse>` (newest first) |
| GET | `/api/alerts/queue-thresholds` | ConsoleRead, `jobdist.alert.view` | none | `QueueThresholdResponse[]` |
| GET | `/api/alerts/queue-thresholds/{id}` | ConsoleRead, `jobdist.alert.view` | route `id` | `QueueThresholdResponse` |
| POST | `/api/alerts/queue-thresholds` (idem) | ConsoleWrite, `jobdist.queue.manage` | body `QueueThresholdRequest` | 201 `QueueThresholdResponse`; 409 if the job type already has one |
| PUT | `/api/alerts/queue-thresholds/{id}` | ConsoleWrite, `jobdist.queue.manage` | body `QueueThresholdRequest` (`version` required) | `QueueThresholdResponse` |
| DELETE | `/api/alerts/queue-thresholds/{id}` | ConsoleWrite, `jobdist.queue.manage` | query `version` | 204 |
| GET | `/api/alerts/queue-status` | ConsoleRead, `jobdist.dashboard.view` | none | `QueueStatusResponse[]` |

Queue rule: a threshold (`jobType` or `*` for all) has `maxPending` and `warnPercent`. At or above the warning level a
`QueueNearLimit` alert is sent; when a submission would exceed `maxPending`, a `QueueOverLimit` alert is sent and the job
(or the whole batch) is rejected with 429 `queue_full`. Each queue alert repeats at most once per `Alerts:QueueAlertCooldownMinutes`.

### Reports, AI assistant, browser logs

| Verb | Route | Policy / permission | Parameters | Returns |
|---|---|---|---|---|
| GET | `/api/reports/job-statistics` | ConsoleRead, `jobdist.report.view` | query `fromDate`, `toDate` (dates, at most `RetentionDays`), `jobType?`, `workerId?`, `groupBy` (`day`, `jobType`, `worker`, comma separated; default `day`), `timeZone` (IANA, default `Asia/Bangkok`) | `JobStatisticsReportResponse` |
| GET | `/api/agent/status` | anonymous | none | `AgentStatusResponse` (whether the AI assistant is configured) |
| POST | `/api/agent/chat` | people only, `jobdist.agent.use` | body `AgentChatRequest` | `AgentChatResponse` (read-only tools, answers with the caller's permissions) |
| POST | `/api/client-logs` | any signed-in caller | body `ClientLogBatchRequest` (1-50 entries) | 202; written to the server log (and LogServer) with the user id |

Report metrics: success rate = succeeded / (succeeded + failed) × 100; average and P95 duration use successful attempts only.
P95 comes from a log histogram with about 5% bucket width, so it is accurate to about ±5%.

### Infrastructure

| Verb | Route | Auth | Returns |
|---|---|---|---|
| POST | `/mcp` | policy AiAgent | MCP JSON-RPC (Streamable HTTP, stateless) |
| GET | `/health` | anonymous | readiness text (database reachable) |
| GET | `/alive` | anonymous | liveness text |

### Example

```bash
curl -s -X POST http://localhost:8080/api/jobs \
  -H "X-Api-Key: dev-submitter-key-change-me" -H "Content-Type: application/json" \
  -d '{"jobType":"image.resize","payload":"{\"url\":\"https://example.com/a.png\"}","priority":80}'
# {"jobId":"...","status":"Pending","duplicate":false}
```

## MCP tools

Source: `src/JobDist.Mcp/JobDistAiTools.cs`. Each public method with `[Description]` becomes a tool named in snake_case
(through `Microsoft.Extensions.AI` `AIFunctionFactory`). Connect with an `AiAgent` (or `Admin`) API key to `POST /mcp`,
or run the stdio host:

```json
{
  "mcpServers": {
    "jobdist": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/jobdist/src/JobDist.McpServer"],
      "env": { "Database__Provider": "PostgreSql", "Database__ConnectionString": "Host=...;Database=jobdist;Username=...;Password=..." }
    }
  }
}
```

The stdio host reads the same database as the servers (`Database:Provider` = `PostgreSql` or `SqlServer`).
Side-effect tools are only listed when `Mcp:AllowSideEffects=true` (default false).

### Read-only tools

| Tool | Parameters | Returns |
|---|---|---|
| `get_job_status` | `jobId: string (GUID)` | `JobStatusResponse` |
| `list_jobs` | `status?: JobStatus`, `jobType?: string`, `take: int = 50` (1-200) | `PagedResponse<JobStatusResponse>` |
| `get_job_history` | `jobId` | `JobAttemptResponse[]` |
| `list_workers` | none | `WorkerResponse[]` |
| `get_cluster_summary` | none | `ClusterSummaryDTO` |
| `get_worker_performance` | `jobType?: string` | `PerformanceStatDTO[]` |
| `recommend_workers` | `jobType: string`, `top: int = 5` (1-100) | `WorkerRecommendationDTO[]` |
| `assess_job_risk` | `jobId` | `RiskAssessmentDTO` |
| `assess_submission_risk` | `jobType: string`, `targetWorkerId?: GUID`, `strictTarget: bool = false`, `timeoutSeconds?: int`, `sensitive: bool = false` | `RiskAssessmentDTO` (creates nothing) |
| `assess_worker_risk` | `workerId` | `RiskAssessmentDTO` |
| `detect_anomalies` | `windowMinutes: int = 15` (5-1440) | `AnomalyReportDTO` |
| `get_queue_status` | none | `QueueStatusResponse[]` |
| `list_alert_logs` | `hours: int = 24` (1-720), `eventType?: AlertEventType` | `AlertLogResponse[]` (max 200) |
| `get_job_statistics` | `fromDate`, `toDate` (yyyy-MM-dd), `jobType?`, `groupBy = "day"` (time zone Asia/Bangkok) | `JobStatisticsReportResponse` |

### Side-effect tools (need `Mcp:AllowSideEffects=true`)

| Tool | Parameters | Returns |
|---|---|---|
| `submit_job` | `jobType: string`, `payload?: string (JSON text)`, `priority: int = 50`, `targetWorkerId?: GUID`, `strictTarget: bool = false`, `idempotencyKey?: string` | `SubmitJobResponse` |
| `cancel_job` | `jobId`, `reason?: string` | `JobStatusResponse` |
| `change_job_priority` | `jobId`, `priority: int` (0-100) | `JobStatusResponse` |

Use `assess_submission_risk` before `submit_job`.

### In-process (no MCP)

`JobDist.Mcp.JobDistAiFunctions`:

- `services.AddJobDistAiTools()`: registers `JobDistAiTools` and options section `Mcp`.
- `JobDistAiFunctions.Create(bool includeSideEffects = true)` returns `IReadOnlyList<AIFunction>`; pass it to any `IChatClient` as tools.
- `mcpBuilder.WithJobDistTools(bool allowSideEffects)` adds the tools to an MCP server builder.

## .NET SDKs

### Submit jobs: `JobDist.Client`

```csharp
services.AddJobDistClient(configuration.GetSection("JobDistClient")); // or AddJobDistClient(o => { ... })
// inject IJobDistClient
```

`JobDistClientOptions` (section `JobDistClient`): `ServerUrls: List<string>` (default `["http://localhost:8080"]`, fails over
on network errors and 5xx), `ApiKey: string` (Submitter role), `Timeout: TimeSpan` (30 s).

| `IJobDistClient` method | Returns |
|---|---|
| `SubmitAsync(SubmitJobRequest request, CancellationToken ct = default)` | `Task<SubmitJobResponse>`; generates an idempotency key when missing |
| `SubmitBatchAsync(IReadOnlyList<SubmitJobRequest> jobs, CancellationToken ct = default)` | `Task<IReadOnlyList<SubmitJobResponse>>` (max 5,000) |
| `GetAsync(Guid jobId, CancellationToken ct = default)` | `Task<JobStatusResponse>` |
| `ListAsync(JobStatus? status = null, string? jobType = null, int skip = 0, int take = 100, CancellationToken ct = default)` | `Task<PagedResponse<JobStatusResponse>>` |
| `GetAttemptsAsync(Guid jobId, CancellationToken ct = default)` | `Task<IReadOnlyList<JobAttemptResponse>>` |
| `ChangePriorityAsync(Guid jobId, int priority, CancellationToken ct = default)` | `Task<JobStatusResponse>` |
| `CancelAsync(Guid jobId, string? reason = null, CancellationToken ct = default)` | `Task<JobStatusResponse>` |
| `WaitForCompletionAsync(Guid jobId, TimeSpan? pollInterval = null, CancellationToken ct = default)` | `Task<JobStatusResponse>` once `Succeeded`/`Failed`/`Cancelled` (poll default 1 s) |

Errors throw `JobDistApiException` (`StatusCode: HttpStatusCode`, `Message`).

### Run jobs: `JobDist.Worker.Sdk`

```csharp
services.AddJobDistWorker(configuration)      // section "JobDistWorker"
        .AddJobHandler<ImageResizeHandler>();  // one per job type

public sealed class ImageResizeHandler : IJobHandler
{
    public string JobType => "image.resize";
    public int MaxConcurrency => 4;
    public double SpeedFactor => 1.0;
    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
        => JobResult.Ok("{\"done\":true}");
}
```

- `IJobHandler`: `string JobType`, `int MaxConcurrency`, `double SpeedFactor`, `Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)`.
  Resolved per execution from a DI scope, so scoped dependencies are fine. Make handlers idempotent (`Attempt > 1` is a retry).
- `JobContext(Guid JobId, Guid AttemptId, string JobType, int Attempt, int Priority, TimeSpan Timeout, string? Payload, bool WasEncrypted)`; `Payload` is already decrypted.
- `JobResult(bool Success, string? Output = null, string? Error = null, bool Retryable = true)`, helpers
  `JobResult.Ok(string? output = null)` and `JobResult.Fail(string error, bool retryable = true)`.
  Throw `NonRetryableJobException(message)` to fail without retry.
- `JobDistWorkerOptions` (section `JobDistWorker`): `ServerUrls`, `ApiKey` (Worker role), `WorkerName` (machine name),
  `MaxTotalConcurrency` (CPU cores), `ConcurrencyOverrides: Dictionary<string,int>`, `BenchmarkScore` (0 = measure),
  `GpuCount`, `Tags`, `PollWaitSeconds` (0-30), `RequireSignatures`, `PinnedServerKeyId`, `RequestTimeout`,
  `ReportRetention`, `UnregisterOnShutdown`, `ShutdownGracePeriod`.
- `JobDistWorkerService.MeasureBenchmark()` returns `double` (relative machine speed, 1.0 = baseline).

## Return types

From `src/JobDist.Core/Domain/AnalyticsDTO.cs`:

- `RiskAssessmentDTO { subject: string, score: double (0-100), level: string, factors: RiskFactorDTO[], recommendation: string }`
- `RiskFactorDTO { factor: string, points: double, explanation: string }`
- `ClusterSummaryDTO { workersOnline, workersDraining, workersOffline, totalCapacity, activeJobs: int, jobsByStatus: {string:int}, pendingByType: {string:int}, leaderInstance?: string, thisInstanceIsLeader: bool, generatedUtc }`
- `PerformanceStatDTO { workerId, jobType, samples, successes, failures: int, meanDurationMs, stdDevDurationMs: double, lastUpdatedUtc }`
- `WorkerRecommendationDTO { workerId, workerName, score, loadRatio, expectedDurationMs, successProbability: double, reason: string }`
- `AnomalyReportDTO { fromUtc, toUtc, anomalies: AnomalyDTO[] }`
- `AnomalyDTO { kind: AnomalyKind, severity: Info|Warning|Critical, subject, description: string, observedValue, expectedValue, score: double, detectedUtc }`
- `AnomalyKind`: `SubmissionSpike`, `HighFailureRate`, `SlowExecution`, `WorkerFlapping`, `QueueStarvation`, `FailoverStorm`, `SuspiciousWorkerActivity`

## Contracts

From `src/JobDist.Contracts` (field docs in `docs/API_EXPORT.md`):

- `SubmitJobRequest { jobType: string, payload?: string (≤1 MB), priority: int = 50, targetWorkerId?: Guid, strictTarget: bool, maxAttempts?: int (1-20), timeoutSeconds?: int (1-86400), runAtUtc?: DateTimeOffset, idempotencyKey?: string, sensitive: bool, tags?: string[] (≤10) }`
- `SubmitJobResponse { jobId: Guid, status: JobStatus, duplicate: bool }`
- `JobStatusResponse { jobId, jobType, status, priority, targetWorkerId?, strictTarget, assignedWorkerId?, attempt, maxAttempts, timeoutSeconds, createdUtc, runAtUtc?, startedUtc?, completedUtc?, lastDurationMs?, result?, lastError?, cancelReason?, sensitive, tags }`
- `JobAttemptResponse { attemptId, jobId, attempt, workerId, workerName, assignedUtc, startedUtc?, finishedUtc?, durationMs?, queueWaitMs, outcome: AttemptOutcome, error?, schedulerScore }`
- `PagedResponse<T> { items: T[], total, skip, take }`
- `WorkerResponse { workerId, name, status: WorkerStatus, capabilities: WorkerCapabilityRequest[], spec: WorkerSpecRequest, maxTotalConcurrency, activeJobs, cpuUsagePercent, memoryUsagePercent, registeredUtc, lastHeartbeatUtc, offlineCount, supportsPqc, version, tags }`
- `WorkerCapabilityRequest { jobType, maxConcurrency (1-1024), speedFactor }`; `WorkerSpecRequest { cpuCores, memoryMb, gpuCount, benchmarkScore, os }`
- `RegisterWorkerRequest { name, capabilities (1-200), spec, maxTotalConcurrency (1-4096), ecdhPublicKey?, mlKemPublicKey?, version?, tags? }`
- `RegisterWorkerResponse { workerId, workerToken, heartbeatIntervalSeconds, offlineAfterSeconds, serverKeys: ServerKeysResponse }`
- `HeartbeatRequest { runningJobIds: Guid[], cpuUsagePercent, memoryUsagePercent }`; `HeartbeatResponse { status: WorkerStatus, commands: WorkerCommandResponse[] }`
- `PollResponse { assignments: JobAssignmentResponse[], commands: WorkerCommandResponse[] }`
- `JobAssignmentResponse { jobId, attemptId, workerId, jobType, attempt, priority, timeoutSeconds, issuedUtc, payload?, encryptedPayload?, payloadSha256, signature: SignatureEnvelope }`
- `JobCompletedRequest { attemptId, outcome: AttemptOutcome, durationMs, result?, error?, retryable }`
- `WorkerCommandResponse { commandId, kind: CancelJob|Drain, workerId, jobId?, reason?, issuedUtc, signature }`
- `ServerKeysResponse { keyId, mlDsaPublicKey?, ecdsaPublicKey }`; `SignatureEnvelope { algorithm, keyId, pqcSignature?, classicSignature }`
- `JobTypeInfoResponse { jobType, onlineWorkers, totalConcurrency, pending }`
- `AlertRecipientRequest { name (≤200), channel (≤50, e.g. Email), address? (≤512, stored encrypted), eventTypes: AlertEventType[] (≥1), minSeverity = Warning, enabled = true, version? }`
- `AlertRecipientResponse { recipientId, name, channel, addressMasked, eventTypes, minSeverity, enabled, createdUtc, updatedUtc, updatedBy?, version }`
- `AlertSendResultResponse { status: AlertSendStatus, correlationId?, error? }`
- `AlertLogResponse { alertLogId, createdUtc, eventType, severity, title, subject, recipientId?, recipientName?, channel, addressMasked, status, attempts, sentUtc?, correlationId?, error?, instanceId? }`
- `QueueThresholdRequest { jobType (≤100, `*` = all), maxPending (1-10,000,000), warnPercent = 80 (1-99), enabled = true, version? }`
- `QueueThresholdResponse { thresholdId, jobType, maxPending, warnPercent, enabled, updatedBy?, updatedUtc, version }`
- `QueueStatusResponse { thresholdId, jobType, pending, maxPending, usagePercent, level: QueueLevel, enabled }`
- `JobStatisticsReportResponse { fromDate, toDate, timeZone, groupBy: string[], rows: JobStatisticsRowResponse[], totals: JobStatisticsRowResponse, generatedUtc }`
- `JobStatisticsRowResponse { day?, jobType?, workerId?, workerName?, submitted, succeeded, failed, cancelled, successRate (%), avgAttempts, avgDurationMs, p95DurationMs, avgQueueWaitMs, throughputPerHour }`
- `AgentChatRequest { messages: { role: user|assistant, content (≤4,000) }[] (1-20), language = "th" (th|en), page? }`; `AgentChatResponse { reply }`; `AgentStatusResponse { enabled }`
- `ClientLogBatchRequest { entries: ClientLogEntry[] (1-50) }`; `ClientLogEntry { entryId, timestampUtc, level, category, message (≤4,000), page?, exception?, correlationId? }`

Enums:

- `JobStatus`: `Pending`, `Assigned`, `Running`, `Succeeded`, `Failed`, `CancelRequested`, `Cancelled` (final: Succeeded, Failed, Cancelled)
- `AttemptOutcome`: `InProgress`, `Succeeded`, `Failed`, `TimedOut`, `Cancelled`, `WorkerLost`, `Released`
- `WorkerStatus`: `Online`, `Draining`, `Offline`, `Unregistered`
- `AlertEventType`: `AnomalyDetected`, `WorkerRegistered`, `WorkerUnregistered`, `WorkerLost`, `LeaderChanged`, `QueueNearLimit`, `QueueOverLimit`, `Test`
- `AlertSeverity`: `Info`, `Warning`, `Critical`; `AlertSendStatus`: `Pending`, `Sent`, `Failed`, `Skipped`; `QueueLevel`: `Normal`, `NearLimit`, `OverLimit`

## Working in this repo

- First clone: `git submodule update --init --recursive` (`external/common`, `external/webcommon`).
- Build/test: `dotnet build JobDist.slnx`, `dotnet test tests/JobDist.Tests` (MSTest). Run: `dotnet run --project src/JobDist.Server`
  (REST + MCP + web console `src/JobDist.Web` on `http://localhost:8080`), sample worker `samples/JobDist.SampleWorker`, AI sample `samples/JobDist.AiClientSample`.
- Web console: Blazor WebAssembly on `WebCommon.UI` (DevExpress Blazor). Texts in `src/JobDist.Web/Localization/AppText.resx` (Thai) and
  `AppText.en.resx`. Create, view and edit use form pages, not popups. Without a DevExpress license the build shows DX1000/DX1001 warnings.
- Logging: `Common.Logging` sends server logs to LogServer (`Logging:LogServer`); browser logs come in through `POST /api/client-logs`.
- Naming: domain `*DTO`, repository interfaces `I*DAO`, implementations `*ADO`, business logic `*Service`.
- Both SQL Server and PostgreSQL must keep working (`Database:Provider`); there is also an in-memory provider for tests.
- The README is in Thai and has more cURL and SDK examples.
