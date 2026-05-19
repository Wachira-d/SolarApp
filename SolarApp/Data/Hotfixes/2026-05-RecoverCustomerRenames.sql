-- =============================================================
-- RECOVERY: undo Customer renames caused by the
-- duplicate Input.CustomerId form-field bug (see Edit.cshtml
-- pre-commit XYZ).  Reads AuditLogs to find Customer.Update
-- entries whose FirstName/LastName/Phone changed unexpectedly
-- during an Installation Edit, and restores the OldValues.
--
-- ALWAYS run the SELECT preview first to confirm which rows
-- would be restored; only then run the UPDATE block.
-- =============================================================

-- ─── 1. Preview: which Customer renames does the audit log show? ────
SELECT TOP 200
    al.Timestamp,
    al.UserName,
    al.EntityId        AS CustomerId,
    al.OldValues,
    al.NewValues,
    c.FirstName        AS CurrentFirstName,
    c.LastName         AS CurrentLastName,
    c.Phone            AS CurrentPhone
FROM   [AuditLogs]  al
LEFT JOIN [Customers] c ON TRY_CAST(al.EntityId AS int) = c.Id
WHERE  al.EntityType = 'Customer'
   AND al.Action     = 'Update'
   AND (al.NewValues LIKE '%FirstName%' OR al.NewValues LIKE '%LastName%' OR al.NewValues LIKE '%Phone%')
ORDER BY al.Timestamp DESC;

-- ─── 2. (Manual step) Identify the unwanted rename audit rows from
--      the preview above.  Note the EntityId (= CustomerId) and the
--      Timestamp of the LATEST legitimate name (= the one you want to
--      restore to).  Pull FirstName / LastName / Phone from its
--      OldValues JSON.
--
-- Example: AuditLog row says
--   OldValues = {"FirstName":"คุณจารุ","LastName":"เพื่อนพ่อแอร์","Phone":"0816996589", ...}
--   NewValues = {"FirstName":"891/3 ...","LastName":"","Phone":"0000000001", ...}
-- Then restore with:
--
--   UPDATE [Customers]
--   SET    FirstName = N'คุณจารุ',
--          LastName  = N'เพื่อนพ่อแอร์',
--          Phone     = N'0816996589',
--          UpdatedAt = SYSUTCDATETIME()
--   WHERE  Id = <the EntityId>;

-- ─── 3. After restoring, verify ────────────────────────────────
-- SELECT Id, FirstName, LastName, Phone, Address, UpdatedAt
-- FROM [Customers] WHERE Id IN (<list of restored ids>);
