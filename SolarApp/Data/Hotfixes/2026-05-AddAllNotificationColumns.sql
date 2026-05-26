-- =============================================================
-- HOTFIX: add all per-installation columns introduced in
-- commits 11d3b84 (service scope), ed2f31f (connection method +
-- sync interval + LINE alerts), and b76584a (quiet hours +
-- recovery toggle).
--
-- Idempotent — safe to run multiple times.  Run this against the
-- SolarApp database in SSMS / Azure Data Studio / sqlcmd whenever
-- the app process hasn't restarted to pick up the schema-upgrade
-- code that runs on startup.
-- =============================================================

SET NOCOUNT ON;
PRINT '== SolarApp schema hotfix: applying ALTER TABLE [Installations] ...';

-- ── Service scope (commit 11d3b84) ─────────────────────────────
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'ServiceLevel' AND Object_ID = Object_ID(N'[Installations]'))
    ALTER TABLE [Installations] ADD [ServiceLevel] int NOT NULL DEFAULT 0;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'IncludesMonitoring' AND Object_ID = Object_ID(N'[Installations]'))
    ALTER TABLE [Installations] ADD [IncludesMonitoring] bit NOT NULL DEFAULT 1;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'IncludesCleaning' AND Object_ID = Object_ID(N'[Installations]'))
    ALTER TABLE [Installations] ADD [IncludesCleaning] bit NOT NULL DEFAULT 1;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'IncludesPreventiveMaintenance' AND Object_ID = Object_ID(N'[Installations]'))
    ALTER TABLE [Installations] ADD [IncludesPreventiveMaintenance] bit NOT NULL DEFAULT 1;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'IncludesCorrectiveMaintenance' AND Object_ID = Object_ID(N'[Installations]'))
    ALTER TABLE [Installations] ADD [IncludesCorrectiveMaintenance] bit NOT NULL DEFAULT 1;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'IncludesWarrantyService' AND Object_ID = Object_ID(N'[Installations]'))
    ALTER TABLE [Installations] ADD [IncludesWarrantyService] bit NOT NULL DEFAULT 1;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'ServiceNotes' AND Object_ID = Object_ID(N'[Installations]'))
    ALTER TABLE [Installations] ADD [ServiceNotes] nvarchar(1000) NULL;

-- ── Cloud connection method + per-site sync interval (ed2f31f) ─
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'ConnectionMethod' AND Object_ID = Object_ID(N'[Installations]'))
    ALTER TABLE [Installations] ADD [ConnectionMethod] int NOT NULL DEFAULT 0;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'SyncIntervalMinutes' AND Object_ID = Object_ID(N'[Installations]'))
    ALTER TABLE [Installations] ADD [SyncIntervalMinutes] int NOT NULL DEFAULT 15;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'NextSyncDueAt' AND Object_ID = Object_ID(N'[Installations]'))
    ALTER TABLE [Installations] ADD [NextSyncDueAt] datetime2 NULL;

-- ── Customer LINE alert routing (ed2f31f) ──────────────────────
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'LineGroupId' AND Object_ID = Object_ID(N'[Installations]'))
    ALTER TABLE [Installations] ADD [LineGroupId] nvarchar(100) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'NotifyOnGridLoss' AND Object_ID = Object_ID(N'[Installations]'))
    ALTER TABLE [Installations] ADD [NotifyOnGridLoss] bit NOT NULL DEFAULT 1;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'NotifyOnApiFailure' AND Object_ID = Object_ID(N'[Installations]'))
    ALTER TABLE [Installations] ADD [NotifyOnApiFailure] bit NOT NULL DEFAULT 0;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'NotifyOnLowProduction' AND Object_ID = Object_ID(N'[Installations]'))
    ALTER TABLE [Installations] ADD [NotifyOnLowProduction] bit NOT NULL DEFAULT 0;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'NotifyOnCleaningDue' AND Object_ID = Object_ID(N'[Installations]'))
    ALTER TABLE [Installations] ADD [NotifyOnCleaningDue] bit NOT NULL DEFAULT 0;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'NotifyOnWarrantyExpiry' AND Object_ID = Object_ID(N'[Installations]'))
    ALTER TABLE [Installations] ADD [NotifyOnWarrantyExpiry] bit NOT NULL DEFAULT 0;

-- ── Quiet hours + recovery toggle (b76584a) ────────────────────
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'QuietHoursStart' AND Object_ID = Object_ID(N'[Installations]'))
    ALTER TABLE [Installations] ADD [QuietHoursStart] time NULL;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'QuietHoursEnd' AND Object_ID = Object_ID(N'[Installations]'))
    ALTER TABLE [Installations] ADD [QuietHoursEnd] time NULL;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'NotifyOnRecovery' AND Object_ID = Object_ID(N'[Installations]'))
    ALTER TABLE [Installations] ADD [NotifyOnRecovery] bit NOT NULL DEFAULT 1;

PRINT '== Schema hotfix complete.  Refresh the SolarApp page and the';
PRINT '   "Invalid column name" SqlException should be gone.';
