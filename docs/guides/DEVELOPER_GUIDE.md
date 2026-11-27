# JobDist: Developer Guide

ปรับปรุง 27 พฤศจิกายน 2569 · สำหรับนักพัฒนาที่แก้ไข JobDist หรือเขียนระบบที่เรียก JobDist
สถาปัตยกรรมดู [ARCHITECTURE.md](../ARCHITECTURE.md), API ทั้งหมดดู [CLAUDE.md](../../CLAUDE.md) และ [API_EXPORT.md](../API_EXPORT.md),
ความสัมพันธ์หน้าจอกับโค้ดดู [TRACEABILITY.md](../TRACEABILITY.md)

## 1. เตรียมเครื่อง

| เครื่องมือ | เวอร์ชัน | หมายเหตุ |
|---|---|---|
| .NET SDK | 10.0.100 ขึ้นไป (`global.json`, roll forward latestFeature) | |
| Git | 2.40+ | repo ใช้ submodule |
| Docker + Compose v2 | ล่าสุด | สำหรับ PostgreSQL/SQL Server ในเครื่องและทดสอบ stack |
| DevExpress license | 26.1 | ไม่มีก็ build ได้ แต่ขึ้นเตือน DX1000/DX1001 และ UI แสดงแถบ trial; ห้าม commit ไฟล์ license |
| IDE | Visual Studio 2026 / Rider / VS Code + C# Dev Kit | |

```bash
git clone https://github.com/mrpichet/jobdist.git
cd jobdist
git submodule update --init --recursive     # external/common, external/webcommon
dotnet build JobDist.slnx
dotnet test tests/JobDist.Tests              # MSTest, ใช้ฐานข้อมูล in-memory ไม่ต้องมี DB
```

## 2. รันในเครื่อง

```bash
# ฐานข้อมูล (เลือกหนึ่ง)
docker run -d --name jobdist-pg -e POSTGRES_PASSWORD=devpass -e POSTGRES_DB=jobdist -p 5432:5432 postgres:17-alpine

# server (REST + MCP + เว็บคอนโซล) ที่ http://localhost:8080
export ASPNETCORE_ENVIRONMENT=Development
export Database__Provider=PostgreSql
export Database__ConnectionString="Host=localhost;Database=jobdist;Username=postgres;Password=devpass"
dotnet run --project src/JobDist.Server

# worker ตัวอย่าง (เทอร์มินัลใหม่)
dotnet run --project samples/JobDist.SampleWorker

# ส่งงานทดสอบ
curl -s -X POST http://localhost:8080/api/v1/jobs -H "X-Api-Key: dev-submitter-key-change-me" \
  -H "Content-Type: application/json" -d '{"jobType":"hash.sha256","payload":"{\"text\":\"hi\"}"}'
```

`appsettings.Development.json` มี API key สำหรับพัฒนา (`dev-*-key-change-me`) ใช้ในเครื่องเท่านั้น
ถ้าไม่ตั้ง `AuthServer:Authority` เว็บคอนโซลจะ login ไม่ได้ แต่ REST/MCP ใช้ API key ได้ตามปกติ ถ้าต้องการทดสอบเว็บ ให้ชี้ไป AuthServer ของ environment พัฒนา

## 3. โครงสร้างโค้ด

```
src/
  JobDist.Contracts       DTO ขาเข้า/ขาออกของ REST และ SDK, enum (ไม่มี dependency)
  JobDist.Core            business logic: Domain (*DTO), DAO (I*DAO), Services (*Service), Options, Security, Observability
  JobDist.Infrastructure  Data (*ADO สำหรับ PostgreSQL/SQL Server + InMemory), Alerts (MessageHub), Messaging (RabbitMQ),
                          Workers (BackgroundService), Http, DependencyInjection.cs
  JobDist.Server          ASP.NET Core host: Controllers, Security (API key, permission, idempotency), Console (AI chat, client logs), Program.cs
  JobDist.Web             Blazor WebAssembly (WebCommon.UI + DevExpress): Pages, Components, Services/JobDistApi.cs, Localization
  JobDist.Mcp             MCP tools (JobDistAiTools) ใช้ร่วมกันระหว่าง HTTP /mcp, stdio และ AI chat
  JobDist.McpServer       MCP host แบบ stdio
  JobDist.Client          SDK สำหรับส่งและติดตามงาน
  JobDist.Worker.Sdk      SDK สำหรับเขียน Worker
  JobDist.ServiceDefaults OpenTelemetry, health checks, resilience
external/common           Common.Logging, Common.AspNetCore (submodule)
external/webcommon        WebCommon.UI (submodule)
db/postgres, db/sqlserver schema.sql (idempotent, รันตอนเริ่มเมื่อ AutoMigrate=true)
tools/JobDist.ApiExport   สร้าง docs/API_EXPORT.md และ docs/api-export.json
samples/                  SampleWorker, AiClientSample
tests/JobDist.Tests       MSTest
```

### 3.1 กฎการตั้งชื่อ

| ชั้น | รูปแบบ | ตัวอย่าง |
|---|---|---|
| Domain model | `*DTO` | `JobDTO`, `WorkerDTO`, `RiskAssessmentDTO` |
| Repository interface | `I*DAO` (ใน `JobDist.Core/DAO`) | `IJobDAO`, `IAlertOutboxDAO` |
| Repository implementation | `*ADO` (ใน `JobDist.Infrastructure/Data`) | `JobADO`, `AlertRecipientADO` |
| Business logic | `*Service` | `JobService`, `QueueThresholdService` |
| REST contract | `*Request`, `*Response` (ใน `JobDist.Contracts`) | `SubmitJobRequest`, `JobStatusResponse` |
| Background job | `*Worker` | `SchedulerWorker`, `AlertOutboxWorker` |

Controller บางมาก: ตรวจ input, เรียก Service, แปลงผลเป็น Response เท่านั้น ห้ามมี SQL หรือ business rule ใน controller

## 4. ขั้นตอนเพิ่ม/แก้ endpoint

1. **Contract**: เพิ่ม/แก้ `*Request`/`*Response` ใน `JobDist.Contracts` พร้อม XML doc comment และ DataAnnotations (ใช้สร้างเอกสาร)
2. **Domain + DAO**: เพิ่ม method ใน `I*DAO` แล้ว implement ให้ครบ **3 ที่**: ADO ของ PostgreSQL, ADO ของ SQL Server (ใน `*ADO` เดียวกันแยกตาม `Database.Provider`)
   และ InMemory (`Data/InMemory/InMemoryADO.cs`, `InMemoryConsoleADO.cs`) ที่ test ใช้
3. **Schema**: แก้ทั้ง `db/postgres/schema.sql` และ `db/sqlserver/schema.sql` แบบ idempotent (PostgreSQL: `CREATE ... IF NOT EXISTS`, `ADD COLUMN IF NOT EXISTS`;
   SQL Server: `IF OBJECT_ID(...) IS NULL`, `IF COL_LENGTH(...) IS NULL`) ห้ามลบหรือเปลี่ยนชนิดคอลัมน์ในที่เดิม เพราะ server หลายตัวอาจรันคนละเวอร์ชันระหว่าง rolling upgrade
4. **Service**: business logic ใน `JobDist.Core/Services` ลงทะเบียนใน `JobDist.Infrastructure/DependencyInjection.cs`
5. **Controller**: route `api/v{version:apiVersion}/...` และ alias `api/...`, ใส่ `[JobDistAuthorize(Permissions.X, RoleSets.Y)]` ตามหลัก "permission OR role"
   (ทั้งสองอยู่ใน `Security/JobDistAuthorization.cs`) และ `[Idempotent]` สำหรับ POST สร้างข้อมูลของคอนโซล
6. **เว็บ**: เพิ่ม method ใน `src/JobDist.Web/Services/JobDistApi.cs`, หน้าใหม่ใน `Pages/...` ใช้ component ของ WebCommon.UI (AppGrid, form page)
   ข้อความใส่ทั้ง `Localization/AppText.resx` (ไทย) และ `AppText.en.resx` สิทธิ์ของเมนูอยู่ที่ `AppMenuProvider.cs` และ `Security/AppPermissions.cs`
7. **MCP** (ถ้าเหมาะ): เพิ่ม method ที่มี `[Description]` ใน `src/JobDist.Mcp/JobDistAiTools.cs` (tool ที่มีผลข้างเคียงต้องอยู่หลัง `Mcp:AllowSideEffects`)
8. **SDK** (ถ้าเหมาะ): `IJobDistClient` / `JobDist.Worker.Sdk`
9. **Test**: เพิ่มใน `tests/JobDist.Tests` (ใช้ `TestHost` ที่ประกอบ service จริงบน InMemory + `FakeTimeProvider`)
10. **เอกสาร**: `dotnet run --project tools/JobDist.ApiExport -- docs` แล้วอัปเดต `CLAUDE.md`, `docs/TRACEABILITY.md` และคู่มือที่เกี่ยวข้อง

### 4.1 Version ของ API

- เพิ่ม endpoint, เพิ่ม field ขาออก, เพิ่ม field ขาเข้าที่ไม่บังคับ = ทำใน v1 ได้
- ลบ/เปลี่ยนชื่อ field, เปลี่ยนชนิด, เพิ่ม field บังคับ, เปลี่ยน status code หรือ **เพิ่มค่า enum ที่ส่งออก** (SDK เก่า deserialize ไม่ได้) = breaking ต้องออก v2
- รายละเอียดและลำดับ rolling upgrade: [VERSIONING.md](../VERSIONING.md)

### 4.2 ความปลอดภัยที่ต้องรักษา

- ห้าม log token, API key, worker token, payload ที่ `sensitive`, รหัสผ่าน หรือที่อยู่ผู้รับแบบเต็ม (ใช้ `addressMasked`)
- ความลับอ่านจาก environment variable / secret store เท่านั้น ห้ามใส่ใน appsettings ที่ commit
- SQL ใช้ parameter ของ Dapper เสมอ ห้ามต่อ string
- การอัปเดตที่มี `version` ต้องตรวจ optimistic concurrency และคืน 409 เมื่อไม่ตรง
- งานที่ทำโดย leader ต้องตรวจ `term` (fencing) ก่อนเขียน เพื่อกัน leader เก่าที่ค้างเขียนทับ

## 5. เขียนระบบที่เรียก JobDist

### 5.1 ส่งงานด้วย `JobDist.Client`

```csharp
// Program.cs
builder.Services.AddJobDistClient(builder.Configuration.GetSection("JobDistClient"));

// ใช้งาน
public sealed class ReportExporter(IJobDistClient jobs)
{
    public async Task<string?> ExportAsync(Guid reportId, CancellationToken ct)
    {
        var submitted = await jobs.SubmitAsync(new SubmitJobRequest(
            JobType: "report.export",
            Payload: JsonSerializer.Serialize(new { reportId }),
            Priority: 70,
            IdempotencyKey: $"report-export-{reportId}"), ct);   // ส่งซ้ำได้ ไม่เกิดงานซ้ำ

        var done = await jobs.WaitForCompletionAsync(submitted.JobId, TimeSpan.FromSeconds(2), ct);
        return done.Status == JobStatus.Succeeded ? done.Result : throw new InvalidOperationException(done.LastError);
    }
}
```

ตัวอย่าง config ดู [USER_MANUAL.md หัวข้อ 11](../manuals/USER_MANUAL.md#11-ตัวอย่าง-config-ให้ระบบอื่นเรียกใช้)

### 5.2 เขียน Worker ด้วย `JobDist.Worker.Sdk`

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddJobDistWorker(builder.Configuration)          // section "JobDistWorker"
                .AddJobHandler<ReportExportHandler>();
builder.Logging.AddLogServer(builder.Configuration);              // ส่ง log ไป LogServer (ไม่บังคับ)
await builder.Build().RunAsync();

public sealed class ReportExportHandler(IReportRepository reports) : IJobHandler
{
    public string JobType => "report.export";
    public int MaxConcurrency => 4;
    public double SpeedFactor => 1.0;

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var input = JsonSerializer.Deserialize<ExportInput>(context.Payload!)!;
        if (input.ReportId == Guid.Empty)
            throw new NonRetryableJobException("reportId is required");      // ล้มเหลวทันที ไม่ลองใหม่
        var url = await reports.ExportAsync(input.ReportId, ct);              // ต้อง idempotent: Attempt > 1 คือการลองใหม่
        return JobResult.Ok(JsonSerializer.Serialize(new { url }));
    }
}
```

- `ct` ถูกยกเลิกเมื่อ timeout, ผู้ใช้สั่งยกเลิก หรือ Worker กำลังปิด ให้ส่งต่อ `ct` ให้ทุก I/O
- handler ถูกสร้างใหม่ใน DI scope ทุกครั้ง ใช้ dependency แบบ scoped ได้

## 6. Test

```bash
dotnet test tests/JobDist.Tests                                    # ทั้งหมด
dotnet test tests/JobDist.Tests --filter "FullyQualifiedName~Failover"   # เฉพาะกลุ่ม
```

| ไฟล์ | ครอบคลุม |
|---|---|
| `JobLifecycleTests.cs` | submit, schedule, poll, complete, retry, cancel, idempotency |
| `FailoverAndWorkerTests.cs` | Worker หาย, lease หมด, drain, leader term |
| `AnalyticsTests.cs` | สถิติประสิทธิภาพ, recommendation, anomaly, risk |
| `ConsoleFeatureTests.cs` | ผู้รับแจ้งเตือน, outbox, เกณฑ์คิว, รายงาน, idempotency header |
| `SecurityAndAiToolsTests.cs` | ลายเซ็น PQC, การเข้ารหัส payload, MCP tools |

ทดสอบกับฐานข้อมูลจริงด้วย docker-compose (PostgreSQL และ overlay SQL Server) ก่อน merge การเปลี่ยน ADO หรือ schema

## 7. Submodule

```bash
git submodule update --remote external/webcommon     # ดึงล่าสุดของ webcommon
cd external/webcommon && git checkout <commit> && cd ../..
git add external/webcommon && git commit -m "Bump webcommon"
```

แก้โค้ดใน `external/*` ที่ repo ต้นทาง (mrpichet/common, mrpichet/webcommon) แล้วค่อยเลื่อน pointer ใน JobDist ห้ามแก้ในโฟลเดอร์ submodule ค้างไว้โดยไม่ commit ที่ต้นทาง

## 8. Log ในโค้ด

- server ใช้ `ILogger<T>` ปกติ `Common.Logging` ส่งต่อไป LogServer ให้เอง (`builder.Logging.AddLogServer(...)`)
- ใช้ structured logging (`{JobId}`, `{WorkerId}`) ไม่ต่อ string, ใส่ `CorrelationId`/`TraceId` ผ่าน scope เมื่อมี
- ฝั่งเว็บใช้ `ILogger` ใน Blazor ระบบส่ง log ระดับ `ClientLogging:MinimumLevel` ขึ้นไปผ่าน `POST /api/v1/client-logs`
