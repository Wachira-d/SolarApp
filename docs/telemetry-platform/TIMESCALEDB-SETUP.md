# ติดตั้ง TimescaleDB สำหรับข้อมูล telemetry

คู่มือนี้ย้ายเฉพาะข้อมูล telemetry (ค่าดิบจาก Home Assistant และข้อมูลสรุปราย 5 นาที / ชั่วโมง / วัน)
ไปเก็บใน PostgreSQL + TimescaleDB ข้อมูลธุรกิจ ผู้ใช้ Hangfire และผลวิเคราะห์ยังอยู่ใน SQL Server เหมือนเดิม

## สรุปการตัดสินใจ

| | SQL Server (ตอนนี้) | PostgreSQL ไม่มี TimescaleDB | **TimescaleDB** |
|---|---|---|---|
| พื้นที่ต่อค่าดิบ | ~2–3 ไบต์ (columnstore) | ~80 ไบต์ (วัดจริง) | ~1–3 ไบต์ หลังบีบอัด |
| สรุป 5 นาทีต่อแถว | ~30 ไบต์ | ~95 ไบต์ (วัดจริง) | ~5–10 ไบต์ หลังบีบอัด |
| เพดานขนาด | 10 GB ต่อฐาน (Express) | ไม่มี | ไม่มี |
| ลบข้อมูลเก่า | DELETE ทีละชุด | DELETE ทีละชุด | ทิ้งทั้ง chunk ทันที |
| ค่าใช้จ่าย license | Express ฟรี / Standard มีค่าใช้จ่าย | ฟรี | ฟรี (Apache / Timescale License) |

ตัวเลข "วัดจริง" มาจาก PostgreSQL 16 บนเครื่องพัฒนา ด้วยข้อมูล 15 ล้านแถวดิบและ 14 ล้านแถวสรุป
ตัวเลขของ TimescaleDB เป็นค่าที่คาดจากการบีบอัดแบบ columnar ยังไม่ได้วัดในระบบเรา ให้วัดซ้ำหลังเปิดใช้หนึ่งสัปดาห์ (ข้อ 8)

ความเร็วในการดึงข้อมูล วัดบน PostgreSQL 16 แบบไม่บีบอัด

| คำสั่ง | เวลา |
|---|---|
| 1 ช่อง ข้อมูลดิบ 24 ชม. (กราฟสด) | 2–8 ms |
| 1 ช่อง สรุป 5 นาที 30 วัน (กราฟ) | ~4 ms |
| 1 ไซต์ 150 ช่อง ข้อมูลดิบ 24 ชม. | 100–170 ms |
| 1 ไซต์ 40 ช่อง สรุป 5 นาที 120 วัน (งานวิเคราะห์รายวัน) | ~1 วินาที |

หน้า "วิเคราะห์ระบบ" อ่านผลที่คำนวณเก็บไว้แล้ว จึงเปิดเร็วเท่าเดิมไม่ว่าใช้ฐานไหน

## 1. เลือกเครื่องที่จะติดตั้ง

* **แนะนำ:** เครื่อง Linux แยก (Ubuntu 24.04, 2 vCPU, RAM 4 GB, SSD 100 GB ขึ้นไปสำหรับ 100 ไซต์) อยู่ในวงแลนเดียวกับเครื่อง IIS
* **ทางเลือก:** Docker บนเครื่องใดก็ได้
* **Windows เครื่องเดียวกับ IIS:** ทำได้ แต่ต้องมีไฟล์ TimescaleDB สำหรับ Windows ที่ตรงกับรุ่น PostgreSQL ให้ตรวจที่หน้า release ของ TimescaleDB ก่อน

ต้องใช้ TimescaleDB **2.11 ขึ้นไป** เพราะระบบเขียนข้อมูลย้อนหลังลง chunk ที่บีบอัดแล้ว แนะนำรุ่นล่าสุดของสาย 2.x

## 2. ติดตั้งบน Ubuntu 24.04

```bash
sudo apt install -y gnupg postgresql-common apt-transport-https lsb-release wget
sudo /usr/share/postgresql-common/pgdg/apt.postgresql.org.sh
echo "deb https://packagecloud.io/timescale/timescaledb/ubuntu/ $(lsb_release -c -s) main" \
  | sudo tee /etc/apt/sources.list.d/timescaledb.list
wget --quiet -O - https://packagecloud.io/timescale/timescaledb/gpgkey \
  | sudo gpg --dearmor -o /etc/apt/trusted.gpg.d/timescaledb.gpg
sudo apt update
sudo apt install -y timescaledb-2-postgresql-17 postgresql-client-17
sudo timescaledb-tune --quiet --yes      # ตั้ง shared_preload_libraries และค่าหน่วยความจำ
sudo systemctl restart postgresql
```

### หรือใช้ Docker

```bash
docker run -d --name timescaledb --restart unless-stopped \
  -p 5432:5432 -e POSTGRES_PASSWORD='<รหัสผู้ดูแล>' \
  -v tsdata:/var/lib/postgresql/data \
  timescale/timescaledb:latest-pg17
```

### หรือบน Windows

1. ติดตั้ง PostgreSQL 17 จากตัวติดตั้งของ EDB
2. ดาวน์โหลดไฟล์ zip ของ TimescaleDB สำหรับ Windows ที่ตรงกับ PostgreSQL 17 แตกไฟล์ แล้วรัน `setup.exe` แบบ Administrator
3. ตอบ "yes" เมื่อถามให้แก้ `postgresql.conf` แล้วรีสตาร์ท service `postgresql-x64-17`
4. ถ้าข้ามขั้นแก้ไฟล์ไป หรือ `CREATE EXTENSION` ขึ้นว่า "must be preloaded" ให้เปิด Notepad แบบ Administrator
   แก้ไฟล์ `C:\Program Files\PostgreSQL\<รุ่น>\data\postgresql.conf` บรรทัด `#shared_preload_libraries = ''`
   เป็น `shared_preload_libraries = 'timescaledb'` (ลบ `#` ออก) แล้วรัน `Restart-Service postgresql-x64-<รุ่น>` ใน PowerShell แบบ Administrator

## 3. สร้างฐานข้อมูลและผู้ใช้

เข้า `psql` ด้วยผู้ดูแล (`sudo -u postgres psql`) แล้วรัน

```sql
CREATE DATABASE solar_telemetry;
CREATE USER solarapp WITH PASSWORD '<รหัสผ่านยาว สุ่ม>';
GRANT ALL PRIVILEGES ON DATABASE solar_telemetry TO solarapp;
\c solar_telemetry
CREATE EXTENSION IF NOT EXISTS timescaledb;   -- ต้องรันด้วยผู้ดูแล
GRANT ALL ON SCHEMA public TO solarapp;
SELECT extversion FROM pg_extension WHERE extname = 'timescaledb';   -- ต้องได้ 2.11 ขึ้นไป
```

แอปสร้างตารางเองตอนใช้งานครั้งแรก ไม่ต้องรันสคริปต์ตารางเอง

## 4. เปิดให้เครื่อง IIS เชื่อมต่อ

ใน `/etc/postgresql/17/main/postgresql.conf`

```
listen_addresses = '*'
```

ใน `/etc/postgresql/17/main/pg_hba.conf` เพิ่มเฉพาะ IP ของเครื่อง IIS

```
host  solar_telemetry  solarapp  <IP เครื่อง IIS>/32  scram-sha-256
```

```bash
sudo systemctl restart postgresql
sudo ufw allow from <IP เครื่อง IIS> to any port 5432 proto tcp
```

อย่าเปิดพอร์ต 5432 สู่อินเทอร์เน็ต

## 5. ตั้งค่าในแอป SolarApp

วิธีที่ 1: `appsettings.Production.json` บนเครื่อง IIS

```json
{
  "ConnectionStrings": {
    "TelemetryPostgres": "Host=<IP เครื่องฐานข้อมูล>;Port=5432;Database=solar_telemetry;Username=solarapp;Password=<รหัสผ่าน>;Maximum Pool Size=50;Timeout=15"
  },
  "Telemetry": {
    "Store": "TimescaleDB"
  }
}
```

วิธีที่ 2: ตัวแปรสภาพแวดล้อมของ App Pool ใน IIS (ไม่ต้องเก็บรหัสผ่านในไฟล์)

```
ConnectionStrings__TelemetryPostgres = Host=...;Database=solar_telemetry;Username=solarapp;Password=...
Telemetry__Store = TimescaleDB
```

ค่า `Telemetry:Store`

| ค่า | ผล |
|---|---|
| ไม่ตั้ง หรือ `SqlServer` | ใช้ SQL Server เหมือนเดิม |
| `TimescaleDB` | ใช้ PostgreSQL และ **บังคับ** ต้องมี TimescaleDB ถ้าไม่มีจะหยุดทำงานพร้อมข้อความชัดเจน |
| `Postgres` | ใช้ PostgreSQL ธรรมดา ไม่บีบอัด ใช้ทดสอบเท่านั้น |

## 6. เริ่มใช้งาน

1. ทำก่อนเชื่อมต่อเกตเวย์ Home Assistant ตัวแรก ข้อมูล telemetry ที่อยู่ใน SQL Server แล้วจะไม่ถูกย้ายให้อัตโนมัติ
   ถ้ามีข้อมูลเดิมอยู่แล้ว ให้เปิดหน้า **ผู้ดูแลระบบ → ตรวจสอบระบบ → ที่เก็บข้อมูล Telemetry** (`/Admin/TelemetryStorage`)
   แล้วกด "คัดลอกข้อมูล telemetry เดิมจาก SQL Server" งานทำเบื้องหลัง กดซ้ำได้ไม่เกิดข้อมูลซ้ำ และไม่ลบข้อมูลใน SQL Server
2. Recycle App Pool ใน IIS
3. เปิด log ของแอป ต้องเห็นบรรทัด

```
Telemetry store: PostgreSQL schema public ready (TimescaleDB on)
```

4. ให้ Home Assistant ส่งข้อมูลเข้ามาสักครู่ แล้วตรวจใน `psql`

```sql
SELECT hypertable_name FROM timescaledb_information.hypertables;   -- ต้องเห็น 4 ตาราง
SELECT count(*), max(ts) FROM telemetry_raw;
```

## 7. สิ่งที่ระบบตั้งให้อัตโนมัติ

| ตาราง | ขนาด chunk | บีบอัดหลัง | เก็บนาน |
|---|---|---|---|
| `telemetry_raw` | 1 วัน | 7 วัน | ตาม `Telemetry.RawRetentionDays` (90 วัน) |
| `telemetry_agg_5m` | 7 วัน | 45 วัน | ตาม `Telemetry.FiveMinuteRetentionDays` (400 วัน) |
| `telemetry_agg_1h` | 90 วัน | 120 วัน | ตลอดไป |
| `telemetry_agg_1d` | 365 วัน | ไม่บีบอัด | ตลอดไป |

งานลบข้อมูลเก่าตอน 03:30 น. จะทิ้งทั้ง chunk ที่หมดอายุ ไม่ต้องตั้ง retention policy ใน TimescaleDB เพิ่ม

## 8. ตรวจการบีบอัดหลังใช้งานหนึ่งสัปดาห์

```sql
SELECT pg_size_pretty(before_compression_total_bytes) AS ก่อน,
       pg_size_pretty(after_compression_total_bytes)  AS หลัง
FROM hypertable_compression_stats('telemetry_raw');

SELECT pg_size_pretty(hypertable_size('telemetry_raw'))     AS raw,
       pg_size_pretty(hypertable_size('telemetry_agg_5m'))  AS agg_5m;
```

TimescaleDB รุ่นใหม่เปลี่ยนชื่อฟังก์ชันนี้ ถ้าขึ้นว่าไม่มีฟังก์ชัน ให้ใช้ `hypertable_columnstore_stats('telemetry_raw')` แทน

นำตัวเลขจริงไปคำนวณขนาดดิสก์ตามจำนวนไซต์

## 9. สำรองข้อมูล

```bash
pg_dump -h localhost -U solarapp -Fc -f solar_telemetry_$(date +%F).dump solar_telemetry
```

การกู้คืนฐานที่มี TimescaleDB ต้องครอบด้วยคำสั่งของ TimescaleDB

```sql
CREATE DATABASE solar_telemetry;  \c solar_telemetry
CREATE EXTENSION timescaledb;
SELECT timescaledb_pre_restore();
-- รัน pg_restore -d solar_telemetry <ไฟล์>.dump จาก shell
SELECT timescaledb_post_restore();
```

## 10. ย้อนกลับ

ลบ `Telemetry:Store` หรือตั้งเป็น `SqlServer` แล้ว recycle App Pool ระบบกลับไปใช้ SQL Server ทันที
ข้อมูลที่เข้าระหว่างใช้ TimescaleDB จะยังอยู่ใน PostgreSQL ไม่ถูกย้ายกลับ
