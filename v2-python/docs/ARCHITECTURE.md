# SolarApp v2 — World-Class Solar Management Platform
# Architecture Document

## System Architecture Overview

```
┌────────────────────────────────────────────────────────────────────┐
│                        CLIENTS                                      │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐  ┌──────────────────┐   │
│  │ NiceGUI  │  │  LINE    │  │  Mobile  │  │ Drone / Thermal  │   │
│  │ Web App  │  │  LIFF    │  │  PWA     │  │  Camera Upload   │   │
│  └────┬─────┘  └────┬─────┘  └────┬─────┘  └────────┬─────────┘   │
└───────┼──────────────┼──────────────┼────────────────┼─────────────┘
        │              │              │                │
        ▼              ▼              ▼                ▼
┌────────────────────────────────────────────────────────────────────┐
│                    API GATEWAY (FastAPI)                             │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐  ┌──────────────────┐   │
│  │ Auth JWT │  │ Rate     │  │ CORS     │  │ File Upload      │   │
│  │ + RBAC   │  │ Limiter  │  │ Proxy    │  │ (S3/Local)       │   │
│  └──────────┘  └──────────┘  └──────────┘  └──────────────────┘   │
└───────┬───────────────────────────────────────┬────────────────────┘
        │                                       │
        ▼                                       ▼
┌───────────────────────┐     ┌─────────────────────────────────────┐
│   CORE SERVICES       │     │   BACKGROUND WORKERS (Celery/APSch) │
│                       │     │                                     │
│  ✦ InstallationSvc    │     │  ⚡ InverterSyncWorker              │
│  ✦ CleaningSvc        │     │     (Huawei/Fox/Sigen/GoodWe)      │
│  ✦ MaintenanceSvc     │     │  ⚡ AIAnalysisWorker                │
│  ✦ WorkOrderSvc       │     │     (Thermal/OCR/Degradation)      │
│  ✦ PhotoSvc           │     │  ⚡ AlertWorker                     │
│  ✦ CustomerPortalSvc  │     │     (LINE/Email push)              │
│  ✦ ReportSvc          │     │  ⚡ WeatherSyncWorker               │
│  ✦ ESGSvc             │     │  ⚡ ReportGeneratorWorker            │
│  ✦ QuotationSvc       │     │  ⚡ WarrantyRegistrationWorker      │
└───────┬───────────────┘     └──────────┬──────────────────────────┘
        │                                │
        ▼                                ▼
┌────────────────────────────────────────────────────────────────────┐
│                    DATA LAYER                                       │
│  ┌──────────────┐  ┌──────────┐  ┌──────────┐  ┌──────────────┐   │
│  │ SQL Server   │  │  Redis   │  │ Blob     │  │ TimeSeries   │   │
│  │ (Core Data)  │  │ (Cache + │  │ Storage  │  │ (InfluxDB /  │   │
│  │              │  │  Queue)  │  │ (Photos) │  │  TimescaleDB)│   │
│  └──────────────┘  └──────────┘  └──────────┘  └──────────────┘   │
└────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌────────────────────────────────────────────────────────────────────┐
│                  EXTERNAL INTEGRATIONS                               │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐  ┌──────────────────┐   │
│  │ Huawei   │  │ Fox ESS  │  │ Sigen-   │  │ Weather API      │   │
│  │ NBI      │  │ OpenAPI  │  │ ergy     │  │ (OpenMeteo/TMD)  │   │
│  └──────────┘  └──────────┘  └──────────┘  └──────────────────┘   │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐  ┌──────────────────┐   │
│  │ GoodWe   │  │ Deye     │  │ Hoymiles │  │ LINE Messaging   │   │
│  │ SEMS     │  │ Cloud    │  │ S-Miles  │  │ API              │   │
│  └──────────┘  └──────────┘  └──────────┘  └──────────────────┘   │
│  ┌──────────┐  ┌──────────┐                                        │
│  │ Solis    │  │ AI/ML    │  (TensorFlow Lite, ONNX for edge)     │
│  │ Cloud    │  │ Models   │                                        │
│  └──────────┘  └──────────┘                                        │
└────────────────────────────────────────────────────────────────────┘
```

## Key Design Decisions

1. **NiceGUI as Primary UI** — Server-rendered reactive UI in pure Python.
   No JS framework needed. Built-in Quasar components for mobile-friendly UX.
2. **FastAPI as API backbone** — NiceGUI runs ON TOP of FastAPI, so both share
   the same process. External consumers (LINE LIFF, mobile PWA) hit FastAPI
   endpoints directly.
3. **SQL Server** — Existing investment; schema designed for partitioning
   (energy_data by month). EF-like ORM via SQLAlchemy 2.0 async.
4. **Photo pipeline** — Upload → EXIF strip → thumbnail → store to blob
   (local disk or Azure Blob). Metadata in SQL. AI analysis runs async.
5. **Offline-first** — PWA with IndexedDB queue + Service Worker. Syncs via
   POST /api/sync/batch when connectivity returns.
6. **AI/ML** — Lightweight models (ONNX) run in-process for OCR/thermal.
   Heavier training (degradation prediction) runs nightly via Celery.
