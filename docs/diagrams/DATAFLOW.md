# JobDist: Dataflow Diagram

ปรับปรุง 27 พฤศจิกายน 2569 · ภาพรวมสถาปัตยกรรมดู [ARCHITECTURE.md](../ARCHITECTURE.md), ตารางดู [ER_DIAGRAM.md](ER_DIAGRAM.md)

สัญลักษณ์: สี่เหลี่ยม = ผู้ใช้หรือระบบภายนอก, วงกลม/สี่เหลี่ยมมน = process, ทรงกระบอก = ที่เก็บข้อมูล (ตาราง)

## 1. DFD Level 0 (Context)

```mermaid
flowchart LR
    Op["ผู้ดูแล / ผู้ใช้<br/>(เว็บคอนโซล)"]
    App["ระบบอื่นที่ส่งงาน<br/>(REST / JobDist.Client)"]
    Agent["AI agent<br/>(MCP)"]
    Wk["Worker<br/>(JobDist.Worker.Sdk)"]
    Auth["AuthServer"]
    Hub["MessageHub"]
    Log["LogServer"]
    Otel["Aspire / OTLP"]

    P0(("0<br/>JobDist"))

    Op -- "ส่ง/แก้/ยกเลิกงาน, ตั้งค่าแจ้งเตือน, ดูรายงาน" --> P0
    P0 -- "สถานะงาน, Worker, รายงาน, ประวัติแจ้งเตือน" --> Op
    App -- "SubmitJobRequest, cancel, priority" --> P0
    P0 -- "SubmitJobResponse, JobStatusResponse" --> App
    Agent -- "tool call (JSON-RPC)" --> P0
    P0 -- "ผล tool" --> Agent
    Wk -- "register, heartbeat, poll, started, completed" --> P0
    P0 -- "งานที่ลงนามแล้ว (payload เข้ารหัสถ้า sensitive), คำสั่ง" --> Wk
    Op -- "login (ผ่าน proxy /api/v1/auth)" --> P0
    P0 -- "login / account, ตรวจ JWT (JWKS)" --> Auth
    P0 -- "แจ้งเตือน (api/v1/messages)" --> Hub
    P0 -- "server log + browser log" --> Log
    P0 -- "traces, metrics" --> Otel
```

## 2. DFD Level 1

```mermaid
flowchart TB
    App["ระบบอื่น / เว็บคอนโซล"]
    Wk["Worker"]
    Hub["MessageHub"]
    Op["ผู้ดูแล"]

    P1(("1 รับงาน<br/>JobService<br/>QueueThresholdService"))
    P2(("2 จัดสรรงาน<br/>SchedulerService<br/>(leader)"))
    P3(("3 ส่งงานให้ Worker<br/>DispatchService<br/>PqcSignatureService"))
    P4(("4 บันทึกผล<br/>JobExecutionService"))
    P5(("5 ดูแล Worker<br/>WorkerRegistryService<br/>FailoverService"))
    P6(("6 วิเคราะห์<br/>AnomalyDetection<br/>JobRisk / Report"))
    P7(("7 แจ้งเตือน<br/>AlertDispatchService<br/>AlertOutboxProcessor"))

    DJ[("jobs")]
    DA[("jobattempts")]
    DW[("workers")]
    DC[("workercommands")]
    DQ[("queuethresholds")]
    DI[("idempotencyrecords")]
    DR[("alertrecipients")]
    DO[("alertoutbox")]
    DL[("leaderleases")]

    App -- "SubmitJobRequest" --> P1
    P1 -- "อ่านเกณฑ์" --> DQ
    P1 -- "นับงานรอ" --> DJ
    P1 -- "insert (status Pending)" --> DJ
    P1 -- "Idempotency-Key" --> DI
    P1 -- "QueueNearLimit / OverLimit" --> P7

    DL -- "term / holder" --> P2
    DJ -- "งาน Pending ที่ถึงเวลา" --> P2
    DW -- "Worker ออนไลน์ + โหลด" --> P2
    P2 -- "update Assigned + leaseexpires" --> DJ
    P2 -- "insert attempt (InProgress)" --> DA

    Wk -- "poll (long-poll)" --> P3
    DJ -- "งานที่ assign ให้ Worker นี้" --> P3
    DC -- "คำสั่งค้าง" --> P3
    P3 -- "JobAssignmentResponse + signature" --> Wk

    Wk -- "started / completed" --> P4
    P4 -- "status, result, retry (nextattemptutc)" --> DJ
    P4 -- "outcome, duration" --> DA

    Wk -- "register / heartbeat" --> P5
    P5 -- "insert / update" --> DW
    P5 -- "ยกเลิก / Drain" --> DC
    P5 -- "Worker หาย: งานกลับเป็น Pending" --> DJ
    P5 -- "WorkerLost / Registered / Unregistered" --> P7

    DJ --> P6
    DA --> P6
    DW --> P6
    P6 -- "AnomalyDetected" --> P7
    P6 -- "รายงาน, ความเสี่ยง" --> Op

    Op -- "ผู้รับ, เกณฑ์คิว" --> DR
    Op --> DQ
    DR -- "ผู้รับที่ตรง eventType + severity" --> P7
    P7 -- "1 แถวต่อเหตุการณ์ต่อผู้รับ" --> DO
    DO -- "แถวที่ถึงเวลา (lock)" --> P7
    P7 -- "POST api/v1/messages" --> Hub
    P7 -- "Sent / Failed / retry" --> DO
```

| Process | ข้อมูลเข้า | ข้อมูลออก | ตาราง |
|---|---|---|---|
| 1 รับงาน | `SubmitJobRequest`, batch, Idempotency-Key | `SubmitJobResponse`, 429 `queue_full` | jobs, queuethresholds, idempotencyrecords |
| 2 จัดสรรงาน | งาน Pending, Worker ออนไลน์, สถิติประสิทธิภาพ | งาน Assigned + attempt ใหม่ | jobs, jobattempts, workers, leaderleases |
| 3 ส่งงาน | poll ของ Worker | `PollResponse` (งานลงนาม ML-DSA-65 + ECDSA, คำสั่ง) | jobs, workercommands |
| 4 บันทึกผล | started, `JobCompletedRequest` | `JobStatusResponse`, 409 ถ้างานถูกกระจายใหม่แล้ว | jobs, jobattempts |
| 5 ดูแล Worker | register, heartbeat, drain, unregister | token (ครั้งเดียว), คำสั่ง | workers, workercommands, jobs |
| 6 วิเคราะห์ | jobs, attempts, workers | Anomaly, ความเสี่ยง, รายงานสถิติ | jobs, jobattempts, workers |
| 7 แจ้งเตือน | เหตุการณ์จาก 1, 5, 6 และเปลี่ยน leader | ข้อความไป MessageHub, ประวัติ | alertrecipients, alertoutbox |

## 3. ลำดับการทำงานหลัก

### 3.1 ชีวิตของงาน (submit → สำเร็จ)

```mermaid
sequenceDiagram
    autonumber
    participant C as ระบบอื่น / Client SDK
    participant S as JobDist server (ตัวใดก็ได้)
    participant L as Leader (SchedulerWorker)
    participant DB as Database
    participant W as Worker SDK

    C->>S: POST /api/v1/jobs (X-Api-Key, idempotencyKey)
    S->>DB: ตรวจเกณฑ์คิว, INSERT jobs (Pending)
    S-->>C: 200 { jobId, status: Pending, duplicate }
    loop ทุกรอบ (SchedulerIdleDelayMs)
        L->>DB: SELECT งาน Pending ที่ถึงเวลา + Worker ออนไลน์
        L->>L: ให้คะแนน (load, speed, reliability, aging)
        L->>DB: UPDATE jobs → Assigned, INSERT jobattempts (ตรวจ term)
    end
    W->>S: POST /workers/{id}/poll (long-poll ≤ 30 s)
    S->>DB: งานที่ assign ให้ Worker นี้ (deliveredutc)
    S-->>W: assignments (ลงนาม, payload ถอดรหัสได้เฉพาะ Worker นี้)
    W->>W: ตรวจลายเซ็น + payloadSha256
    W->>S: POST .../jobs/{jobId}/started
    S->>DB: jobs → Running
    W->>W: IJobHandler.ExecuteAsync
    W->>S: POST .../jobs/{jobId}/completed (outcome, result)
    S->>DB: jobs → Succeeded / retry Pending / Failed, jobattempts.outcome
    C->>S: GET /api/v1/jobs/{jobId} (WaitForCompletionAsync)
    S-->>C: JobStatusResponse
```

ถ้างานล้มเหลวแบบลองใหม่ได้และยังไม่ครบ `maxAttempts` สถานะกลับเป็น Pending โดย `nextattemptutc` เลื่อนออกแบบ exponential back-off
(`RetryBaseDelaySeconds` ถึง `RetryMaxDelaySeconds`)

### 3.2 Worker หายและการกระจายงานใหม่

```mermaid
sequenceDiagram
    autonumber
    participant W as Worker A
    participant F as Leader (FailoverWorker, ทุก 2 s)
    participant DB as Database
    participant H as AlertDispatchService
    W--xF: heartbeat ขาดเกิน OfflineAfterSeconds (20 s)
    F->>DB: workers → Offline, offlinecount+1
    F->>DB: งานที่ Worker A ถือ → Pending, attempt.outcome = WorkerLost
    F->>H: WorkerLost
    H->>DB: INSERT alertoutbox (unique eventkey + recipient)
    Note over F,DB: งานที่ lease หมด (timeout + LeaseGraceSeconds) ถูกคืนคิวแบบเดียวกัน
```

### 3.3 การแจ้งเตือนผ่าน outbox

```mermaid
sequenceDiagram
    autonumber
    participant E as เหตุการณ์ (คิว / Worker / Anomaly / Leader)
    participant D as AlertDispatchService
    participant DB as alertoutbox
    participant P as AlertOutboxWorker (ทุก server)
    participant R as RabbitMQ (ถ้าเปิด)
    participant M as MessageHub
    E->>D: AlertEvent (eventKey, severity)
    D->>DB: INSERT 1 แถวต่อผู้รับที่ตรงเงื่อนไข (ซ้ำ = ข้าม)
    alt Messaging:Transport = Database
        P->>DB: lock แถวที่ถึงเวลา (lockedby, lockeduntilutc)
        P->>M: POST api/v1/messages (X-Api-Key, sign = Alerts:MessageHub:Sign)
        M-->>P: 2xx / error
        P->>DB: Sent หรือ retry (back-off, circuit breaker) หรือ Failed เมื่อครบ MaxAttempts
    else Messaging:Transport = RabbitMq
        P->>R: publish jobdist.alert.v1 (RabbitMqOutboxRelayWorker)
        R->>P: consume (RabbitMqAlertConsumerWorker)
        P->>M: POST api/v1/messages
        Note over R: เกิน DeliveryLimit → dead-letter jobdist.alerts.dlx
    end
```

### 3.4 Login ของเว็บคอนโซล

```mermaid
sequenceDiagram
    autonumber
    participant B as Browser (Blazor WASM)
    participant S as JobDist server (YARP proxy)
    participant A as AuthServer
    B->>S: POST /api/v1/auth/login
    S->>A: POST /api/v1/auth/login
    A-->>S: LoginResultDTO (tokens)
    S-->>B: LoginResultDTO
    B->>S: GET /api/v1/jobs (Authorization: Bearer)
    S->>S: ตรวจ JWT กับ JWKS ของ AuthServer + permission jobdist.*
    S-->>B: PagedResponse
    B->>S: POST /api/v1/client-logs (log ฝั่ง browser)
    S->>S: เขียน log พร้อม user id → LogServer
```
