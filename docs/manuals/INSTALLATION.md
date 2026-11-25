# คู่มือการติดตั้ง JobDist (Installation Manual)

เวอร์ชัน 2.0 · ปรับปรุง 25 พฤศจิกายน 2569

คู่มือนี้อธิบายการติดตั้ง JobDist Server (REST API + MCP + เว็บคอนโซล) พร้อมการเชื่อมต่อ AuthServer, LogServer, MessageHub และ RabbitMQ

---

## 1. องค์ประกอบ

| ส่วน | หน้าที่ | จำเป็น |
|---|---|---|
| JobDist Server | API, MCP, เว็บคอนโซล (Blazor WebAssembly ฝังอยู่ใน server) ติดตั้งได้หลายเครื่องพร้อมกัน (active-active) | ✓ |
| ฐานข้อมูล | PostgreSQL 15+ หรือ SQL Server 2019+ | ✓ |
| Worker | เครื่องประมวลผลที่ใช้ `JobDist.Worker.Sdk` | ✓ |
| AuthServer | เข้าสู่ระบบและสิทธิ์ของเว็บคอนโซล | ✓ สำหรับเว็บ |
| MessageHub | ส่งแจ้งเตือน | แนะนำ |
| LogServer | รวม log กลาง (ผ่าน Common.Logging) | ไม่บังคับ |
| RabbitMQ 3.13+/4.x | ช่องทางส่งแจ้งเตือนแบบ broker | ไม่บังคับ (ค่าเริ่มต้นใช้ตาราง outbox ในฐานข้อมูล) |
| ผู้ให้บริการ AI | ผู้ช่วย AI ในเว็บ | ไม่บังคับ |

## 2. สิ่งที่ต้องเตรียม

- .NET SDK 10 (สำหรับ build) หรือ Docker 24+ พร้อม Compose v2 และ BuildKit
- Git (ต้องดึง submodule)
- ไฟล์ license DevExpress (`DevExpress_License.txt`) ถ้าไม่มี เว็บจะขึ้นแถบ trial และ build จะแสดงคำเตือน DX1000/DX1001
- ชื่อโดเมนและใบรับรอง TLS สำหรับ reverse proxy หน้า server

## 3. ดึงซอร์สโค้ด

```bash
git clone https://github.com/mrpichet/jobdist.git
cd jobdist
git submodule update --init --recursive    # external/common และ external/webcommon
```

ถ้าข้ามขั้น submodule การ build และ `docker build` จะล้มเหลว

## 4. ตั้งค่า AuthServer (ทำครั้งเดียว)

ทำใน AuthServer โดยผู้ที่มีสิทธิ์ admin การสร้าง/แก้ไขในหน้า admin ของ AuthServer ต้องให้ผู้ดูแลอีกคนอนุมัติ (maker-checker) ก่อนมีผล

1. **Application** (`api/v1/admin/applications`): code `jobdist`, ชื่อ "JobDist ระบบกระจายงาน", กำหนดจำนวนผู้ใช้พร้อมกันตามต้องการ (0 = ไม่จำกัด)
2. **Permission** (`api/v1/admin/permissions` หรือ Import ไฟล์): applicationCode `jobdist` จำนวน 16 รายการ ใช้ไฟล์ CSV นี้ Import ได้ในครั้งเดียว

   ```csv
   ApplicationCode,Code,Name,Description
   jobdist,jobdist.dashboard.view,ดูภาพรวมระบบ,
   jobdist,jobdist.job.view,ดู Job,
   jobdist,jobdist.job.create,ส่ง Job,
   jobdist,jobdist.job.edit,เปลี่ยน Priority ของ Job,
   jobdist,jobdist.job.cancel,ยกเลิก Job,
   jobdist,jobdist.job.export,ส่งออกรายการ Job,
   jobdist,jobdist.worker.view,ดู Worker,
   jobdist,jobdist.worker.drain,Drain Worker,
   jobdist,jobdist.worker.unregister,ถอด Worker ออก,
   jobdist,jobdist.insight.view,ดูการวิเคราะห์,
   jobdist,jobdist.alert.send,ส่งแจ้งเตือนทันที,
   jobdist,jobdist.alert.view,ดูการแจ้งเตือน,
   jobdist,jobdist.alert.manage,จัดการผู้รับแจ้งเตือน,
   jobdist,jobdist.queue.manage,จัดการเกณฑ์คิว,
   jobdist,jobdist.report.view,ดูรายงาน,
   jobdist,jobdist.agent.use,ใช้ผู้ช่วย AI,
   ```

3. **OAuth client** (`PUT api/v1/admin/clients`):

   ```json
   { "clientId": "jobdist-web", "name": "JobDist Web", "applicationCode": "jobdist",
     "redirectUris": "", "allowedGrantTypes": "password refresh_token",
     "allowedScopes": "openid profile email", "maxConcurrentUsers": 0,
     "requireMfa": false, "requireCompliantDevice": false, "enabled": true,
     "public": true, "generateSecret": false }
   ```

   ตั้ง `requireMfa` เป็น `true` ได้ถ้านโยบายหน่วยงานต้องการ

4. **Role / Group**: สร้างบทบาทแล้วผูก permission ข้างต้น เช่น `jobdist-viewer`, `jobdist-operator`, `jobdist-admin` (ดูตัวอย่างในคู่มือการใช้งานหัวข้อ 10) แล้วกำหนดให้ผู้ใช้หรือกลุ่ม
5. จดค่า **Issuer URL** ของ AuthServer ไว้ใช้เป็น `AuthServer:Authority`

เว็บคอนโซลเรียก AuthServer ผ่าน proxy ใน JobDist Server เอง (`/api/auth/*`, `/api/account/*`) จึงไม่ต้องเปิด CORS ที่ AuthServer

## 5. ตั้งค่า MessageHub

1. ขอ API key ของ MessageHub สำหรับระบบ JobDist (role User ขึ้นไป)
2. ตรวจว่ามีช่องทาง (เช่น Email) เปิดใช้งานใน MessageHub
3. ใส่ค่าใน `Alerts:MessageHub` (หัวข้อ 7) ผู้รับแจ้งเตือนกำหนดในเว็บคอนโซล เมนู การแจ้งเตือน > ผู้รับการแจ้งเตือน ไม่ใช่ใน appsettings

## 6. ติดตั้งด้วย Docker Compose (แนะนำ)

ชุด compose มี server 2 ตัว (HA), Worker ตัวอย่าง, PostgreSQL และ Aspire dashboard

```bash
cp .env.example .env
# แก้ .env ทุกค่า REPLACE_... และสร้าง secret ตามคำสั่งในคอมเมนต์ เช่น
openssl rand -base64 32                       # PAYLOAD_PROTECTION_KEY, MLDSA_PRIVATE_SEED, API keys
printf '%s' 'the-api-key' | sha256sum         # *_API_KEY_SHA256 (server เก็บเฉพาะ hash)
openssl rand -base64 24                       # POSTGRES_PASSWORD, RABBITMQ_PASSWORD
chmod 600 .env
```

ค่าที่เพิ่มในเวอร์ชันนี้:

| ตัวแปร | ความหมาย |
|---|---|
| `AUTHSERVER_AUTHORITY` | URL ของ AuthServer (ว่าง = ใช้ได้เฉพาะ API key, เว็บเข้าสู่ระบบไม่ได้) |
| `MESSAGEHUB_ENABLED`, `MESSAGEHUB_URL`, `MESSAGEHUB_API_KEY` | การเชื่อม MessageHub |
| `MESSAGING_TRANSPORT` | `Database` (ค่าเริ่มต้น) หรือ `RabbitMq` |
| `RABBITMQ_USER`, `RABBITMQ_PASSWORD` | บัญชี RabbitMQ (ต้องตั้งรหัสผ่านไว้เสมอ แม้ไม่ได้ใช้ RabbitMQ) |
| `LOGSERVER_ENABLED`, `LOGSERVER_URLS`, `LOGSERVER_API_KEY` | ส่ง log ไป LogServer |
| `AGENTCHAT_*` | ผู้ช่วย AI (ดูหัวข้อ 7.6) |
| `DEVEXPRESS_LICENSE_FILE` | path ไฟล์ license DevExpress บนเครื่อง build |

เริ่มระบบ:

```bash
docker compose up -d --build                         # ใช้ตาราง outbox
docker compose --profile rabbitmq up -d --build      # พร้อม RabbitMQ (ตั้ง MESSAGING_TRANSPORT=RabbitMq)
docker compose -f docker-compose.yml -f docker-compose.sqlserver.yml up -d --build   # ใช้ SQL Server
```

server ฟังที่ `127.0.0.1:8080` และ `127.0.0.1:8081` ให้วาง reverse proxy (nginx, IIS, YARP หรือ load balancer) ที่ทำ TLS ไว้หน้า server ทั้งสองตัว โดยส่ง header `X-Forwarded-For` และ `X-Forwarded-Proto`

## 7. ตั้งค่า (appsettings หรือ environment variable)

ใช้ `appsettings.Production.json` หรือ environment variable (`:` เปลี่ยนเป็น `__` เช่น `AuthServer__Authority`) **ห้ามเก็บรหัสผ่าน, API key หรือ secret ในไฟล์ที่ commit** ให้ใช้ environment variable หรือ secret store

### 7.1 ฐานข้อมูล
```json
"Database": { "Provider": "PostgreSql", "ConnectionString": "<จาก secret>", "AutoMigrate": true }
```
`AutoMigrate=true` สร้างตารางใหม่ของเวอร์ชันนี้ให้อัตโนมัติตอนเริ่ม (alertrecipients, alertoutbox, queuethresholds, idempotencyrecords และคอลัมน์ term ของ leaderleases) script อยู่ที่ `db/postgres/schema.sql` และ `db/sqlserver/schema.sql` ถ้าต้องการให้ DBA รันเอง ให้ตั้ง `AutoMigrate=false`

### 7.2 AuthServer
```json
"AuthServer": { "Authority": "https://auth.example.com", "Audience": "authserver-api", "RequireHttpsMetadata": true, "AdminRole": "admin" }
```

### 7.3 การแจ้งเตือน
```json
"Alerts": {
  "MaxAttempts": 10, "RetryBaseDelaySeconds": 5, "RetryMaxDelaySeconds": 900, "CircuitOpenDelaySeconds": 30,
  "RetentionDays": 90, "QueueCheckSeconds": 15, "QueueAlertCooldownMinutes": 30, "PendingCacheMilliseconds": 2000,
  "MessageHub": { "Enabled": true, "BaseUrl": "https://messagehub.example.com", "ApiKey": "<จาก secret>", "Sign": true, "TimeoutSeconds": 10 }
},
"Anomaly": { "AlertCooldownMinutes": 30, "MinAlertSeverity": "Warning" }
```
- ส่งไม่สำเร็จแบบชั่วคราว ระบบลองใหม่โดยเว้นระยะ 5 วินาที เพิ่มเป็นเท่าตัวจนถึง 15 นาที (สุ่ม ±20%) ไม่เกิน 10 ครั้ง ข้อผิดพลาดถาวร (400/401/403/404) หยุดทันที
- `PendingCacheMilliseconds` คือความสดของจำนวนงานรอที่ใช้ตัดสินคิวเต็ม ค่าน้อยแม่นขึ้นแต่โหลดฐานข้อมูลมากขึ้น

### 7.4 RabbitMQ (เมื่อ `Messaging:Transport = RabbitMq`)
```json
"Messaging": { "Transport": "RabbitMq",
  "RabbitMq": { "HostName": "rabbitmq", "Port": 5672, "VirtualHost": "/", "UserName": "jobdist", "Password": "<จาก secret>",
                "UseTls": false, "Exchange": "jobdist.alerts", "Queue": "jobdist.alerts", "DeadLetterExchange": "jobdist.alerts.dlx",
                "PrefetchCount": 20, "RelayLockSeconds": 300, "DeliveryLimit": 5 } }
```
ระบบยังเขียนตาราง outbox ก่อนเสมอ แล้วจึงส่งต่อเข้า RabbitMQ (persistent + publisher confirm) จึงไม่เสียข้อความถ้า RabbitMQ ล่มชั่วคราว ระบบสร้าง exchange, quorum queue และ dead-letter queue ให้เอง ข้อความที่ส่งไม่สำเร็จเกิน `DeliveryLimit` ครั้งจะไปอยู่ใน dead-letter queue ใน production ควรเปิด `UseTls`

### 7.5 Log
```json
"Logging": { "LogServer": { "Enabled": true, "ServerUrls": "https://logserver.example.com", "Source": "jobdist", "MinimumLevel": "Information" } },
"ClientLogging": { "Enabled": true, "MinimumLevel": "Warning" }
```
API key ของ LogServer อ่านจาก environment variable `LOGSERVER_API_KEY` log จากเบราว์เซอร์ส่งผ่าน server (`POST /api/client-logs`) แล้วเข้า LogServer พร้อมรหัสผู้ใช้ ระบบไม่บันทึก token, รหัสผ่าน หรือ secret ลง log

### 7.6 ผู้ช่วย AI
```json
"AgentChat": { "Enabled": true, "Provider": "OpenAI", "Endpoint": "", "Model": "<ชื่อโมเดล>", "ApiKey": "<จาก secret>", "MaxHistory": 20, "TimeoutSeconds": 60 }
```
Provider อื่นที่เข้ากันได้กับ OpenAI API ให้ใส่ `Endpoint` ด้วย ผู้ช่วยใช้เครื่องมืออ่านอย่างเดียว ถ้าตั้งไม่ครบ ระบบปิดผู้ช่วยเองและซ่อนปุ่มในเว็บ

### 7.7 อื่น ๆ
- `JobDist:MinWorkerSdkVersion`: ปฏิเสธ Worker ที่ใช้ SDK เก่ากว่าเวอร์ชันนี้ (ว่าง = ไม่ตรวจ)
- `Security:ApiKeys`: API key ของ Worker, SDK และ MCP ใช้ได้เหมือนเดิม ใน Production endpoint ใหม่ของคอนโซล (การแจ้งเตือน เกณฑ์คิว รายงาน) ใช้ได้เฉพาะ key บทบาท Admin
- ค่าของเว็บ (ชื่อหน่วยงาน, ผู้ติดต่อ, นโยบายความเป็นส่วนตัว, ภาพหน้า login) อยู่ใน `src/JobDist.Web/wwwroot/appsettings.json` แก้ก่อน build หรือแทนไฟล์ใน `wwwroot` ของ server ที่ publish แล้ว

## 8. ติดตั้งแบบไม่ใช้ Docker

```bash
# วางไฟล์ license DevExpress ที่ $HOME/.config/DevExpress/DevExpress_License.txt (Linux) ก่อน publish
dotnet publish src/JobDist.Server -c Release -o /opt/jobdist
cd /opt/jobdist
export ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://127.0.0.1:8080
export Database__ConnectionString='...' AuthServer__Authority='https://auth.example.com' # และ secret อื่น
dotnet JobDist.Server.dll
```
ติดตั้งเป็น service (systemd หรือ Windows Service) และให้ server ทุกตัวใช้ `JobDist:PayloadProtectionKey`, `Crypto:MlDsaPrivateSeed`, `Crypto:EcdsaPrivateKeyPkcs8` ชุดเดียวกัน กำหนด `JobDist:InstanceId` ไม่ซ้ำกันในแต่ละเครื่อง

## 9. ตรวจสอบหลังติดตั้ง

1. `curl https://jobdist.example.com/alive` ได้ `Healthy` และ `/health` ได้ `Healthy` (เชื่อมฐานข้อมูลได้)
2. เปิดเว็บ เข้าสู่ระบบด้วยบัญชีที่มีสิทธิ์ `jobdist.*` เห็นหน้าภาพรวม
3. เมนู การแจ้งเตือน > ผู้รับการแจ้งเตือน เพิ่มผู้รับ แล้วกด **ส่งทดสอบ** ตรวจว่าข้อความมาถึง และหน้าประวัติแสดงสถานะ "ส่งแล้ว"
4. เพิ่มเกณฑ์คิว เช่น `*` สูงสุด 100,000 งาน เตือนที่ 80%
5. เริ่ม Worker ด้วย API key บทบาท Worker แล้วตรวจว่า Worker ขึ้นสถานะออนไลน์ และผู้รับได้แจ้งเตือน "Worker ลงทะเบียน"
6. ถ้าใช้ LogServer ตรวจว่ามี log จาก source `jobdist`
7. ถ้าใช้ RabbitMQ เปิด management UI (`http://127.0.0.1:15672`) ตรวจว่ามี queue `jobdist.alerts`

## 10. อัปเกรดจากเวอร์ชันก่อน

- หน้าเว็บเดิม (`src/JobDist.UI`, เข้าด้วย API key) ถูกแทนด้วย `src/JobDist.Web` ซึ่งเข้าสู่ระบบผ่าน AuthServer ต้องตั้งค่า AuthServer ตามหัวข้อ 4 ก่อน
- ค่า `Alerts:MessageHub:Channel` และ `Alerts:MessageHub:Recipients` เลิกใช้ ให้เพิ่มผู้รับในเว็บแทน
- API ย้ายไปที่ `api/v1/...` ส่วน `api/...` เดิมยังใช้ได้และเท่ากับ v1 SDK และ Worker เดิมไม่ต้องแก้
- งานใหม่อาจถูกปฏิเสธด้วย 429 `queue_full` เมื่อมีการตั้งเกณฑ์คิว ระบบที่ส่งงานควรรอตาม `Retry-After` แล้วส่งใหม่
- ตารางใหม่ถูกสร้างอัตโนมัติเมื่อ `AutoMigrate=true` สำรองฐานข้อมูลก่อนอัปเกรดเสมอ

## 11. ความปลอดภัย

- เก็บ secret ทั้งหมดใน environment variable หรือ secret store ไม่ commit ลง git (`.env` อยู่ใน `.gitignore`)
- ใช้ HTTPS ทุกจุด (AuthServer, MessageHub, LogServer, RabbitMQ ผ่าน TLS)
- server ส่ง header ความปลอดภัย (CSP, X-Frame-Options, nosniff, Referrer-Policy) ให้อัตโนมัติ
- ที่อยู่ผู้รับแจ้งเตือนเข้ารหัสด้วย `JobDist:PayloadProtectionKey` ถ้าเปลี่ยน key ต้องกรอกที่อยู่ผู้รับใหม่
- ไฟล์ license DevExpress ส่งเข้า build เป็น BuildKit secret ไม่ติดใน image ห้าม commit ไฟล์นี้
