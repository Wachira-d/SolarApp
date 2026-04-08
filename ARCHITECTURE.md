# SolarApp - Solar Installation Management System Architecture

## Overview
ระบบจัดการหน้างานลูกค้าที่ติดตั้งระบบ Solar Cell แบบครบวงจร เชื่อมต่อ Cloud API ของ Inverter หลายแบรนด์ พร้อม Dashboard, Report และระบบแจ้งเตือนผ่าน LINE

## Tech Stack
- **Backend**: ASP.NET Core 8.0 (Razor Pages + Web API)
- **Database**: SQLite (Development) / PostgreSQL (Production) via Entity Framework Core
- **Authentication**: ASP.NET Core Identity with Role-based Authorization
- **Background Jobs**: Hosted Services (IHostedService)
- **Notifications**: LINE Messaging API
- **Charts**: Chart.js
- **UI**: Bootstrap 5 + jQuery

---

## System Architecture Diagram

```
┌─────────────────────────────────────────────────────────┐
│                    Frontend (Razor Pages)                │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐  ┌────────┐  │
│  │Dashboard │  │Installa- │  │Mainten-  │  │Reports │  │
│  │  Pages   │  │  tions   │  │  ance    │  │ Pages  │  │
│  └──────────┘  └──────────┘  └──────────┘  └────────┘  │
├─────────────────────────────────────────────────────────┤
│                   ASP.NET Core Identity                  │
│          (Customer / Partner / Admin Roles)              │
├─────────────────────────────────────────────────────────┤
│                    Service Layer                         │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐  ┌────────┐  │
│  │Install-  │  │Warranty  │  │Cleaning  │  │Alert   │  │
│  │ation Svc │  │  Svc     │  │  Svc     │  │  Svc   │  │
│  └──────────┘  └──────────┘  └──────────┘  └────────┘  │
├─────────────────────────────────────────────────────────┤
│              Inverter API Adapter Layer                  │
│  ┌────────┐ ┌───────┐ ┌──────┐ ┌──────┐ ┌─────┐        │
│  │Huawei  │ │FOX ESS│ │Solis │ │GoodWe│ │Deye │  ...   │
│  │Adapter │ │Adapter│ │Adapt.│ │Adapt.│ │Adpt.│        │
│  └────────┘ └───────┘ └──────┘ └──────┘ └─────┘        │
├─────────────────────────────────────────────────────────┤
│  ┌──────────────────┐  ┌──────────────────────────┐     │
│  │ Background Jobs   │  │  LINE Messaging API      │     │
│  │ - Health Monitor  │  │  - Offline Alerts        │     │
│  │ - Data Sync       │  │  - Error Notifications   │     │
│  │ - Alert Checker   │  │  - Maintenance Reminders │     │
│  └──────────────────┘  └──────────────────────────┘     │
├─────────────────────────────────────────────────────────┤
│                 Entity Framework Core                    │
│                   SQLite / PostgreSQL                    │
└─────────────────────────────────────────────────────────┘
```

---

## Database Schema

### Core Entities

#### Users (ASP.NET Identity)
- Extended with: Phone, LineUserId, CompanyName, Role (Customer/Partner/Admin)

#### Installations (หน้างานติดตั้ง)
- Id, SiteName, Address, Latitude, Longitude
- CustomerId (FK -> User), PartnerId (FK -> User, nullable)
- InverterBrand (Huawei/FoxESS/Solis/GoodWe/Deye)
- CloudApiCredentials (encrypted)
- PlantId (ID ในระบบ Cloud ของแบรนด์)
- SystemCapacityKw, NumberOfPanels, PanelWattage
- InstallationDate, CommissionDate
- WarrantyYears, WarrantyExpiryDate
- TotalCleaningCount, CompletedCleaningCount, LastCleaningDate, NextCleaningDueDate
- Status (Active/Inactive/Maintenance/Offline)
- Notes, CreatedAt, UpdatedAt

#### Equipment (อุปกรณ์ในแต่ละหน้างาน)
- Id, InstallationId (FK)
- EquipmentType (Inverter/Panel/Battery/Meter/Optimizer)
- Brand, Model, SerialNumber
- WarrantyYears, WarrantyExpiryDate
- InstalledDate, Status, Notes

#### EnergyData (ข้อมูลการผลิตไฟฟ้า)
- Id, InstallationId (FK)
- Timestamp, Granularity (Realtime/Hourly/Daily/Monthly)
- PowerGenerationKw, EnergyGenerationKwh
- GridExportKwh, GridImportKwh
- SelfConsumptionKwh, BatterySocPercent
- BatteryChargeKwh, BatteryDischargeKwh

#### SystemAlerts (การแจ้งเตือน)
- Id, InstallationId (FK)
- AlertType (Offline/Error/Warning/Maintenance)
- Severity (Critical/High/Medium/Low)
- Title, Message, RootCauseAnalysis
- OccurredAt, AcknowledgedAt, ResolvedAt
- AcknowledgedByUserId, IsNotified

#### MaintenanceRecords (ประวัติการบำรุงรักษา)
- Id, InstallationId (FK)
- MaintenanceType (PanelCleaning/Inspection/Repair/Replacement)
- ScheduledDate, CompletedDate
- PerformedByUserId, Description, Cost
- Status (Scheduled/InProgress/Completed/Cancelled)
- BeforePhotos, AfterPhotos, Notes

#### ServiceTickets (ใบงาน)
- Id, InstallationId (FK)
- TicketNumber, Subject, Description
- Priority (Urgent/High/Normal/Low)
- Status (Open/Assigned/InProgress/Resolved/Closed)
- ReportedByUserId, AssignedToUserId
- CreatedAt, UpdatedAt, ResolvedAt

#### InverterApiLogs (ล็อกการเรียก API)
- Id, InstallationId (FK)
- ApiProvider, Endpoint, RequestTime
- ResponseStatusCode, ResponseMessage
- IsSuccess, ErrorMessage

---

## User Roles & Permissions

| Feature | Customer | Partner | Admin |
|---------|----------|---------|-------|
| View own installations | Y | Y (own customers) | Y (all) |
| View Dashboard | Y (own) | Y (own customers) | Y (all) |
| View Reports | Y (own) | Y (own customers) | Y (all) |
| Manage installations | - | Limited | Full |
| Manage equipment | - | Limited | Full |
| Create service tickets | Y | Y | Y |
| Manage maintenance | - | View | Full |
| View alerts | Y (own) | Y (own customers) | Y (all) |
| Manage users | - | - | Y |
| System settings | - | - | Y |

---

## Inverter API Adapter Pattern

```csharp
public interface IInverterApiAdapter
{
    InverterBrand Brand { get; }
    Task<bool> TestConnectionAsync(ApiCredentials credentials);
    Task<PlantInfo> GetPlantInfoAsync(string plantId);
    Task<RealtimeData> GetRealtimeDataAsync(string plantId);
    Task<List<EnergyRecord>> GetHistoricalDataAsync(string plantId, DateTime from, DateTime to);
    Task<List<AlarmRecord>> GetAlarmsAsync(string plantId, DateTime from, DateTime to);
    Task<List<DeviceInfo>> GetDevicesAsync(string plantId);
}
```

Each brand implements this interface, registered via `IInverterApiAdapterFactory`.

---

## Implementation Phases

### Phase 1: Foundation (Current)
- Project structure, models, DbContext, Identity setup
- Base layout and navigation
- Admin user seeding

### Phase 2: Core Management
- Installation CRUD pages
- Equipment management
- Warranty tracking
- Cleaning schedule management

### Phase 3: API Integration
- Inverter API adapter implementations
- Data synchronization background jobs
- Energy data storage

### Phase 4: Monitoring & Alerts
- System health monitoring
- LINE Messaging API integration
- Alert management
- Root cause analysis

### Phase 5: Dashboard & Reports
- Real-time dashboard with charts
- Production reports (daily/monthly/yearly)
- Export to PDF/Excel
