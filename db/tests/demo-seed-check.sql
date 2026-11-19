-- =====================================================================================================
-- db/tests/demo-seed-check.sql
-- Checks the result of `seed-demo --yes-demo` (dotnet/src/Crm.Web/Data/DemoSeeder.cs). TEST DATABASES ONLY.
-- Run after seed-test and seed-demo:   dotnet run --project dotnet/src/Crm.Web -- run-sql db/tests/demo-seed-check.sql
-- Prints one row per case (Case, Result PASS/FAIL, Description, Detail); the runner exits 1 on any FAIL.
-- Read-only: it only SELECTs. RLS applies to dbo too, so the session gets an Admin context first (one connection, all batches).
-- "Today" is the Copenhagen calendar date, like the app's default time zone.
-- =====================================================================================================
SET NOCOUNT ON;
EXEC sp_set_session_context @key = N'UserId', @value = 0, @read_only = 0;
EXEC sp_set_session_context @key = N'Role',   @value = N'Admin', @read_only = 0;
GO
SELECT 'D1' AS [Case],
  CASE WHEN a = 40 AND c = 100 AND o = 180 AND x = 250 THEN 'PASS' ELSE 'FAIL' END AS Result,
  'Demo volumes: 40 accounts, 100 contacts, 180 opportunities, 250 activities' AS Description,
  CONCAT('accounts=', a, ' contacts=', c, ' opportunities=', o, ' activities=', x) AS Detail
FROM (SELECT
  (SELECT COUNT(*) FROM crm.Accounts      WHERE CreatedBy IN (SELECT UserId FROM crm.Users WHERE LOWER(CONVERT(char(36), EntraOid)) LIKE '00000000-0000-4000-8000-00000000d0%')) AS a,
  (SELECT COUNT(*) FROM crm.Contacts      WHERE CreatedBy IN (SELECT UserId FROM crm.Users WHERE LOWER(CONVERT(char(36), EntraOid)) LIKE '00000000-0000-4000-8000-00000000d0%')) AS c,
  (SELECT COUNT(*) FROM crm.Opportunities WHERE CreatedBy IN (SELECT UserId FROM crm.Users WHERE LOWER(CONVERT(char(36), EntraOid)) LIKE '00000000-0000-4000-8000-00000000d0%')) AS o,
  (SELECT COUNT(*) FROM crm.Activities    WHERE CreatedBy IN (SELECT UserId FROM crm.Users WHERE LOWER(CONVERT(char(36), EntraOid)) LIKE '00000000-0000-4000-8000-00000000d0%')) AS x) t;
GO
SELECT 'D2' AS [Case], CASE WHEN COUNT(*) = 1 THEN 'PASS' ELSE 'FAIL' END AS Result,
  'Marker setting demo.seeded exists exactly once' AS Description, CONCAT('rows=', COUNT(*)) AS Detail
FROM crm.Settings WHERE SettingKey = 'demo.seeded';
GO
SELECT 'D3' AS [Case], CASE WHEN COUNT(*) = 10 AND COUNT(DISTINCT TeamId) = 3 AND SUM(CASE WHEN IsActive = 0 THEN 1 ELSE 0 END) = 1 AND SUM(CASE WHEN TeamId IS NULL AND Role = 'User' THEN 1 ELSE 0 END) = 1 THEN 'PASS' ELSE 'FAIL' END AS Result,
  '10 demo users in the test-login oid range (Admin, Managers, Users; one without team, one inactive) on 3 demo teams' AS Description,
  CONCAT('users=', COUNT(*), ' roles=', COUNT(DISTINCT Role), ' teams=', COUNT(DISTINCT TeamId)) AS Detail
FROM crm.Users WHERE LOWER(CONVERT(char(36), EntraOid)) LIKE '00000000-0000-4000-8000-00000000d0%';
GO
SELECT 'D4' AS [Case], CASE WHEN bad = 0 AND total > 500 THEN 'PASS' ELSE 'FAIL' END AS Result,
  'Audit rows exist and name the record owner as the author (never a system or admin user)' AS Description,
  CONCAT('audit=', total, ' wrongAuthor=', bad) AS Detail
FROM (SELECT COUNT(*) AS total,
        SUM(CASE WHEN a.ChangedBy <> a.OwnerId OR u.Role = 'Admin' THEN 1 ELSE 0 END) AS bad
      FROM crm.AuditLog a JOIN crm.Users u ON u.UserId = a.ChangedBy
      WHERE LOWER(CONVERT(char(36), u.EntraOid)) LIKE '00000000-0000-4000-8000-00000000d0%') t;
GO
SELECT 'D5' AS [Case], CASE WHEN bad = 0 THEN 'PASS' ELSE 'FAIL' END AS Result,
  'Demo contacts use example.com emails and 555 phone numbers only' AS Description, CONCAT('bad=', bad) AS Detail
FROM (SELECT COUNT(*) AS bad FROM crm.Contacts c
      WHERE c.CreatedBy IN (SELECT UserId FROM crm.Users WHERE LOWER(CONVERT(char(36), EntraOid)) LIKE '00000000-0000-4000-8000-00000000d0%')
        AND (c.Email NOT LIKE '%@%example.com' OR c.Phone NOT LIKE '+% 555 0%' OR c.Mobile NOT LIKE '+% 555 0%')) t;
GO
SELECT 'D6' AS [Case], CASE WHEN cur = 4 AND cn = 7 THEN 'PASS' ELSE 'FAIL' END AS Result,
  'Opportunities use DKK, EUR, SEK, USD; accounts cover DK, SE, DE, PL, US, IT, CN' AS Description,
  CONCAT('currencies=', cur, ' countries=', cn) AS Detail
FROM (SELECT (SELECT COUNT(DISTINCT CurrencyCode) FROM crm.Opportunities) AS cur, (SELECT COUNT(DISTINCT CountryCode) FROM crm.Accounts) AS cn) t;
GO
SELECT 'D7' AS [Case], CASE WHEN mn >= 18 AND cm >= 3 AND eurok = 1 AND sekok = 1 AND usdok = 1 THEN 'PASS' ELSE 'FAIL' END AS Result,
  'FX rates: EUR, SEK, USD monthly for 18+ months up to the current month, at realistic values' AS Description,
  CONCAT('months=', mn, ' currentMonthRows=', cm) AS Detail
FROM (SELECT COUNT(DISTINCT RateMonth) AS mn,
        SUM(CASE WHEN RateMonth = DATEFROMPARTS(YEAR(CAST(SYSDATETIMEOFFSET() AT TIME ZONE 'Central European Standard Time' AS date)), MONTH(CAST(SYSDATETIMEOFFSET() AT TIME ZONE 'Central European Standard Time' AS date)), 1) THEN 1 ELSE 0 END) AS cm,
        CASE WHEN MIN(CASE WHEN CurrencyCode = 'EUR' THEN Rate END) >= 7.4 AND MAX(CASE WHEN CurrencyCode = 'EUR' THEN Rate END) <= 7.5 THEN 1 ELSE 0 END AS eurok,
        CASE WHEN MIN(CASE WHEN CurrencyCode = 'SEK' THEN Rate END) >= 0.6 AND MAX(CASE WHEN CurrencyCode = 'SEK' THEN Rate END) <= 0.75 THEN 1 ELSE 0 END AS sekok,
        CASE WHEN MIN(CASE WHEN CurrencyCode = 'USD' THEN Rate END) >= 6.3 AND MAX(CASE WHEN CurrencyCode = 'USD' THEN Rate END) <= 7.1 THEN 1 ELSE 0 END AS usdok
      FROM crm.FxRates WHERE BaseCurrency = 'DKK' AND CurrencyCode IN ('EUR', 'SEK', 'USD')
        AND ModifiedBy IN (SELECT UserId FROM crm.Users WHERE LOWER(CONVERT(char(36), EntraOid)) LIKE '00000000-0000-4000-8000-00000000d0%')) t;
GO
-- Dashboard KPIs (same definitions as the app): open pipeline, won this month / quarter, closed in the last 90 days, activities overdue / today / next 7 days
DECLARE @today date = CAST(SYSDATETIMEOFFSET() AT TIME ZONE 'Central European Standard Time' AS date);
DECLARE @monthStart date = DATEFROMPARTS(YEAR(@today), MONTH(@today), 1);
DECLARE @quarterStart date = DATEFROMPARTS(YEAR(@today), ((MONTH(@today) - 1) / 3) * 3 + 1, 1);
SELECT 'D8' AS [Case], CASE WHEN openN > 0 AND wonMonth > 0 AND wonQuarter > 0 AND closed90 > 0 AND overdue > 0 AND dueToday > 0 AND next7 > 0 THEN 'PASS' ELSE 'FAIL' END AS Result,
  'Dashboard KPIs are non-zero: open pipeline, won this month and quarter, win-rate base, overdue, due today, next 7 days' AS Description,
  CONCAT('open=', openN, ' wonMonth=', wonMonth, ' wonQuarter=', wonQuarter, ' closed90=', closed90, ' overdue=', overdue, ' today=', dueToday, ' next7=', next7) AS Detail
FROM (SELECT
  (SELECT COUNT(*) FROM crm.Opportunities o JOIN crm.PipelineStages s ON s.StageId = o.StageId WHERE s.StageType = 'open') AS openN,
  (SELECT COUNT(*) FROM crm.Opportunities o JOIN crm.PipelineStages s ON s.StageId = o.StageId WHERE s.StageType = 'won' AND o.CloseDate >= @monthStart AND o.CloseDate <= @today) AS wonMonth,
  (SELECT COUNT(*) FROM crm.Opportunities o JOIN crm.PipelineStages s ON s.StageId = o.StageId WHERE s.StageType = 'won' AND o.CloseDate >= @quarterStart AND o.CloseDate <= @today) AS wonQuarter,
  (SELECT COUNT(*) FROM crm.Opportunities o JOIN crm.PipelineStages s ON s.StageId = o.StageId WHERE s.StageType IN ('won', 'lost') AND o.CloseDate >= DATEADD(day, -89, @today) AND o.CloseDate <= @today) AS closed90,
  (SELECT COUNT(*) FROM crm.Activities WHERE IsDone = 0 AND ActivityType <> 'note' AND DueDate < @today) AS overdue,
  (SELECT COUNT(*) FROM crm.Activities WHERE IsDone = 0 AND ActivityType <> 'note' AND DueDate = @today) AS dueToday,
  (SELECT COUNT(*) FROM crm.Activities WHERE IsDone = 0 AND ActivityType <> 'note' AND DueDate > @today AND DueDate <= DATEADD(day, 7, @today)) AS next7) t;
GO
SELECT 'D9' AS [Case], CASE WHEN lost > 0 AND lost = withReason THEN 'PASS' ELSE 'FAIL' END AS Result,
  'Every lost deal has a reason from the lost_reason picklist' AS Description,
  CONCAT('lost=', lost, ' withReason=', withReason) AS Detail
FROM (SELECT COUNT(*) AS lost,
        SUM(CASE WHEN o.LostReasonValueId IN (SELECT v.ValueId FROM crm.PicklistValues v JOIN crm.Picklists p ON p.PicklistId = v.PicklistId WHERE p.Code = 'lost_reason') THEN 1 ELSE 0 END) AS withReason
      FROM crm.Opportunities o JOIN crm.PipelineStages s ON s.StageId = o.StageId WHERE s.StageType = 'lost') t;
GO
EXEC sp_set_session_context @key = N'UserId', @value = NULL, @read_only = 0;
EXEC sp_set_session_context @key = N'Role',   @value = NULL, @read_only = 0;
GO
