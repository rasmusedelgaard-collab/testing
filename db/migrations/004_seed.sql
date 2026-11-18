-- 004_seed.sql: default settings, pipeline stages and picklists. Idempotent.
-- Config tables are protected by sec.CrmConfigPolicy (Admin-only writes, even for dbo), so the seed runs
-- with a migration-scoped Admin context instead of switching the policy off. UserId 0 is not a real user.
-- Context is set and used inside ONE batch, and cleared at the end.

EXEC sp_set_session_context @key = N'UserId', @value = 0;
EXEC sp_set_session_context @key = N'Role',   @value = N'Admin';

MERGE crm.Settings AS t
USING (VALUES
  ('app.name',              N'CRM'),
  ('branding.accentColor',  N'#1D4ED8'),
  ('locale.code',           N'da-DK'),
  ('locale.timezone',       N'Europe/Copenhagen'),
  ('locale.firstDayOfWeek', N'1'),
  ('fiscal.startMonth',     N'1'),
  ('currency.base',         N'DKK'),
  ('currency.enabled',      N'DKK,EUR,USD,SEK,NOK'),
  ('visibility.mode',       N'private')
) AS s (SettingKey, SettingValue) ON t.SettingKey = s.SettingKey
WHEN NOT MATCHED THEN INSERT (SettingKey, SettingValue) VALUES (s.SettingKey, s.SettingValue);

MERGE crm.PipelineStages AS t
USING (VALUES
  (N'Qualification',  1, 10,  'open'),
  (N'Needs analysis', 2, 25,  'open'),
  (N'Proposal',       3, 50,  'open'),
  (N'Negotiation',    4, 75,  'open'),
  (N'Closed won',     5, 100, 'won'),
  (N'Closed lost',    6, 0,   'lost')
) AS s (Name, SortOrder, Probability, StageType) ON t.Name = s.Name
WHEN NOT MATCHED THEN INSERT (Name, SortOrder, Probability, StageType) VALUES (s.Name, s.SortOrder, s.Probability, s.StageType);

MERGE crm.Picklists AS t
USING (VALUES
  ('industry',      N'Industry'),
  ('lead_source',   N'Lead source'),
  ('lost_reason',   N'Lost reason'),
  ('activity_type', N'Activity type')
) AS s (Code, Name) ON t.Code = s.Code
WHEN NOT MATCHED THEN INSERT (Code, Name) VALUES (s.Code, s.Name);

MERGE crm.PicklistValues AS t
USING (
  SELECT p.PicklistId, v.Code, v.Label, v.SortOrder
  FROM (VALUES
    ('industry', NULL, N'Manufacturing', 1), ('industry', NULL, N'Technology', 2), ('industry', NULL, N'Finance', 3),
    ('industry', NULL, N'Healthcare', 4), ('industry', NULL, N'Retail', 5), ('industry', NULL, N'Services', 6),
    ('industry', NULL, N'Public sector', 7), ('industry', NULL, N'Other', 8),
    ('lead_source', NULL, N'Website', 1), ('lead_source', NULL, N'Referral', 2), ('lead_source', NULL, N'Event', 3),
    ('lead_source', NULL, N'Outbound', 4), ('lead_source', NULL, N'Partner', 5), ('lead_source', NULL, N'Other', 6),
    ('lost_reason', NULL, N'Price', 1), ('lost_reason', NULL, N'Competitor', 2), ('lost_reason', NULL, N'No budget', 3),
    ('lost_reason', NULL, N'No decision', 4), ('lost_reason', NULL, N'Timing', 5), ('lost_reason', NULL, N'Other', 6),
    ('activity_type', 'call', N'Call', 1), ('activity_type', 'meeting', N'Meeting', 2),
    ('activity_type', 'task', N'Task', 3), ('activity_type', 'note', N'Note', 4)
  ) AS v (PicklistCode, Code, Label, SortOrder)
  JOIN crm.Picklists p ON p.Code = v.PicklistCode
) AS s ON t.PicklistId = s.PicklistId AND t.Label = s.Label
WHEN NOT MATCHED THEN INSERT (PicklistId, Code, Label, SortOrder) VALUES (s.PicklistId, s.Code, s.Label, s.SortOrder);

EXEC sp_set_session_context @key = N'UserId', @value = NULL;
EXEC sp_set_session_context @key = N'Role',   @value = NULL;
GO
