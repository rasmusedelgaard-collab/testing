# Internal CRM: UI Specification (v0.3)

> **Superseded in part (19.11.2026):** the visual and layout sections of this spec (1 Navigation and layout, the ASCII layouts and visual details in 2 and 3, and 5 Design tokens) are replaced by `docs/ux/ui-redesign.md` v1.0 (DAFA design system). All rules, fields, validation messages and copy below stay binding. The stack line below is historical: the app is now ASP.NET Core Razor Pages (ADR-0007).

Status: v0.3, product owner decisions applied (see section 9; v0.3 adds Entra-group teams, Team-mode edit rights and the database wake-up state) · Owner: UX · Stack: Express + EJS, plain CSS, Chart.js, minimal JS (progressive enhancement: every page works with JS off except charts, which fall back to their data tables).

## 0. Users, assumptions, goals
| Role | Main tasks | Sees |
|---|---|---|
| User (sales/finance) | Log activities, update own opportunities, find accounts/contacts | Own records (Visibility = Private) or team records (Visibility = Team). In Team mode a User can view and edit team-mates' records, but can't delete them or move them to another team. A User with no team sees only their own records. |
| Manager | Same as User, plus review team pipeline and analytics | Always their team's records, whatever the Visibility setting |
| Admin | Same as Manager, plus configure settings | All records + Settings |

Delete rights (confirmed): the record owner, Managers and Admins can delete a record. Every delete is written to the audit trail.
Teams (confirmed): a team is a Microsoft Entra security group that an Admin links in Settings > Users & teams. Membership comes only from Entra; the app never edits it.

Assumptions to check with real users: (A1) desktop is the main device, with occasional phone use for logging activities; (A2) one currency per opportunity, with totals converted to the base currency using the monthly FX rates Admins enter in Settings; (A3) users want "my work" first on the dashboard; (A4) roles and team membership come only from Entra and cannot be edited in the app; (A5) team membership is refreshed at each sign-in (to confirm with the architect); (A6) the database pauses when idle often enough that the wake-up state is seen mainly first thing in the morning.
Success metrics: logging an activity takes 30 s or less; finding a record takes 3 clicks or fewer; no WCAG 2.2 AA failures.

## 1. Navigation and layout
```
+--------------------------------------------------------------------------+
| [Skip to main content]                                                   |
| [Logo] CRM name      [ Search all records...    ] [+ New v]  (JD) v     |  top bar 56px
+--------------+-----------------------------------------------------------+
| Dashboard    |  Breadcrumb: Accounts / Acme A/S                          |
| Accounts     |  <h1>Page title</h1>              [Primary action]        |
| Contacts     |  [flash message region, role=status / role=alert]         |
| Opportunities|                                                           |
| Activities   |  main content (max-width 1280px)                          |
| Analytics    |                                                           |
| ------------ |                                                           |
| Settings*    |                                                           |
+--------------+-----------------------------------------------------------+
 left nav 224px          * Admin only (hidden for other roles, not greyed out)
```
- Left nav is a `<nav aria-label="Main">` list. The current item has `aria-current="page"`, a 4px left bar and a highlighted background.
- "+ New" menu: Account, Contact, Opportunity, Activity. Uses `<details>/<summary>` so it works without JS.
- User menu (initials avatar): name, email, role badge (from Entra), "Sign out".
- Global search goes to `/search?q=` and shows results grouped by entity (top 5 each, plus "See all").
- Responsive: at 1024px and above the nav is fixed. From 640–1023px the nav collapses to icons with labels kept as tooltips plus `aria-label`. Below 640px the nav sits behind a "Menu" button (a drawer that traps focus and closes on Esc), lists become stacked cards and form fields go to one column.
- Page `<title>`: "{Page} · {Record name} – {CRM name}".

## 2. Shared patterns
**List page**
```
<h1>Opportunities (128)</h1>                               [New opportunity]
[Search name, account...] [Stage v] [Owner v] [Close date: from-to] [Apply] [Clear filters]
+------------------------------------------------------------------------+
| Name ^v       | Account   | Stage     | Amount     | Close date | Owner|
| Renewal 2027  | Acme A/S  | Proposal  | DKK 120.000| 15.12.2026 | J. Do|
+------------------------------------------------------------------------+
Showing 1–25 of 128      [< Previous] 1 2 3 ... 6 [Next >]   Rows: [25 v]
```
- Filters are a GET form, so the state lives in the URL and can be shared and bookmarked. Sortable columns are header links with `aria-sort`.
- The first column links to the detail page. Numbers are right-aligned with tabular figures.
- Default page size is 25 (options 25/50/100).
- Below 640px each row becomes a card: name (link), plus 2–3 key fields.

**Detail page**
```
Accounts / Acme A/S
<h1>Acme A/S</h1>   Owner: Jane Doe        [Edit] [More v: Delete]
+-- Details (dl, 2 columns) ----------+ +-- Activities ------------------+
| Industry   Manufacturing            | | [+ Log activity]               |
| Website    acme.dk                  | | o Call · 19.11.2026 · Due      |
| ...                                 | | v Meeting · 12.11.2026 · Done  |
+-------------------------------------+ +--------------------------------+
Tabs (links, not JS tabs): Contacts (4) | Opportunities (2) | Activities (9)
Created 01.10.2026 by Jane Doe · Last modified by Anna Smith, 18.11.2026 14:02
```
- **Last modified line (every record detail page):** "Last modified by {name}, {datetime}". Name is the user's display name from Entra (falls back to their email if the name is missing; a user who has left still shows by name). Date-time follows section 6. On a record never edited, it shows the creator and creation time. It sits in a `<footer>` of the record, as plain text in `--color-text-muted`, and is not a live region.
- **Edit rights:** Edit is shown to anyone who can see the record (owner, team-mates in Team mode, Managers, Admins). Delete follows the delete rule below.
**Form page** (create and edit share one template)
- One column, max width 640px. Labels sit above fields. Required fields are marked "(required)" in the label text, not with an asterisk alone.
- Buttons: [Save] as primary, then [Cancel] as a secondary link that returns to the previous page.
- On a validation error the server re-renders the form. An error summary sits at the top (`role="alert"`, receives focus, title "Fix 2 problems to save") and lists links to each field. Each field shows an inline error linked with `aria-describedby`, and the field gets `aria-invalid="true"`. User input is kept.
- On success the user goes to the detail page and sees the flash "Opportunity saved." (`role="status"`).
- Delete is shown only to the record owner, Managers and Admins. For a User viewing a team-mate's record, the "More" menu leaves the Delete item out (hidden, not greyed out); if "More" would then be empty, the "More" button is not shown. The server refuses the request with the 403 page. Each delete is written to the audit trail (who, when, which record, a snapshot of its values).
- Delete goes to a confirmation page or a `<dialog>`: "Delete Acme A/S? This also removes 4 contacts' link to this account. This cannot be undone." Buttons: [Delete account] in danger style, and [Cancel].
- Unsaved-change warning (with JS only): "You have unsaved changes. Leave anyway?"
- **Owner field (all entities):** a `<select>` that defaults to the signed-in user on create.
  - For Users, the options are only **active members of their own team** (including themselves), sorted by name. A User with no team sees only themselves. This is how Users are kept from moving a record to another team.
  - Managers: active members of their team. Admins: every active user, with the team in brackets, e.g. "Anna Smith (Sales North)".
  - If the current owner is no longer an option (inactive, or now in another team), they stay as the selected option labelled "Anna Smith (inactive)" or "Anna Smith (other team)", so saving other fields never forces a change of owner.
  - Help text under the field for Users: "You can choose people in your team."
  - Server check (the list can be tampered with): "Choose an owner from your team." 

## 3. Pages

### 3.1 Sign-in / sign-out
```
        [Logo]  CRM name
        Sign in with your work account.
        [ Sign in with Microsoft ]
```
- Unauthenticated users go to `/signin` and then back to the page they first asked for.
- Sign-out ends the session, then shows "You are signed out." with [Sign in again].
- SSO error copy: "We couldn't sign you in. Try again, or contact your administrator if this continues." plus a reference ID.
- Valid Entra account but no CRM role: "You don't have access to the CRM yet. Ask your administrator to add you to a CRM group in Microsoft Entra ID."

### 3.2 Dashboard
```
<h1>Good morning, Jane</h1>                         Data as of 18.11.2026 09:14
+-----------+-----------+-----------+-----------+-----------+-----------+
|Open pipe- |Weighted   |Won this   |Won this   |Win rate   |Activities |
|line       |pipeline   |month      |quarter    |(last 90 d)|3 due today|
|DKK 2,4 m  |DKK 1,1 m  |DKK 320 k  |DKK 910 k  |34 %       |2 overdue  |
|42 opps    |           |5 deals    |14 deals   |14 of 41   |           |
+-----------+-----------+-----------+-----------+-----------+-----------+
+-- My open opportunities ------------+ +-- Upcoming activities ---------+
| table: Name|Account|Stage|Amount|   | | Overdue (2)  [Mark done]       |
|        Close date (sorted asc)      | | Today (3)                      |
| [View all my opportunities]         | | Next 7 days (5)  [View all]    |
+-------------------------------------+ +--------------------------------+
```
- Each tile is a link to the matching filtered list. Big number, label, then context line. Amounts are compact and in the base currency, with the full value in the accessible name and a `title` attribute (e.g. "Open pipeline: DKK 2.412.500").
- The overdue count uses danger colour plus the word "overdue", never colour alone.
- Grid: 6 columns at 1280px and up, 3 at 640px and up, 2 below that. Lists show at most 10 rows.
- Definitions (shown as a help tooltip and in the docs):
  - Weighted = sum of amount × stage probability.
  - Win rate = won ÷ (won + lost), by close date in the last 90 days (confirmed). The tile label always says "(last 90 d)".
  - Activities due = open Calls, Meetings and Tasks due today or overdue. Notes are excluded (confirmed). The tile's link opens the Activities list filtered to these types.
  - Month and quarter are calendar periods in the configured timezone.
  - Money totals (Open pipeline, Weighted pipeline, Won this month/quarter) convert to the base currency using the FX rules in section 6.
- Missing FX rate note: if any record in a tile used a fallback rate, the tile shows a context line in warning style, "Uses latest rate for 1 currency", with an icon and text (never colour alone). If no rate exists at all for a currency, those records are left out of the total and the line reads "3 opportunities in SEK not included: no FX rate". Admins also see the link [Add FX rates].
- Empty state: "No open opportunities yet. [New opportunity]" / "Nothing due. You're all caught up."

### 3.3 Analytics
```
<h1>Analytics</h1>
Period: [This quarter v] (This month, Last month, This quarter, Last quarter, This year, Last 12 months, Custom)
        From [dd.mm.yyyy] To [dd.mm.yyyy]  Owner/team [All I can see v]  [Apply]
+-- Pipeline by stage (bar) ---------+ +-- Won revenue by month (line) --+
| chart                              | | chart                           |
| [Show as table]                    | | [Show as table]                 |
+------------------------------------+ +---------------------------------+
+-- Win rate by owner (h. bar) ------+ +-- Activities by type (bar) -----+
+-- Opportunities by lead source (h. bar; no pie charts) ---------------+
```
- Each chart is a `<figure>` with a `<figcaption>` holding the title and a one-line key takeaway (e.g. "Proposal holds the most value: DKK 900 k"). The canvas has `role="img"` and an `aria-label` summary.
- The data `<table>` is in the HTML from the server. It is visible by default with JS off. With JS on it collapses behind a "Show as table" `<details>`.
- Charts use 1 or 2 colours from the chart palette with direct labels or legend plus patterns. Axes start at zero. Currency is in the base currency, converted with the FX rules in section 6. If a fallback or missing rate affects a chart, a note appears under the figcaption (same copy as the dashboard) and the data table marks affected rows with "Latest rate used" or "Not included: no FX rate".
- Custom range validation: "End date must be on or after start date." / "Choose a range of 5 years or less."
- Empty: "No data for this period. Try a longer date range."

### 3.4 Accounts
- List columns: Name, Industry, City, Owner, Open pipeline. Filters: Owner, Industry.
- Detail tabs: Contacts, Opportunities, Activities.

| Field | Type | Rules / error message |
|---|---|---|
| Name (required) | text, max 120 | "Enter an account name." / "Name must be 120 characters or fewer." / duplicate warning (not blocking): "An account named Acme A/S already exists. [View it]" |
| Website | url | "Enter a web address like example.com." |
| Phone | tel | "Enter a valid phone number." |
| Industry | select (picklist) | none |
| Address (street, postcode, city, country) | text, select | none |
| Owner (required) | select, defaults to me; options per Owner field rule (section 2) | "Choose an owner." / "Choose an owner from your team." |
| Notes | textarea, max 2000 | none |

### 3.5 Contacts
- List columns: Name, Account, Title, Email, Phone, Owner. Filters: Account, Owner.

| Field | Type | Rules / error message |
|---|---|---|
| First name, Last name (last name required) | text, max 80 | "Enter a last name." |
| Account | searchable select (`<input list>` datalist, server-validated) | "Choose an account from the list." |
| Job title | text | none |
| Email | email | "Enter an email address like name@example.com." |
| Phone, Mobile | tel | "Enter a valid phone number." |
| Owner (required) | select, defaults to me; options per Owner field rule (section 2) | "Choose an owner." / "Choose an owner from your team." |

### 3.6 Opportunities
- List columns: Name, Account, Stage, Amount, Close date, Owner.
- Filters: Stage, Owner, Close date range, and status Open/Won/Lost (default Open).
- Overdue close dates (open opportunity, close date in the past) are marked "Past close date" in text.
- Detail header shows a stage progress bar (`<ol>` with the current step marked `aria-current="step"`), Amount, Weighted amount and Close date.

| Field | Type | Rules / error message |
|---|---|---|
| Name (required) | text, max 120 | "Enter an opportunity name." |
| Account (required) | searchable select | "Choose an account." |
| Primary contact | select (filtered to account) | none |
| Stage (required) | select (Settings order) | none |
| Probability | read-only, from stage | none |
| Amount (required) | text `inputmode="decimal"`, parsed with locale | "Enter an amount, for example 12.500,00." / "Amount can't be negative." |
| Currency (required) | select, defaults to base | none |
| Close date (required) | date | "Enter a close date." / "Enter a real date, for example 31.12.2026." |
| Lead source | select (picklist) | none |
| Lost reason | select; required if stage is Lost | "Choose why this was lost." |
| Owner (required) | select, defaults to me; options per Owner field rule (section 2) | "Choose an owner from your team." |

- Inline stage change from the detail page: [Mark as won] and [Mark as lost].
- Moving to Won/Lost asks for confirmation and, if the close date is in the future, sets close date to today ("Close date set to today").

### 3.7 Activities
- List columns: [checkbox Done], Type, Subject, Related to, Due date, Owner.
- Filters: Type, Status (Open/Done, default Open), Due (Overdue, Today, This week, Any), Owner.
- The Done checkbox is a tiny POST form, accessible name "Mark 'Call Acme' as done". Flash: "Marked as done. [Undo]".

| Field | Type | Rules / error message |
|---|---|---|
| Type (required) | radio group: Call, Meeting, Task, Note | none |
| Subject (required) | text, max 160 | "Enter a subject." |
| Related to | account / contact / opportunity (pre-filled when started from a record) | "Link this activity to an account, contact or opportunity." (at least one required) |
| Due date | date (+ optional time); hidden for Note | "Enter a real date, for example 31.12.2026." |
| Done | checkbox | none |
| Description | textarea, max 4000 | none |
| Owner | select, defaults to me; options per Owner field rule (section 2) | "Choose an owner from your team." |

### 3.8 Settings (Admin only)
Sub-nav: General · Currency & locale · FX rates · Pipeline stages · Picklists · Visibility · Users & teams. Each section saves on its own and shows the flash "Settings saved."
- **General:** CRM name (required, max 40), logo upload (PNG/SVG, max 500 KB, with preview; "Logo must be PNG or SVG, 500 KB or smaller."), accent colour picker. The accent colour is refused if its contrast against white is below 4.5:1: "This colour is too light for text and buttons. Choose a darker shade."
- **Currency & locale:** base currency, enabled currencies (multi-select), locale (e.g. da-DK, en-GB, en-US), timezone, first day of week, and fiscal year start month. A live preview line shows the result, e.g. "1.234.567,89 kr. · 18.11.2026".
- **FX rates:** Admin enters one rate per currency per month. A rate means "1 unit of this currency = X base currency".
  ```
  Base currency: DKK                      Filter: Currency [All v]  Year [2026 v]
  | Currency | Month    | Rate to DKK | Last changed            |              |
  | EUR      | 11.2026  | 7,4590      | 01.11.2026 by A. Admin  | Edit Delete  |
  | SEK      | 11.2026  | –  Missing  |                         | Add rate     |
  Add rate:  Currency [EUR v]  Month [2026-11]  Rate to DKK [ 7,4590 ]   [Save rate]
  ```
  - Fields: Currency (required, select of enabled currencies except the base), Month (required, `<input type="month">`), Rate to base currency (required, decimal, up to 6 decimals, locale-parsed).
  - Errors: "Choose a currency." / "Choose a month." / "Enter a rate, for example 7,4590." / "Rate must be more than 0." / "A rate for EUR in November 2026 already exists. [Edit it]".
  - The table lists a "Missing" row (text, not colour alone) for each enabled currency without a rate for the current month, with an [Add rate] action.
  - Changing or deleting a rate asks for confirmation: "This changes totals on the dashboard and in analytics for November 2026. Continue?" Every change is written to the audit trail.
  - Flash: "Rate saved." / "Rate deleted."
- **Pipeline stages:** a table where each row has a drag handle plus [Move up]/[Move down] buttons (keyboard alternative), Name, Probability %, Won flag and Lost flag.
  ```
  | Order | Stage name     | Probability | Type        | Opps |        |
  | [^][v]| Qualification  | [ 10 ] %    | ( ) Open    | 12   | Delete |
  | [^][v]| Closed won     | [100 ] %    | (o) Won     | 40   |        |
  [+ Add stage]                                              [Save stages]
  ```
  Rules:
  - Exactly one Won stage and one Lost stage: "You need exactly one Won stage and one Lost stage."
  - Probability must be 0–100: "Probability must be between 0 and 100."
  - Won stage is 100, Lost stage is 0.
  - Names must be unique: "Each stage needs a different name."
  - A stage in use can't be deleted until its opportunities move. Clicking Delete opens a dialog: "Move 12 opportunities to [stage v] before deleting."
- **Picklists:** choose a list (Industry, Lead source, Lost reason, Activity type label). Values can be added, renamed, reordered and deactivated. Deactivated values stay on old records but can't be picked for new ones. There is no hard delete.
- **Visibility:** a `fieldset` with legend "Who can see records" and two radio options:
  - "Private: users see only the records they own."
  - "Team: users see and edit all records owned by their team. Only the owner, Managers and Admins can delete."
  - A fixed help line below: "Managers always see their team's records. Admins see all records."
  - Switching opens a confirmation: "This changes what everyone can see immediately. Continue?"
- **Users & teams:** two sections on one page, Teams first.
  ```
  <h2>Teams</h2>
  Team membership is managed in Microsoft Entra ID; ask IT to add people to the group.
  | Team name    | Entra group object ID                | Members | Status   |                   |
  | Sales North  | 3f2b9c1e-7a4d-4e8b-9c0f-1a2b3c4d5e6f | 8       | Active   | Edit  Deactivate  |
  | Finance      | 9d8e7f6a-5b4c-4d3e-8f2a-0b1c2d3e4f5a | 0       | Inactive | Edit  Reactivate  |
  Add team:  Team name (required) [            ]  Entra group object ID (required) [            ]  [Add team]

  <h2>Users</h2>
  Roles and team membership are managed in Microsoft Entra ID.  [Open Entra admin centre]
  | Name        | Email           | Role  | Team                              | Last sign-in     | Status |
  | Jane Doe    | jane@example.dk | User  | Sales North                       | 18.11.2026 09:02 | Active |
  | Ole Hansen  | ole@example.dk  | User  | (!) No team — sees own records only | 17.11.2026 15:40 | Active |
  ```
  - **Teams table:** columns Team name, Entra group object ID (monospace, full value, selectable for copying), Members (count of users last seen in the group), Status (Active/Inactive as text), actions. Row actions have unique names ("Edit Sales North", "Deactivate Sales North"). Empty state: "No teams yet. Add a team to link an Entra group." Teams are never hard-deleted, so record history keeps its team.
  - **Add team form** (Admin only, a POST form; on error the page re-renders with the error summary and keeps input):
    | Field | Type | Rules / error message |
    |---|---|---|
    | Team name (required) | text, max 100, trimmed | "Enter a team name." / "Team name must be 100 characters or fewer." / "A team named Sales North already exists." |
    | Entra group object ID (required) | text, `spellcheck="false"`, `autocomplete="off"`, trimmed, case-insensitive, stored lower-case | "Enter the Entra group object ID." / "Enter the group object ID in this format: 00000000-0000-0000-0000-000000000000." / "This group is already linked to the team Sales North." |
    Help text under the ID field: "Find this in Microsoft Entra ID > Groups > the group > Object ID." GUID format = 8-4-4-4-12 hexadecimal characters with hyphens; braces and surrounding spaces are stripped before checking. Optional (only if the app has Microsoft Graph read access, architect to confirm): "We can't find this group in Microsoft Entra ID. Check the object ID." Flash: "Team Sales North added."
  - **Edit** opens the same two fields pre-filled (own page or `<dialog>`), with [Save team] and [Cancel]. If the group ID changes, confirm: "Changing the group changes who is in Sales North. People are moved at their next sign-in. Continue?" Flash: "Team Sales North saved."
  - **Deactivate** confirms: "Deactivate Sales North? Its 8 members will have no team and will see only their own records. Their records keep their owners." Buttons: [Deactivate team] (danger) and [Cancel]. Flash: "Team Sales North deactivated." Inactive teams show [Reactivate] (no confirmation; flash "Team Sales North reactivated."). Every add, edit, deactivate and reactivate is written to the audit trail.
  - **Users table** stays read-only: Name, Email, Role (from Entra), Team, Last sign-in, Status. No edit actions for users or roles. Filters: role, team (including "No team"). Search by name/email.
  - **No-team badge:** a user who is in no active linked group shows a warning badge in the Team column: icon plus the text "No team — sees own records only" (`--color-warning-text`, never colour alone). The icon is decorative (`aria-hidden="true"`); the text carries the meaning. Above the table, if any exist, a summary line links to the filter: "3 users have no team. [Show them]".

### 3.9 System states
| State | Title / copy | Actions |
|---|---|---|
| 403 Access denied | "You don't have access to this page." / "If you think you should, ask your administrator." | [Go to dashboard] |
| 404 Not found (also used for records hidden by RLS, so their existence isn't leaked) | "We can't find that page or record." / "It may have been deleted, or you may not have access." | [Go to dashboard] [Search] |
| 500 Error | "Something went wrong on our side." / "Try again in a moment. Reference: {id}" | [Try again] [Go to dashboard] |
| Session expired | "Your session has ended. Sign in again to continue." Form input can't be kept, so say so in the copy. | [Sign in] |
| Saving conflict (record changed by someone else) | "Someone else changed this record while you were editing. Review their changes, then save again." | Show the current values |
| Empty list (no records) | "No opportunities yet." / "Opportunities track deals from first contact to close." | [New opportunity] |
| Empty list (filters) | "No opportunities match these filters." | [Clear filters] |
| Waking up the database | "Waking up the database" / "The CRM pauses its database when no one has used it for a while. It's starting up again, which can take up to a minute. This page will continue by itself." | [Try again now] (see 3.10) |
| Slow load | Server-rendered, so no spinners are needed. Buttons show "Saving..." and are disabled on submit to stop double posting. | none |

### 3.10 Waking up the database (serverless Azure SQL resume)
Why: the database pauses when idle and can take up to about 60 s to resume. Without this state the first person in each morning sees a 500 error or a hanging page and assumes the CRM is broken.

```
        [Logo]  CRM name
        <h1>Waking up the database</h1>
        The CRM pauses its database when no one has used it for a while.
        It's starting up again, which can take up to a minute.
        This page will continue by itself.

        [=====>        ]  (indeterminate bar, decorative)
        Still waking up. About 20 seconds so far.      <- role="status"
        [Try again now]
```
- **When:** the server gets the "database resuming / not currently available" error on a request (or the connection attempt times out while resuming). It returns HTTP 503 with `Retry-After: 5` and this page instead of the 500 page. Sign-in uses it too, then continues to the page the user asked for.
- **Renders without the database:** no nav, no Settings values. Use the default CRM name and logo from app config, default accent, `lang="en"`. Page `<title>`: "Waking up the database – {CRM name}".
- **Auto-retry (JS on):** the page checks a lightweight health endpoint every 5 s. When it succeeds, it reloads the original URL (GET only) and focus lands on the new page's `h1` as normal. Total wait before giving up: 90 s.
- **No JS:** no auto-refresh (avoids `meta refresh` timing problems, WCAG 2.2.1). The copy line "This page will continue by itself." is replaced by "Wait a minute, then choose Try again." and [Try again now] reloads the original URL.
- **Form submissions (POST):** never replayed automatically (risk of double saves). The form re-renders with all input kept and a warning banner above the error summary position: "We couldn't save yet because the database is waking up. Your changes are still here. Try saving again in a minute." Focus moves to the banner (`role="alert"`). If the page can't render the form, show this interstitial with the copy "Your changes weren't saved. Go back to the form to try again." and [Go back].
- **Live region:** one `role="status"` (polite) element, present in the HTML from the start. Update its text only at 0, 20, 40 and 60 s ("Still waking up. About 20 seconds so far."), not every check, so screen readers aren't flooded. No visual or spoken per-second countdown.
- **Not spinner-only:** the heading and text always explain what is happening and how long. The indeterminate bar is `aria-hidden="true"` and stops animating under `prefers-reduced-motion` (a static bar is shown instead).
- **After 90 s (give up):** stop retrying, replace the status with an alert (`role="alert"`): "This is taking longer than usual." / "Try again, or contact your administrator if this continues. Reference: {id}". Actions: [Try again]. Focus moves to the alert text.
- **[Try again now]** is a real button/link at least 44 px high, works at any time, and restarts the check without resetting the 90 s budget.

## 4. UX copy rules
- Sentence case. Verbs on buttons: "Save", "New account", "Log activity", "Mark as done", "Delete account", "Apply", "Clear filters", "Sign out".
- Errors say what to do, not what went wrong in system terms. No "invalid", no codes except the reference ID.
- Confirmations name the object: "Account deleted." / "Acme A/S saved."
- Use the same word everywhere: "Opportunity" (never "deal" in UI, except the KPI context line), "Owner", "Close date".

## 5. Design tokens
All text pairs below were checked against WCAG 2.2: normal text needs 4.5:1, UI components and graphics need 3:1.
| Token | Value | Use | Contrast |
|---|---|---|---|
| `--color-text` | #1F2937 | body text | 14.7:1 on #FFF, 14.1:1 on bg |
| `--color-text-muted` | #4B5563 | secondary text, help | 7.6:1 on #FFF |
| `--color-bg` | #F9FAFB | page background | none |
| `--color-surface` | #FFFFFF | cards, tables, inputs | none |
| `--color-border` | #D1D5DB | decorative dividers only | none |
| `--color-border-input` | #6B7280 | input/checkbox borders | 4.8:1 (≥3:1 needed) |
| `--color-primary` | #1D4ED8 | links, primary button bg, focus | 6.7:1 (white on it 6.7:1) |
| `--color-primary-hover` | #1E40AF | hover/active | white 8.7:1 |
| `--color-primary-subtle` | #EFF6FF | selected row, info bg | #1E40AF text 8.0:1 |
| `--color-danger` | #B91C1C | errors, destructive | 6.5:1 on #FFF; 5.9:1 on #FEF2F2 |
| `--color-success` | #15803D | success text/icons | 5.0:1 on #FFF |
| `--color-success-text-on-subtle` | #166534 on #F0FDF4 | success flash | 6.8:1 |
| `--color-warning-text` | #92400E on #FFFBEB | warnings | 6.8:1 |
| `--color-nav-bg` | #111827 | left nav, top bar | none |
| `--color-nav-text` | #F9FAFB (muted #D1D5DB) | nav items | 17.0:1 / 12.0:1 |
| `--color-nav-active-bg` | #374151 | current nav item | white 10.3:1 |
| `--color-nav-focus` | #93C5FD | focus ring on dark nav | 9.8:1 |
| Chart palette | #1D4ED8, #B45309, #047857, #7E22CE, #BE185D, #0E7490 | series, in this order | all 5.0:1 or higher on white |

- **Focus:** `outline: 2px solid var(--color-primary); outline-offset: 2px` on every focusable element, on all backgrounds (#93C5FD on dark). Never remove it. Use `:focus-visible`.
- **Typography:** system stack `system-ui, -apple-system, "Segoe UI", Roboto, sans-serif`. Base 16px / 1.5. Scale: 12 (captions only, never essential info), 14 (table, help), 16 (body), 20 (h2), 24 (h1), 32 (KPI value). Weights 400/600. Numbers use `font-variant-numeric: tabular-nums`.
- **Spacing:** 4px base. Tokens `--space-1..8` = 4, 8, 12, 16, 24, 32, 48, 64. Radius 6px. Card padding 24. Form field gap 16. Section gap 32.
- **Components:** button (primary / secondary / danger / link), input, select, textarea, checkbox, radio, error summary, flash, badge (stage/status, always with text), table, pagination, card, KPI tile, figure+table, dialog, tabs (as links), breadcrumb.
- **Sizing:** every interactive target is at least 24×24 px (WCAG 2.5.8). Buttons and inputs are 40px high on desktop and 44px on touch widths.
- **States:** hover darkens. Disabled uses #F3F4F6 bg with #6B7280 text plus `disabled` (avoid disabled buttons where possible and explain instead). Loading shows "Saving..." text. Errors use a danger border, an icon and text.

## 6. Formatting (locale-driven from Settings)
Use `Intl.NumberFormat` and `Intl.DateTimeFormat` with the locale and timezone from Settings, all on the server. Never hand-format.
| Item | Rule | da-DK example | en-GB example |
|---|---|---|---|
| Money (tables, detail) | currency of the record, 2 decimals, currency code shown | DKK 12.500,00 | DKK 12,500.00 |
| Money (KPI/chart axis) | base currency, compact, 0–1 decimals; full value in tooltip/table | 2,4 mio. kr. | DKK 2.4M |
| Mixed currencies | totals convert to the base currency and are labelled "in DKK". Each record converts at the Admin-entered rate (Settings > FX rates) for its close month. If that month has no rate, use the latest earlier rate for that currency and show the note "Uses latest rate for 1 currency". If no rate exists at all, leave the record out and show "N opportunities in {currency} not included: no FX rate". Records in the base currency are never converted. | none | none |
| Percent | 0 decimals in tiles, 1 decimal in tables | 34 % | 34% |
| Date | short numeric date, locale order | 18.11.2026 | 18/11/2026 |
| Date-time | date + 24h/12h per locale, in the configured timezone | 18.11.2026 14:02 | 18/11/2026 14:02 |
| Relative | only as a secondary hint ("in 3 days", "2 days overdue"); the absolute date is always visible | none | none |
| Input parsing | amounts accept the locale decimal separator, and grouping separators are optional. Placeholders/examples match the locale. Native `<input type="date">` is used for dates. | none | none |
| Negative / empty | minus sign; empty value shows "–" with a visually hidden "Not set" | none | none |

## 7. Accessibility requirements (WCAG 2.2 AA)
1. **Structure:** landmarks (`header`, `nav`, `main`, `footer`), one `h1` per page, logical heading order, a skip link as the first focusable element, and `lang` set from the locale.
2. **Keyboard:** everything works with keyboard alone, in DOM order. No traps except the modal dialog (focus moves into it, Esc closes it, focus returns to the trigger). Stage reorder has Move up/down buttons, so it doesn't need drag (2.5.7). Menus use `<details>` or a button with `aria-expanded`.
3. **Focus:** visible as in section 5, and not hidden by a sticky top bar (2.4.11, via `scroll-padding-top`). After a form error, focus goes to the error summary. After a successful save, focus goes to the `h1` of the new page.
4. **Forms:** every input has a visible `<label for>`. Groups use `fieldset/legend` (activity type, visibility). Help and error text are linked with `aria-describedby`. `autocomplete` is set on email/tel. Required is shown in text and with `required`. Input is never cleared on error.
5. **Tables:** `<caption>`, `<th scope>`, and sortable headers with `aria-sort`. Row actions have unique accessible names ("Edit Acme A/S").
6. **Colour:** never the only carrier of meaning (stages, overdue, won/lost all carry text). Contrast as in section 5. Works at 200% zoom and 320px width (reflow).
7. **Charts:** each chart has a `figure`/`figcaption` and an `aria-label` summary on the canvas. A **data table fallback with the same data is always in the HTML**. Chart.js animations are turned off under `prefers-reduced-motion`. Tooltips are not the only way to read values.
8. **Live regions:** flash success uses `role="status"`, errors use `role="alert"`. Filter results announce "128 opportunities found" through `role="status"`. The database wake-up page announces progress through one polite `role="status"` at most every 20 s, and its give-up message through `role="alert"` (section 3.10).
9. **Touch targets:** 24×24 px minimum (2.5.8). Consistent help location (3.2.6). No re-entry of data already given in the same flow (3.3.7). Sign-in uses Microsoft SSO, with no cognitive test (3.3.8).
10. **Testing:** run axe on every page template with zero serious or critical issues. Do a keyboard-only walkthrough and an NVDA + Firefox or VoiceOver spot check of Dashboard, an Opportunity form and Analytics.

## 8. UI acceptance criteria (summary)
- Settings is not shown in the nav for non-Admins. `/settings*` returns the 403 page for them.
- Every list has search, filters, sorting and pagination, all reflected in the URL, plus both empty states.
- Every form shows the error summary and inline errors with the exact copy above, and keeps input.
- All numbers, money and dates follow section 6 and change when the Settings locale changes.
- Every chart has a server-rendered data table with the same values.
- Delete is shown and allowed only for the record owner, Managers and Admins, and every delete appears in the audit trail.
- The dashboard win rate uses the last 90 days by close date. Activities due excludes Notes.
- Dashboard and analytics money totals convert at the rate for each record's close month, fall back to the latest earlier rate, and show the missing-rate note whenever a fallback or exclusion happens.
- With Visibility = Private, a User sees only their own records. With Visibility = Team, a User sees their team's records. Managers always see their team's records in both modes.
- With Visibility = Team, a User can open and edit a team-mate's record, but sees no Delete item and gets 403 on a direct delete request. The User's Owner dropdown lists only active team-mates; posting another owner returns "Choose an owner from your team." Admin can choose any active user.
- Every record detail page shows "Last modified by {name}, {datetime}" and it updates after each save.
- Settings > Users & teams: Admin can add a team (name required, max 100; group object ID required, GUID format, unique) with the exact error copy in 3.8, edit it and deactivate/reactivate it; all actions are audited. Users and roles have no edit controls. Users in no active team show the badge "No team — sees own records only".
- When the database is resuming, users see the "Waking up the database" page (HTTP 503), not the 500 page. With JS it continues to the requested page by itself once the database is up; without JS [Try again now] works. POSTs are never replayed and form input is kept. After 90 s the give-up alert appears with a reference ID.
- axe shows zero serious or critical issues, and keyboard-only completion of "create opportunity" and "mark activity done" works.

## 9. Confirmed decisions (product owner)
1. **Win rate window:** last 90 days by close date, labelled on the tile.
2. **Delete rights:** the record owner, Managers and Admins can delete. Every delete is audited.
3. **Activities due:** includes Calls, Meetings and Tasks. Notes are excluded.
4. **FX conversion:** Admin enters monthly FX rates in Settings > FX rates. Totals convert at the rate for the record's close month, fall back to the latest available rate, and show a visible note when a rate is missing.
5. **Visibility:** Settings offers Private (users see their own records) or Team (users see their team's records). Managers always see their team.
6. **Teams (v0.3):** teams are Entra security groups linked by Admin in Settings > Users & teams (team name + group object ID; edit and deactivate per team). Users and roles stay read-only, managed in Entra. Users with no team see only their own records and are flagged with a badge.
7. **Team-mode rights (v0.3):** in Team mode, Users can view and edit team-mates' records but cannot delete them or move them to another team. Delete is hidden for non-owner Users. Owner dropdown: active team-mates for Users, anyone for Admin. Every record detail shows "Last modified by {name}, {datetime}".
8. **Database wake-up (v0.3):** serverless Azure SQL resume (up to ~60 s) shows a friendly "Waking up the database" state with auto-retry and an accessible live region, never a spinner alone.

Still to validate with real users: assumptions A1-A6 in section 0.

Open questions (recommendation in brackets):
- A user in two or more linked groups: which team applies? (Recommend: not supported; show the badge "In 2 teams — check Entra groups", treat as no team until fixed in Entra.)
- Can a Manager-role user's Owner choice span teams? (Spec assumes own team only, like Users.)
- Does the app get Microsoft Graph read access to check that a group ID exists? (Architect to confirm; the spec works without it.)
