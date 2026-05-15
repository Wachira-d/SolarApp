-- ============================================================
-- SolarApp v2 — Complete Database Schema (SQL Server)
-- ============================================================

-- ─── CORE: Identity & Multi-Tenancy ─────────────────────────

CREATE TABLE partners (
    id              INT IDENTITY PRIMARY KEY,
    name            NVARCHAR(200)  NOT NULL,
    tax_id          NVARCHAR(20)   NULL,
    address         NVARCHAR(500)  NULL,
    phone           NVARCHAR(50)   NULL,
    email           NVARCHAR(200)  NULL,
    logo_url        NVARCHAR(500)  NULL,
    is_active       BIT NOT NULL DEFAULT 1,
    created_at      DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE users (
    id              INT IDENTITY PRIMARY KEY,
    email           NVARCHAR(200)  NOT NULL UNIQUE,
    password_hash   NVARCHAR(500)  NOT NULL,
    full_name       NVARCHAR(200)  NOT NULL,
    phone           NVARCHAR(50)   NULL,
    avatar_url      NVARCHAR(500)  NULL,
    role            NVARCHAR(20)   NOT NULL DEFAULT 'technician',  -- admin / partner / technician / customer
    partner_id      INT NULL REFERENCES partners(id),
    line_user_id    NVARCHAR(100)  NULL,
    is_active       BIT NOT NULL DEFAULT 1,
    last_login_at   DATETIME2 NULL,
    created_at      DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX ix_users_role ON users(role);
CREATE INDEX ix_users_partner ON users(partner_id);

CREATE TABLE customers (
    id              INT IDENTITY PRIMARY KEY,
    user_id         INT NULL REFERENCES users(id),
    partner_id      INT NULL REFERENCES partners(id),
    first_name      NVARCHAR(100)  NOT NULL,
    last_name       NVARCHAR(100)  NOT NULL,
    phone           NVARCHAR(50)   NULL,
    email           NVARCHAR(200)  NULL,
    line_user_id    NVARCHAR(100)  NULL,
    address         NVARCHAR(500)  NULL,
    tax_id          NVARCHAR(20)   NULL,
    is_active       BIT NOT NULL DEFAULT 1,
    created_at      DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

-- ─── INSTALLATIONS ──────────────────────────────────────────

CREATE TABLE installations (
    id                   INT IDENTITY PRIMARY KEY,
    site_code            NVARCHAR(30)   NOT NULL UNIQUE,
    site_name            NVARCHAR(200)  NOT NULL,
    address              NVARCHAR(500)  NULL,
    latitude             FLOAT NULL,
    longitude            FLOAT NULL,
    customer_id          INT NOT NULL REFERENCES customers(id),
    partner_id           INT NULL REFERENCES partners(id),

    -- System specs
    system_capacity_kw   DECIMAL(10,3)  NOT NULL DEFAULT 0,
    number_of_panels     INT NOT NULL DEFAULT 0,
    panel_brand          NVARCHAR(100)  NULL,
    panel_model          NVARCHAR(100)  NULL,
    panel_watt           INT NULL,
    inverter_brand       NVARCHAR(50)   NOT NULL,  -- Huawei/FoxESS/Sigenergy/GoodWe/Deye/Hoymiles/Solis
    inverter_model       NVARCHAR(100)  NULL,
    inverter_size_kw     DECIMAL(10,3)  NOT NULL DEFAULT 0,
    inverter_count       INT NOT NULL DEFAULT 1,
    battery_brand        NVARCHAR(100)  NULL,
    battery_capacity_kwh DECIMAL(10,2)  NULL,
    roof_type            NVARCHAR(50)   NULL,  -- concrete / metal / tile
    roof_angle_deg       DECIMAL(5,2)   NULL,
    azimuth_deg          DECIMAL(5,2)   NULL,

    -- Cloud API credentials (encrypted)
    cloud_username       NVARCHAR(500)  NULL,
    cloud_password       NVARCHAR(500)  NULL,
    cloud_api_key        NVARCHAR(500)  NULL,
    cloud_api_secret     NVARCHAR(500)  NULL,
    plant_id             NVARCHAR(100)  NULL,

    -- Status + Maintenance
    status               INT NOT NULL DEFAULT 1,  -- 1=Active, 2=Inactive, 3=Maintenance, 4=Offline
    health_score         DECIMAL(5,2)   NULL,      -- 0-100 AI-computed
    installation_date    DATE NOT NULL,
    warranty_years       INT NOT NULL DEFAULT 1,
    warranty_expiry      DATE NOT NULL,
    electricity_rate     DECIMAL(8,4)   NOT NULL DEFAULT 4.15,  -- THB/kWh
    tou_plan             NVARCHAR(20)   NULL,       -- flat / tou_normal / tou_large

    -- Cleaning contract
    cleaning_frequency_months  INT NOT NULL DEFAULT 6,
    total_cleaning_count       INT NOT NULL DEFAULT 0,
    completed_cleaning_count   INT NOT NULL DEFAULT 0,
    next_cleaning_due          DATE NULL,
    last_cleaned_at            DATETIME2 NULL,

    -- Sync tracking
    last_online_at       DATETIME2 NULL,
    last_sync_at         DATETIME2 NULL,

    created_at           DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at           DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX ix_inst_customer ON installations(customer_id);
CREATE INDEX ix_inst_partner ON installations(partner_id);
CREATE INDEX ix_inst_status ON installations(status);
CREATE INDEX ix_inst_brand ON installations(inverter_brand);

-- ─── EQUIPMENT (individual devices under an installation) ───

CREATE TABLE equipment (
    id               INT IDENTITY PRIMARY KEY,
    installation_id  INT NOT NULL REFERENCES installations(id) ON DELETE CASCADE,
    device_type      NVARCHAR(50)  NOT NULL,  -- inverter / panel / battery / optimizer / meter / logger
    brand            NVARCHAR(100) NULL,
    model            NVARCHAR(100) NULL,
    serial_number    NVARCHAR(100) NULL,
    rating_kw        DECIMAL(10,3) NULL,
    quantity         INT NOT NULL DEFAULT 1,
    warranty_expiry  DATE NULL,
    firmware         NVARCHAR(50)  NULL,
    is_active        BIT NOT NULL DEFAULT 1,
    created_at       DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX ix_equip_install ON equipment(installation_id);

-- ─── ENERGY DATA (time-series) ──────────────────────────────

CREATE TABLE energy_data (
    id                    BIGINT IDENTITY PRIMARY KEY,
    installation_id       INT NOT NULL REFERENCES installations(id),
    timestamp             DATETIME2 NOT NULL,
    granularity           INT NOT NULL,  -- 1=realtime, 2=hourly, 3=daily, 4=monthly
    power_kw              DECIMAL(10,3) NOT NULL DEFAULT 0,
    energy_kwh            DECIMAL(12,3) NOT NULL DEFAULT 0,
    grid_export_kwh       DECIMAL(12,3) NOT NULL DEFAULT 0,
    grid_import_kwh       DECIMAL(12,3) NOT NULL DEFAULT 0,
    self_consumption_kwh  DECIMAL(12,3) NOT NULL DEFAULT 0,
    battery_soc_pct       DECIMAL(5,2)  NULL,
    battery_charge_kwh    DECIMAL(10,3) NULL,
    battery_discharge_kwh DECIMAL(10,3) NULL,
    -- Weather context (filled by WeatherSyncWorker)
    irradiance_wm2        DECIMAL(8,2)  NULL,
    temperature_c          DECIMAL(5,1)  NULL,
    humidity_pct           DECIMAL(5,1)  NULL,
    cloud_cover_pct        DECIMAL(5,1)  NULL
);
CREATE INDEX ix_energy_install_ts ON energy_data(installation_id, timestamp DESC);
CREATE INDEX ix_energy_granularity ON energy_data(granularity, timestamp DESC);

-- ─── JOBS (unified work order model) ────────────────────────

CREATE TABLE jobs (
    id                   INT IDENTITY PRIMARY KEY,
    job_number           NVARCHAR(20)   NOT NULL UNIQUE,
    job_type             INT NOT NULL,  -- 1=Installation, 2=Cleaning, 3=AnnualMA, 4=Repair, 5=Inspection, 6=Commissioning, 7=Other
    status               INT NOT NULL DEFAULT 0,
        -- 0=Draft, 1=Scheduled, 2=Assigned, 3=Accepted,
        -- 4=PPECheck, 5=InProgress, 6=Completed, 7=Verified,
        -- 8=Invoiced, 9=Closed, 10=Cancelled
    installation_id      INT NULL REFERENCES installations(id),
    customer_id          INT NULL REFERENCES customers(id),

    -- Standalone site (for non-monitored sites)
    ext_site_name        NVARCHAR(200)  NULL,
    ext_site_address     NVARCHAR(500)  NULL,
    ext_customer_name    NVARCHAR(200)  NULL,
    ext_customer_phone   NVARCHAR(50)   NULL,
    ext_latitude         FLOAT NULL,
    ext_longitude        FLOAT NULL,

    title                NVARCHAR(200)  NOT NULL,
    description          NVARCHAR(MAX)  NULL,
    scope                NVARCHAR(MAX)  NULL,
    special_instructions NVARCHAR(MAX)  NULL,

    -- Schedule
    scheduled_date       DATE NOT NULL,
    scheduled_time       TIME NULL,
    estimated_minutes    INT NOT NULL DEFAULT 120,
    actual_start         DATETIME2 NULL,
    actual_end           DATETIME2 NULL,
    recurrence_months    INT NULL,
    parent_job_id        INT NULL REFERENCES jobs(id),

    -- Assignment
    assigned_team        NVARCHAR(100)  NULL,
    assigned_to_user_id  INT NULL REFERENCES users(id),
    created_by_user_id   INT NULL REFERENCES users(id),

    -- Costs
    labor_cost           DECIMAL(12,2) NOT NULL DEFAULT 0,
    material_cost        DECIMAL(12,2) NOT NULL DEFAULT 0,
    travel_cost          DECIMAL(12,2) NOT NULL DEFAULT 0,
    other_cost           DECIMAL(12,2) NOT NULL DEFAULT 0,
    cost_notes           NVARCHAR(MAX) NULL,

    -- Completion
    completion_notes     NVARCHAR(MAX) NULL,
    customer_signed      BIT NOT NULL DEFAULT 0,
    customer_rating      INT NULL,
    customer_feedback    NVARCHAR(MAX) NULL,

    -- Performance verification (cleaning jobs)
    yield_before         DECIMAL(10,2) NULL,   -- kWh or Amp reading before
    yield_after          DECIMAL(10,2) NULL,   -- kWh or Amp reading after
    yield_improvement_pct DECIMAL(5,2) NULL,   -- computed % gain

    -- PPE safety check
    ppe_check_passed     BIT NOT NULL DEFAULT 0,
    ppe_check_photo_id   INT NULL,  -- FK to photos
    ppe_check_at         DATETIME2 NULL,

    -- Notifications
    reminder_sent_at     DATETIME2 NULL,

    created_at           DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at           DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX ix_jobs_status ON jobs(status);
CREATE INDEX ix_jobs_type ON jobs(job_type);
CREATE INDEX ix_jobs_install ON jobs(installation_id);
CREATE INDEX ix_jobs_scheduled ON jobs(scheduled_date);
CREATE INDEX ix_jobs_assigned ON jobs(assigned_to_user_id);

-- ─── JOB CHECKLISTS ─────────────────────────────────────────

CREATE TABLE job_checklist_items (
    id                  INT IDENTITY PRIMARY KEY,
    job_id              INT NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
    sort_order          INT NOT NULL DEFAULT 0,
    description         NVARCHAR(300) NOT NULL,
    is_completed        BIT NOT NULL DEFAULT 0,
    completed_at        DATETIME2 NULL,
    completed_by_user_id INT NULL REFERENCES users(id),
    notes               NVARCHAR(MAX) NULL,
    -- Structured check (for MA items like temperature/torque)
    check_type          NVARCHAR(30)  NULL,  -- temperature / torque / visual / measurement
    expected_value       NVARCHAR(100) NULL,  -- e.g. "< 60C"
    actual_value         NVARCHAR(100) NULL,  -- e.g. "42C"
    is_pass              BIT NULL
);
CREATE INDEX ix_checklist_job ON job_checklist_items(job_id, sort_order);

-- ─── JOB PHOTOS (rich metadata) ─────────────────────────────

CREATE TABLE job_photos (
    id                  INT IDENTITY PRIMARY KEY,
    job_id              INT NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
    category            INT NOT NULL,
        -- Installation: 10=SiteOverview, 11=PanelArray, 12=PanelCloseup,
        --              13=InverterZone, 14=InverterWiring, 15=CombinerBox,
        --              16=Conduit, 17=WarningSigns, 18=GroundConnection,
        --              19=BatteryInstall
        -- Cleaning:    30=BeforeWide, 31=BeforeMacro, 32=AfterWide,
        --              33=AfterMacro, 34=DrainageCheck, 35=YieldBefore,
        --              36=YieldAfter, 37=DroneView
        -- MA:          50=ThermalPanelOverall, 51=ThermalHotspot,
        --              52=ThermalMC4, 53=ThermalBreaker, 54=InPipeInspect,
        --              55=StructuralBolt, 56=ReTorque, 57=SealingPoint
        -- Safety:      70=PPEFull, 71=PPEHarness, 72=PPEHelmet
        -- General:     90=MeterReading, 91=CustomerSignoff, 92=Other
    timing              INT NOT NULL DEFAULT 1,  -- 1=Before, 2=During, 3=After
    file_path           NVARCHAR(500) NOT NULL,
    thumbnail_path      NVARCHAR(500) NULL,
    content_type        NVARCHAR(100) NULL,
    file_size_bytes     BIGINT NOT NULL DEFAULT 0,
    width_px            INT NULL,
    height_px           INT NULL,

    -- Geo + metadata
    latitude            FLOAT NULL,
    longitude           FLOAT NULL,
    taken_at            DATETIME2 NULL,  -- from EXIF or manual
    device_model        NVARCHAR(100)  NULL,

    -- AI analysis results
    ai_analysis_status  INT NOT NULL DEFAULT 0,  -- 0=Pending, 1=Processing, 2=Done, 3=Failed
    ai_hotspot_detected BIT NULL,
    ai_hotspot_temp_c   DECIMAL(5,1)  NULL,
    ai_ocr_text         NVARCHAR(MAX) NULL,  -- extracted serial numbers, meter readings
    ai_quality_score    DECIMAL(5,2)  NULL,  -- 0-100 image quality
    ai_notes            NVARCHAR(MAX) NULL,

    -- Display
    description         NVARCHAR(500) NULL,
    auto_caption        NVARCHAR(500) NULL,  -- auto-generated for customer view
    sort_order          INT NOT NULL DEFAULT 0,
    uploaded_by_user_id INT NULL REFERENCES users(id),
    uploaded_at         DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX ix_photos_job ON job_photos(job_id, category, timing);

-- ─── JOB TYPE TEMPLATES (admin-configurable defaults) ───────

CREATE TABLE job_type_templates (
    id              INT IDENTITY PRIMARY KEY,
    job_type        INT NOT NULL UNIQUE,
    title           NVARCHAR(200) NOT NULL DEFAULT '',
    checklist_items NVARCHAR(MAX) NOT NULL DEFAULT '',  -- newline-separated
    required_photo_categories NVARCHAR(500) NULL,       -- comma-separated ints
    guide_tips      NVARCHAR(MAX) NULL,                 -- JSON tips for technicians
    customer_captions NVARCHAR(MAX) NULL,               -- JSON auto-captions per category
    updated_at      DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_by_user_id INT NULL REFERENCES users(id)
);

-- ─── ALERTS & NOTIFICATIONS ─────────────────────────────────

CREATE TABLE alerts (
    id                  INT IDENTITY PRIMARY KEY,
    installation_id     INT NOT NULL REFERENCES installations(id),
    alert_type          INT NOT NULL,  -- 1=Offline, 2=Fault, 3=LowPerformance, 4=CleaningDue, 5=WarrantyExpiring, 6=AnomalyDetected
    severity            INT NOT NULL,  -- 1=Critical, 2=High, 3=Medium, 4=Low
    title               NVARCHAR(200) NOT NULL,
    message             NVARCHAR(MAX) NOT NULL,
    root_cause          NVARCHAR(MAX) NULL,
    fault_code          NVARCHAR(50)  NULL,   -- inverter fault code if applicable
    auto_ticket_job_id  INT NULL REFERENCES jobs(id),  -- auto-created repair ticket
    is_notified         BIT NOT NULL DEFAULT 0,
    occurred_at         DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    acknowledged_at     DATETIME2 NULL,
    acknowledged_by     INT NULL REFERENCES users(id),
    resolved_at         DATETIME2 NULL
);
CREATE INDEX ix_alerts_install ON alerts(installation_id, resolved_at);
CREATE INDEX ix_alerts_severity ON alerts(severity) WHERE resolved_at IS NULL;

CREATE TABLE notification_log (
    id                  INT IDENTITY PRIMARY KEY,
    recipient_line_id   NVARCHAR(100) NULL,
    recipient_email     NVARCHAR(200) NULL,
    channel             INT NOT NULL,  -- 1=LINE, 2=Email, 3=InApp
    title               NVARCHAR(200) NOT NULL,
    message             NVARCHAR(MAX) NULL,
    related_entity_type NVARCHAR(50)  NULL,
    related_entity_id   INT NULL,
    status              INT NOT NULL DEFAULT 0,  -- 0=Pending, 1=Sent, 2=Failed, 3=Read
    error_message       NVARCHAR(MAX) NULL,
    sent_at             DATETIME2 NULL,
    created_at          DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

-- ─── DOCUMENTS (Digital Vault) ──────────────────────────────

CREATE TABLE documents (
    id                  INT IDENTITY PRIMARY KEY,
    installation_id     INT NULL REFERENCES installations(id),
    customer_id         INT NULL REFERENCES customers(id),
    doc_type            NVARCHAR(50)  NOT NULL,  -- single_line_diagram, permit, warranty_cert, invoice, report
    title               NVARCHAR(200) NOT NULL,
    file_path           NVARCHAR(500) NOT NULL,
    file_size_bytes     BIGINT NOT NULL DEFAULT 0,
    uploaded_by_user_id INT NULL REFERENCES users(id),
    uploaded_at         DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

-- ─── ESG & CARBON TRACKING ──────────────────────────────────

CREATE TABLE carbon_reports (
    id                  INT IDENTITY PRIMARY KEY,
    installation_id     INT NOT NULL REFERENCES installations(id),
    period_start        DATE NOT NULL,
    period_end          DATE NOT NULL,
    energy_generated_kwh DECIMAL(14,3) NOT NULL DEFAULT 0,
    carbon_offset_kg     DECIMAL(14,3) NOT NULL DEFAULT 0,  -- kWh * emission_factor
    trees_equivalent     DECIMAL(10,2) NOT NULL DEFAULT 0,
    emission_factor      DECIMAL(8,6)  NOT NULL DEFAULT 0.5986,  -- Thailand grid EF (kg CO2/kWh)
    report_pdf_path      NVARCHAR(500) NULL,
    created_at           DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

-- ─── QUOTATIONS (AI-driven upsell) ─────────────────────────

CREATE TABLE quotations (
    id                  INT IDENTITY PRIMARY KEY,
    customer_id         INT NOT NULL REFERENCES customers(id),
    installation_id     INT NULL REFERENCES installations(id),
    quote_type          NVARCHAR(50)  NOT NULL,  -- battery_upsell / cleaning_plan / ma_plan / system_upgrade
    title               NVARCHAR(200) NOT NULL,
    description         NVARCHAR(MAX) NULL,
    total_price         DECIMAL(14,2) NOT NULL DEFAULT 0,
    payback_months      INT NULL,
    monthly_saving      DECIMAL(10,2) NULL,
    ai_reasoning        NVARCHAR(MAX) NULL,   -- why AI recommended this
    status              INT NOT NULL DEFAULT 0,  -- 0=Draft, 1=Sent, 2=Accepted, 3=Rejected
    sent_at             DATETIME2 NULL,
    response_at         DATETIME2 NULL,
    created_at          DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

-- ─── INVERTER API LOGS ──────────────────────────────────────

CREATE TABLE api_logs (
    id                  BIGINT IDENTITY PRIMARY KEY,
    installation_id     INT NOT NULL,
    api_provider        NVARCHAR(50)  NOT NULL,
    endpoint            NVARCHAR(200) NOT NULL,
    status_code         INT NOT NULL DEFAULT 0,
    is_success          BIT NOT NULL DEFAULT 0,
    error_message       NVARCHAR(MAX) NULL,
    duration_ms         INT NULL,
    request_time        DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX ix_api_logs_install ON api_logs(installation_id, request_time DESC);

-- ─── SYSTEM SETTINGS ────────────────────────────────────────

CREATE TABLE system_settings (
    id          INT IDENTITY PRIMARY KEY,
    [key]       NVARCHAR(100) NOT NULL UNIQUE,
    value       NVARCHAR(MAX) NOT NULL,
    description NVARCHAR(300) NULL,
    updated_at  DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_by  INT NULL REFERENCES users(id)
);

-- ─── AUDIT LOG ──────────────────────────────────────────────

CREATE TABLE audit_log (
    id          BIGINT IDENTITY PRIMARY KEY,
    user_id     INT NULL REFERENCES users(id),
    action      NVARCHAR(50)  NOT NULL,
    entity_type NVARCHAR(50)  NOT NULL,
    entity_id   INT NULL,
    old_values  NVARCHAR(MAX) NULL,
    new_values  NVARCHAR(MAX) NULL,
    ip_address  NVARCHAR(50)  NULL,
    created_at  DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX ix_audit_entity ON audit_log(entity_type, entity_id);
CREATE INDEX ix_audit_user ON audit_log(user_id, created_at DESC);
