# JobDist: ระบบกระจายงาน (Distributed Job Scheduler) บน .NET 10

JobDist รับงาน (job) จากระบบอื่น แล้วกระจายไปให้เครื่อง worker ที่เหมาะที่สุด โดยดูจากความสามารถ สเปกเครื่อง ภาระงานปัจจุบัน และสิ่งที่ระบบเรียนรู้จากประวัติ (ML)
business logic ชุดเดียวกันเปิดให้ใช้ 2 ทางพร้อมกัน:

- **REST Web API** (ASP.NET Core controllers, ใช้ไลบรารี built-in ของ Microsoft) ที่ `/api/*`
- **MCP Server** ที่สร้างด้วย **Microsoft.Extensions.AI** (`AIFunctionFactory` → `McpServerTool`) ที่ `/mcp` (HTTP) หรือแบบ stdio

และมี **Blazor WebAssembly UI (DevExpress 26.1)** สำหรับดูสถานะ เปลี่ยน priority และยกเลิกงาน

> สรุป API ทั้งหมดสำหรับ Claude / AI ในโปรเจกต์อื่น: [`docs/API_EXPORT.md`](docs/API_EXPORT.md) (และ [`docs/api-export.json`](docs/api-export.json))

---

## สารบัญ

1. [ภาพรวมสถาปัตยกรรม](#1-ภาพรวมสถาปัตยกรรม)
2. [โครงสร้างโปรเจกต์และการตั้งชื่อ](#2-โครงสร้างโปรเจกต์และการตั้งชื่อ)
3. [เริ่มใช้งานเร็ว (Development)](#3-เริ่มใช้งานเร็ว-development)
4. [ตัวอย่าง cURL](#4-ตัวอย่าง-curl)
5. [AI / MCP ผ่าน Microsoft.Extensions.AI](#5-ai--mcp-ผ่าน-microsoftextensionsai)
6. [ให้โปรเจกต์อื่นส่งงาน (Client SDK)](#6-ให้โปรเจกต์อื่นส่งงาน-client-sdk)
7. [ให้โปรเจกต์อื่นทำ worker (Worker SDK)](#7-ให้โปรเจกต์อื่นทำ-worker-worker-sdk)
8. [แนวคิดหลักทีละเรื่อง](#8-แนวคิดหลักทีละเรื่อง)
9. [ความปลอดภัย](#9-ความปลอดภัย)
10. [Docker / Production](#10-docker--production)
11. [การตั้งค่า](#11-การตั้งค่า)
12. [Tests](#12-tests)
13. [ตารางเทียบความต้องการกับสิ่งที่ทำ](#13-ตารางเทียบความต้องการกับสิ่งที่ทำ)

---

## 1. ภาพรวมสถาปัตยกรรม

```
   ระบบอื่น (Client SDK / cURL)        AI agent (MCP)          ผู้ดูแล (Blazor UI)
              │  X-Api-Key                  │ X-Api-Key              │ X-Api-Key
              ▼                             ▼                        ▼
   ┌───────────────────────── JobDist.Server × N (active-active) ─────────────────────────┐
   │  REST controllers  │  MCP /mcp (stateless)  │  Blazor WASM static files              │
   │──────────────────────────── business services (JobDist.Core) ────────────────────────│
   │  JobService · SchedulerService · DispatchService · FailoverService · RetryPolicy      │
   │  PerformanceLearningService (ML) · AnomalyDetectionService · JobRiskService · PQC     │
   │──────────────── DAO (interface) → ADO (Dapper: SQL Server | PostgreSQL) ─────────────│
   └──────────────────────────────────────────┬───────────────────────────────────────────┘
          leader lease (เลือก leader 1 ตัว)     │           แจ้งเตือน admin → mrpichet/messagehub
                                               ▼
                                  SQL Server หรือ PostgreSQL
              ▲  register / heartbeat / long-poll / started / completed (pull model)
              │  X-Api-Key + X-Worker-Token, งานถูกเซ็นแบบ ML-DSA + ECDSA
   ┌──────────┴─────────┐   ┌────────────────────┐   ┌────────────────────┐
   │ Worker (SDK) #1    │   │ Worker #2          │   │ Worker #n          │  ← เครือข่ายไม่เสถียรได้
   └────────────────────┘   └────────────────────┘   └────────────────────┘
        metrics ทั้ง server และ worker → OpenTelemetry (OTLP) → .NET Aspire Dashboard
```

**ลำดับการทำงานของงานหนึ่งชิ้น**

1. ระบบอื่นส่งงานมาที่ `POST /api/jobs` แล้วงานถูกบันทึกเป็น `Pending` (ถ้าเป็นงาน `sensitive` payload จะถูกเข้ารหัส AES-256-GCM ก่อนลง DB)
2. **leader** รอบ scheduler (ทุก 250 ms เมื่อว่าง) เลือกงานที่พร้อม เรียงตาม priority + aging แล้วจับคู่กับ worker ที่ดีที่สุด จากนั้นบันทึกเป็น `Assigned` แบบมีเงื่อนไข (optimistic concurrency)
3. worker ที่ long-poll อยู่ถูกปลุกทันที แล้วได้รับงานที่ **เซ็นด้วย hybrid PQC signature** (ถ้าเป็นงาน sensitive จะได้ payload ที่เข้ารหัสด้วย ML-KEM + ECDH สำหรับ worker ตัวนั้นเท่านั้น)
4. worker ตรวจลายเซ็น ถอดรหัส ตรวจ hash แล้วรายงาน `started` จากนั้นรัน handler และรายงาน `completed`
5. server บันทึกประวัติ (attempt) พร้อมเวลา แล้วป้อนให้ ML เรียนรู้เพื่อใช้กระจายงานครั้งต่อไป
6. ถ้า worker หายไป (heartbeat ขาดเกิน `OfflineAfterSeconds`) งานของ worker นั้นจะถูกคืนเข้าคิวและส่งให้ worker อื่น

---

## 2. โครงสร้างโปรเจกต์และการตั้งชื่อ

| โปรเจกต์ | หน้าที่ |
|---|---|
| `src/JobDist.Contracts` | DTO สาธารณะ (request/response), enum, ตัวตรวจลายเซ็น, PQC envelope. ใช้เฉพาะ BCL จึงใช้ร่วมกับ SDK ได้ |
| `src/JobDist.Core` | **business logic แบบ pure C#**: `*DTO` (domain), `I*DAO` (interface ของ repository), `*Service` (scheduler, ML, retry, anomaly, risk, PQC) |
| `src/JobDist.Infrastructure` | `*ADO` (Dapper) สำหรับ SQL Server / PostgreSQL / InMemory, DB initializer, background workers, ตัวส่ง alert ไป MessageHub |
| `src/JobDist.Mcp` | AI tools (Microsoft.Extensions.AI `AIFunctionFactory`) แล้วห่อเป็น MCP tools |
| `src/JobDist.Server` | REST controllers + MCP over HTTP + โฮสต์ UI, auth, rate limit, security headers |
| `src/JobDist.McpServer` | MCP server แบบ stdio สำหรับ Claude Desktop / Claude Code / VS Code |
| `src/JobDist.UI` | Blazor WebAssembly + DevExpress 26.1 (แยกโปรเจกต์) |
| `src/JobDist.ServiceDefaults` | OpenTelemetry (metrics/traces/logs → Aspire) + health checks |
| `src/JobDist.Client` | **SDK สำหรับโปรเจกต์ที่ส่งงาน** (`IJobDistClient`) |
| `src/JobDist.Worker.Sdk` | **SDK สำหรับโปรเจกต์ที่ทำ worker** (`IJobHandler`) |
| `samples/JobDist.SampleWorker` | worker ตัวอย่าง 4 แบบ (hash/primes แบบ Parallel/resize แบบ SIMD/report ที่ fail แบบสุ่ม) |
| `samples/JobDist.AiClientSample` | ตัวอย่าง AI เรียก MCP tools และส่ง tools ให้ `IChatClient` |
| `tests/JobDist.Tests` | MSTest 53 tests |
| `tools/JobDist.ApiExport` | สร้าง `docs/API_EXPORT.md` จาก assembly จริง + XML docs |
| `db/sqlserver`, `db/postgres` | schema (idempotent, รันอัตโนมัติตอน start เมื่อ `AutoMigrate=true`) |

**กฎการตั้งชื่อ** (ตามที่กำหนด): domain class ลงท้าย `DTO` (เช่น `JobDTO`), interface repository ลงท้าย `DAO` (เช่น `IJobDAO`), implementation ลงท้าย `ADO` (เช่น `JobADO`, `InMemoryJobADO`), class business ลงท้าย `Service` (เช่น `SchedulerService`)
ทุก function มี XML comment บอกหน้าที่ พารามิเตอร์ และค่าที่คืน และมี comment `Step 1, Step 2, ...` ในตัว function

---

## 3. เริ่มใช้งานเร็ว (Development)

ต้องมี .NET SDK 10 เท่านั้น (โหมด Development ใช้ฐานข้อมูล InMemory จึงไม่ต้องติดตั้ง DB)

```bash
# 1) server (REST + MCP + UI) ที่ http://localhost:8080
dotnet run --project src/JobDist.Server

# 2) worker ตัวอย่าง (อีก terminal)
DOTNET_ENVIRONMENT=Development dotnet run --project samples/JobDist.SampleWorker

# 3) เปิด UI: http://localhost:8080  แล้วใส่ API key: dev-admin-key-change-me
```

API key สำหรับ Development (อยู่ใน `appsettings.Development.json` ในรูป SHA-256 เท่านั้น):

| Key | Role |
|---|---|
| `dev-admin-key-change-me` | Admin, Submitter, Viewer, AiAgent |
| `dev-submitter-key-change-me` | Submitter |
| `dev-worker-key-change-me` | Worker |
| `dev-ai-agent-key-change-me` | AiAgent |

> ถ้าจะใช้ SQL Server หรือ PostgreSQL ตอน dev ให้ตั้ง `Database:Provider` เป็น `SqlServer` หรือ `PostgreSql` และ `Database:ConnectionString` (ผ่าน user-secrets หรือ environment variable)

---

## 4. ตัวอย่าง cURL

```bash
BASE=http://localhost:8080
ADMIN=dev-admin-key-change-me
SUB=dev-submitter-key-change-me
```

**ส่งงาน** (priority 0-100, ยิ่งมากยิ่งได้ก่อน, `idempotencyKey` กันงานซ้ำเวลา retry)

```bash
curl -s -X POST $BASE/api/jobs -H "X-Api-Key: $SUB" -H "Content-Type: application/json" -d '{
  "jobType": "image.resize",
  "payload": "{\"width\":4000,\"height\":3000,\"targetWidth\":800}",
  "priority": 80,
  "maxAttempts": 5,
  "timeoutSeconds": 120,
  "idempotencyKey": "order-1001-resize",
  "tags": ["web", "thumbnail"]
}'
# {"jobId":"...","status":"Pending","duplicate":false}
```

**งานลับ** (payload เข้ารหัสทั้งตอนเก็บและตอนส่งไป worker), **ระบุ worker** และ **ตั้งเวลา**

```bash
curl -s -X POST $BASE/api/jobs -H "X-Api-Key: $SUB" -H "Content-Type: application/json" \
  -d '{"jobType":"hash.sha256","payload":"{\"text\":\"secret\"}","sensitive":true}'

curl -s -X POST $BASE/api/jobs -H "X-Api-Key: $SUB" -H "Content-Type: application/json" \
  -d '{"jobType":"math.primes","payload":"{\"limit\":5000000}","targetWorkerId":"<WORKER_ID>","strictTarget":true}'

curl -s -X POST $BASE/api/jobs -H "X-Api-Key: $SUB" -H "Content-Type: application/json" \
  -d '{"jobType":"report.generate","payload":"{\"seconds\":5}","runAtUtc":"2026-12-01T01:00:00Z"}'
```

`strictTarget=false` หมายถึง "อยากให้รันที่ worker นี้" (ได้คะแนนพิเศษ) แต่ถ้า worker นั้นไม่ว่างก็ไปเครื่องอื่นได้

**ส่งทีละหลายงาน** (สูงสุด 5,000 งาน, bulk insert)

```bash
curl -s -X POST $BASE/api/jobs/batch -H "X-Api-Key: $SUB" -H "Content-Type: application/json" \
  -d '{"jobs":[{"jobType":"hash.sha256","payload":"{\"text\":\"a\"}"},{"jobType":"hash.sha256","payload":"{\"text\":\"b\"}"}]}'
```

**ดูสถานะ, รายการ, ประวัติการกระจาย**

```bash
curl -s $BASE/api/jobs/<JOB_ID> -H "X-Api-Key: $ADMIN"
curl -s "$BASE/api/jobs?status=Running&jobType=image.resize&skip=0&take=50" -H "X-Api-Key: $ADMIN"
curl -s $BASE/api/jobs/<JOB_ID>/attempts -H "X-Api-Key: $ADMIN"     # worker, เวลารอคิว, เวลาทำงาน, ผล, คะแนน scheduler
```

**เปลี่ยน priority และยกเลิก** (ถ้า worker กำลังทำอยู่ server จะส่งคำสั่ง cancel ที่เซ็นแล้วไปให้ worker)

```bash
curl -s -X PATCH $BASE/api/jobs/<JOB_ID>/priority -H "X-Api-Key: $SUB" -H "Content-Type: application/json" -d '{"priority":100}'
curl -s -X POST  $BASE/api/jobs/<JOB_ID>/cancel   -H "X-Api-Key: $SUB" -H "Content-Type: application/json" -d '{"reason":"ลูกค้ายกเลิก"}'
```

**ความเสี่ยง (risk 0-100), คำแนะนำ worker, anomaly, ภาพรวม cluster**

```bash
curl -s "$BASE/api/jobs/risk?jobType=image.resize&sensitive=true" -H "X-Api-Key: $ADMIN"   # what-if ก่อนส่งงาน
curl -s $BASE/api/jobs/<JOB_ID>/risk -H "X-Api-Key: $ADMIN"
curl -s $BASE/api/workers/<WORKER_ID>/risk -H "X-Api-Key: $ADMIN"
curl -s "$BASE/api/admin/recommendations?jobType=image.resize&top=5" -H "X-Api-Key: $ADMIN"
curl -s "$BASE/api/admin/anomalies?windowMinutes=60" -H "X-Api-Key: $ADMIN"
curl -s -X POST "$BASE/api/admin/anomalies/alert?windowMinutes=15" -H "X-Api-Key: $ADMIN"   # ส่งแจ้งเตือนทันที
curl -s $BASE/api/admin/summary -H "X-Api-Key: $ADMIN"
curl -s $BASE/api/admin/performance -H "X-Api-Key: $ADMIN"                                 # สิ่งที่ ML เรียนรู้
```

**Worker protocol** (ปกติใช้ Worker SDK แทน แต่แสดงไว้ให้เข้าใจ)

```bash
WK=dev-worker-key-change-me
# ลงทะเบียน: ได้ workerId + workerToken (ได้ครั้งเดียว server เก็บแค่ hash) + public key ของ server
curl -s -X POST $BASE/api/workers/register -H "X-Api-Key: $WK" -H "Content-Type: application/json" -d '{
  "name":"gpu-node-01",
  "capabilities":[{"jobType":"image.resize","maxConcurrency":4,"speedFactor":1.5},{"jobType":"math.primes","maxConcurrency":2}],
  "spec":{"cpuCores":16,"memoryMb":65536,"gpuCount":1,"benchmarkScore":2.4,"os":"Linux"},
  "maxTotalConcurrency":6,
  "tags":["zone=a"]
}'
WID=<workerId>; TOK=<workerToken>
curl -s -X POST $BASE/api/workers/$WID/heartbeat -H "X-Api-Key: $WK" -H "X-Worker-Token: $TOK" -H "Content-Type: application/json" -d '{"runningJobIds":[],"cpuUsagePercent":12,"memoryUsagePercent":40}'
curl -s -X POST $BASE/api/workers/$WID/poll      -H "X-Api-Key: $WK" -H "X-Worker-Token: $TOK" -H "Content-Type: application/json" -d '{"maxJobs":4,"waitSeconds":20}'
curl -s -X POST $BASE/api/workers/$WID/jobs/<JOB_ID>/started   -H "X-Api-Key: $WK" -H "X-Worker-Token: $TOK" -H "Content-Type: application/json" -d '{"attemptId":"<ATTEMPT_ID>"}'
curl -s -X POST $BASE/api/workers/$WID/jobs/<JOB_ID>/completed -H "X-Api-Key: $WK" -H "X-Worker-Token: $TOK" -H "Content-Type: application/json" \
  -d '{"attemptId":"<ATTEMPT_ID>","outcome":"Succeeded","durationMs":1234,"result":"{\"ok\":true}"}'
curl -s -X DELETE $BASE/api/workers/$WID -H "X-Api-Key: $WK" -H "X-Worker-Token: $TOK"       # ยกเลิกการลงทะเบียน
curl -s -X POST $BASE/api/workers/$WID/drain -H "X-Api-Key: $ADMIN"                           # admin สั่งหยุดรับงานใหม่
```

---

## 5. AI / MCP ผ่าน Microsoft.Extensions.AI

### แนวคิด

แต่ละ method ใน `JobDistAiTools` มี `[Description]` แล้ว `AIFunctionFactory.Create(...)` จะสร้าง `AIFunction` พร้อม JSON schema จาก signature ให้อัตโนมัติ
`AIFunction` ชุดเดียวกันนี้ใช้ได้ 2 แบบ:

1. ส่งให้ `IChatClient` ใดก็ได้ผ่าน `ChatOptions.Tools` (function calling ใน process เดียวกัน)
2. ห่อด้วย `McpServerTool.Create(aiFunction)` เพื่อเปิดให้ AI client ภายนอกเรียกผ่าน MCP

**Tools ที่มี** (snake_case)

| กลุ่ม | Tool |
|---|---|
| คำนวณความเสี่ยง | `assess_job_risk`, `assess_submission_risk` (what-if), `assess_worker_risk` |
| วิเคราะห์ | `detect_anomalies`, `recommend_workers`, `get_worker_performance`, `get_cluster_summary` |
| อ่านข้อมูล | `get_job_status`, `list_jobs`, `get_job_history`, `list_workers` |
| มีผลข้างเคียง (ปิดเป็นค่าเริ่มต้น) | `submit_job`, `cancel_job`, `change_job_priority`. เปิดด้วย `Mcp:AllowSideEffects=true` |

### เรียกผ่าน MCP (HTTP)

```bash
dotnet run --project samples/JobDist.AiClientSample -- http://localhost:8080/mcp dev-ai-agent-key-change-me image.resize
```

```csharp
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

// Step 1: ต่อ MCP endpoint ด้วย API key ที่มี role AiAgent
var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = new Uri("http://localhost:8080/mcp"),
    AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = "dev-ai-agent-key-change-me" }
});
await using var mcp = await McpClient.CreateAsync(transport);

// Step 2: McpClientTool แต่ละตัวเป็น AIFunction อยู่แล้ว
IList<McpClientTool> tools = await mcp.ListToolsAsync();

// Step 3: เรียกตรงๆ โดยไม่ใช้ LLM
var risk = await mcp.CallToolAsync("assess_submission_risk", new Dictionary<string, object?> { ["jobType"] = "image.resize" });

// Step 4: ให้ LLM ตัดสินใจเรียก tool เอง (IChatClient จาก provider ใดก็ได้: Azure OpenAI, OpenAI, Ollama, ...)
IChatClient chat = new ChatClientBuilder(innerChatClient).UseFunctionInvocation().Build();
var answer = await chat.GetResponseAsync(
    "ตอนนี้ส่งงาน image.resize 500 งานได้ปลอดภัยไหม จะไปลง worker ไหน และมีความผิดปกติอะไรหรือเปล่า",
    new ChatOptions { Tools = [.. tools] });
Console.WriteLine(answer.Text);
```

### ใช้ใน process เดียวกัน (ไม่ผ่าน MCP)

```csharp
services.AddJobDist(configuration).AddJobDistAiTools();
var tools = JobDistAiFunctions.Create(includeSideEffects: false);   // IReadOnlyList<AIFunction>
var response = await chat.GetResponseAsync(prompt, new ChatOptions { Tools = [.. tools] });
// FunctionInvokingChatClient ส่ง IServiceProvider มาใน AIFunctionArguments.Services ทำให้ resolve JobDistAiTools จาก DI ได้
```

### MCP แบบ stdio (Claude Desktop / Claude Code)

ต่อฐานข้อมูลเดียวกับ server (ต้องเป็น SQL Server หรือ PostgreSQL):

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

---

## 6. ให้โปรเจกต์อื่นส่งงาน (Client SDK)

```csharp
// appsettings.json: "JobDistClient": { "ServerUrls": ["https://jobdist-a", "https://jobdist-b"], "ApiKey": "<จาก secret store>" }
builder.Services.AddJobDistClient(builder.Configuration.GetSection("JobDistClient"));

public sealed class OrderService(IJobDistClient jobs)
{
    public async Task<string?> MakeThumbnailAsync(Guid orderId, CancellationToken ct)
    {
        // Step 1: ส่งงาน (SDK สร้าง idempotency key ให้ถ้าไม่ระบุ จึง retry/failover ข้าม server ได้โดยไม่เกิดงานซ้ำ)
        var submitted = await jobs.SubmitAsync(new SubmitJobRequest("image.resize",
            Payload: """{"width":4000,"height":3000,"targetWidth":800}""", Priority: 70, IdempotencyKey: $"thumb-{orderId}"), ct);

        // Step 2: รอผล (หรือเก็บ jobId แล้วมาเช็คทีหลัง)
        var done = await jobs.WaitForCompletionAsync(submitted.JobId, TimeSpan.FromSeconds(1), ct);
        return done.Status == JobStatus.Succeeded ? done.Result : null;
    }
}
```

SDK จะ failover ไป server ถัดไปเองเมื่อเจอ network error, timeout หรือ 5xx

---

## 7. ให้โปรเจกต์อื่นทำ worker (Worker SDK)

```csharp
// Program.cs
var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();                          // metrics → Aspire
builder.Services.AddJobDistWorker(builder.Configuration)
    .AddJobHandler<InvoicePdfHandler>();
await builder.Build().RunAsync();

// Handler: 1 class ต่อ 1 job type
public sealed class InvoicePdfHandler(IInvoiceRenderer renderer) : IJobHandler
{
    public string JobType => "invoice.pdf";
    public int MaxConcurrency => 4;                    // รับพร้อมกันได้กี่งานของประเภทนี้

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        // ct ถูกยกเลิกเมื่อ timeout, server สั่ง cancel หรือ worker กำลังปิด
        var input = JsonSerializer.Deserialize<InvoiceInput>(context.Payload!)
                    ?? throw new NonRetryableJobException("payload ไม่ถูกต้อง");   // ไม่ retry
        var url = await renderer.RenderAsync(input, ct);
        return JobResult.Ok(JsonSerializer.Serialize(new { url }));
        // throw exception อื่น = ล้มเหลวแบบ retry ได้, หรือ JobResult.Fail("...", retryable: false)
    }
}
```

```json
"JobDistWorker": {
  "ServerUrls": ["https://jobdist-a", "https://jobdist-b"],
  "ApiKey": "<worker key จาก secret store>",
  "MaxTotalConcurrency": 8,
  "ConcurrencyOverrides": { "invoice.pdf": 2 },
  "PinnedServerKeyId": "jobdist-prod",
  "RequireSignatures": true
}
```

สิ่งที่ SDK ทำให้: ลงทะเบียน (retry แบบ backoff), heartbeat พร้อม CPU/RAM, long-poll, ตรวจลายเซ็น PQC และ hash, ถอดรหัส payload, จำกัด concurrency ต่อประเภทและรวม, timeout, รับคำสั่ง cancel/drain, เก็บผลไว้ใน outbox แล้วส่งซ้ำเมื่อเน็ตกลับมา, ลงทะเบียนใหม่เมื่อ server ลืม worker, ปิดเครื่องอย่างนุ่มนวล (รองานเสร็จแล้ว unregister) และส่ง metrics `JobDist.Worker`

---

## 8. แนวคิดหลักทีละเรื่อง

### 8.1 การจับคู่งานกับ worker (SchedulerService)

ขั้นที่ 1 **กรองเครื่องที่รับได้**: ต้อง Online, ลงทะเบียน job type นั้น, ยังมีช่องว่างทั้งของประเภทนั้นและรวม, ตรงกับ target ถ้า `strictTarget`, และต้องมี public key สำหรับเข้ารหัสถ้างานเป็น sensitive

ขั้นที่ 2 **ให้คะแนน** แต่ละเครื่อง:

```
score = wLoad·(1 − load) + wSpeed·speed + wReliability·P(success) + wExploration·UCB + bonus
```

- `load` = งานที่ทำอยู่ / ความจุ
- `speed` = ความเร็วที่ ML ทำนาย (normalize ในกลุ่มผู้สมัคร)
- `P(success)` = โอกาสสำเร็จที่ ML เรียนรู้
- `UCB` = โบนัสสำรวจ ให้เครื่องใหม่ได้ลองงานบ้าง
- `bonus` = +preferred worker, −เครื่องที่เพิ่ง fail งานนี้

ขั้นที่ 3 **เรียงงาน** ด้วย effective priority = priority + aging (รอนานได้คะแนนเพิ่มสูงสุด +100 กันงานค้างตลอดไป) แล้วจับคู่แบบ greedy โดยอัปเดต load ทันทีหลังแต่ละการจับคู่

**ประสิทธิภาพ**: คำนวณคะแนนด้วย **SIMD** (`Vector<double>`, เปิดปิดได้ด้วย `JobDist:UseSimd`), ทำนายแยกตามประเภทงานแบบ **`Parallel.For`** เมื่อจำนวนเกิน `ParallelThreshold`, ใช้ scratch buffer ซ้ำ (ไม่ allocate ใน hot loop), บันทึกการจับคู่ทั้งชุดในคำสั่ง SQL เดียว (OPENJSON / jsonb_to_recordset) และส่งงานหลายพันงานด้วย SqlBulkCopy / binary COPY

### 8.2 Machine Learning ที่เรียนรู้ระหว่างใช้งาน (PerformanceLearningService)

- **ต่อ (worker, job type)**: EWMA ของ log(duration) และ Beta-Bernoulli สำหรับอัตราสำเร็จ
- **ต่อ job type**: online linear regression (NLMS) จาก feature `[bias, −ln benchmark, −ln cores, −ln speedFactor, load, gpu]` ใช้ทำนายเครื่องใหม่ที่ยังไม่มีประวัติ (cold start)
- รวมสองแหล่งด้วยน้ำหนักความมั่นใจ `n/(n+3)`
- เรียนรู้ทุกครั้งที่งานจบ และ train ใหม่จากประวัติตอน start และทุก 5 นาที (ทุก instance มี model ที่สอดคล้องกัน)
- ดูสิ่งที่เรียนรู้ได้ที่ `GET /api/admin/performance` หรือ tool `get_worker_performance`

### 8.3 Retry และเครือข่ายไม่เสถียร

| เหตุการณ์ | ผล |
|---|---|
| worker หาย (heartbeat ขาด) | คืนงานเข้าคิว **ทันที**, นับเป็น attempt หรือไม่ ตาม `CountWorkerLossAsAttempt` |
| worker ปิดตัวเอง / ลงทะเบียนใหม่ | คืนงานทันที **ไม่นับ** attempt |
| ได้งานแต่ไม่ start ภายใน `AssignmentStartTimeoutSeconds` | คืนงาน |
| Failed (retryable) / TimedOut | รอแบบ exponential backoff `base·2^(n−1)` ±20% jitter สูงสุด `RetryMaxDelaySeconds` |
| Failed (non-retryable) หรือ attempt หมด | `Failed` |
| ไม่มี worker ที่รับได้ | งานรออยู่ในคิว (anomaly `QueueStarvation` แจ้ง admin) |
| รายงานผลมาช้าหลังงานถูกย้ายแล้ว | server ตอบ 409 แล้ว SDK ทิ้งผลนั้น (ไม่เกิดผลซ้ำ) |

### 8.4 High Availability (ไม่มีจุดเดียวที่ล่มแล้วทั้งระบบล่ม)

- server ทุกตัว **active-active**: ตอบ API, MCP, UI และ poll ของ worker ได้ทุกตัว (MCP เป็น stateless)
- งานระดับ cluster (scheduler, failover, anomaly scan, housekeeping) ทำโดย **leader 1 ตัว** ที่ได้ lease ในตาราง `LeaderLeases`. ถ้า leader ตาย ตัวอื่นรับช่วงภายใน `LeaderLeaseSeconds` (ค่าเริ่มต้น 15 วินาที)
- ทุกการเปลี่ยนสถานะเป็นแบบมีเงื่อนไข (`WHERE Status=@expected AND Version=@v`) จึงปลอดภัยแม้หลาย instance แข่งกัน
- Client SDK และ Worker SDK รู้จัก server หลายตัวและ failover เอง จึงไม่ต้องพึ่ง load balancer ตัวเดียว
- ฐานข้อมูลควรรันแบบ HA ด้วย (SQL Server Always On / PostgreSQL Patroni หรือ managed service)
- **ทดสอบแล้วจริงใน Docker** ทั้ง PostgreSQL และ SQL Server: kill server ที่เป็น leader กับ worker 1 ตัวระหว่างที่งานกำลังรัน ผลคือ leader ย้ายไป server-b, งานของ worker ที่ตายถูกส่งให้ worker อื่น (attempt 2) และสำเร็จทุกงาน

### 8.5 การตรวจจับความผิดปกติและแจ้งเตือน admin

leader สแกนทุก `Anomaly:ScanIntervalMinutes` นาที (window ล่าสุดเทียบกับ baseline 24 ชั่วโมง):

| Anomaly | วิธีตรวจ |
|---|---|
| `SubmissionSpike` | z-score ของจำนวนงานต่อนาที |
| `HighFailureRate` | อัตรา fail ต่อ job type และต่อ worker เกิน threshold และสูงกว่าปกติ |
| `SlowExecution` | z-score ของเวลาเฉลี่ยเทียบประวัติ |
| `WorkerFlapping` | worker หลุดซ้ำหลายครั้ง |
| `FailoverStorm` | งานถูกย้ายเพราะ worker หายจำนวนมาก |
| `QueueStarvation` | มีงานค้าง แต่ไม่มี worker ที่ทำประเภทนี้ได้ |
| `SuspiciousWorkerActivity` | รายงานที่ถูกปฏิเสธจำนวนมาก (token ผิด / attempt เก่า) |

แจ้งเตือนส่งผ่าน **mrpichet/messagehub**: `POST {BaseUrl}/api/messages` header `X-Api-Key` body `{channel, recipient, subject, body, sign}` มี cooldown ต่อ (ชนิด, subject) และกรองตามความรุนแรง ตั้งค่าที่ `Alerts:MessageHub`

### 8.6 Metrics → .NET Aspire

- server: meter `JobDist.Server` เช่น `jobdist.jobs.submitted`, `jobdist.jobs.assigned`, `jobdist.jobs.completed`, `jobdist.scheduler.cycle.duration`, `jobdist.job.queue_wait`, `jobdist.failovers`, `jobdist.risk.score`, gauge `jobdist.queue.depth`, `jobdist.workers.online`, `jobdist.leader`
- worker: meter `JobDist.Worker` เช่น `jobdist.worker.jobs.received`, `jobdist.worker.jobs.completed`, `jobdist.worker.job.duration`, `jobdist.worker.jobs.running`, `jobdist.worker.outbox.size`, `jobdist.worker.messages.rejected`
- ส่งผ่าน OTLP เมื่อมี `OTEL_EXPORTER_OTLP_ENDPOINT` (compose ตั้งค่าให้แล้ว). เปิด dashboard ที่ http://localhost:18888 (login token อยู่ใน log ของ container `aspire-dashboard`)

---

## 9. ความปลอดภัย

| ด้าน | วิธีที่ใช้ |
|---|---|
| Authentication | API key ใน header `X-Api-Key`. server เก็บเฉพาะ **SHA-256** และเทียบแบบ constant-time. key แยกตาม role (Admin/Submitter/Viewer/Worker/AiAgent) และมี fallback policy ปฏิเสธทุก endpoint ที่ไม่ได้ระบุสิทธิ์ |
| ตัวตน worker | แต่ละ worker ได้ **token ของตัวเอง** (`X-Worker-Token`) ตอนลงทะเบียน server เก็บแค่ hash. key ของ fleet อย่างเดียวจึงสวมรอยเป็น worker ตัวอื่นไม่ได้ |
| ความถูกต้องของคำสั่ง | งานและคำสั่ง cancel/drain ถูกเซ็นแบบ **hybrid ML-DSA-65 (FIPS 204) + ECDSA P-384** ครอบ job, attempt, worker, เวลา และ hash ของ payload จึงปลอมหรือ replay ไปเครื่องอื่นไม่ได้. worker ต้องผ่านทั้งสองลายเซ็น (กัน downgrade) และ pin key id ได้ |
| ความลับของ payload | งาน `sensitive`: เก็บใน DB แบบ AES-256-GCM (AAD = job id) และส่งให้ worker แบบ **ML-KEM-768 + ECDH P-384 → HKDF-SHA384 → AES-256-GCM** (AAD = attempt id) |
| Post-quantum | ML-DSA / ML-KEM ของ .NET 10 (ต้องใช้ OpenSSL ≥ 3.5, image Alpine `-extra` มีให้แล้ว). ถ้า OS ไม่รองรับจะ fallback เป็น ECDSA/ECDH และระบุ algorithm ไว้ชัด |
| Input | DataAnnotations ทุก request, regex ของ job type, จำกัดขนาด payload/result, ตัด control characters, SQL แบบ parameter (Dapper) เท่านั้น |
| Abuse | rate limit (global, submit, register) ต่อ API key, request size limit |
| HTTP | security headers (CSP แยกสำหรับ API และ UI, nosniff, no-referrer, frame-ancestors none), CORS เฉพาะ origin ที่อนุญาต, HSTS/HTTPS redirect เปิดได้ |
| Errors | ProblemDetails ที่ไม่เปิดเผย stack trace. worker ส่งแค่ชนิดและข้อความของ exception |
| AI | tool ที่มีผลข้างเคียงปิดเป็นค่าเริ่มต้น (least privilege / จำกัดผลของ prompt injection) |
| Container | ดูหัวข้อ Docker |

**เรื่องประสิทธิภาพ**: ตรวจ API key และ worker token ใช้ SHA-256 ครั้งเดียวต่อ request, ลายเซ็นทำเฉพาะตอนส่งงาน (ต่ำกว่า 1 ms), เข้ารหัสเฉพาะงาน sensitive

---

## 10. Docker / Production

```bash
cp .env.example .env            # สร้าง secret ทุกตัวตามคำสั่งในไฟล์
docker compose up -d --build    # PostgreSQL
# หรือ SQL Server
docker compose -f docker-compose.yml -f docker-compose.sqlserver.yml up -d --build
```

ได้: `server-a` (http://localhost:8080), `server-b` (http://localhost:8081), worker 2 ตัว, PostgreSQL หรือ SQL Server, Aspire dashboard (http://localhost:18888)

**Hardening**

- multi-stage build: SDK ไม่ติดไปกับ runtime image
- runtime เป็น Alpine `-extra`: เล็ก, มี ICU + tzdata, OpenSSL 3.5 สำหรับ PQC
- รันด้วย user ที่ไม่ใช่ root, ไฟล์แอปเป็นของ root แบบ read-only (0555)
- `read_only: true` root filesystem + tmpfs `/tmp` แบบ `noexec,nosuid`
- `cap_drop: [ALL]`, `no-new-privileges`, จำกัด CPU/RAM/pids
- ฐานข้อมูลอยู่ใน network `internal` (เข้าจากเครื่อง host ไม่ได้)
- port ผูกกับ `127.0.0.1` เท่านั้น
- secret มาจาก `.env` (อยู่ใน `.gitignore`). license DevExpress ส่งเป็น BuildKit secret ไม่ติดใน layer
- `DOTNET_EnableDiagnostics=0`, healthcheck ที่ `/alive`

**สำคัญสำหรับ HA**: server ทุกตัวต้องใช้ `PAYLOAD_PROTECTION_KEY`, `MLDSA_PRIVATE_SEED`, `ECDSA_PRIVATE_KEY_PKCS8` ชุดเดียวกัน (compose บังคับให้ตั้งค่า)

**DevExpress license**: DevExpress.Blazor 26.1 ดาวน์โหลดจาก nuget.org ได้ แต่ต้องมี license key ตอน build ไม่งั้น UI จะขึ้น trial banner. ใส่ไฟล์ key ใน `DEVEXPRESS_LICENSE_FILE` (ใน `.env`) หรือ build เองด้วย `--secret id=devexpress_license,src=<ไฟล์>` (Dockerfile วางไฟล์ไว้ที่ `$HOME/.config/DevExpress/DevExpress_License.txt` เฉพาะตอน publish แล้วลบทิ้ง ไม่ติดใน image). ตอน build บนเครื่อง dev ให้วางไฟล์ `DevExpress_License.txt` ไว้ที่ `$HOME/.config/DevExpress` (Linux), `%AppData%\DevExpress` (Windows) หรือ `$HOME/Library/Application Support/DevExpress` (macOS) ตามที่ DevExpress กำหนด (คำเตือน DX1001 ตอน build หมายถึงยังไม่มีไฟล์นี้)

---

## 11. การตั้งค่า

ค่าทั้งหมดอยู่ใน `src/JobDist.Server/appsettings.json` (override ด้วย environment variable เช่น `JobDist__OfflineAfterSeconds=30`)

| Section | ค่าสำคัญ |
|---|---|
| `Database` | `Provider` (`SqlServer` / `PostgreSql` / `InMemory`), `ConnectionString`, `AutoMigrate` |
| `JobDist` | `DefaultMaxAttempts`, `DefaultTimeoutSeconds`, `HeartbeatIntervalSeconds`, `OfflineAfterSeconds`, `AssignmentStartTimeoutSeconds`, `RetryBaseDelaySeconds`, `RetryMaxDelaySeconds`, `CountWorkerLossAsAttempt`, `AgingPointsPerMinute`, `UseSimd`, `ParallelThreshold`, `MaxDegreeOfParallelism`, `Weights`, `LeaderLeaseSeconds`, `RetentionDays`, `PayloadProtectionKey` |
| `Anomaly` | `ScanIntervalMinutes`, `WindowMinutes`, `BaselineHours`, `ZScoreThreshold`, `FailureRateThreshold`, `MinSamples`, `AlertCooldownMinutes`, `MinAlertSeverity` |
| `Alerts:MessageHub` | `Enabled`, `BaseUrl`, `ApiKey`, `Channel`, `Recipients`, `Sign` |
| `Crypto` | `KeyId`, `MlDsaPrivateSeed`, `EcdsaPrivateKeyPkcs8` |
| `Security` | `ApiKeys[] {Name, KeyHash, Roles[]}`, `AllowedOrigins`, `HttpsRedirection` |
| `Mcp` | `AllowSideEffects`, `AgentClientName` |

สร้าง hash ของ API key: `printf '%s' 'my-key' | sha256sum`

---

## 12. Tests

```bash
dotnet test tests/JobDist.Tests     # 53 tests, ไม่ต้องมี DB (InMemory ADO + FakeTimeProvider)
```

ครอบคลุม: วงจรชีวิตงาน, ลายเซ็นและการปลอม, การเข้ารหัส PQC ผูกกับ attempt, priority และการเปลี่ยน priority, strict target, capability, concurrency ต่อประเภท, idempotency, retry/backoff/attempt หมด, cancel พร้อมคำสั่งที่เซ็นแล้ว, failover เมื่อ worker หาย, รายงานที่มาช้า (409), start timeout, unregister, worker token, drain, SIMD = scalar, ML เรียนรู้เครื่องที่เร็วกว่าและ cold start, anomaly ทุกชนิดหลัก, alert cooldown, risk, AI tools (snake_case, ปิด side effect, เรียกผ่าน `AIFunction`)

---

## 13. ตารางเทียบความต้องการกับสิ่งที่ทำ

| ความต้องการ | ที่อยู่ในโค้ด |
|---|---|
| REST API + MCP ใช้ business logic เดียวกัน | `JobDist.Server/Controllers`, `JobDist.Mcp/JobDistAiTools.cs` → `JobDist.Core/Services` |
| worker ลงทะเบียน job type + จำนวนที่รับได้ | `POST /api/workers/register`, `WorkerRegistryService` |
| กระจายงานตามความสามารถ สเปก ภาระ และตารางเวลา | `SchedulerService`, `runAtUtc` |
| priority / ระบุ worker | `Priority`, `TargetWorkerId`, `StrictTarget`, aging |
| ลงทะเบียน / ยกเลิกผ่าน REST | `register`, `DELETE /api/workers/{id}` |
| worker หาย ย้ายงานไปเครื่องอื่น | `FailoverService`, heartbeat |
| retry ตามที่ตั้งค่า | `RetryPolicyService`, `JobDist:Retry*` |
| เก็บประวัติและเวลา ใช้กระจายครั้งต่อไป | `JobAttempts`, `PerformanceLearningService` |
| HA ไม่มี single point of failure | leader lease, active-active, SDK failover |
| ความปลอดภัยเข้มงวดแต่เร็ว | หัวข้อ 9 |
| แสดงสถานะ / เปลี่ยน priority / ยกเลิกและแจ้ง worker | UI, `PATCH priority`, `POST cancel` + signed command |
| API แยกสำหรับส่งงาน และสำหรับทำ worker | `JobDist.Client`, `JobDist.Worker.Sdk` |
| ML ปรับปรุงการกระจายงาน | `PerformanceLearningService` |
| Metrics ทั้ง server และ worker → Aspire | `JobDistMetrics`, `WorkerMetrics`, `ServiceDefaults` |
| Anomaly → แจ้ง admin ผ่าน messagehub | `AnomalyDetectionService`, `AlertService`, `MessageHubAlertSender` |
| DTO / DAO / ADO / Service | หัวข้อ 2 |
| PQC | ML-DSA-65, ML-KEM-768 แบบ hybrid |
| Blazor WASM + DevExpress 26.1 แยกโปรเจกต์ | `src/JobDist.UI` |
| MCP tools คำนวณความเสี่ยง | `assess_job_risk`, `assess_submission_risk`, `assess_worker_risk` |
| MSTest | `tests/JobDist.Tests` |
| ตัวอย่าง cURL และ AI | หัวข้อ 4, 5, `samples/JobDist.AiClientSample` |
| Parallel + SIMD เปิดปิดได้ | `ScoringMath`, `JobDist:UseSimd`, `ParallelThreshold` |
| Throughput สูง | batch SQL, bulk insert, long-poll + signal, Server GC, cache |
| Dockerfile + Compose แบบ hardening | `docker/`, `docker-compose*.yml` |
| Dapper + SQL Server หรือ Postgres | `JobDist.Infrastructure/Data/*ADO.cs` (เลือกด้วย `Database:Provider`) |
| ไฟล์สรุป API ให้ Claude โปรเจกต์อื่น | `docs/API_EXPORT.md`, `tools/JobDist.ApiExport` |
