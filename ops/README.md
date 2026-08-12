# การดูแลระบบ (Operations)

สิ่งที่ต้องตั้งค่าบนเซิร์ฟเวอร์ นอกเหนือจากตัวแอป — ทั้งหมดนี้ไม่ได้อยู่ในโค้ด
แต่ถ้าไม่ทำ ระบบจะพังแบบกู้คืนไม่ได้

---

## 1. สำรองข้อมูลอัตโนมัติ ⚠ สำคัญที่สุด

SQL Server Express **ไม่มี SQL Agent** จึงไม่มีอะไรสั่งสำรองข้อมูลให้เอง
ตอนนี้ประวัติการผลิตไฟย้อนหลังหลายปี ประวัติการล้างแผง และหลักฐานงานทั้งหมด
ของลูกค้าทุกราย อยู่บนดิสก์ลูกเดียวในเครื่องเดียว

แอปเพิ่งเพิ่ม soft delete เพื่อกันไม่ให้ประวัติหายจากความผิดพลาดของคนใช้งาน —
แต่มันไม่ช่วยอะไรเลยถ้าดิสก์พัง

### วิธีตั้งค่า

```powershell
# ทดสอบด้วยมือก่อน
powershell -NoProfile -ExecutionPolicy Bypass -File "C:\ops\backup-database.ps1" `
    -ServerInstance "localhost\SQLEXPRESS" `
    -BackupPath "D:\Backups\SolarApp" `
    -OffsitePath "\\nas\backups\solarapp"
```

จากนั้นตั้ง **Task Scheduler** ให้รันทุกวัน:

| ช่อง | ค่า |
|---|---|
| Program | `powershell.exe` |
| Arguments | `-NoProfile -ExecutionPolicy Bypass -File "C:\ops\backup-database.ps1" -ServerInstance "localhost\SQLEXPRESS" -BackupPath "D:\Backups\SolarApp" -OffsitePath "\\nas\backups\solarapp"` |
| Trigger | Daily 02:00 |
| Run whether user is logged on or not | ✓ |
| บัญชีที่ใช้รัน | ต้องเขียนได้ทั้ง 2 path และมีสิทธิ์ `BACKUP DATABASE` |

สคริปต์จะ `RESTORE VERIFYONLY` ทุกครั้ง และ `exit 1` เมื่อล้มเหลว
เพื่อให้ Task Scheduler ขึ้นสถานะ failed — **งานสำรองข้อมูลที่ล้มเหลวเงียบ ๆ
แย่กว่าไม่มีเลย เพราะมันให้ความมั่นใจโดยไม่ได้ให้ความคุ้มครอง**

### สิ่งที่คนมักข้าม

ทดลอง **restore** ไฟล์สำรองลง database ชื่ออื่น แล้วเปิดแอปชี้ไปที่นั้น
อย่างน้อยไตรมาสละครั้ง ไฟล์สำรองที่ไม่เคยถูกกู้คืนคือความเชื่อ ไม่ใช่ข้อมูลสำรอง

### ไฟล์อัปโหลด

สคริปต์นี้สำรองเฉพาะฐานข้อมูล รูปภาพงานล้างแผง/ซ่อม และเอกสารอยู่ในโฟลเดอร์
`wwwroot/uploads` ต้องสำรองแยก (robocopy ไป NAS เดียวกันก็พอ) —
ฐานข้อมูลเก็บแค่ path ไม่ได้เก็บตัวไฟล์

---

## 2. เฝ้าระวังจากภายนอก

แอปมี `/health` (เช็ค DB ด้วย) และ `/ping` (เบาที่สุด) ที่เข้าถึงได้โดยไม่ต้อง login

ตั้ง UptimeRobot / Better Stack / หรือบริการอะไรก็ได้ให้ ping `/health` ทุก 5 นาที
แล้วแจ้งเตือนเข้า LINE หรืออีเมลเมื่อล่ม

**เหตุผลที่ต้องเป็นบริการภายนอก:** ถ้าเซิร์ฟเวอร์ล่ม ระบบแจ้งเตือนที่อยู่บนเซิร์ฟเวอร์
ตัวเดียวกันก็ล่มไปด้วย ตอนนี้เรารู้ว่าเว็บล่มเมื่อลูกค้าโทรมา

การ ping ยังช่วยให้ IIS ไม่ idle-shutdown ด้วย ซึ่งจำเป็นเพราะ Hangfire
(sync ข้อมูล, แจ้งเตือน LINE, สรุปรายวัน) ทำงานในโปรเซสเดียวกับเว็บ —
ถ้าไม่มีคนเข้าเว็บทั้งคืน งานเบื้องหลังก็หยุดไปด้วย

---

## 3. Staging site

ตอนนี้ deploy ตรงขึ้น production — ที่ผ่านมามีเหตุเว็บล่มจากการ deploy
โค้ดที่ยังไม่ได้ทดสอบมาแล้ว 2 ครั้ง

วิธีที่ถูกที่สุด: สร้าง site ที่สองบน IIS เดิม

```
Site name : SolarApp-Staging
Port      : 8081  (หรือ host header staging.solarapp.mangkorn.co.th)
App pool  : SolarAppStaging  (แยก pool — ห้ามใช้ร่วมกับ production)
Database  : SolarAppDb_Staging  (restore จากไฟล์สำรองล่าสุด)
```

ขั้นตอน deploy ที่ควรเป็น: publish → staging → เปิดดูหน้าหลัก, หน้าไซต์งาน,
กด Test Connection สัก 1 ไซต์ → ถ้าปกติค่อย publish ขึ้น production

**สำคัญ:** staging ต้องใช้ database คนละตัว และควรลบ LINE Channel Access Token
ออกจาก staging มิฉะนั้นลูกค้าจริงจะได้รับแจ้งเตือนจากการทดสอบ

---

## 4. ตารางงานเบื้องหลัง (Hangfire)

ปรับได้ที่ **ผู้ดูแลระบบ → ตั้งค่าระบบ → ตารางงานเบื้องหลัง** หรือดูสถานะจริงที่ `/hangfire`

| งาน | ค่าเริ่มต้น | หน้าที่ |
|---|---|---|
| `sync-production-data` | ทุก 15 นาที | ดึงข้อมูลจากคลาวด์ผู้ผลิต |
| `check-system-health` | ทุก 5 นาที | ตรวจออฟไลน์ / ครบกำหนดล้าง / ประกันใกล้หมด |
| `workorder-reminders` | ทุก 30 นาที | เตือนงานที่ใกล้ถึงกำหนดผ่าน LINE |
| `daily-summary-report` | 18:00 น. | สรุปรายวันเข้ากลุ่ม LINE |
| `predictive-diagnostics` | 08:00 น. | วิเคราะห์แนวโน้มความผิดปกติ |
| `data-retention` | 01:00 น. | ลบข้อมูล realtime/log เก่า (ข้อมูลสรุปรายวันไม่ถูกลบ) |
| `contract-expiry` | 08:00 น. | เช็คสัญญาหมดอายุ / ต่ออัตโนมัติ |
| `monthly-customer-report` | วันที่ 1 เวลา 09:00 น. | ส่งสรุปผลผลิตรายเดือนให้ลูกค้าทาง LINE |

---

## 5. หลัง deploy ทุกครั้ง

1. เปิด `/health` — ต้องได้ `"status":"ok"` และ `"dbConnected":true`
2. เปิด **ผู้ดูแลระบบ → ตรวจสอบระบบ → บันทึกการเริ่มระบบ** — ดูว่ามี schema upgrade
   ตัวไหน fail ไหม (แอปจะไม่ล้มถ้า upgrade ตัวใดตัวหนึ่งพลาด แต่ฟีเจอร์ที่ใช้
   คอลัมน์นั้นจะพัง)
3. เปิด `/hangfire` — ดูว่างานทุกตัวขึ้น Scheduled ไม่ใช่ Failed
4. เปลี่ยนค่าใน **ตั้งค่า LINE** แล้วต้อง **รีสตาร์ท app pool** ถ้าเป็นค่าที่อ่านตอน startup
   (external auth) — ค่า LINE token/secret อ่านสดทุกครั้ง ไม่ต้องรีสตาร์ท
