# การเปลี่ยนเวอร์ชันระหว่างบริการ (Versioning and compatibility)

ปรับปรุง 27 พฤศจิกายน 2569 · ใช้กับ JobDist 2.0

เอกสารนี้ระบุว่าแต่ละช่องทางที่ JobDist คุยกับระบบอื่นมีเวอร์ชันอย่างไร การเปลี่ยนแบบไหนไม่กระทบผู้ใช้เดิม แบบไหนนับเป็น breaking change และขั้นตอนออกเวอร์ชันใหม่โดยไม่ให้บริการหยุด

## 1. แผนที่ช่องทางและกลไกเวอร์ชัน

| ช่องทาง | ทิศทาง | กลไกเวอร์ชัน | ถ้าอีกฝั่งเก่ากว่า/ใหม่กว่า |
|---|---|---|---|
| REST API (`/api/v1/...`) | ระบบอื่น, SDK → JobDist | URL segment `v1` (alias `/api/...` = v1, header `api-version`, query `?api-version`) response มี `api-supported-versions` | เวอร์ชันที่ไม่มี: URL ได้ 404, header/query ได้ 400 |
| `JobDist.Client`, `JobDist.Worker.Sdk` | SDK → JobDist | เรียก `api/v1/...` ตายตัว และส่ง `User-Agent: JobDist.Client/x.y.z` / `JobDist.Worker.Sdk/x.y.z` | server ไม่มี v1 แล้ว: SDK ได้ error ชัดเจน ไม่เปลี่ยนความหมายเงียบ ๆ |
| Worker protocol | Worker → JobDist | `RegisterWorkerRequest.version` + `JobDist:MinWorkerSdkVersion` | Worker เก่ากว่าค่าขั้นต่ำ ลงทะเบียนไม่ได้ (400 พร้อมข้อความให้อัปเกรด) |
| MCP tools (`/mcp`) | AI agent → JobDist | ชื่อ tool + JSON schema ที่ client อ่านใหม่ทุกครั้งที่เชื่อมต่อ | ดูกฎข้อ 2 |
| เว็บคอนโซล | เบราว์เซอร์ → JobDist | ฝังอยู่ใน server ออกพร้อมกันเสมอ ไฟล์มี fingerprint | เปิดหน้าใหม่ได้เวอร์ชันล่าสุด |
| ข้อความ RabbitMQ | JobDist → JobDist | property `type` = `jobdist.alert.v1` เนื้อความเป็นเพียง id ของแถว outbox (claim-check) | type ที่ไม่รู้จัก ส่งเข้า dead-letter แล้ว outbox ส่งใหม่หลัง `RelayLockSeconds` ให้ instance ที่รู้จัก ไม่มีข้อความหาย |
| ฐานข้อมูล | server หลายตัว | schema แบบเพิ่มอย่างเดียว (idempotent script) + `leaderleases.term` | server เวอร์ชันเก่าและใหม่รันพร้อมกันได้ระหว่าง rolling upgrade |
| AuthServer | JobDist → AuthServer | AuthServer เหลือแต่ `api/v1/...` (path ไม่มีเลขได้ 404) เว็บ (webcommon) เรียก `/api/v1/auth/*`, `/api/v1/account/*` ผ่าน proxy ของ JobDist ซึ่งส่งต่อไป AuthServer ตรง ๆ ส่วน path เก่า `/api/auth/*`, `/api/account/*` ยังรับอยู่และแปลงเป็น `/api/v1/...` ตรวจ JWT ด้วย JWKS (รีเฟรชทุก 10 นาที รองรับการหมุนกุญแจ) ถ้า AuthServer ส่ง header `Deprecation`/`Sunset` จะเขียน log เตือนวันละครั้ง | AuthServer ออก v2: อัปเดต webcommon และเพิ่ม route `/api/v2/...` ใน proxy |
| MessageHub | JobDist → MessageHub | เรียก `/api/v1/messages` และ `/api/v1/channels` (`Alerts:MessageHub:ApiBasePath` ค่าเริ่มต้น `api/v1`; path ไม่มีเลขของ MessageHub deprecated, sunset 2027-05-31) อ่าน `code` ใน problem response และเขียน log เตือนวันละครั้งเมื่อเจอ `Deprecation`/`Sunset` | MessageHub ออก v2: เปลี่ยนค่าเป็น `api/v2` หลังตรวจสัญญาแล้ว |
| LogServer | JobDist → LogServer | `Common.Logging` ส่งที่ `/api/v1/logs` | อัปเดตผ่าน submodule `external/common` |
| Submodule (`external/common`, `external/webcommon`) | ตอน build | ตรึง commit ไว้ใน repo | อัปเดตเมื่อพร้อม แล้ว build/test ใหม่ |

## 2. การเปลี่ยนแบบไหนกระทบใคร

**ไม่กระทบ (ทำใน v1 ได้เลย)**

- เพิ่ม endpoint หรือ MCP tool ใหม่
- เพิ่ม field ใน response (client ทุกตัวใช้ System.Text.Json ซึ่งไม่สนใจ field ที่ไม่รู้จัก)
- เพิ่ม field ใน request ที่ไม่บังคับและมีค่าเริ่มต้นที่ให้ผลเหมือนเดิม
- เพิ่ม parameter ที่ไม่บังคับใน MCP tool
- เพิ่มตารางหรือคอลัมน์ที่ nullable หรือมีค่าเริ่มต้น
- ผ่อนการตรวจสอบ เช่น เพิ่มขนาดสูงสุด

**Breaking change (ต้องเป็นเวอร์ชันใหม่)**

- ลบหรือเปลี่ยนชื่อ endpoint, field, MCP tool หรือ parameter
- เปลี่ยนชนิดข้อมูลหรือความหมายของ field เดิม
- เพิ่ม field ที่บังคับ หรือตรวจสอบเข้มขึ้น
- **เพิ่มค่าใหม่ใน enum ที่ส่งออกไป** เช่น `JobStatus`, `AttemptOutcome`, `WorkerStatus` เพราะ SDK เวอร์ชันเก่าแปลงค่าที่ไม่รู้จักไม่ได้ ถ้าจำเป็นต้องเพิ่ม ให้ออก SDK ที่รู้จักค่าใหม่ก่อน ตั้ง `MinWorkerSdkVersion` แล้วจึงเปิดใช้ค่าใหม่ หรือรวมไว้ใน v2
- เปลี่ยนเนื้อความหรือความหมายของข้อความ RabbitMQ (ต้องใช้ type ใหม่ เช่น `jobdist.alert.v2`)
- ลบหรือเปลี่ยนชนิดคอลัมน์ในฐานข้อมูล (ทำแบบ expand/contract ตามข้อ 3)

## 3. ขั้นตอนออกเวอร์ชันใหม่ (ไม่ให้บริการหยุด)

ลำดับที่ใช้ทุกครั้ง: **ฐานข้อมูล → server ทีละตัว → Worker/SDK → เก็บกวาด**

1. **Expand**: เพิ่มตาราง/คอลัมน์ใหม่ (nullable หรือมีค่าเริ่มต้น) ใน `db/postgres/schema.sql` และ `db/sqlserver/schema.sql` server เก่ายังทำงานได้
2. **Rolling upgrade server**: อัปเกรดทีละตัว ระหว่างนั้นเก่าและใหม่รันพร้อมกัน
   - ผู้บริโภคข้อความ RabbitMQ ต้องรับ type เดิมต่อไปอย่างน้อยหนึ่ง release (`AlertMessageTypes.IsSupported`) และค่อยเปลี่ยน `AlertMessageTypes.Current` ไปเป็น type ใหม่ใน release ถัดไป
   - leader ถูกเลือกใหม่อัตโนมัติ (`term` กันไม่ให้ leader เก่าเขียนทับ)
3. **Worker และ SDK**: ออกเวอร์ชันใหม่ ถ้าต้องบังคับ ตั้ง `JobDist:MinWorkerSdkVersion`
4. **Contract**: ลบคอลัมน์หรือของเก่าใน release ถัดไป เมื่อไม่มีใครใช้แล้ว
5. **เอกสาร**: รัน `dotnet run --project tools/JobDist.ApiExport -- docs` แล้วอัปเดต `CLAUDE.md`

**ดูว่าใครยังเรียกเวอร์ชันไหน**: metric `jobdist.api.calls` (OpenTelemetry/Aspire) แยกตาม `api.version` และ `api.client` (เช่น `JobDist.Client/2.0.0`, `JobDist.Worker.Sdk/1.4.2`, `web`, `other`) ใช้ตรวจก่อนเลิกเวอร์ชันหรือก่อนเพิ่ม `MinWorkerSdkVersion`

## 4. นโยบายเวอร์ชันหลักของ REST API

ตามที่ตกลงไว้ (27 พ.ย. 2569, แทนคำตอบ Q20 เดิม): breaking change ออกเป็น v2 และ **เปิด v1 คู่กับ v2 ต่ออีก 1 release** (release ที่ออก v2)
v1 ถูกติดป้าย deprecated และแจ้งวันเลิกใช้ แล้วจึงลบ v1 ใน release ถัดไป ระบบอื่น SDK และ Worker จึงทยอยอัปเกรดได้เองภายในช่วงนั้น ไม่ต้องอัปเกรดพร้อม server

| release | v1 | v2 | alias `/api/...` |
|---|---|---|---|
| N (ปัจจุบัน) | ใช้งานปกติ | – | v1 |
| N+1 (ออก v2) | ยังใช้ได้ แต่ deprecated: response มี `api-deprecated-versions: 1.0`, `Deprecation` และ `Sunset` (วันที่ปล่อย N+2 ตามแผน) | ใช้งานปกติ | v2 |
| N+2 | ลบแล้ว (URL ได้ 404) | ใช้งานปกติ | v2 |

ขั้นตอนใน release N+1 (ออก v2):

1. controller ที่เปลี่ยนแบบ breaking: เพิ่ม `[ApiVersion("2.0")]` (route `api/v{version:apiVersion}/...` ทำไว้แล้ว) และติด `[ApiVersion("1.0", Deprecated = true)]` ให้ของเดิม
   ถ้าพฤติกรรมต่างกันให้แยก action ด้วย `[MapToApiVersion]` ส่วน controller ที่ไม่เปลี่ยนให้รองรับทั้ง 1.0 และ 2.0
2. เพิ่ม header `Deprecation` และ `Sunset` ให้ response ของ v1 (middleware ตามวันที่ใน config) เพื่อให้ระบบที่เรียกเขียน log เตือนได้ แบบเดียวกับที่ JobDist อ่านจาก AuthServer/MessageHub
3. ออก `JobDist.Client` และ `JobDist.Worker.Sdk` เวอร์ชันหลักใหม่ที่เรียก `api/v2/` (SDK เก่ายังใช้ v1 ได้ตลอด release นี้)
4. ตั้ง `DefaultApiVersion` เป็น 2.0 (alias `/api/...` ชี้ v2) และแจ้งเจ้าของระบบที่เรียก JobDist พร้อมวันเลิกใช้ v1
5. ติดตาม metric `jobdist.api.calls` แยกตาม `api.version` และ `api.client` ว่าใครยังเรียก v1

ขั้นตอนใน release N+2 (เลิก v1):

1. ตรวจ `jobdist.api.calls` ว่าไม่มีการเรียก v1 แล้ว (ถ้ายังมี ให้แจ้งเจ้าของระบบหรือเลื่อนออกไปอีก release)
2. ลบ `ApiVersion("1.0")` และโค้ดของ v1, ตั้ง `JobDist:MinWorkerSdkVersion` เป็น SDK ที่เรียก v2
3. รัน `dotnet run --project tools/JobDist.ApiExport -- docs` แล้วอัปเดต `CLAUDE.md` และเอกสารที่เกี่ยวข้อง
