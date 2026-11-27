# JobDist: Operation Guide

ปรับปรุง 27 พฤศจิกายน 2569 · สำหรับผู้ดูแลระบบที่ดูแล JobDist ใน production
ติดตั้งดู [INSTALLATION.md](../manuals/INSTALLATION.md), โครงสร้างการติดตั้งดู [DEPLOYMENT.md](../diagrams/DEPLOYMENT.md), การใช้หน้าจอดู [USER_MANUAL.md](../manuals/USER_MANUAL.md)

## 1. งานประจำ

| ความถี่ | สิ่งที่ตรวจ | ที่ไหน |
|---|---|---|
| ต่อเนื่อง (alert) | `WorkerLost`, `LeaderChanged`, `QueueNearLimit`, `QueueOverLimit`, `AnomalyDetected` | MessageHub ตามผู้รับที่ตั้งในเมนู การแจ้งเตือน > ผู้รับ |
| ทุกวัน | หน้าภาพรวม: Worker ออฟไลน์, งานรอคิว, สถานะเกณฑ์คิว | เว็บคอนโซล `/` |
| ทุกวัน | การแจ้งเตือนที่ `Failed` | การแจ้งเตือน > ประวัติ (กรองสถานะ Failed) |
| ทุกวัน | log ระดับ Warning/Error | LogServer (source `jobdist`) |
| ทุกสัปดาห์ | อัตราสำเร็จ, P95, เวลารอคิว | วิเคราะห์ > รายงานสถิติ Job |
| ทุกสัปดาห์ | ขนาดฐานข้อมูล, การ backup และทดสอบ restore | DBA |
| ทุกเดือน | เวอร์ชัน Worker SDK ที่ยังใช้, การเรียก API ตาม `api.client` | metric `jobdist.api.calls`, หน้า Worker คอลัมน์ Version |
| ตามประกาศ | log `UpstreamVersionMonitor` ที่เตือน `Deprecation`/`Sunset` ของ AuthServer/MessageHub | LogServer |

## 2. Health check

| Endpoint | ใช้กับ | ปกติ | ผิดปกติ |
|---|---|---|---|
| `GET /alive` | liveness (process ยังทำงาน) | 200 `Healthy` | restart container/service |
| `GET /health` | readiness (เข้าถึงฐานข้อมูลได้) | 200 `Healthy` | ถอด instance ออกจาก gateway จนกว่าจะกลับมา |
| `GET /api/v1/admin/summary` (API key Viewer) | ตรวจภาพรวม | `leaderInstance` มีค่า, `workersOnline` > 0 | ดูหัวข้อ 5 |

```bash
curl -fsS http://localhost:8080/health && curl -fsS http://localhost:8081/health
curl -s http://localhost:8080/api/v1/admin/summary -H "X-Api-Key: $VIEWER_API_KEY" | jq '{leaderInstance, workersOnline, activeJobs, pendingByType}'
```

## 3. Metrics (OpenTelemetry meter `JobDist`)

| Metric | ความหมาย | เกณฑ์เตือนที่แนะนำ |
|---|---|---|
| `jobdist.queue.depth` | งานรอคิว (ตาม job type) | เพิ่มต่อเนื่อง 15 นาที หรือใกล้ `maxPending` |
| `jobdist.job.queue_wait` | เวลารอคิว | P95 สูงกว่าค่าปกติ 3 เท่า |
| `jobdist.job.duration` | เวลาทำงาน | P95 สูงผิดปกติ (ดู Anomaly `SlowExecution`) |
| `jobdist.jobs.completed` (tag `job.type`, `outcome`) | งานที่จบ | สัดส่วน Failed > 30% (`Anomaly:FailureRateThreshold`) |
| `jobdist.jobs.retried` | งานที่ลองใหม่ | กระโดดขึ้นทันที |
| `jobdist.workers.online` | Worker ออนไลน์ | ต่ำกว่าจำนวนขั้นต่ำที่ต้องการ |
| `jobdist.leader` | 1 = instance นี้เป็น leader | ผลรวมทุก instance ต้องเป็น 1 พอดี |
| `jobdist.failovers` | งานที่ถูกกระจายใหม่ | > `Anomaly:FailoverStormThreshold` (20) ใน 15 นาที |
| `jobdist.scheduler.cycle.duration` | เวลาต่อรอบของ scheduler | > 1 วินาทีต่อเนื่อง (ฐานข้อมูลช้า) |
| `jobdist.alerts.sent` (tag success) | การส่ง alert | มี Failed ต่อเนื่อง |
| `jobdist.worker.reports.rejected` | รายงานจาก Worker ที่ถูกปฏิเสธ | > 0 (token ผิด, งานถูกกระจายใหม่แล้ว, สงสัยปลอม) |
| `jobdist.api.calls` (tag `api.version`, `api.client`) | การเรียก API ตามเวอร์ชันและ client | ใช้วางแผนเลิก v1 |

ใน docker-compose ดูได้ที่ Aspire Dashboard `http://127.0.0.1:18888` (token อยู่ใน `docker compose logs aspire-dashboard`)

## 4. Log

- server ส่ง log ไป LogServer เมื่อ `Logging:LogServer:Enabled=true` (API key จาก env `LOGSERVER_API_KEY`) ถ้า LogServer ล่ม log ถูกพักใน spool แล้วส่งภายหลัง
- log จากเบราว์เซอร์มาทาง `POST /api/v1/client-logs` มี user id และ `page` ใช้ไล่ปัญหาที่ผู้ใช้แจ้ง
- ทุกคำขอมี `traceId` ใน problem details ใช้ค้นใน LogServer/Aspire
- ไม่มี token, API key, worker token, payload ที่ sensitive หรือที่อยู่ผู้รับแบบเต็มใน log ถ้าพบ ให้แจ้งทีมพัฒนาเป็นเหตุด้านความปลอดภัย
- container log: `docker compose logs -f server-a` (json-file หมุน 10 MB × 3)

## 5. Runbook

### 5.1 ไม่มี leader (`leaderInstance` ว่าง, งานค้าง Pending)

1. ตรวจ `/health` ของทุก instance ถ้าทุกตัวไม่ ready แปลว่าฐานข้อมูลมีปัญหา (ไป 5.6)
2. ดูตาราง lease: `SELECT name, holderid, expiresutc, term FROM leaderleases;`
3. ถ้า `expiresutc` เลยมานาน แต่ไม่มีใครรับต่อ ให้ดู log `LeaderElectionWorker` ของแต่ละ instance (เวลาเครื่องคลาดเคลื่อน, สิทธิ์เขียนตาราง)
4. restart instance ใดก็ได้ ระบบเลือก leader ใหม่ภายใน `JobDist:LeaderLeaseSeconds` (15 วินาที) และส่ง `LeaderChanged`

### 5.2 Worker ออฟไลน์

1. หน้า Worker > รายละเอียด ดู heartbeat ล่าสุดและ offline count
2. งานที่ Worker นั้นถืออยู่ถูกคืนคิวและส่งให้ Worker อื่นแล้ว (ไม่ต้องทำอะไร)
3. ตรวจเครื่อง Worker: process, เครือข่ายไป server, log ของ service, การใช้ CPU/หน่วยความจำ
4. Worker ที่กลับมาจะลงทะเบียนด้วยชื่อเดิมและได้ Worker เดิม ถ้าเครื่องถูกเลิกใช้ ให้ **ถอดออกจากระบบ** ในหน้า Worker
5. ถ้า offline count เพิ่มขึ้นเรื่อย ๆ (Anomaly `WorkerFlapping`) ให้ตรวจเครือข่ายหรือเพิ่ม `JobDist:OfflineAfterSeconds`

### 5.3 คิวเต็ม (`QueueOverLimit`, ผู้ส่งได้ 429 `queue_full`)

1. หน้าภาพรวม/การแจ้งเตือน > เกณฑ์คิว ดู job type ที่เกิน
2. ตรวจว่ามี Worker ที่รองรับ job type นั้นออนไลน์ (หน้า Worker, `GET /api/v1/jobs/types`)
3. แก้ที่ต้นเหตุ: เพิ่ม Worker (`WORKER_REPLICAS` หรือเครื่องใหม่), เพิ่ม `MaxConcurrency`, ตรวจงานที่ช้าผิดปกติ
4. ถ้าเป็นช่วงพีคตามปกติ ปรับ `maxPending` ที่หน้าเกณฑ์คิว (มีผลทันที, ระบบแคชจำนวนงานรอประมาณ `Alerts:PendingCacheMilliseconds`)

### 5.4 งานล้มเหลวจำนวนมาก (`HighFailureRate`)

1. รายการ Job กรองสถานะ Failed และ job type ดู `lastError` และประวัติแต่ละครั้ง
2. ถ้าล้มเหลวบน Worker เดียว: Drain Worker นั้นแล้วตรวจเครื่อง
3. ถ้าล้มเหลวทุก Worker: ปัญหาอยู่ที่ระบบปลายทางหรือ payload ให้แจ้งเจ้าของ job type
4. งานที่ล้มเหลวครบ `maxAttempts` ไม่ถูกลองใหม่อัตโนมัติ ส่งใหม่จากระบบต้นทาง (ใช้ `idempotencyKey` ใหม่)

### 5.5 การแจ้งเตือนไม่ถึง

1. การแจ้งเตือน > ประวัติ ดูสถานะและข้อผิดพลาด
   - `Skipped` = ไม่มีผู้รับที่ตรง event type/ระดับความรุนแรง, ผู้รับถูกปิดหรือลบ, หรือ MessageHub ยังไม่ได้ตั้งค่า (`Enabled`, `BaseUrl`, `ApiKey`)
   - `Pending` ค้าง = ส่งไม่สำเร็จ กำลังรอลองใหม่ (back-off ถึง `Alerts:RetryMaxDelaySeconds`) หรือ circuit breaker เปิด
   - `Failed` = ลองครบ `Alerts:MaxAttempts` (10) แล้ว
2. หน้าผู้รับ กด **ทดสอบส่ง** เพื่อตรวจทางเดินทั้งเส้น
3. ตรวจ MessageHub: URL (`Alerts:MessageHub:BaseUrl`), API key, ช่องทางเปิดอยู่ (`GET /api/v1/alerts/channels`), `ApiBasePath` = `api/v1`
4. ใช้ RabbitMQ: ดู queue `jobdist.alerts` และ dead-letter queue `jobdist.alerts.dead` ใน management UI (`127.0.0.1:15672`)
   ข้อความที่ส่งไม่สำเร็จเกิน `Messaging:RabbitMq:DeliveryLimit` (5) ครั้งจะไปอยู่ที่ `.dead` แก้สาเหตุแล้ว shovel/move กลับไป `jobdist.alerts`

### 5.6 ฐานข้อมูลล่มหรือช้า

- ระหว่างล่ม: API ตอบ 500/503, `/health` ไม่ ready, Worker ลองใหม่เองและ failover ระหว่าง URL งานที่กำลังทำต่อได้ และรายงานผลเมื่อฐานข้อมูลกลับมา
- หลังกลับมา: leader ถูกเลือกใหม่, งานที่ lease หมดระหว่างนั้นถูกคืนคิวอัตโนมัติ ไม่ต้องแก้ข้อมูลเอง
- ช้า: ดู `jobdist.scheduler.cycle.duration`, connection pool (`Maximum Pool Size` / `Max Pool Size` ใน connection string), index ใน [ER_DIAGRAM.md](../diagrams/ER_DIAGRAM.md#2-index-ที่สำคัญ),
  ขนาดตาราง `jobs`/`jobattempts` (ลด `JobDist:RetentionDays` ถ้าโตเกิน)

### 5.7 Login เว็บคอนโซลไม่ได้

1. `AuthServer:Authority` ต้องตั้งไว้และ server เข้าถึงได้ (ลอง `GET {Authority}/.well-known/openid-configuration` จากเครื่อง server)
2. ใน AuthServer: Application `jobdist`, client `jobdist-web` เปิดอยู่, ผู้ใช้มี permission `jobdist.*` ที่ต้องการ
3. 429 `concurrency_limit` = Application `jobdist` เต็มจำนวนผู้ใช้พร้อมกัน (ปรับที่ AuthServer)
4. 502 จาก `/api/v1/auth/*` = proxy ไปหา AuthServer ไม่ได้ ตรวจเครือข่าย/DNS/TLS

## 6. งานบำรุงรักษา

### 6.1 Scale

- **Worker**: เพิ่มเครื่องหรือ replica ได้ทันที ไม่ต้องตั้งค่าที่ server ลดจำนวนด้วย **Drain** ก่อน แล้วค่อยหยุด service (SDK ถอดตัวเองเมื่อ `UnregisterOnShutdown=true`)
- **Server**: เพิ่ม instance ได้ทันที (ใช้ความลับชุดเดียวกัน, `JobDist:InstanceId` ไม่ซ้ำ) แล้วเพิ่ม URL ใน gateway และ `ServerUrls` ของ Worker/Client
- **ฐานข้อมูล**: คอขวดหลักเมื่องานมาก ปรับ connection pool และขยายเครื่องก่อนเพิ่ม server

### 6.2 Backup และ restore

- ตารางทั้งหมดอยู่ในฐานข้อมูลเดียว backup ตามปกติของ DB: PostgreSQL `pg_dump -Fc` รายวัน + WAL archiving, SQL Server full + log backup
- ต้อง backup `JobDist:PayloadProtectionKey` แยกในที่เก็บความลับ ถ้าหาย payload ที่ sensitive และที่อยู่ผู้รับ alert จะถอดรหัสไม่ได้
- หลัง restore: เริ่ม server ระบบตรวจ schema เอง (`AutoMigrate`) งานที่ค้าง Assigned/Running ถูกคืนคิวเมื่อ lease หมด Worker ลงทะเบียนใหม่เอง

```bash
# docker-compose
docker compose exec -T postgres pg_dump -U jobdist -Fc jobdist > jobdist-$(date +%F).dump
docker compose exec -T postgres pg_restore -U jobdist -d jobdist --clean < jobdist-2026-11-27.dump
```

### 6.3 อายุข้อมูล

| ข้อมูล | ค่า | ทำโดย |
|---|---|---|
| งานที่จบแล้ว + ประวัติ | `JobDist:RetentionDays` (30) | `HousekeepingWorker` ทุก 1 ชม. |
| ประวัติการแจ้งเตือน | `Alerts:RetentionDays` (90) | `ConsoleHousekeepingWorker` ทุก 1 ชม. |
| Idempotency-Key | 24 ชม. | `ConsoleHousekeepingWorker` |

รายงานสถิติ Job เลือกช่วงวันได้ไม่เกิน `JobDist:RetentionDays`

### 6.4 เปลี่ยนกุญแจและความลับ

| ความลับ | วิธีเปลี่ยน | ผลกระทบ |
|---|---|---|
| API key | สร้าง key ใหม่ ใส่ SHA-256 ใน `Security:ApiKeys` (มีได้หลายตัว) → เปลี่ยนที่ระบบผู้เรียก → ลบ key เก่า | ไม่มี downtime |
| กุญแจลงนาม (`Crypto:KeyId`, `MlDsaPrivateSeed`, `EcdsaPrivateKeyPkcs8`) | เปลี่ยนพร้อมกันทุก server และใช้ `KeyId` ใหม่ ถ้า Worker ตั้ง `PinnedServerKeyId` ต้องอัปเดตด้วย | Worker ที่ pin key เก่าจะปฏิเสธงานจนกว่าจะอัปเดต |
| `JobDist:PayloadProtectionKey` | ระบบรองรับ key เดียว: รอให้งาน sensitive ที่ค้างจบก่อน แล้วเปลี่ยนพร้อมกันทุก server และกรอกที่อยู่ผู้รับ alert ใหม่ | ข้อมูลที่เข้ารหัสด้วย key เก่าอ่านไม่ได้ |
| `Alerts:MessageHub:ApiKey`, `LOGSERVER_API_KEY`, RabbitMQ password | เปลี่ยนที่ปลายทาง → อัปเดต env → restart ทีละ instance | alert/log ถูกพักใน outbox/spool ระหว่างเปลี่ยน |
| Worker token | ถอด Worker แล้วให้ลงทะเบียนใหม่ | ได้ token ใหม่ |

### 6.5 อัปเกรดเวอร์ชัน (rolling)

1. backup ฐานข้อมูล
2. อัปเกรด server ทีละตัว (schema เปลี่ยนแบบเพิ่มเท่านั้น server เก่าและใหม่อยู่ร่วมกันได้) ตรวจ `/health` ก่อนไปตัวถัดไป
3. อัปเกรด Worker ทีละกลุ่มด้วย Drain → หยุด → อัปเดต → เริ่ม
4. ถ้าตั้ง `JobDist:MinWorkerSdkVersion` Worker ที่เก่ากว่าจะลงทะเบียนไม่ได้
5. ลำดับเมื่อมี API เวอร์ชันใหม่ดู [VERSIONING.md](../VERSIONING.md)

## 7. ค่าที่ปรับบ่อย

| ค่า | ค่าเริ่มต้น | ปรับเมื่อ |
|---|---|---|
| `JobDist:OfflineAfterSeconds` | 20 | เครือข่ายไม่เสถียร (เพิ่ม) หรือต้องการย้ายงานเร็วขึ้น (ลด) ต้องมากกว่า `HeartbeatIntervalSeconds` × 3 |
| `JobDist:LeaderLeaseSeconds` | 15 | ต้องการ failover ของ leader เร็วขึ้น/ช้าลง |
| `JobDist:DefaultMaxAttempts`, `DefaultTimeoutSeconds` | 3, 300 | ค่าเริ่มต้นเมื่อผู้ส่งไม่ระบุ |
| `JobDist:SchedulerBatchSize` | 2000 | งานจำนวนมาก/ฐานข้อมูลแรง |
| `Anomaly:*` | ดู appsettings | alert ความผิดปกติถี่หรือน้อยเกินไป |
| `Alerts:QueueAlertCooldownMinutes` | 30 | alert คิวถี่เกินไป |
| `Mcp:AllowSideEffects` | false | อนุญาตให้ AI agent ส่ง/ยกเลิกงาน |
