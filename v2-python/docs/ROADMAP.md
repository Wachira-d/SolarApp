# SolarApp v2 — Development Roadmap

## Phase 1: Foundation (สัปดาห์ 1-6) — MVP ที่ใช้งานได้จริง

### Sprint 1-2: Core Infrastructure
- [ ] Setup Python project + NiceGUI + FastAPI + SQL Server connection
- [ ] User authentication (JWT + session) with role-based access
- [ ] CRUD: Partners, Customers, Installations, Equipment
- [ ] Photo upload pipeline (EXIF strip, thumbnail, local storage)
- [ ] Database migration system (Alembic)

### Sprint 3-4: Job System + Field Workflow
- [ ] Unified Job model (Installation/Cleaning/MA/Repair)
- [ ] Admin: Job Type Templates (checklist + required photos per type)
- [ ] Mobile-friendly job execution flow:
      Create → Assign → PPE Check → Start → Checklist → Photos → Complete
- [ ] Cleaning workflow: Before/After photos + Yield verification
- [ ] MA workflow: Thermal scan upload + structured checklist
- [ ] Installation workflow: guided photo upload with tooltips

### Sprint 5-6: Inverter API + Alerts
- [ ] Adapter pattern for: Huawei, FoxESS, Sigenergy, GoodWe
- [ ] Background sync (APScheduler) with rate-limiting
- [ ] Health state mapping (Healthy / Faulty / Offline)
- [ ] Alert system + LINE notification pipeline
- [ ] Dashboard: site overview, KPIs, map (Leaflet)

### Phase 1 Deliverable:
> Working system that technicians use daily to manage jobs,
> upload photos, and get LINE alerts. Replaces paper checklists.

---

## Phase 2: Intelligence (สัปดาห์ 7-12) — Smart Features

### Sprint 7-8: Customer Portal + Reports
- [ ] Customer-facing dashboard (Health Score, Savings, ROI)
- [ ] Auto-generated cleaning report (Before/After + Yield %)
- [ ] Annual MA report with thermal analysis summary
- [ ] PDF/Email export + LINE push to customer
- [ ] Digital Document Vault (SLD, permits, warranties)

### Sprint 9-10: AI Features (Phase 1)
- [ ] OCR: auto-read serial numbers from equipment photos
- [ ] OCR: auto-read meter/yield values from inverter display photos
- [ ] Thermal image analysis: auto-detect hotspots (>Xc delta)
- [ ] Photo quality scoring (blur/dark/wrong-angle detection)
- [ ] Auto-captions for customer-facing photo gallery
- [ ] Weather API integration (irradiance + yield correlation)

### Sprint 11-12: Advanced Operations
- [ ] ESG Carbon Tracker (kWh → CO2 offset report)
- [ ] Anomaly detection: fault code parsing → auto-ticket creation
- [ ] Predictive cleaning: health score drop → suggest cleaning
- [ ] Quotation system: AI-suggested battery upsell with ROI calc
- [ ] TOU rate optimizer dashboard (peak/off-peak analysis)

### Phase 2 Deliverable:
> AI-enhanced platform with automatic reports, anomaly detection,
> and a customer portal that builds trust through transparency.

---

## Phase 3: World-Class (สัปดาห์ 13-20) — Differentiation

### Sprint 13-14: Offline-First + PWA
- [ ] Service Worker + IndexedDB for offline checklist/photo capture
- [ ] Background sync queue when connectivity returns
- [ ] Native camera integration (capture + annotate on-device)
- [ ] Push notifications via Web Push API

### Sprint 15-16: Advanced AI
- [ ] ML degradation model: predict panel/inverter degradation curve
- [ ] Smart Energy Advisory: nighttime grid usage → battery upsell
- [ ] AI warranty registration: OCR serial → call manufacturer API
- [ ] PPE detection: camera check for safety gear before job start

### Sprint 17-18: Immersive Operations
- [ ] AR assist: camera overlay with wiring guide (WebXR / 8th Wall)
- [ ] Drone SDK: auto-import DJI/Autel photos + thermal mapping
- [ ] Drone thermal AI: auto-scan entire array for hotspots
- [ ] 3D site model from drone photogrammetry (optional)

### Sprint 19-20: Enterprise + Scale
- [ ] Multi-tenant white-label (partner branding)
- [ ] Dynamic TOU optimizer: AI controls battery charge/discharge
- [ ] Corporate ESG dashboard with exportable sustainability reports
- [ ] API gateway for third-party integrations
- [ ] Performance optimization + load testing

### Phase 3 Deliverable:
> The world's best solar management platform — AI-driven,
> offline-capable, AR-assisted, with enterprise-grade reporting.

---

## Migration Strategy (from current .NET system)

Since solarapp.mangkorn.co.th already runs a production .NET 8 system,
the migration should be gradual:

1. **Phase 1 parallel**: Run v2 on a subdomain (v2.solarapp.mangkorn.co.th)
   with the SAME SQL Server database. NiceGUI reads/writes the same tables.
2. **Phase 1 cutover**: Once v2 reaches feature parity for the daily
   technician workflow, redirect the main domain.
3. **Phase 2+**: Build new features exclusively in v2.

This avoids a Big Bang rewrite and lets both systems coexist.

---

## Tech Stack Summary

| Layer | Technology | Why |
|---|---|---|
| Frontend | NiceGUI (Quasar/Vue) | Pure Python, mobile-friendly, real-time |
| API | FastAPI | Async, OpenAPI docs, shares process with NiceGUI |
| ORM | SQLAlchemy 2.0 async | Mature, SQL Server support, migrations via Alembic |
| Database | SQL Server | Existing investment, enterprise support |
| Cache/Queue | Redis | APScheduler job store + API response cache |
| Background | APScheduler + asyncio | Lightweight, in-process, no Celery overhead |
| AI/ML | ONNX Runtime + Pillow | Edge inference for OCR/thermal, no GPU required |
| Notifications | LINE Messaging API + SMTP | Direct push to technicians and customers |
| Storage | Local disk → Azure Blob | Start local, migrate to cloud when ready |
| Auth | JWT + httponly cookies | Stateless API + secure web sessions |
