# Internal CRM: Architecture Overview (v1)

Status: Approved for build (user decisions 2nd round, 2026-11-18) · Owner: Architect · Related: [ADR-0001](../adr/0001-stack.md) to [ADR-0005](../adr/0005-fx-rates.md), [UI spec](../ux/ui-spec.md)

This is the build reference. Where it conflicts with the plan, this document and the ADRs take precedence. Section 13 lists the deviations.

---

## 1. Context and goals

This is a small internal CRM (about 10 to 200 users) for Accounts, Contacts, Opportunities and Activities, with a dashboard, an analytics page and Admin settings.

- **Hosting:** Azure App Service (Linux, Node 22) and Azure SQL Database, in an EU region.
- **Sign-in:** Microsoft Entra ID. The app never stores passwords.
- **Data access:** enforced in the database by Row-Level Security (RLS). The app sets the identity, and the database decides which rows are visible. A bug in a query must not be able to leak rows.
- **Non-functional targets:** see CLAUDE.md §8. Key pages render in under 2 s. Availability is business hours. WCAG 2.2 AA. GDPR applies and data is hosted in the EU.

## 2. Components

```mermaid
flowchart LR
  subgraph Browser
    B[Server-rendered HTML<br/>+ vendored Chart.js]
  end
  subgraph Entra["Microsoft Entra ID"]
    E[App registration<br/>app roles + group claim]
  end
  subgraph AppSvc["Azure App Service (Linux, Node 22)"]
    direction TB
    MW[Express 5 middleware<br/>helmet/CSP, session, CSRF,<br/>requireAuth, requireRole]
    R[Routes + EJS views]
    S[Services<br/>kpi, analytics, fx, audit]
    REPO[Repositories]
    RLS[withRlsContext<br/>rlsPool: ResettingConnectionPool]
    SYS[systemPool<br/>sessions, login upsert, public branding]
    MI((System-assigned<br/>Managed Identity))
  end
  subgraph SQL["Azure SQL Database (Entra-only auth)"]
    CRM[(schema crm<br/>RLS policies)]
    SEC[(schema sec<br/>predicate functions)]
    APPS[(schema app<br/>Sessions - no RLS)]
  end
  KV[Key Vault<br/>client secret, session secret]
  AI[Application Insights]

  B -- HTTPS --> MW --> R --> S --> REPO --> RLS
  MW --> SYS
  R -- auth-code + PKCE --> E
  RLS -- TDS/TLS, MI token --> CRM
  SYS -- TDS/TLS, MI token --> APPS
  SYS --> CRM
  CRM -. predicates .- SEC
  AppSvc -- KV references --> KV
  AppSvc -- telemetry --> AI
  MI -.-> SQL
```

| Component | Responsibility | Must not |
|---|---|---|
| `src/config.ts` | Load and validate env vars with zod. Fail fast on startup. | Read env vars anywhere else |
| `src/auth/msal.ts` | MSAL `ConfidentialClientApplication`: login URL, code redemption, logout URL | Persist tokens |
| `src/auth/session.ts` + `sessionStore.ts` | `express-session` with the custom SQL store (ADR-0004) | Use `rlsPool` |
| `src/auth/userSync.ts` | Map roles and groups, then call `crm.usp_UpsertUserOnLogin` on `systemPool` | Touch other tables |
| `src/auth/requireRole.ts` | `requireAuth`, `requireRole('Admin')`, role hierarchy | Be the only line of defence for rows (RLS is) |
| `src/db/pool.ts` | Create `rlsPool` (a `ResettingConnectionPool`) and `systemPool` | Export `rlsPool` to anything except `withRlsContext.ts` |
| `src/db/withRlsContext.ts` | Transaction per request, set session context, commit or rollback | Render views or call external I/O inside the callback |
| `src/db/repositories/*` | Parameterised SQL only, taking a `Transaction` argument | Concatenate user input into SQL |
| `src/services/*` | KPI maths, FX conversion, audit row building. Pure functions where possible, so they can be unit-tested. | Import `mssql` |
| `src/routes/*` | Validate input (zod), call services and repositories, render | Contain SQL |

**Guardrail (increment 1 acceptance criterion):** an ESLint `no-restricted-imports` rule allows `src/db/pool.ts` to be imported only by `src/db/withRlsContext.ts`, `src/auth/sessionStore.ts`, `src/auth/userSync.ts` and `src/db/publicReads.ts`. A unit test greps `src/` for `new sql.Request(` and `.query(` outside `src/db/`.

## 3. Request flows

### 3.1 Sign-in (auth-code + PKCE, ADR-0003)

```mermaid
sequenceDiagram
  autonumber
  participant U as Browser
  participant A as App
  participant E as Entra ID
  participant D as Azure SQL
  U->>A: GET /accounts (no session)
  A-->>U: 302 /signin?returnTo=/accounts
  U->>A: GET /auth/login?returnTo=/accounts
  A->>A: PKCE verifier/challenge, state, nonce → session.auth (saved to app.Sessions)
  A-->>U: 302 authorize?response_mode=query&code_challenge=…&state=…&nonce=…
  U->>E: sign in (+MFA per tenant policy)
  E-->>U: 302 /auth/redirect?code=…&state=…
  U->>A: GET /auth/redirect
  A->>A: state == session.auth.state (timing-safe)
  A->>E: acquireTokenByCode(code, codeVerifier)
  E-->>A: id_token claims (oid, tid, name, email, roles, groups)
  A->>A: tid == ENTRA_TENANT_ID, nonce matches, role = highest(roles) or no-access
  A->>D: EXEC crm.usp_UpsertUserOnLogin (systemPool)
  D-->>A: UserId, TeamId
  A->>A: session.regenerate(); session.user = {userId, oid, name, email, role, teamId, authAt}; new csrfToken
  A-->>U: 302 /accounts (returnTo validated: starts with "/" and not "//")
```

### 3.2 Authenticated request (auth → session → withRlsContext → query)

```mermaid
sequenceDiagram
  autonumber
  participant U as Browser
  participant M as Middleware
  participant H as Route handler
  participant W as withRlsContext
  participant P as rlsPool
  participant D as Azure SQL
  U->>M: GET /opportunities/42 (cookie __Host-crm.sid)
  M->>M: load session (systemPool → app.Sessions), idle/absolute timeout check
  M->>M: requireAuth → req.rls = {userId, role, teamId}; POST → CSRF check
  M->>H: next()
  H->>H: zod-validate params/query
  H->>W: withRlsContext(req.rls, tx => repo.getOpportunity(tx, 42))
  W->>P: new Transaction(rlsPool).begin(READ_COMMITTED)
  W->>D: guard + sp_set_session_context UserId/Role/TeamId @read_only=1
  W->>D: SELECT … WHERE OpportunityId=@id (RLS filter applied by engine)
  D-->>W: 0 or 1 row
  W->>D: COMMIT (or ROLLBACK on error)
  W->>P: release → tedious Connection.reset() → sp_reset_connection → back to pool
  W-->>H: result
  H-->>U: 200 render, or 404 if no row (no existence leak)
```

## 4. Data model (schema `crm`)

Conventions:
- Surrogate keys are `int IDENTITY`. AuditLog uses `bigint`.
- All timestamps are `datetime2(3)` in UTC (`SYSUTCDATETIME()`). Dates without a time (`CloseDate`, `DueDate`) are `date`, interpreted in the timezone set in Settings.
- Money is `decimal(19,2)`. FX rates are `decimal(19,8)`.
- Text is `nvarchar`. Codes are `varchar` or `char`.
- **Entity tables** (Accounts, Contacts, Opportunities, Activities) all carry the common columns: `OwnerId int NOT NULL FK Users`, `TeamId int NULL FK Teams`, `CreatedBy int NOT NULL FK Users`, `CreatedAt`, `ModifiedBy int NOT NULL FK Users`, `ModifiedAt` and `RowVer rowversion`.
- **Server-side TeamId:** the app never takes `TeamId` from a form. It sets `TeamId = (SELECT TeamId FROM crm.Users WHERE UserId = @OwnerId)` on every insert and on every owner change.
- **Foreign keys use `NO ACTION`** (no cascades). SQL Server rejects multiple cascade paths here. Explicit app logic is also easier to audit. See §4.3.

```mermaid
erDiagram
  Teams ||--o{ Users : "TeamId"
  Users ||--o{ Accounts : owns
  Accounts ||--o{ Contacts : "AccountId (nullable)"
  Accounts ||--o{ Opportunities : AccountId
  Contacts ||--o{ Opportunities : PrimaryContactId
  PipelineStages ||--o{ Opportunities : StageId
  Accounts ||--o{ Activities : AccountId
  Contacts ||--o{ Activities : ContactId
  Opportunities ||--o{ Activities : OpportunityId
  Picklists ||--o{ PicklistValues : PicklistId
  PicklistValues ||--o{ Accounts : IndustryValueId
  PicklistValues ||--o{ Opportunities : "LeadSource/LostReason"
  Users ||--o{ AuditLog : ChangedBy
```

### 4.1 DDL (migration `001_schema.sql`)

```sql
CREATE SCHEMA crm AUTHORIZATION dbo;
GO
CREATE SCHEMA sec AUTHORIZATION dbo;
GO
CREATE SCHEMA app AUTHORIZATION dbo;
GO

CREATE TABLE dbo.SchemaVersions (
  Version    int           NOT NULL CONSTRAINT PK_SchemaVersions PRIMARY KEY,
  Name       nvarchar(200) NOT NULL,
  Checksum   char(64)      NOT NULL,            -- SHA-256 of the file; migrate refuses if an applied file changed
  AppliedAt  datetime2(3)  NOT NULL CONSTRAINT DF_SchemaVersions_AppliedAt DEFAULT SYSUTCDATETIME()
);

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
```

### 4.2 Login upsert procedure (also in `001_schema.sql`)

`crm_app` has no INSERT or UPDATE on `crm.Users`. Login writes go through this procedure. Both the procedure and the table are owned by dbo, so ownership chaining lets the procedure write.

```sql
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
```
**Exactly one team group (user decision 2026-11-18).**
- `TeamStatus = 'multiple'` (the groups claim contains more than one linked, active team group): `TeamId = NULL`, so the user sees own records only. The app logs a **warning** with the `oid` and the match count: "user in several team groups; no team applied". Settings > Users & teams shows the warning badge "In {n} teams — check Entra groups" (UI spec).
- `TeamStatus = 'none'`: `TeamId = NULL`, badge "No team — sees own records only".
- If the ID token signals groups overage (`_claim_names.groups` present, or `groups` missing while `hasgroups` is true), the app passes `@GroupIdsJson = '[]'` and `@GroupsOverage = 1`. The user gets `TeamId = NULL` and `TeamStatus = 'overage'`, and the app logs an **error** with the `oid` (§12.2).
- The badge is driven by `crm.Users.TeamStatus`, as stored at the user's last sign-in.

### 4.3 Deletes and links (no cascades)
Each delete runs in one `withRlsContext` transaction:
- **Delete an Account:** null `Contacts.AccountId` and `Activities.AccountId` on the rows you can see, then `DELETE`. If Opportunities still reference the account, or rows you cannot see still link to it, the DB raises error 547. The app shows: "This account still has linked opportunities or records you can't see. Move or delete them first."
- **Delete a Contact:** null `Opportunities.PrimaryContactId` and `Activities.ContactId`, then delete.
- **Delete an Opportunity:** null `Activities.OpportunityId`, then delete.
- Activities left with no links are kept. They show "Related to: (deleted record)".
- Use `DELETE … OUTPUT deleted.OwnerId, deleted.TeamId, deleted.Name` to write the `D` audit row (§7).

### 4.4 Settings keys (seeded in `004_seed.sql`)
| Key | Example | Notes |
|---|---|---|
| `app.name` | `CRM` | max 40 |
| `branding.accentColor` | `#1D4ED8` | contrast against white must be at least 4.5:1, validated in the app |
| `locale.code` | `da-DK` | `da-DK`, `en-GB` or `en-US` |
| `locale.timezone` | `Europe/Copenhagen` | IANA name |
| `locale.firstDayOfWeek` | `1` | 1 = Monday |
| `fiscal.startMonth` | `1` | 1–12 |
| `currency.base` | `DKK` | ISO 4217 |
| `currency.enabled` | `DKK,EUR,USD,SEK,NOK` | CSV; must include the base currency |
| `visibility.mode` | `private` | `private` or `team`. A DB CHECK enforces the values. RLS reads this key. |

The seed also inserts default stages:
- Qualification 10
- Needs analysis 25
- Proposal 50
- Negotiation 75
- Closed won 100 (won)
- Closed lost 0 (lost)

It also inserts the four picklists and their activity-type labels with codes.

## 5. Row-Level Security (migration `002_rls.sql`, ADR-0002)

### 5.1 Session context contract
| Key | Type | Source | Notes |
|---|---|---|---|
| `UserId` | int | `crm.Users.UserId` from the session | NULL or missing means zero rows and every write blocked |
| `Role` | nvarchar(10) | `'Admin'`, `'Manager'` or `'User'` | Any other value is treated as no access |
| `TeamId` | int or NULL | `crm.Users.TeamId` from the session | NULL means no team, so only own rows (Admin excepted) |

All three keys are set with `@read_only = 1`. Visibility mode is **not** a context key. The predicate reads it from `crm.Settings`.

**Why read visibility from Settings rather than a `Visibility` context key:**
- There is one source of truth in the database. An Admin change takes effect on the next statement on every App Service instance, with no cache and no invalidation. This meets the acceptance criterion "changes take effect immediately".
- The app supplies one less trusted input.
- It fails closed. A missing row, or any value other than `team`, behaves as `private`.
- Only Admin can change the row (admin block predicate, §6).
- Cost: one unique-key seek on a table of about ten rows, which the optimiser folds into the plan. This is negligible.
- Trade-off: `SCHEMABINDING` means `crm.Settings` and `crm.Users` cannot be altered while the policy exists. A migration must `ALTER SECURITY POLICY … WITH (STATE = OFF)` or drop and recreate the policy around such changes.

### 5.2 Access rules
| Operation | Admin | Manager | User (`private`) | User (`team`) |
|---|---|---|---|---|
| Read (FILTER) | all | own + own team | own | own + own team |
| Update existing row (BEFORE UPDATE, `fn_RowUpdate`) | all | own + own team | own | own + own team |
| Delete existing row (BEFORE DELETE, `fn_RowDelete`) | all | own + own team | own | **own only** |
| Resulting row (AFTER INSERT/UPDATE, `fn_RowWriteTarget`) | any | owner is self, or an active member of own team; TeamId = own team | same as Manager | same as Manager |

User decisions (2026-11-18, ADR-0002):
- In `team` mode a User can **read and update** team-mates' records, but **cannot delete** them. Delete stays with the owner, a Manager of the same team, and Admin.
- **Nobody except Admin can move a record across teams.** `fn_RowWriteTarget` runs AFTER UPDATE as well as AFTER INSERT, so the resulting `TeamId` must equal the caller's `TeamId`.
- **Owner reassignment rule (applies to Managers and Users alike, both modes; only Admin is exempt, so only Admin can reassign across teams):** the resulting `OwnerId` must be either the caller, or a user whose `crm.Users.TeamId` equals the caller's `TeamId` and whose `IsActive = 1`. The resulting `TeamId` must equal the caller's `TeamId`. So in `team` mode a User may reassign a team-mate's record to themselves or to any other **active** team-mate. They may not assign it to an inactive user or to anyone outside the team. In `private` mode a User may hand their **own** record to an active team-mate (and then loses sight of it, §9).
- Consequence of the rule: a record owned by a team-mate who has since been deactivated can be updated by a non-Admin only if the same update reassigns it to an active team-mate. The edit form shows the owner picker with "Current owner is inactive: choose a new owner" in that case.
- A user with `TeamId = NULL` (no team group, or groups overage) never matches a team rule. They can read, update and delete only their own records.
- Every edit is attributed: `ModifiedBy` on the row, and `AuditLog.ChangedBy` for audited fields (§7). The record page shows "Last modified by {name}".

### 5.3 Predicate functions

```sql
-- Read access: FILTER predicate
CREATE FUNCTION sec.fn_RowAccess(@OwnerId int, @TeamId int)
RETURNS TABLE WITH SCHEMABINDING AS
RETURN
  SELECT 1 AS ok
  FROM (SELECT CAST(SESSION_CONTEXT(N'UserId') AS int)          AS UserId,
               CAST(SESSION_CONTEXT(N'Role')   AS nvarchar(10)) AS Role,
               CAST(SESSION_CONTEXT(N'TeamId') AS int)          AS TeamId) AS c
  WHERE c.UserId IS NOT NULL
    AND c.Role IN (N'Admin', N'Manager', N'User')
    AND (   c.Role = N'Admin'
         OR @OwnerId = c.UserId
         OR (    @TeamId = c.TeamId                       -- NULL team never matches
             AND (   c.Role = N'Manager'
                  OR EXISTS (SELECT 1 FROM crm.Settings AS s
                             WHERE s.SettingKey = 'visibility.mode'
                               AND s.SettingValue = N'team'))));
GO

-- Update an existing row: BEFORE UPDATE.
-- Admin, owner, Manager of the same team, or (visibility = 'team' AND same team).
-- Deliberately the same rule as fn_RowAccess today, but a separate function so read and
-- update rules can diverge later by migration without touching the other.
CREATE FUNCTION sec.fn_RowUpdate(@OwnerId int, @TeamId int)
RETURNS TABLE WITH SCHEMABINDING AS
RETURN
  SELECT 1 AS ok
  FROM (SELECT CAST(SESSION_CONTEXT(N'UserId') AS int)          AS UserId,
               CAST(SESSION_CONTEXT(N'Role')   AS nvarchar(10)) AS Role,
               CAST(SESSION_CONTEXT(N'TeamId') AS int)          AS TeamId) AS c
  WHERE c.UserId IS NOT NULL
    AND c.Role IN (N'Admin', N'Manager', N'User')
    AND (   c.Role = N'Admin'
         OR @OwnerId = c.UserId
         OR (    @TeamId = c.TeamId                       -- NULL team never matches
             AND (   c.Role = N'Manager'
                  OR EXISTS (SELECT 1 FROM crm.Settings AS s
                             WHERE s.SettingKey = 'visibility.mode'
                               AND s.SettingValue = N'team'))));
GO

-- Delete an existing row: BEFORE DELETE.
-- Admin, owner, or Manager of the same team. Visibility mode never grants delete.
CREATE FUNCTION sec.fn_RowDelete(@OwnerId int, @TeamId int)
RETURNS TABLE WITH SCHEMABINDING AS
RETURN
  SELECT 1 AS ok
  FROM (SELECT CAST(SESSION_CONTEXT(N'UserId') AS int)          AS UserId,
               CAST(SESSION_CONTEXT(N'Role')   AS nvarchar(10)) AS Role,
               CAST(SESSION_CONTEXT(N'TeamId') AS int)          AS TeamId) AS c
  WHERE c.UserId IS NOT NULL
    AND c.Role IN (N'Admin', N'Manager', N'User')
    AND (   c.Role = N'Admin'
         OR @OwnerId = c.UserId
         OR (c.Role = N'Manager' AND @TeamId = c.TeamId));
GO

-- Values a row may end up with: AFTER INSERT / AFTER UPDATE.
-- Non-Admin: resulting TeamId = caller's TeamId (no cross-team moves), and resulting
-- OwnerId = caller, or an ACTIVE user whose crm.Users.TeamId = caller's TeamId.
CREATE FUNCTION sec.fn_RowWriteTarget(@OwnerId int, @TeamId int)
RETURNS TABLE WITH SCHEMABINDING AS
RETURN
  SELECT 1 AS ok
  FROM (SELECT CAST(SESSION_CONTEXT(N'UserId') AS int)          AS UserId,
               CAST(SESSION_CONTEXT(N'Role')   AS nvarchar(10)) AS Role,
               CAST(SESSION_CONTEXT(N'TeamId') AS int)          AS TeamId) AS c
  WHERE c.UserId IS NOT NULL
    AND c.Role IN (N'Admin', N'Manager', N'User')
    AND (   c.Role = N'Admin'
         OR (    @OwnerId = c.UserId
             AND (@TeamId = c.TeamId OR (@TeamId IS NULL AND c.TeamId IS NULL)))
         OR (    @TeamId = c.TeamId
             AND EXISTS (SELECT 1 FROM crm.Users AS u
                         WHERE u.UserId = @OwnerId AND u.TeamId = @TeamId AND u.IsActive = 1)));
GO

-- Audit rows: ChangedBy must be the caller, and the row must be within the caller's write scope
CREATE FUNCTION sec.fn_AuditInsert(@ChangedBy int, @OwnerId int, @TeamId int)
RETURNS TABLE WITH SCHEMABINDING AS
RETURN
  SELECT 1 AS ok
  FROM (SELECT CAST(SESSION_CONTEXT(N'UserId') AS int)          AS UserId,
               CAST(SESSION_CONTEXT(N'Role')   AS nvarchar(10)) AS Role,
               CAST(SESSION_CONTEXT(N'TeamId') AS int)          AS TeamId) AS c
  WHERE c.UserId IS NOT NULL
    AND c.Role IN (N'Admin', N'Manager', N'User')
    AND @ChangedBy = c.UserId
    AND (c.Role = N'Admin' OR @OwnerId = c.UserId OR @TeamId = c.TeamId);
GO

-- Config tables: any valid context may read
CREATE FUNCTION sec.fn_HasContext(@RowId int)
RETURNS TABLE WITH SCHEMABINDING AS
RETURN
  SELECT 1 AS ok
  WHERE CAST(SESSION_CONTEXT(N'UserId') AS int) IS NOT NULL
    AND CAST(SESSION_CONTEXT(N'Role') AS nvarchar(10)) IN (N'Admin', N'Manager', N'User');
GO

-- Config tables: only Admin may write. @RowId is unused; it exists so every predicate binds to a column.
CREATE FUNCTION sec.fn_AdminWrite(@RowId int)
RETURNS TABLE WITH SCHEMABINDING AS
RETURN
  SELECT 1 AS ok
  WHERE CAST(SESSION_CONTEXT(N'UserId') AS int) IS NOT NULL
    AND CAST(SESSION_CONTEXT(N'Role') AS nvarchar(10)) = N'Admin';
GO
```

### 5.4 Security policies

```sql
CREATE SECURITY POLICY sec.CrmRowPolicy
  ADD FILTER PREDICATE sec.fn_RowAccess(OwnerId, TeamId)      ON crm.Accounts,
  ADD BLOCK  PREDICATE sec.fn_RowWriteTarget(OwnerId, TeamId) ON crm.Accounts AFTER INSERT,
  ADD BLOCK  PREDICATE sec.fn_RowWriteTarget(OwnerId, TeamId) ON crm.Accounts AFTER UPDATE,
  ADD BLOCK  PREDICATE sec.fn_RowUpdate(OwnerId, TeamId)      ON crm.Accounts BEFORE UPDATE,
  ADD BLOCK  PREDICATE sec.fn_RowDelete(OwnerId, TeamId)      ON crm.Accounts BEFORE DELETE,

  ADD FILTER PREDICATE sec.fn_RowAccess(OwnerId, TeamId)      ON crm.Contacts,
  ADD BLOCK  PREDICATE sec.fn_RowWriteTarget(OwnerId, TeamId) ON crm.Contacts AFTER INSERT,
  ADD BLOCK  PREDICATE sec.fn_RowWriteTarget(OwnerId, TeamId) ON crm.Contacts AFTER UPDATE,
  ADD BLOCK  PREDICATE sec.fn_RowUpdate(OwnerId, TeamId)      ON crm.Contacts BEFORE UPDATE,
  ADD BLOCK  PREDICATE sec.fn_RowDelete(OwnerId, TeamId)      ON crm.Contacts BEFORE DELETE,

  ADD FILTER PREDICATE sec.fn_RowAccess(OwnerId, TeamId)      ON crm.Opportunities,
  ADD BLOCK  PREDICATE sec.fn_RowWriteTarget(OwnerId, TeamId) ON crm.Opportunities AFTER INSERT,
  ADD BLOCK  PREDICATE sec.fn_RowWriteTarget(OwnerId, TeamId) ON crm.Opportunities AFTER UPDATE,
  ADD BLOCK  PREDICATE sec.fn_RowUpdate(OwnerId, TeamId)      ON crm.Opportunities BEFORE UPDATE,
  ADD BLOCK  PREDICATE sec.fn_RowDelete(OwnerId, TeamId)      ON crm.Opportunities BEFORE DELETE,

  ADD FILTER PREDICATE sec.fn_RowAccess(OwnerId, TeamId)      ON crm.Activities,
  ADD BLOCK  PREDICATE sec.fn_RowWriteTarget(OwnerId, TeamId) ON crm.Activities AFTER INSERT,
  ADD BLOCK  PREDICATE sec.fn_RowWriteTarget(OwnerId, TeamId) ON crm.Activities AFTER UPDATE,
  ADD BLOCK  PREDICATE sec.fn_RowUpdate(OwnerId, TeamId)      ON crm.Activities BEFORE UPDATE,
  ADD BLOCK  PREDICATE sec.fn_RowDelete(OwnerId, TeamId)      ON crm.Activities BEFORE DELETE,

  -- AuditLog is append-only: crm_app has no UPDATE/DELETE grant, so only the insert is guarded
  ADD FILTER PREDICATE sec.fn_RowAccess(OwnerId, TeamId)                ON crm.AuditLog,
  ADD BLOCK  PREDICATE sec.fn_AuditInsert(ChangedBy, OwnerId, TeamId)   ON crm.AuditLog AFTER INSERT
WITH (STATE = ON, SCHEMABINDING = ON);
GO

CREATE SECURITY POLICY sec.CrmConfigPolicy
  -- crm.Settings: no FILTER (fn_RowAccess reads it; the sign-in page reads app.name). Admin-only writes.
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(SettingId) ON crm.Settings AFTER INSERT,
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(SettingId) ON crm.Settings BEFORE UPDATE,
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(SettingId) ON crm.Settings BEFORE DELETE,

  -- crm.BrandingAssets: no FILTER (logo shown on the public sign-in page). Admin-only writes.
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(AssetId) ON crm.BrandingAssets AFTER INSERT,
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(AssetId) ON crm.BrandingAssets BEFORE UPDATE,
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(AssetId) ON crm.BrandingAssets BEFORE DELETE,

  ADD FILTER PREDICATE sec.fn_HasContext(FxRateId) ON crm.FxRates,
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(FxRateId) ON crm.FxRates AFTER INSERT,
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(FxRateId) ON crm.FxRates BEFORE UPDATE,
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(FxRateId) ON crm.FxRates BEFORE DELETE,

  ADD FILTER PREDICATE sec.fn_HasContext(StageId) ON crm.PipelineStages,
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(StageId) ON crm.PipelineStages AFTER INSERT,
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(StageId) ON crm.PipelineStages BEFORE UPDATE,
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(StageId) ON crm.PipelineStages BEFORE DELETE,

  ADD FILTER PREDICATE sec.fn_HasContext(ValueId) ON crm.PicklistValues,
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(ValueId) ON crm.PicklistValues AFTER INSERT,
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(ValueId) ON crm.PicklistValues BEFORE UPDATE,

  -- crm.Teams: no FILTER (usp_UpsertUserOnLogin resolves teams before any context exists). Admin-only writes.
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(TeamId) ON crm.Teams AFTER INSERT,
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(TeamId) ON crm.Teams BEFORE UPDATE
WITH (STATE = ON, SCHEMABINDING = ON);
```

Notes:
- `crm.Users` and `crm.Picklists` have no policy, and `crm.Teams` has no FILTER (team names are not sensitive; the login procedure must read it without context):
  - `crm.Users` is read by `fn_RowWriteTarget`, and the owner picker needs it.
  - The app cannot write `crm.Users` directly (procedure only).
  - `crm.Picklists` is read-only for the app.
- A FILTER predicate also limits UPDATE and DELETE to visible rows. So a hidden row reports `@@ROWCOUNT = 0` and the app returns 404. A blocked write raises error 33504. The app maps that to 403 "You don't have access to this page."
  - `fn_RowUpdate` is therefore defence in depth today (it equals the read rule). `fn_RowDelete` is the one that bites: in `team` mode a team-mate's row is visible, so a User's DELETE of it raises 33504 and gets 403. The UI hides the Delete button when the user is not the owner, a Manager or Admin (UI spec), but the database is the enforcement.
- Admin write enforcement is layered:
  1. Route `requireRole('Admin')` gives a 403 page.
  2. `fn_AdminWrite` raises error 33504 even if a route check is missed.
  3. Grants: no DELETE on Settings or PicklistValues.

### 5.5 RLS integration test (`db/tests/rls-integration.sql`)
Run as a `WITHOUT LOGIN` test user that is a member of `crm_app_role`, using `EXECUTE AS USER`. Set context with `@read_only = 0` (test only) between cases, or use one batch per case. The tests seed 2 teams (each with an `EntraGroupId`), 5 users (A1 Admin, M1 Manager team 1, U1 and U2 User team 1, U4 User team 1 with `IsActive = 0`, U3 User team 2) and one record per owner in each entity table. "Team mode" means `visibility.mode = 'team'`, set as A1 before the case.

Required assertions:
1. No context returns 0 rows on every entity table, AuditLog, FxRates, PipelineStages and PicklistValues.
2. U1 private: sees own rows only. U1 team: sees U1 + U2 + U4 rows, not U3 rows.
3. M1: sees team 1 rows. A1: sees all.
4. **U1 private mode:** an UPDATE of U2's row affects 0 rows (filtered). A DELETE of U2's row affects 0 rows.
5. **U1 team mode, update allowed:** an UPDATE of U2's row that changes a business field (e.g. Opportunity `Amount`, Account `Name`) succeeds, and `ModifiedBy` = U1.
6. **U1 team mode, delete blocked:** a DELETE of U2's row fails with 33504, and the row still exists.
7. **U1 team mode, no team change:** an UPDATE of U2's row (and of U1's own row) that sets `TeamId = 2` fails with 33504.
8. **U1 team mode, owner reassignment:** UPDATE of U2's row with `OwnerId = U1` succeeds, and setting it back to `OwnerId = U2` succeeds; with `OwnerId = U3` fails with 33504; with `OwnerId = U4` (inactive team-mate) fails with 33504.
9. **U1 team mode, other team:** an UPDATE or DELETE of U3's row affects 0 rows.
10. U1 (either mode): INSERT with Owner U3 or TeamId 2 fails. INSERT with Owner U2 (active, same team) succeeds. INSERT with Owner U4 (inactive) fails.
11. M1: UPDATE and DELETE of U2's row succeed. UPDATE/DELETE of U3's row affects 0 rows. UPDATE setting `TeamId = 2` fails. UPDATE of U2's row with `OwnerId = U1` succeeds; with `OwnerId = U3` (other team) fails with 33504.
12. A1: UPDATE moving U2's row to team 2 with Owner U3 succeeds (only Admin can move across teams).
13. A user with no team (context `TeamId` NULL) in team mode: sees, updates and deletes own rows only; another NULL-team user's rows are invisible.
14. Non-admin: INSERT or UPDATE on Settings, FxRates, PipelineStages, PicklistValues, Teams or BrandingAssets fails with 33504. Admin succeeds.
15. AuditLog INSERT with `ChangedBy` other than self fails. U1 in team mode can insert an audit row for U2's record (`OwnerId = U2`, `TeamId = 1`, `ChangedBy = U1`).
16. `crm_app_role` cannot run `ALTER SECURITY POLICY`, `SELECT` on `dbo.SchemaVersions`, or `INSERT` on `crm.Users`.
17. Calling `sp_set_session_context 'UserId'` twice with `@read_only = 1` fails on the second call (error 15664).

## 6. Database principals and grants (migration `003_roles_grants.sql`)

- **App identity.** The App Service system-assigned managed identity is mapped to a contained user named `crm_app`. `WITH OBJECT_ID` binds the user to the identity, so the name does not have to match the web app name.
- **Deployer identity.** Migrations run as a separate deployer identity: an Entra group "CRM SQL Admins", which is the server's Entra admin, or the CI workload identity made `db_owner` in the database. The app never runs DDL.

```sql
-- Run once per environment by an Entra admin (not by migrate if the deployer lacks ALTER ANY USER):
CREATE USER [crm_app] FROM EXTERNAL PROVIDER WITH OBJECT_ID = '<web app managed identity CLIENT/APP ID>';
GO
CREATE ROLE crm_app_role AUTHORIZATION dbo;
ALTER ROLE crm_app_role ADD MEMBER [crm_app];
-- Dev only: ALTER ROLE crm_app_role ADD MEMBER [developer@contoso.com];  (local azure-active-directory-default)

GRANT SELECT, INSERT, UPDATE, DELETE ON crm.Accounts      TO crm_app_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON crm.Contacts      TO crm_app_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON crm.Opportunities TO crm_app_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON crm.Activities    TO crm_app_role;
GRANT SELECT, INSERT                 ON crm.AuditLog      TO crm_app_role;   -- append-only
GRANT SELECT                         ON crm.Users         TO crm_app_role;   -- writes via proc only
GRANT SELECT                         ON crm.Picklists     TO crm_app_role;
GRANT SELECT, INSERT, UPDATE         ON crm.PicklistValues TO crm_app_role;  -- deactivate, never delete
GRANT SELECT, INSERT, UPDATE, DELETE ON crm.PipelineStages TO crm_app_role;
GRANT SELECT, INSERT, UPDATE         ON crm.Settings      TO crm_app_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON crm.FxRates       TO crm_app_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON crm.BrandingAssets TO crm_app_role;
GRANT SELECT, INSERT, UPDATE         ON crm.Teams         TO crm_app_role;
GRANT EXECUTE ON crm.usp_UpsertUserOnLogin                TO crm_app_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON app.Sessions      TO crm_app_role;
-- Deliberately NOT granted: anything on schema sec or dbo, ALTER, CONTROL, VIEW DEFINITION,
-- ALTER ANY SECURITY POLICY, db_owner/db_ddladmin/db_datawriter/db_datareader membership.
```

## 7. Audit log design
- **Where:** `src/services/audit.ts` builds rows. The repository inserts them in the **same transaction** as the change, so either both commit or neither does. There are no triggers, which keeps them explicit and testable.
- **What is logged:**
  - Create (`C`, one summary row) and delete (`D`, one row, with the record name in `OldValue`) for all four entities.
  - Field rows (`U`) for Opportunity `Amount`, `StageId` (logged as the stage name), `CurrencyCode`, `CloseDate` and `OwnerId`. Also `OwnerId` on Accounts, Contacts and Activities.
  - Every change to Settings, FxRates, PipelineStages, PicklistValues, Teams and Branding. For these rows `OwnerId = ChangedBy` and `TeamId = NULL`.
- **How old and new values are captured:** `UPDATE … OUTPUT deleted.Amount, inserted.Amount, …`, so they come from the database and not from the form.
- **Values:** text in invariant format (decimal point `.`, ISO dates), truncated to 4000 characters. No full contact payloads, to keep personal data to a minimum.
- **Who sees it:** the FILTER predicate follows the record's owner and team at the time of the change. A record detail page can show "History" from it.
- **Tamper resistance:**
  - No UPDATE or DELETE grant.
  - The block predicate forces `ChangedBy` = caller.
  - Azure SQL Auditing (server level, to Log Analytics) records DDL and admin actions outside the app.
- **Retention:** keep indefinitely in v1. This is an open item for the product owner (GDPR).

## 8. Connection pool and session context

Two pools are created in `src/db/pool.ts`. Both use the same identity, chosen by `DB_AUTH_MODE` (§11): the managed identity in Azure, or the SQL login on the interim test host (§12.3).

| Pool | Used by | Session context |
|---|---|---|
| `rlsPool` (`ResettingConnectionPool`, `max` = `SQL_POOL_MAX`, default 10) | `withRlsContext` only | Set per request, read-only, cleared on release |
| `systemPool` (`max` 4) | session store, `usp_UpsertUserOnLogin`, `publicReads` (app.name, branding, logo), `/readyz` | Never set, so RLS tables return 0 rows |

Both pools use `min: 0` and `idleTimeoutMillis: 30000`. Do not keep warm connections: serverless auto-pause only happens when the database has **no sessions** and no CPU for the whole delay (§8.1).

**Important:** `mssql` 12.x (checked against 12.7.14) does **not** reset a connection when it is released to the pool. `ConnectionPool.release()` only hands it back to tarn. Read-only context keys would otherwise survive into the next request. The design therefore makes the reset explicit:

```ts
// src/db/pool.ts (shape, not final code)
class ResettingConnectionPool extends sql.ConnectionPool {
  override release(connection: TediousConnection): this {
    // tedious Connection.reset() runs sp_reset_connection (TDS RESETCONNECTION flag): clears SESSION_CONTEXT,
    // temp tables, SET options, open transactions.
    connection.reset((err) => {
      if (err) { logger.error({ err }, 'connection reset failed; discarding'); connection.close(); }
      super.release(connection);           // closed connections fail tarn validation and are destroyed
    });
    return this;
  }
}
```

`withRlsContext` contract:
```ts
type RlsContext = { userId: number; role: 'Admin' | 'Manager' | 'User'; teamId: number | null };
export async function withRlsContext<T>(ctx: RlsContext, fn: (tx: sql.Transaction) => Promise<T>): Promise<T>
```
1. Validate `ctx` with zod. A missing or invalid value throws, and no DB call is made.
2. `const tx = new sql.Transaction(rlsPool); await tx.begin(sql.ISOLATION_LEVEL.READ_COMMITTED);` READ_COMMITTED_SNAPSHOT is on by default in Azure SQL.
3. Run one parameterised batch on `tx`:
   ```sql
   IF SESSION_CONTEXT(N'UserId') IS NOT NULL OR SESSION_CONTEXT(N'Role') IS NOT NULL
     THROW 51000, 'Stale RLS context on pooled connection', 1;   -- defence in depth if reset ever fails
   EXEC sp_set_session_context @key = N'UserId', @value = @UserId, @read_only = 1;
   EXEC sp_set_session_context @key = N'Role',   @value = @Role,   @read_only = 1;
   EXEC sp_set_session_context @key = N'TeamId', @value = @TeamId, @read_only = 1;
   ```
   Bind `@UserId` as `sql.Int`, `@Role` as `sql.NVarChar(10)` and `@TeamId` as `sql.Int` (NULL allowed).
4. `const result = await fn(tx); await tx.commit(); return result;`
5. On any error: `rollback` if the transaction is still active, then rethrow. Error 51000 is logged as a security event.
6. One call per request. Collect all data, including the per-request Settings read, in a single callback. Render after commit. Never `await` HTTP or other I/O inside the callback.
7. Map errors in the error handler:

   | Error | Response |
   |---|---|
   | 33504 (RLS block) | 403 |
   | 547 (FK) | friendly 409 message |
   | 2627 / 2601 (unique) | validation error |
   | optimistic-concurrency miss | 409 conflict page |

**Optimistic concurrency:** edit forms carry `RowVer` as base64 in a hidden field. Updates use `UPDATE … WHERE Id = @Id AND RowVer = @RowVer`. If `@@ROWCOUNT = 0`, run `SELECT 1 WHERE Id = @Id`: a row found means a conflict (409), no row means 404.

### 8.1 Serverless auto-pause: transient errors and "waking up the database"

User decision (2026-11-18): production Azure SQL is **serverless with auto-pause after 60 minutes** (§12.1). The first request after about an hour idle finds the database paused. Connecting triggers a resume, which typically takes from a few seconds up to about a minute. Until then, logins fail with **error 40613** ("Database … is not currently available").

**Transient error set** (`src/db/transient.ts`, pure function `isTransient(err)`, unit-tested):
- SQL error numbers: `40613`, `40197`, `40501`, `40540`, `49918`, `49919`, `49920`, `4221`, `10928`, `10929`.
- tedious/driver connection errors during connect: `ETIMEOUT`, `ESOCKET`, `ECONNRESET`, `ECONNCLOSED`.
- Everything else (including 33504, 547, 51000, login/permission failures such as 18456) is **not** transient and is never retried.

**Retry policy** (`src/db/retry.ts`, `withDbRetry(fn, { budgetMs: 60000 })`):
- Exponential backoff with jitter: about 1 s, 2 s, 4 s, 8 s, then 15 s steps, stopping when the next wait would exceed a total budget of **60 s**.
- **What is retried.** Only work that cannot have committed anything:
  - opening/acquiring a connection, and the session-context batch in `withRlsContext` (before `fn` runs);
  - the session store `get`/`set`/`touch`/`destroy` and `publicReads` (idempotent);
  - `crm.usp_UpsertUserOnLogin` (idempotent MERGE).
- **What is not retried.** Once `fn` in `withRlsContext` has started, a transient error rolls back and is **not** replayed automatically (the commit outcome could be ambiguous, and a form POST must not be applied twice). The user sees the "waking up" page and resubmits.
- One shared "database waking" flag per instance. While it is set, further requests wait on the same wake-up attempt instead of each starting their own retry loop.
- Each retry logs at `warn` (error number, attempt, elapsed ms). Exhausting the budget logs at `error`. App Insights gets a custom event `db.resume` with the total wake-up time, so the cold-start cost is measurable.

**User-facing behaviour:**
- The request is held while retrying (the browser shows its normal loading state). Most resumes complete inside the budget, and the user just sees a slow first page.
- If the budget is exhausted, or a transient error occurs after `fn` started, the error handler returns **503** with `Retry-After: 15` and the friendly page **"Waking up the database"**: "The CRM has been idle and its database is starting. This usually takes under a minute. [Try again]". The "Try again" link repeats the same GET. For a failed POST the page says "Your changes were not saved. Go back and submit again." No automatic page refresh (WCAG 2.2.1). Final wording and layout belong to the UI spec.
- The sign-in page and `/healthz` never wait on the database. `publicReads` (app name, logo) is cached in memory for 5 minutes and falls back to defaults on a transient error, so sign-in still renders while the database wakes.

**Keeping auto-pause effective** (anything that touches the database resets the 60-minute timer):
- App Service health check and Always On use `/healthz`, which has no DB call. Nothing may poll `/readyz` on a schedule; use it only for deployment slot checks.
- The session cleanup (ADR-0004) runs opportunistically on request traffic, not on a timer.
- Pools keep no idle connections (`min: 0`, §8).
- No App Insights availability test may hit a page that touches the database.

**Trade-off (accepted by the user 2026-11-18):** lower cost (compute is billed only while the database runs; storage always) in exchange for a cold start of up to about 60 s on the first request after an idle hour, typically the first user each morning. To switch off later: set `autoPauseDelay = -1` in Bicep (one-line change, no app change; the retry logic stays as harmless resilience). The README documents both.

## 9. Routes and role requirements

"Any" means any signed-in user with a CRM role. Every POST requires a valid CSRF token. Row-level access is always enforced by RLS on top of the route check.

| Method | Path | Role | Notes |
|---|---|---|---|
| GET | `/healthz` | public | liveness, no DB, body `ok` |
| GET | `/readyz` | public | `SELECT 1` on systemPool; body `ok`/`fail` only |
| GET | `/signin`, `/signed-out`, `/no-access` | public | static pages; branding via `publicReads` |
| GET | `/branding/logo` | public | see §10 headers |
| GET | `/auth/login` | public | `?returnTo=` (relative only) |
| GET | `/auth/redirect` | public | Entra callback (`response_mode=query`) |
| POST | `/auth/signout` | Any | destroys session, 302 to Entra logout, then `/signed-out` |
| GET | `/` | Any | Dashboard |
| GET | `/analytics` | Any | `?period=&from=&to=&owner=` |
| GET | `/search` | Any | `?q=` grouped top 5 |
| GET | `/accounts`, `/accounts/new`, `/accounts/:id`, `/accounts/:id/edit`, `/accounts/:id/delete` | Any | list / form / detail / edit / confirm |
| POST | `/accounts`, `/accounts/:id`, `/accounts/:id/delete` | Any | create / update / delete (RLS decides) |
| GET/POST | `/contacts…` | Any | same pattern as accounts |
| GET/POST | `/opportunities…` | Any | same pattern, plus `POST /opportunities/:id/won` and `/lost` |
| GET/POST | `/activities…` | Any | same pattern, plus `POST /activities/:id/done` and `/undone` |
| GET/POST | `/settings/general` | Admin | name, accent, logo upload (multipart, max 500 KB) |
| GET/POST | `/settings/locale` | Admin | base and enabled currencies, locale, timezone, week, fiscal month |
| GET/POST | `/settings/fx`, `POST /settings/fx/:id/delete` | Admin | ADR-0005 |
| GET/POST | `/settings/stages` | Admin | whole-table save in one transaction |
| GET/POST | `/settings/picklists/:code` | Admin | add / rename / reorder / deactivate |
| GET/POST | `/settings/visibility` | Admin | `private` or `team`, with confirmation |
| GET | `/settings/users` | Admin | users (read-only, from Entra) and teams, with the "Add team" form |
| POST | `/settings/teams`, `/settings/teams/:id` | Admin | "Add team" / edit team: name (1–80 chars) + Entra group object ID (validated as a GUID, unique). Audited (`EntityType = 'team'`). Approved 2026-11-18 (ADR-0003) |
| (test only) | `/__test/login` | n/a | mounted only when `NODE_ENV=test` and `WEBSITE_SITE_NAME` is unset; the process refuses to start otherwise if `AUTH_TEST_BYPASS=1` |

`:id` is validated as a positive int by zod. An invalid value returns 404, not 400.

**Ownership hand-off.** If a create or update leaves a record the user can no longer see, redirect to the list with "Saved and assigned to {name}." This happens, for example, when a User assigns a record to a team-mate in `private` mode.

**Owner picker.** For non-Admins it lists only the caller and active members of the caller's team (the same rule `fn_RowWriteTarget` enforces, §5.2). The route validates the chosen owner against that list before writing; RLS is the backstop (33504 → 403).

**Delete button.** Shown only when the caller is Admin, the owner, or a Manager of the record's team. In `team` mode a User editing a team-mate's record sees Edit but not Delete.

**Entity references.** Before insert or update, verify that referenced entities (`AccountId`, `ContactId`, `OpportunityId`) are visible under RLS. A hidden or unknown ID gives "Choose an account from the list." Foreign keys are checked without RLS, so this check stops users probing IDs.

## 10. Cross-cutting security
- **helmet:**
  - CSP: `default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; object-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'self' https://login.microsoftonline.com`.
  - No inline script. Chart data goes in `<script type="application/json">` blocks read by `charts.js`.
  - HSTS.
  - `Referrer-Policy: same-origin`.
- **Session cookie:**
  - `__Host-crm.sid`, `Secure`, `HttpOnly`, `SameSite=Lax`, `Path=/`.
  - Idle timeout 60 min (rolling). Absolute timeout 10 h (`authAt` check).
  - `app.set('trust proxy', 1)` for App Service.
- **CSRF:** synchronizer token in the session, hidden `_csrf` field, constant-time compare. It is rotated on login.
- **Logo:**
  - Uploads: PNG validated by magic bytes. SVG is rejected if it contains `<script`, `on*=` attributes, `javascript:`, `<foreignObject` or external `href`.
  - Served with `X-Content-Type-Options: nosniff`, `Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; sandbox` and `Cache-Control: no-cache` + ETag.
  - Only ever rendered via `<img>`.
- **Logging (pino to stdout and App Insights):**
  - Request id (UUID), userId, route, status and duration.
  - Never tokens, cookies, form bodies, email or names. Log `oid`, not the email address.
- **Validation:** all input is validated with zod at route level. Amounts are parsed with the locale, then checked as `decimal(19,2)` with a value of at least 0.

## 11. Configuration (environment variables, validated in `src/config.ts`)

| Variable | Required | Example / default | Notes |
|---|---|---|---|
| `NODE_ENV` | yes | `production` | `development` / `test` / `production` |
| `PORT` | no | `8080` | App Service sets it. Under iisnode it is a **named pipe** path (e.g. `\\.\pipe\…`), not a number: pass `process.env.PORT` to `listen()` unchanged and validate it as "integer or non-empty string" |
| `BASE_URL` | yes | `https://crm-prod.azurewebsites.net` | builds the redirect URI and post-logout URI |
| `ENTRA_TENANT_ID` | yes | GUID | single tenant; also checked against the `tid` claim |
| `ENTRA_CLIENT_ID` | yes | GUID | |
| `ENTRA_CLIENT_SECRET` | yes | Key Vault reference | `@Microsoft.KeyVault(SecretUri=…)` |
| `SESSION_SECRET` | yes | Key Vault reference | at least 32 random bytes; supports a comma-separated list for rotation |
| `SESSION_IDLE_MINUTES` | no | `60` | |
| `SESSION_ABSOLUTE_HOURS` | no | `10` | |
| `SQL_SERVER` | yes | `sql-crm-prod.database.windows.net` | |
| `SQL_DATABASE` | yes | `sqldb-crm` | |
| `DB_AUTH_MODE` | yes | `managed-identity` | `managed-identity` → `azure-active-directory-msi-app-service` (Azure); `default-azure` → `azure-active-directory-default` (local dev); `sql-password` → SQL login (interim MonsterASP test host only, §12.3). Config validation rejects `sql-password` when `WEBSITE_SITE_NAME` indicates Azure App Service. |
| `DB_USER`, `DB_PASSWORD` | only for `sql-password` | host app settings | Set in the host's control panel / app settings, **never** in the repo or `.env.example` values. Required when `DB_AUTH_MODE=sql-password`, rejected otherwise. Never logged. |
| `SQL_POOL_MAX` | no | `10` | rlsPool size |
| `LOG_LEVEL` | no | `info` | |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | prod | set by infra | |
| `AUTH_TEST_BYPASS` | no | unset | honoured only if `NODE_ENV=test`; startup fails if set in any other mode or on App Service |

`.env` is used locally only, is git-ignored, and has a committed `.env.example` with placeholders.

## 12. Azure resources and Entra setup

### 12.1 Resources (one set per environment, EU region such as Sweden Central or West Europe)
| Resource | Configuration |
|---|---|
| Resource group | `rg-crm-<env>` |
| App Service plan | Linux, B1 (dev/test), P0v3 (prod) |
| Web App | Node 22 LTS, HTTPS only, minimum TLS 1.2, FTP and basic auth disabled, Always On (prod), health check `/healthz`, system-assigned managed identity, Key Vault references in app settings |
| Azure SQL logical server | **Entra-only authentication** (SQL auth disabled), Entra admin = group "CRM SQL Admins", minimum TLS 1.2, auditing to Log Analytics. **Public endpoint** with the firewall rule "Allow Azure services and resources to access this server" on, and no other firewall rules (user decision 2026-11-18). Entra-only auth means a reachable endpoint still requires a valid Entra token for a principal that is a database user. Private endpoint/VNet integration is a later hardening option. |
| Azure SQL Database | General Purpose serverless Gen5, 1–2 vCores, **auto-pause after 60 min in all environments, including prod** (user decision 2026-11-18; cold-start trade-off and retry design in §8.1), PITR 7–35 days. Avoid features that block auto-pause (e.g. geo-replication/failover groups); check LTR against current Microsoft guidance before enabling it. |
| Key Vault | RBAC mode. Web App managed identity has *Key Vault Secrets User*. Secrets: `entra-client-secret`, `session-secret` |
| Log Analytics + Application Insights | workspace-based; 30–90 day retention |

### 12.2 Entra app registration (one per environment)
1. In Entra admin centre, go to App registrations and create a new registration named `CRM (<env>)`. Choose **single tenant**.
2. Under Authentication, add the Web platform redirect URI `https://<host>/auth/redirect`. For dev, also add `http://localhost:3000/auth/redirect`. Set the front-channel logout URL to `https://<host>/signed-out`. Leave implicit and hybrid flows **off**.
3. Under Certificates & secrets, create a client secret (12-month expiry) and store it in Key Vault as `entra-client-secret`. Record the expiry date for rotation.
4. Under App roles, create:
   - `CRM.Admin`, display name "CRM Admin"
   - `CRM.Manager`, display name "CRM Manager"
   - `CRM.User`, display name "CRM User"

   For each role set allowed member types to Users/Groups and enable it.
5. Under Token configuration:
   - Add a groups claim: **Groups assigned to the application**, ID token, emit as Group ID.
   - Add optional claims `email` and `preferred_username` to the ID token.
6. Under API permissions: Microsoft Graph delegated `openid`, `profile` and `email`. Grant admin consent.
7. In Enterprise applications, open `CRM (<env>)` and go to Properties. Set **Assignment required = Yes**.
8. Under Users and groups, assign role groups such as `CRM-Admins` → CRM.Admin, `CRM-Managers` → CRM.Manager and `CRM-Users` → CRM.User. Also assign each **team group**, such as `CRM-Team-Nordics`, to the app, usually with CRM.User. The groups claim only contains groups assigned to the app.
9. In CRM, go to Settings > Users & teams and add each team with its Entra group object ID.

**Role mapping (unit-tested):**
- If `roles` contains `CRM.Admin`, the role is Admin. Otherwise `CRM.Manager` gives Manager, and `CRM.User` gives User.
- If none of these is present, show `/no-access`. No DB write and no session user.
- Unknown role values are ignored.

**Team (user decision 2026-11-18, ADR-0003):** a team is an Entra security group.
- Each team group is assigned to the enterprise app, so it appears in the `groups` claim, which is limited to **groups assigned to the application**.
- At login, `TeamId` is the intersection of the `groups` claim with active `crm.Teams.EntraGroupId` values (§4.2). **A user must be in exactly one team group.** More than one match: `TeamId` is NULL (fail closed: own records only), a warning is logged with the `oid`, and the Users & teams page shows the warning badge.
- With no match, `TeamId` is NULL (own records only).
- **Overage** (`_claim_names` contains `groups`, or `hasgroups` is true): the app does **not** call Graph. `TeamId` is NULL (fail closed) and an error is logged with the `oid`, so an Admin can fix the group assignment.
- Admins create teams in Settings > Users & teams > "Add team" (name + group object ID). Membership changes in Entra take effect at the user's next sign-in.

### 12.3 Interim test hosting on MonsterASP.NET (until Azure go-live)

User decision (2026-11-18): until the Azure environment is live, the **test** environment runs on MonsterASP.NET. **Test data only**: no customer or personal production data, ever.

| Aspect | Interim host | Design consequence |
|---|---|---|
| Web server | Windows / IIS with **iisnode** | `web.config` routes to the built `dist/server.js`. The app listens on `process.env.PORT` exactly as given, which under iisnode is a named pipe (§11). `trust proxy` stays at 1 (IIS is the proxy). HTTPS is enforced at the host; the `__Host-` cookie still needs `Secure`, so the test site must be served over HTTPS. |
| Database | Shared hosting MSSQL (SQL Server, RLS and `SESSION_CONTEXT` supported) | Same migrations and RLS. **Not serverless**: the 40613 retry and "waking up" logic (§8.1) simply never fires there, which is harmless. |
| DB auth | SQL logins, no Entra/managed identity | `DB_AUTH_MODE=sql-password`, `DB_USER`/`DB_PASSWORD` from the host's app settings, never in the repo. |
| Secrets | No Key Vault | `ENTRA_CLIENT_SECRET` and `SESSION_SECRET` set as host app settings. Use a separate "CRM (test-interim)" Entra app registration whose redirect URI is the MonsterASP URL; delete it at Azure go-live. |
| Telemetry | No App Insights required | `APPLICATIONINSIGHTS_CONNECTION_STRING` optional; pino logs go to the iisnode log files. |

**Database principals on the interim host:**
- **Migrations** run as the database **owner login** supplied by the host. This replaces the Entra deployer identity of §6.
- **The app should run as a separate low-privilege SQL user**, created by migration `003` in place of `CREATE USER … FROM EXTERNAL PROVIDER`: `CREATE USER crm_app_test WITH PASSWORD = …` (contained user) or a host-provided second login mapped to a user, added to `crm_app_role`. It then gets exactly the grants of §6, and RLS is fully meaningful.
- **If the host does not allow a second login/user:** the app connects as the owner. This is a **documented test-only gap**: the RLS predicates still filter the owner's queries, but the owner *can* `ALTER SECURITY POLICY … STATE = OFF`, drop predicates or read `dbo` tables. So a SQL injection or compromised app could disable RLS. Acceptable only because the host holds test data only. The RLS integration script (§5.5) must still be run as a `WITHOUT LOGIN` user in `crm_app_role` (`EXECUTE AS USER`), so it remains a valid proof. This gap must not carry over to Azure, where `DB_AUTH_MODE=managed-identity` and `crm_app` are mandatory.

## 13. Deviations from the approved plan

| # | Plan | This design | Reason |
|---|---|---|---|
| 1 | "The pool's `sp_reset_connection` clears the context on release" | An explicit `ResettingConnectionPool.release()` calls tedious `Connection.reset()`, and the context batch starts with a stale-context guard | `mssql` 12.x does not reset connections on release (checked in the source). Without this, read-only keys from one user's request would persist on the pooled connection. |
| 2 | One predicate `sec.fn_RowAccess` for filter and block | Four predicates: read (`fn_RowAccess`, FILTER), update-existing (`fn_RowUpdate`, BEFORE UPDATE), delete-existing (`fn_RowDelete`, BEFORE DELETE) and resulting-row (`fn_RowWriteTarget`, AFTER INSERT/UPDATE) | User decision 2026-11-18: in `team` mode Users may read and update team-mates' records but not delete them ("delete is allowed for owner, Manager, Admin"), and nobody but Admin may move records across teams or assign them outside the team. |
| 3 | Session table `app.Sessions` via a library store | Custom store (about 100 lines) on `systemPool` | ADR-0004: `connect-mssql-v2` pins `mssql` ^11 (a second driver copy) and recommends `db_ddladmin`. |
| 4 | Team source not specified | Teams map to Entra security groups (`crm.Teams.EntraGroupId`), resolved at login, plus a small Admin "Add team" form | Keeps "membership is managed in Entra ID" (UI spec 3.8). Approved by the user 2026-11-18. |
| 5 | Settings/FX "writable by Admin" | Config tables have an `int` surrogate key so one `fn_AdminWrite(@RowId int)` binds to every table. Settings and BrandingAssets have no FILTER. | Settings is read by the predicate and by the public sign-in page. |
| 6 | Audit fields: Amount, Stage, Currency, Owner | Adds CloseDate, plus create/delete rows and config changes | Win rate and the period KPIs depend on CloseDate. Deletes must be audited (UX decision). |
