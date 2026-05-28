-- =============================================================
-- HOTFIX: clean up implausibly high daily-energy rows caused by the
-- HoymilesAdapter / SigenergyAdapter sum-of-fallback bug
-- (commits up to 34feb23 wrote 2× / 3× / 10×+ inflated kWh).
--
-- Idempotent: deletes only rows whose EnergyGenerationKwh exceeds
-- 18 × Installation.SystemCapacityKw (the physical ceiling — solar
-- panels can't produce more than ~13 PSH-equivalent in a day even
-- under perfect conditions; 18× gives generous numeric headroom).
--
-- After running, the next sync round will:
--   1. Re-read realtime data with the FIXED adapter (no more sum bug)
--   2. Insert correct rows for today
--   3. Re-fill the deleted dates via the 30-day auto-backfill or the
--      manual "ดึงข้อมูลย้อนหลังจาก API" button.
-- =============================================================

SET NOCOUNT ON;

-- Preview which rows would be deleted (run this first, verify, then
-- uncomment the DELETE below).
PRINT '== Rows that look implausibly high (capacity × 18 = max plausible):';

SELECT
    e.Id,
    i.SiteName,
    i.SystemCapacityKw AS CapKw,
    e.Timestamp,
    e.EnergyGenerationKwh AS RecordedKwh,
    i.SystemCapacityKw * 18 AS PlausibleMax,
    CAST(e.EnergyGenerationKwh / NULLIF(i.SystemCapacityKw, 0) AS decimal(10,2)) AS MultipleOfCapacity
FROM [EnergyData] e
INNER JOIN [Installations] i ON e.InstallationId = i.Id
WHERE e.Granularity = 1            -- Daily
  AND i.SystemCapacityKw > 0
  AND e.EnergyGenerationKwh > i.SystemCapacityKw * 18
ORDER BY MultipleOfCapacity DESC;

-- Uncomment to actually delete after reviewing the preview above:
--
-- DELETE e
-- FROM [EnergyData] e
-- INNER JOIN [Installations] i ON e.InstallationId = i.Id
-- WHERE e.Granularity = 1
--   AND i.SystemCapacityKw > 0
--   AND e.EnergyGenerationKwh > i.SystemCapacityKw * 18;
--
-- PRINT 'Deleted implausibly high rows.  Trigger a sync round or use the';
-- PRINT '"ดึงข้อมูลย้อนหลังจาก API" button in Edit Installation to refill.';
