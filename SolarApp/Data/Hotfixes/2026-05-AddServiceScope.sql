-- =============================================================
-- HOTFIX: add service-scope columns to Installations
-- Idempotent — safe to run multiple times.
-- Run this against the SolarApp database in SSMS / Azure Data Studio
-- / sqlcmd if the app process hasn't restarted to pick up the new
-- schema upgrade code (commit 11d3b84).
-- =============================================================

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

PRINT 'Schema upgrade complete. Refresh the app and the error should be gone.';
