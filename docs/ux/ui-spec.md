# Internal CRM: UI Specification (v0.2)

Status: v0.2, product owner decisions applied (see section 9) · Owner: UX · Stack: Express + EJS, plain CSS, Chart.js, minimal JS (progressive enhancement: every page works with JS off except charts, which fall back to their data tables).

## 0. Users, assumptions, goals
| Role | Main tasks | Sees |
|---|---|---|
| User (sales/finance) | Log activities, update own opportunities, find accounts/contacts | Own records (Visibility = Private) or team records (Visibility = Team) |
| Manager | Same as User, plus review team pipeline and analytics | Always their team's records, whatever the Visibility setting |
| Admin | Same as Manager, plus configure settings | All records + Settings |

Delete rights (confirmed): the record owner, Managers and Admins can delete a record. Every delete is written to the audit trail.

Assumptions to check with real users: (A1) desktop is the main device, with occasional phone use for logging activities; (A2) one currency per opportunity, with totals converted to the base currency using the monthly FX rates Admins enter in Settings; (A3) users want "my work" first on the dashboard; (A4) roles come only from Entra and cannot be edited in the app.
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
Created 01.10.2026 by J. Doe · Last changed 18.11.2026 14:02 by A. Smith
```
**Form page** (create and edit share one template)
- One column, max width 640px. Labels sit above fields. Required fields are marked "(required)" in the label text, not with an asterisk alone.
- Buttons: [Save] as primary, then [Cancel] as a secondary link that returns to the previous page.
- On a validation error the server re-renders the form. An error summary sits at the top (`role="alert"`, receives focus, title "Fix 2 problems to save") and lists links to each field. Each field shows an inline error linked with `aria-describedby`, and the field gets `aria-invalid="true"`. User input is kept.
- On success the user goes to the detail page and sees the flash "Opportunity saved." (`role="status"`).
- Delete is shown only to the record owner, Managers and Admins. For everyone else the "More" menu leaves the Delete item out (it is not greyed out), and the server refuses the request with the 403 page. Each delete is written to the audit trail (who, when, which record, a snapshot of its values).
- Delete goes to a confirmation page or a `<dialog>`: "Delete Acme A/S? This also removes 4 contacts' link to this account. This cannot be undone." Buttons: [Delete account] in danger style, and [Cancel].
- Unsaved-change warning (with JS only): "You have unsaved changes. Leave anyway?"

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
| Owner (required) | select users, defaults to me | "Choose an owner." |
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
| Owner (required) | select, defaults to me | "Choose an owner." |

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
| Owner (required) | select, defaults to me | none |

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
| Owner | select, defaults to me | none |

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
  - "Team: users see all records owned by their team."
  - A fixed help line below: "Managers always see their team's records. Admins see all records."
  - Switching opens a confirmation: "This changes what everyone can see immediately. Continue?"
- **Users & teams:** a read-only table with Name, Email, Role (from Entra), Team, Last sign-in, Status. The page shows the note "Roles and membership are managed in Microsoft Entra ID." and a link to the Entra admin portal. Filters: role, team. Search by name/email.

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
| Slow load | Server-rendered, so no spinners are needed. Buttons show "Saving..." and are disabled on submit to stop double posting. | none |

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
8. **Live regions:** flash success uses `role="status"`, errors use `role="alert"`. Filter results announce "128 opportunities found" through `role="status"`.
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
- axe shows zero serious or critical issues, and keyboard-only completion of "create opportunity" and "mark activity done" works.

## 9. Confirmed decisions (product owner)
1. **Win rate window:** last 90 days by close date, labelled on the tile.
2. **Delete rights:** the record owner, Managers and Admins can delete. Every delete is audited.
3. **Activities due:** includes Calls, Meetings and Tasks. Notes are excluded.
4. **FX conversion:** Admin enters monthly FX rates in Settings > FX rates. Totals convert at the rate for the record's close month, fall back to the latest available rate, and show a visible note when a rate is missing.
5. **Visibility:** Settings offers Private (users see their own records) or Team (users see their team's records). Managers always see their team.

Still to validate with real users: assumptions A1-A4 in section 0.
