# เอกสาร JobDist

ปรับปรุง 27 พฤศจิกายน 2569

| กลุ่ม | เอกสาร | สำหรับ |
|---|---|---|
| คู่มือ | [manuals/USER_MANUAL.md](manuals/USER_MANUAL.md) | ผู้ใช้เว็บคอนโซล และตัวอย่าง config ให้ระบบอื่นเรียกใช้ (Client SDK, REST, Worker SDK, MCP, Appender ส่ง log ไป LogServer) |
| คู่มือ | [manuals/INSTALLATION.md](manuals/INSTALLATION.md) | การติดตั้ง server, Worker และการเชื่อม AuthServer, MessageHub, LogServer, RabbitMQ |
| ออกแบบ | [ARCHITECTURE.md](ARCHITECTURE.md) | สถาปัตยกรรม, โปรเจกต์, service, HA, ความปลอดภัย, การเชื่อมระบบ |
| ออกแบบ | [diagrams/ER_DIAGRAM.md](diagrams/ER_DIAGRAM.md) | ER Diagram, index, ค่า enum ในฐานข้อมูล |
| ออกแบบ | [diagrams/DATAFLOW.md](diagrams/DATAFLOW.md) | DFD level 0/1 และลำดับการทำงานหลัก |
| ออกแบบ | [diagrams/DEPLOYMENT.md](diagrams/DEPLOYMENT.md) | Docker Compose และ production topology, พอร์ต |
| ออกแบบ | [TRACEABILITY.md](TRACEABILITY.md) | ความสัมพันธ์ หน้าจอ ↔ REST API ↔ Controller ↔ Service ↔ DAO/ADO ↔ Table |
| คู่มือทีม | [guides/DEVELOPER_GUIDE.md](guides/DEVELOPER_GUIDE.md) | นักพัฒนา: build, test, โครงสร้าง, ขั้นตอนเพิ่ม endpoint, ใช้ SDK |
| คู่มือทีม | [guides/OPERATION_GUIDE.md](guides/OPERATION_GUIDE.md) | ผู้ดูแลระบบ: health, metrics, log, runbook, backup, เปลี่ยนกุญแจ, อัปเกรด |
| API | [../CLAUDE.md](../CLAUDE.md) | สรุป REST, MCP และ SDK ทั้งหมด |
| API | [API_EXPORT.md](API_EXPORT.md), [api-export.json](api-export.json) | contract ฉบับเต็ม (สร้างด้วย `tools/JobDist.ApiExport`) |
| API | [VERSIONING.md](VERSIONING.md) | นโยบายเวอร์ชันของ API และการอัปเกรดแบบ rolling |

แผนภาพเขียนด้วย Mermaid แสดงผลได้บน GitHub และ IDE ที่รองรับ Markdown + Mermaid
