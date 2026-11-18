-- 001_schema.sql: schema, tables and the login upsert procedure. Derived from docs/architecture/overview.md 4.1 and 4.2.
-- dbo.SchemaVersions is created by scripts/migrate.ts before the first migration runs.

CREATE SCHEMA crm AUTHORIZATION dbo;
GO
CREATE SCHEMA sec AUTHORIZATION dbo;
GO
CREATE SCHEMA app AUTHORIZATION dbo;
GO

CREATE TABLE crm.Teams (
  TeamId       int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Teams PRIMARY KEY,
  Name         nvarchar(80)     NOT NULL CONSTRAINT UQ_Teams_Name UNIQUE,
  EntraGroupId uniqueidentifier NOT NULL CONSTRAINT UQ_Teams_EntraGroupId UNIQUE,
  IsActive     bit              NOT NULL CONSTRAINT DF_Teams_IsActive DEFAULT 1,
  CreatedAt    datetime2(3)     NOT NULL CONSTRAINT DF_Teams_CreatedAt DEFAULT SYSUTCDATETIME(),
  ModifiedAt   datetime2(3)     NOT NULL CONSTRAINT DF_Teams_ModifiedAt DEFAULT SYSUTCDATETIME(),
  ModifiedBy   int              NULL,       -- FK added after Users exists
  RowVer       rowversion       NOT NULL
);

CREATE TABLE crm.Users (
  UserId       int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Users PRIMARY KEY,
  EntraOid     uniqueidentifier NOT NULL CONSTRAINT UQ_Users_EntraOid UNIQUE,
  TenantId     uniqueidentifier NOT NULL,
  DisplayName  nvarchar(200)    NOT NULL,
  Email        nvarchar(254)    NULL,
  Role         varchar(10)      NOT NULL CONSTRAINT CK_Users_Role CHECK (Role IN ('Admin','Manager','User')),
  TeamId       int              NULL CONSTRAINT FK_Users_Teams REFERENCES crm.Teams(TeamId),
  TeamStatus   varchar(10)      NOT NULL CONSTRAINT DF_Users_TeamStatus DEFAULT 'none'
                 CONSTRAINT CK_Users_TeamStatus CHECK (TeamStatus IN ('ok','none','multiple','overage')),
                 -- set at login; drives the "no team" / "in several teams" badge on Settings > Users & teams
  IsActive     bit              NOT NULL CONSTRAINT DF_Users_IsActive DEFAULT 1,
  LastSignInAt datetime2(3)     NULL,
  CreatedAt    datetime2(3)     NOT NULL CONSTRAINT DF_Users_CreatedAt DEFAULT SYSUTCDATETIME(),
  ModifiedAt   datetime2(3)     NOT NULL CONSTRAINT DF_Users_ModifiedAt DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_Users_TeamId ON crm.Users(TeamId) INCLUDE (IsActive);
ALTER TABLE crm.Teams ADD CONSTRAINT FK_Teams_ModifiedBy FOREIGN KEY (ModifiedBy) REFERENCES crm.Users(UserId);

CREATE TABLE crm.Picklists (
  PicklistId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Picklists PRIMARY KEY,
  Code       varchar(30)  NOT NULL CONSTRAINT UQ_Picklists_Code UNIQUE,  -- industry | lead_source | lost_reason | activity_type
  Name       nvarchar(60) NOT NULL
);

CREATE TABLE crm.PicklistValues (
  ValueId    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_PicklistValues PRIMARY KEY,
  PicklistId int          NOT NULL CONSTRAINT FK_PicklistValues_Picklists REFERENCES crm.Picklists(PicklistId),
  Code       varchar(30)  NULL,       -- system code, e.g. 'call' for activity_type labels; NULL for user-defined
  Label      nvarchar(80) NOT NULL,
  SortOrder  smallint     NOT NULL,
  IsActive   bit          NOT NULL CONSTRAINT DF_PicklistValues_IsActive DEFAULT 1,  -- no hard delete (UI spec 3.8)
  ModifiedBy int          NULL CONSTRAINT FK_PicklistValues_ModifiedBy REFERENCES crm.Users(UserId),
  ModifiedAt datetime2(3) NOT NULL CONSTRAINT DF_PicklistValues_ModifiedAt DEFAULT SYSUTCDATETIME(),
  RowVer     rowversion   NOT NULL,
  CONSTRAINT UQ_PicklistValues_Label UNIQUE (PicklistId, Label)
);

CREATE TABLE crm.PipelineStages (
  StageId     int IDENTITY(1,1) NOT NULL CONSTRAINT PK_PipelineStages PRIMARY KEY,
  Name        nvarchar(60) NOT NULL CONSTRAINT UQ_PipelineStages_Name UNIQUE,
  SortOrder   smallint     NOT NULL,
  Probability tinyint      NOT NULL CONSTRAINT CK_Stages_Prob CHECK (Probability BETWEEN 0 AND 100),
  StageType   varchar(4)   NOT NULL CONSTRAINT CK_Stages_Type CHECK (StageType IN ('open','won','lost')),
  ModifiedBy  int          NULL CONSTRAINT FK_Stages_ModifiedBy REFERENCES crm.Users(UserId),
  ModifiedAt  datetime2(3) NOT NULL CONSTRAINT DF_Stages_ModifiedAt DEFAULT SYSUTCDATETIME(),
  RowVer      rowversion   NOT NULL,
  CONSTRAINT CK_Stages_WonProb  CHECK (StageType <> 'won'  OR Probability = 100),
  CONSTRAINT CK_Stages_LostProb CHECK (StageType <> 'lost' OR Probability = 0)
);
-- At most one won and one lost stage; "at least one" is validated in the app (save is one transaction).
CREATE UNIQUE INDEX UX_Stages_OneWon  ON crm.PipelineStages(StageType) WHERE StageType = 'won';
CREATE UNIQUE INDEX UX_Stages_OneLost ON crm.PipelineStages(StageType) WHERE StageType = 'lost';

CREATE TABLE crm.Settings (
  SettingId    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Settings PRIMARY KEY,  -- int key for the admin block predicate
  SettingKey   varchar(64)    NOT NULL CONSTRAINT UQ_Settings_Key UNIQUE,
  SettingValue nvarchar(1000) NOT NULL,
  ModifiedBy   int            NULL CONSTRAINT FK_Settings_ModifiedBy REFERENCES crm.Users(UserId),
  ModifiedAt   datetime2(3)   NOT NULL CONSTRAINT DF_Settings_ModifiedAt DEFAULT SYSUTCDATETIME(),
  RowVer       rowversion     NOT NULL,
  CONSTRAINT CK_Settings_Visibility CHECK (SettingKey <> 'visibility.mode' OR SettingValue IN (N'private', N'team'))
);

CREATE TABLE crm.FxRates (                         -- ADR-0005
  FxRateId     int IDENTITY(1,1) NOT NULL CONSTRAINT PK_FxRates PRIMARY KEY,
  BaseCurrency char(3)       NOT NULL,
  CurrencyCode char(3)       NOT NULL,
  RateMonth    date          NOT NULL CONSTRAINT CK_FxRates_Month CHECK (DAY(RateMonth) = 1),
  Rate         decimal(19,8) NOT NULL CONSTRAINT CK_FxRates_Rate CHECK (Rate > 0),  -- 1 CurrencyCode = Rate BaseCurrency
  ModifiedBy   int           NOT NULL CONSTRAINT FK_FxRates_ModifiedBy REFERENCES crm.Users(UserId),
  ModifiedAt   datetime2(3)  NOT NULL CONSTRAINT DF_FxRates_ModifiedAt DEFAULT SYSUTCDATETIME(),
  RowVer       rowversion    NOT NULL,
  CONSTRAINT CK_FxRates_NotBase CHECK (CurrencyCode <> BaseCurrency),
  CONSTRAINT CK_FxRates_Codes   CHECK (BaseCurrency LIKE '[A-Z][A-Z][A-Z]' AND CurrencyCode LIKE '[A-Z][A-Z][A-Z]'),
  CONSTRAINT UQ_FxRates UNIQUE (BaseCurrency, CurrencyCode, RateMonth)
);

CREATE TABLE crm.BrandingAssets (
  AssetId     int IDENTITY(1,1) NOT NULL CONSTRAINT PK_BrandingAssets PRIMARY KEY,
  AssetKey    varchar(30)    NOT NULL CONSTRAINT UQ_BrandingAssets_Key UNIQUE,   -- 'logo'
  ContentType varchar(30)    NOT NULL CONSTRAINT CK_Branding_Type CHECK (ContentType IN ('image/png','image/svg+xml')),
  Content     varbinary(max) NOT NULL CONSTRAINT CK_Branding_Size CHECK (DATALENGTH(Content) <= 512000),
  Sha256      binary(32)     NOT NULL,                                          -- ETag
  ModifiedBy  int            NOT NULL CONSTRAINT FK_Branding_ModifiedBy REFERENCES crm.Users(UserId),
  ModifiedAt  datetime2(3)   NOT NULL CONSTRAINT DF_Branding_ModifiedAt DEFAULT SYSUTCDATETIME(),
  RowVer      rowversion     NOT NULL
);

CREATE TABLE crm.Accounts (
  AccountId       int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Accounts PRIMARY KEY,
  Name            nvarchar(120)  NOT NULL,
  Website         nvarchar(255)  NULL,
  Phone           nvarchar(40)   NULL,
  IndustryValueId int            NULL CONSTRAINT FK_Accounts_Industry REFERENCES crm.PicklistValues(ValueId),
  Street          nvarchar(200)  NULL,
  PostalCode      nvarchar(20)   NULL,
  City            nvarchar(100)  NULL,
  CountryCode     char(2)        NULL,
  Notes           nvarchar(2000) NULL,
  OwnerId    int NOT NULL CONSTRAINT FK_Accounts_Owner    REFERENCES crm.Users(UserId),
  TeamId     int NULL     CONSTRAINT FK_Accounts_Team     REFERENCES crm.Teams(TeamId),
  CreatedBy  int NOT NULL CONSTRAINT FK_Accounts_CreatedBy  REFERENCES crm.Users(UserId),
  CreatedAt  datetime2(3) NOT NULL CONSTRAINT DF_Accounts_CreatedAt  DEFAULT SYSUTCDATETIME(),
  ModifiedBy int NOT NULL CONSTRAINT FK_Accounts_ModifiedBy REFERENCES crm.Users(UserId),
  ModifiedAt datetime2(3) NOT NULL CONSTRAINT DF_Accounts_ModifiedAt DEFAULT SYSUTCDATETIME(),
  RowVer     rowversion NOT NULL
);
CREATE INDEX IX_Accounts_Name       ON crm.Accounts(Name);
CREATE INDEX IX_Accounts_TeamOwner  ON crm.Accounts(TeamId, OwnerId);
CREATE INDEX IX_Accounts_Owner      ON crm.Accounts(OwnerId);

CREATE TABLE crm.Contacts (
  ContactId  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Contacts PRIMARY KEY,
  AccountId  int           NULL CONSTRAINT FK_Contacts_Account REFERENCES crm.Accounts(AccountId),
  FirstName  nvarchar(80)  NULL,
  LastName   nvarchar(80)  NOT NULL,
  JobTitle   nvarchar(120) NULL,
  Email      nvarchar(254) NULL,
  Phone      nvarchar(40)  NULL,
  Mobile     nvarchar(40)  NULL,
  OwnerId    int NOT NULL CONSTRAINT FK_Contacts_Owner    REFERENCES crm.Users(UserId),
  TeamId     int NULL     CONSTRAINT FK_Contacts_Team     REFERENCES crm.Teams(TeamId),
  CreatedBy  int NOT NULL CONSTRAINT FK_Contacts_CreatedBy  REFERENCES crm.Users(UserId),
  CreatedAt  datetime2(3) NOT NULL CONSTRAINT DF_Contacts_CreatedAt  DEFAULT SYSUTCDATETIME(),
  ModifiedBy int NOT NULL CONSTRAINT FK_Contacts_ModifiedBy REFERENCES crm.Users(UserId),
  ModifiedAt datetime2(3) NOT NULL CONSTRAINT DF_Contacts_ModifiedAt DEFAULT SYSUTCDATETIME(),
  RowVer     rowversion NOT NULL
);
CREATE INDEX IX_Contacts_Account   ON crm.Contacts(AccountId);
CREATE INDEX IX_Contacts_Name      ON crm.Contacts(LastName, FirstName);
CREATE INDEX IX_Contacts_Email     ON crm.Contacts(Email);
CREATE INDEX IX_Contacts_TeamOwner ON crm.Contacts(TeamId, OwnerId);

CREATE TABLE crm.Opportunities (
  OpportunityId     int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Opportunities PRIMARY KEY,
  Name              nvarchar(120) NOT NULL,
  AccountId         int           NOT NULL CONSTRAINT FK_Opps_Account REFERENCES crm.Accounts(AccountId),
  PrimaryContactId  int           NULL     CONSTRAINT FK_Opps_Contact REFERENCES crm.Contacts(ContactId),
  StageId           int           NOT NULL CONSTRAINT FK_Opps_Stage   REFERENCES crm.PipelineStages(StageId),
  Amount            decimal(19,2) NOT NULL CONSTRAINT CK_Opps_Amount CHECK (Amount >= 0),
  CurrencyCode      char(3)       NOT NULL CONSTRAINT CK_Opps_Currency CHECK (CurrencyCode LIKE '[A-Z][A-Z][A-Z]'),
  CloseDate         date          NOT NULL,
  LeadSourceValueId int           NULL CONSTRAINT FK_Opps_LeadSource REFERENCES crm.PicklistValues(ValueId),
  LostReasonValueId int           NULL CONSTRAINT FK_Opps_LostReason REFERENCES crm.PicklistValues(ValueId),
  ClosedAt          datetime2(3)  NULL,    -- set when moved to won/lost, cleared when reopened
  OwnerId    int NOT NULL CONSTRAINT FK_Opps_Owner    REFERENCES crm.Users(UserId),
  TeamId     int NULL     CONSTRAINT FK_Opps_Team     REFERENCES crm.Teams(TeamId),
  CreatedBy  int NOT NULL CONSTRAINT FK_Opps_CreatedBy  REFERENCES crm.Users(UserId),
  CreatedAt  datetime2(3) NOT NULL CONSTRAINT DF_Opps_CreatedAt  DEFAULT SYSUTCDATETIME(),
  ModifiedBy int NOT NULL CONSTRAINT FK_Opps_ModifiedBy REFERENCES crm.Users(UserId),
  ModifiedAt datetime2(3) NOT NULL CONSTRAINT DF_Opps_ModifiedAt DEFAULT SYSUTCDATETIME(),
  RowVer     rowversion NOT NULL
);
CREATE INDEX IX_Opps_Account    ON crm.Opportunities(AccountId);
CREATE INDEX IX_Opps_StageClose ON crm.Opportunities(StageId, CloseDate) INCLUDE (Amount, CurrencyCode, OwnerId, TeamId);
CREATE INDEX IX_Opps_CloseDate  ON crm.Opportunities(CloseDate) INCLUDE (StageId, Amount, CurrencyCode, OwnerId, TeamId, LeadSourceValueId);
CREATE INDEX IX_Opps_TeamOwner  ON crm.Opportunities(TeamId, OwnerId);
CREATE INDEX IX_Opps_Contact    ON crm.Opportunities(PrimaryContactId);

CREATE TABLE crm.Activities (
  ActivityId    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Activities PRIMARY KEY,
  ActivityType  varchar(10)    NOT NULL CONSTRAINT CK_Act_Type CHECK (ActivityType IN ('call','meeting','task','note')),
  Subject       nvarchar(160)  NOT NULL,
  Description   nvarchar(4000) NULL,
  AccountId     int NULL CONSTRAINT FK_Act_Account     REFERENCES crm.Accounts(AccountId),
  ContactId     int NULL CONSTRAINT FK_Act_Contact     REFERENCES crm.Contacts(ContactId),
  OpportunityId int NULL CONSTRAINT FK_Act_Opportunity REFERENCES crm.Opportunities(OpportunityId),
  DueDate       date           NULL,   -- local date in Settings timezone
  DueTime       time(0)        NULL,
  IsDone        bit            NOT NULL CONSTRAINT DF_Act_IsDone DEFAULT 0,
  DoneAt        datetime2(3)   NULL,
  OwnerId    int NOT NULL CONSTRAINT FK_Act_Owner    REFERENCES crm.Users(UserId),
  TeamId     int NULL     CONSTRAINT FK_Act_Team     REFERENCES crm.Teams(TeamId),
  CreatedBy  int NOT NULL CONSTRAINT FK_Act_CreatedBy  REFERENCES crm.Users(UserId),
  CreatedAt  datetime2(3) NOT NULL CONSTRAINT DF_Act_CreatedAt  DEFAULT SYSUTCDATETIME(),
  ModifiedBy int NOT NULL CONSTRAINT FK_Act_ModifiedBy REFERENCES crm.Users(UserId),
  ModifiedAt datetime2(3) NOT NULL CONSTRAINT DF_Act_ModifiedAt DEFAULT SYSUTCDATETIME(),
  RowVer     rowversion NOT NULL,
  CONSTRAINT CK_Act_NoteNoDue CHECK (ActivityType <> 'note' OR (DueDate IS NULL AND DueTime IS NULL)),
  CONSTRAINT CK_Act_TimeNeedsDate CHECK (DueTime IS NULL OR DueDate IS NOT NULL)
  -- "at least one related record" is validated in the app only; links may be nulled when a parent is deleted (§4.3)
);
CREATE INDEX IX_Act_OpenDue    ON crm.Activities(DueDate) INCLUDE (ActivityType, OwnerId, TeamId) WHERE IsDone = 0;
CREATE INDEX IX_Act_Account    ON crm.Activities(AccountId);
CREATE INDEX IX_Act_Contact    ON crm.Activities(ContactId);
CREATE INDEX IX_Act_Opp        ON crm.Activities(OpportunityId);
CREATE INDEX IX_Act_TeamOwner  ON crm.Activities(TeamId, OwnerId);

CREATE TABLE crm.AuditLog (
  AuditId       bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuditLog PRIMARY KEY,
  EntityType    varchar(20)      NOT NULL CONSTRAINT CK_Audit_Entity CHECK (EntityType IN
                  ('account','contact','opportunity','activity','setting','fxrate','stage','picklistvalue','team','branding')),
  EntityId      int              NOT NULL,       -- no FK: rows outlive deleted entities
  Action        char(1)          NOT NULL CONSTRAINT CK_Audit_Action CHECK (Action IN ('C','U','D')),
  FieldName     varchar(64)      NULL,           -- NULL for C/D summary rows
  OldValue      nvarchar(4000)   NULL,
  NewValue      nvarchar(4000)   NULL,
  OwnerId       int              NOT NULL CONSTRAINT FK_Audit_Owner REFERENCES crm.Users(UserId),   -- record owner after change (before, for D)
  TeamId        int              NULL     CONSTRAINT FK_Audit_Team  REFERENCES crm.Teams(TeamId),
  ChangedBy     int              NOT NULL CONSTRAINT FK_Audit_ChangedBy REFERENCES crm.Users(UserId),
  ChangedAt     datetime2(3)     NOT NULL CONSTRAINT DF_Audit_ChangedAt DEFAULT SYSUTCDATETIME(),
  CorrelationId uniqueidentifier NOT NULL        -- request id; also shown as "Reference" on 500 pages
);
CREATE INDEX IX_Audit_Entity    ON crm.AuditLog(EntityType, EntityId, ChangedAt DESC);
CREATE INDEX IX_Audit_ChangedAt ON crm.AuditLog(ChangedAt);

CREATE TABLE app.Sessions (                         -- ADR-0004; deliberately outside crm and without RLS
  Sid       varchar(128)  NOT NULL CONSTRAINT PK_Sessions PRIMARY KEY,
  Data      nvarchar(max) NOT NULL,                 -- JSON, minimal claims only (no tokens)
  ExpiresAt datetime2(0)  NOT NULL
);
CREATE INDEX IX_Sessions_ExpiresAt ON app.Sessions(ExpiresAt);
GO

CREATE PROCEDURE crm.usp_UpsertUserOnLogin
  @EntraOid uniqueidentifier, @TenantId uniqueidentifier,
  @DisplayName nvarchar(200), @Email nvarchar(254) = NULL,
  @Role varchar(10), @GroupIdsJson nvarchar(max),         -- JSON array of group object IDs from the token
  @GroupsOverage bit = 0                                    -- 1 when the token signalled groups overage
AS
BEGIN
  SET NOCOUNT ON; SET XACT_ABORT ON;
  IF @Role NOT IN ('Admin','Manager','User') THROW 50010, 'Invalid role', 1;

  DECLARE @TeamId int, @TeamMatches int, @TeamStatus varchar(10);
  SELECT @TeamMatches = COUNT(DISTINCT t.TeamId), @TeamId = MIN(t.TeamId)
  FROM crm.Teams t
  JOIN OPENJSON(@GroupIdsJson) g ON TRY_CAST(g.[value] AS uniqueidentifier) = t.EntraGroupId
  WHERE t.IsActive = 1;

  -- A user must be in exactly ONE linked team group. Anything else fails closed (TeamId NULL = own records only).
  SET @TeamStatus = CASE WHEN @GroupsOverage = 1 THEN 'overage'
                         WHEN @TeamMatches = 1   THEN 'ok'
                         WHEN @TeamMatches = 0   THEN 'none'
                         ELSE 'multiple' END;
  IF @TeamStatus <> 'ok' SET @TeamId = NULL;

  MERGE crm.Users WITH (HOLDLOCK) AS u
  USING (SELECT @EntraOid AS EntraOid) AS s ON u.EntraOid = s.EntraOid
  WHEN MATCHED THEN UPDATE SET DisplayName = @DisplayName, Email = @Email, Role = @Role, TeamId = @TeamId,
       TeamStatus = @TeamStatus, TenantId = @TenantId, IsActive = 1,
       LastSignInAt = SYSUTCDATETIME(), ModifiedAt = SYSUTCDATETIME()
  WHEN NOT MATCHED THEN INSERT (EntraOid, TenantId, DisplayName, Email, Role, TeamId, TeamStatus, LastSignInAt)
       VALUES (@EntraOid, @TenantId, @DisplayName, @Email, @Role, @TeamId, @TeamStatus, SYSUTCDATETIME());

  SELECT UserId, TeamId, TeamStatus, @TeamMatches AS TeamMatches FROM crm.Users WHERE EntraOid = @EntraOid;
END
GO
