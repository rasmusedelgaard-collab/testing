# UX review: increment 4 (Dashboard and Analytics) and re-test of increment 3 fixes

Reviewer: UX · Date: 18.11.2026 · Spec: `docs/ux/ui-spec.md` v0.3 (sections 3.2, 3.3, 5, 6, 7, 8) and ADR-0005 (FX).

Build reviewed:
- Requested base: `2a643d5`.
- While the review was running, the branch moved to `e884349`. That adds QA fixes: impossible dates are refused, the Overdue / Today / Next 7 days windows no longer overlap, and the connection info appears on Users & teams.
- Every finding below was re-checked on a fresh build of `e884349` (demo mode, `NODE_ENV=test`, port 3999). `src/` was not changed.

## Verdict: PASS WITH ISSUES

**Increment 3 re-test:** H1, H2, H3 and M1–M6 are all fixed. Two small leftovers are listed as Low.

**Increment 4:**
- No serious or critical axe results on Dashboard or Analytics for Admin, Manager or User, at 390, 800, 1024, 1280 and 1440 px. That includes the borderline accent colour.
- The structure follows the spec:
  - Each chart is a figure with a caption and a one-line takeaway.
  - Each chart canvas has `role="img"` and a summary label.
  - The data table is in the server HTML. It is visible with JS off and sits behind "Show as table" with JS on.
  - There is one colour per chart, no pie charts, and axes start at zero.
- **One High issue:** the dashboard scrolls sideways at 1024–1439 px for any user who has rows in both lists. It is a one-line CSS fix and is needed before release.
- **Five Medium issues** should be fixed in the same pass.

Accepted and not raised:
- the date inputs show the browser's own format (native `<input type="date">`, spec 6)
- the demo banner sits outside a landmark (increment-2 L2, axe "region", moderate)

## Method
- **Browser:** Playwright with Chromium headless, deviceScaleFactor 1.
  - Widths: 1280×900, 1024, 1440, 800×900, and 390×844 (mobile, touch).
  - Roles: Admin, Manager and User, plus Analytics with JavaScript turned off.
- **axe-core 4.14:** loaded from the scratchpad, not added to `package.json`. Tags: wcag2a/aa, wcag21a/aa, wcag22aa, best-practice.
- **States checked:**
  - Dashboard: no data, an FX exclusion, an FX fallback (an EUR rate was added for 10.2026 so November falls back), and an overdue activity with a long subject.
  - Analytics: this quarter, this year, last 12 months, last month, and empty periods.
  - Custom range errors: inverted, more than 5 years, empty, 31 February, and an unknown period.
  - Keyboard: tab order through the filter, and opening "Show as table" with Enter.
- **Increment-3 re-test:**
  - accent colours #757575 (must be refused) and #737373 (just accepted), then axe on 5 pages
  - stages invalid save
  - en-GB "6,8" / "1,234.5" and da-DK "7.4590" / "1.234,5" in FX rates and in Amount
  - focus after moving a stage or picklist value
  - removing an in-use stage
  - logo errors
  - editing an FX rate
  - add team, edit team, picklist add and rename
  - scroll widths at 390 and 800 px

## Part 1: Re-test of increment 3

| ID | Result | Evidence |
|---|---|---|
| H1 Accent contrast | **Fixed** | #757575 is refused with "This colour is too light for text and buttons. Choose a darker shade." The field gets `aria-invalid` and `aria-describedby="h-accentColor e-accentColor"`. #737373 (just above 4.5:1 on #F9FAFB) is accepted, and axe then shows no colour-contrast issue on Dashboard, Analytics, Opportunities, FX rates or Accounts. The help text names both backgrounds. |
| H2 Stages summary | **Fixed** | "Fix 4 problems to save". Row 1, Row 2 and Row 3 errors link to `#f-name0`, `#f-prob1` and `#f-name2`, followed by the Won/Lost rule. Each field with an error has `aria-invalid` and `aria-describedby`. |
| H3 Strict numbers | **Fixed** | en-GB "6,8" is refused with "Enter a rate, for example 7.4590.". da-DK "7.4590" is refused with "...for example 7,4590.". Amount follows the same rule ("for example 12,500.00" / "12.500,00"), and "1,234.5" / "1.234,5" are accepted as amounts. FX rates refuse any grouping separator, which is acceptable for rates. |
| M1 Inline error pattern | **Fixed** | Add team, Edit team, picklist add and rename, FX and logo all set `aria-invalid` and `aria-describedby`. Leftover: L13. |
| M2 Focus after reorder | **Fixed** | After "Move ... down", focus is on the moved row's button (`#down-2`). Same for picklists. |
| M3 In-use stage removal | **Fixed (minimum option)** | The action is refused at once and the row stays. Summary and inline: "Row 2: Can't remove "Needs analysis": 1 opportunity uses it. Move it to another stage first." There is a "View these opportunities" link. The bulk-move step is still a PO decision (increment-3 decision 2). |
| M4 Logo errors inline | **Fixed** | "Choose a logo file first." and "Logo must be a PNG, 500 KB or smaller." appear in the summary and inline, and `#f-logo` has `aria-invalid`. |
| M5 FX edit confirmation | **Fixed** | "Change rate: This changes totals on the dashboard and in analytics for 10.2026. Continue?" with [Save rate] / Cancel. The month is shown as "10.2026" rather than "October 2026" (increment-3 L3a, still open). |
| M6 Settings tables | **Fixed** | Every `td` on Users, Stages, FX and Picklists has a `data-label`. No horizontal scroll on `/settings/users` at 390 px or `/settings/stages` at 800 px. |

## Part 2: Increment 4, Dashboard and Analytics

### What works well
- **KPI tiles:**
  - Each tile is a single link to the matching filtered list: open, won this month or quarter by close date, closed in the last 90 days, and due activities of the actionable types.
  - The compact value is shown and the full value is in a visually hidden span and in `title`. Example name: "Open pipeline 120 t kr. (DKK 120.000,00) 3 opps".
  - The focus ring is 2 px #1D4ED8.
  - The label reads "Win rate (last 90 d)".
  - Overdue uses danger colour plus the words "1 overdue".
  - The grid is 6, then 3, then 2 columns as specified.
  - At 390 and 800 px there is no horizontal scroll and the overdue table becomes labelled cards.
- **FX notes:**
  - Exclusion and fallback notes appear per tile and per chart as icon plus text, in warning colours.
  - Admins get [Add FX rates]; Managers and Users get "Ask an administrator." (a good adaptation).
  - Analytics adds a page-level note.
- **Lists:**
  - The tables have hidden captions ("My open opportunities, earliest close date first").
  - The "Mark done" button includes the activity subject ("☐ Mark done: Call Acme").
  - The empty states use the spec copy.
  - "View all my opportunities (1)" shows the total.
- **Charts:**
  - Each chart is a `figure` with a `figcaption` holding a bold title and a takeaway, for example "Proposal holds the most value: 120 t kr."
  - The canvas `aria-label` repeats the chart type and the takeaway.
  - The data table is rendered on the server:
    - With JS off, all 5 are open and no canvas is shown.
    - With JS on, they collapse behind a native `<details>` "Show as table" that opens with Enter and shows the focus ring.
  - Empty charts show the spec copy "No data for this period. Try a longer date range." and keep the table open.
  - Colour, axes and motion:
    - The single series uses #1D4ED8 (6.7:1 on white).
    - Tick text is #666 (5.7:1).
    - Value axes start at zero.
    - Animation is off under `prefers-reduced-motion`.
    - There are no pie charts.
  - Table rows affected by FX get the badges "Not included: no FX rate" and "Latest rate used".
- **Period filter:**
  - It is a GET form, so the state lives in the URL.
  - The error copy matches the spec ("End date must be on or after start date.", "Choose a range of 5 years or less."), plus "Enter a real date, for example 31.12.2026." for empty or impossible dates.
  - Focus goes to `#f-from`, which has `aria-invalid` and `aria-describedby="e-range"`.
  - The message says what happens next: "Showing this quarter instead."
  - The rendered range is always stated ("Close dates from 01.10.2026 – 31.12.2026.").

### Issues

| ID | Severity | Screen | Issue | Expected | Recommendation |
|---|---|---|---|---|---|
| H1 | High | Dashboard, 1024–1439 px (the main desktop range) | When both lists have rows, the page scrolls sideways. Measured as User: scrollWidth 1303 at 1024 and 1280 px. The "Upcoming activities" column runs past the viewport, so its Owner column and part of the content are cut off. Cause: `.dash-grid { grid-template-columns: 1fr 1fr }`. `1fr` has an `auto` minimum, so the left table's min-content (559 px) pushes the right column out. The `.table-wrap` scroller never engages. The e2e "no horizontal scroll" check passes only because the Admin demo user has no rows. | Spec 1: no page-level horizontal scroll. Wide tables scroll inside their wrapper. | `.dash-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); } .dash-grid > section { min-width: 0; }` Add an e2e check as **User** at 1024 and 1280 px (scrollWidth equals clientWidth). Also see L5: dropping the Owner column removes most of the pressure. |
| M1 | Medium | Dashboard tiles at 1280 px (6 columns, about 155 px each) | `overflow-wrap: anywhere` on `.tile-value`, added for 390 px, breaks numbers inside words at 32 px: "120 t k / r.", "455,7 t / kr.", "0 due / today". The label also wraps to "Win rate (last 90 / d)". The key number on the home page is harder to read. | Spec 5: KPI value at 32 px, readable at a glance. | Remove `overflow-wrap: anywhere` and use `white-space: nowrap` on the value. Size it to fit: 24 px from 1280 to 1535 px (6 columns) and 32 px elsewhere, or `font-size: clamp(20px, 1.9vw, 32px)`. Keep the label on two lines if needed, but use a no-break space in "90 d" ("90&nbsp;d"). |
| M2 | Medium | Dashboard tiles with excluded deals | The FX note sits outside the tile link. A keyboard or screen-reader user tabbing through tiles hears "Open pipeline 120 t kr. (DKK 120.000,00) 3 opps". Nothing says that 2 of the 3 are not in the total. The next tab stop is "Add FX rates" with no context. The visible "3 opps" also counts the excluded records while the amount does not include them. | Spec 3.2: when a record is excluded or a fallback is used, the tile says so. A total must not look complete when it isn't (financial-data rule). | Give each note an id (`note-open-0`) and add `aria-describedby` on the tile link pointing to the notes. Make the "Add FX rates" link name explicit: "Add FX rates for EUR, SEK". |
| M3 | Medium | Analytics: "Activities by type" and "Opportunities by lead source" (count charts) | With small counts, Chart.js picks 0.1 steps and the 0-decimal formatter rounds them. The axis reads "0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1" and "0, 1, 1, 2, 2". The labels are wrong and misleading. | Spec 3.3: charts readable; axes start at zero and show true values. | For `valueKind: 'count'`, set the value-axis `ticks: { precision: 0 }` (integers only). For percent, set `max: 100`. Add an e2e check that tick labels on count charts are unique. |
| M4 | Medium | Analytics: period filter | From and To are always editable. When Period is not "Custom", they are silently ignored. For example, choose This month, change From to 01.01.2026, press Apply: the URL carries `from=2026-01-01`, but the page shows 01.11.2026 – 30.11.2026 with no message. The user believes they filtered. | Spec 3.3: Custom with From/To. Errors and outcomes are never silent. | Server: if `from` or `to` differ from the selected preset's range, treat the request as `custom`. This works without JS. With JS, switch the select to "Custom" when a date changes. Put "Custom range" in a `fieldset` with legend "Custom range" and help "Used when Period is Custom". Hiding the dates for presets is optional. |
| M5 | Medium | Analytics tables below 640 px | Chart data tables use `table.data`, so they become cards, but their `td`s have no `data-label`. Cards show bare values: "jan. 2026 / 0 / DKK 0,00" and "Negotiation / 1 / DKK 0,00 Not included". Without the column names, "1" and "0" make no sense. This is the same pattern as increment-3 M6, which is now fixed for settings. | Spec 1: cards show labelled fields. Spec 7.7: the table is the accessible alternative, so it must be readable. | Add `data-label="<%= ch.table.columns[i] %>"` to each chart `td` in `analytics.ejs`. Alternatively, keep chart tables as real tables (`.chart-table table.data` excluded from the card CSS) inside a scrolling `.table-wrap`; they have 2–4 columns and fit at 390 px. |
| L1 | Low | Dashboard and Analytics FX notes | The copy differs from the spec: "2 deals excluded (no FX rate for EUR, SEK)" and "Some deals are left out of the money charts...", "at each deal's FX rate", "Deals" column header. Spec 4 allows "deal" only in the KPI context line. | Spec 3.2 / 6: "2 opportunities in EUR, SEK not included: no FX rate". | Use the spec copy in `exclusionNote`. Analytics banner: "Some opportunities are not included in the money charts: no FX rate." Column header: "Won opportunities". |
| L2 | Low | Analytics tables with excluded rows | An excluded row shows "DKK 0,00" plus the badge "Not included: no FX rate". "0,00" reads as a real value. | Spec 6: no value shows "–" with hidden "Not set". | Show "–" with visually hidden "Not converted" in the amount cell when everything in the row is excluded, and keep the badge. |
| L3 | Low | Analytics empty charts | "Won revenue by month" with no wins shows a 12-row table of zeros. "Win rate by owner" shows headers only. Both add noise below the clear empty message. | Spec 3.3 empty state. | When `empty`, show only the empty message and no table, or collapse the `details`. |
| L4 | Low | Dashboard: Win rate tile with no closed deals | The name repeats itself: "Win rate (last 90 d) – (No closed deals yet) No closed deals yet". | Concise names. | When the value is null, leave out the hidden full value. The context line already says it. Show "–" with hidden "Not set". |
| L5 | Low | Dashboard: both lists | Both "My" tables include an Owner column that always shows the signed-in user. The spec's columns are Name, Account, Stage, Amount, Close date. Removing it also eases H1. | Spec 3.2 layout. | Drop Owner from both dashboard tables. For activities, keep Done, Subject, Related to and Due date; Type can stay. |
| L6 | Low | Dashboard: Upcoming activities | The "View all" links (one per group) all share one name. | Clear link purpose (2.4.4 / 2.4.9 good practice). | "View all overdue (2)", "View all due today (3)", "View all next 7 days (5)". Or keep "View all" with hidden text. |
| L7 | Low | Dashboard: Activities due tile | The big value is "0 due today" even when there is "1 overdue". The most urgent number is the smaller one. | Lead with what matters (dashboard principle). | When overdue > 0, show "1 overdue" as the value (danger) and "0 due today" as the context. Or keep the order as specified and ask users (A3). |
| L8 | Low | Analytics at 390 px | Rotated x-axis labels are cut at the canvas edge ("ification", "ntiation"). There are no value labels on bars, so values can only be read on hover or in the table. | Spec 3.3: direct labels or legend. | Use horizontal bars below 640 px (`indexAxis: 'y'`) or `ticks.maxRotation: 0` with `autoSkip`. Optionally draw value labels at the bar ends. A small inline plugin is enough; no new dependency is needed. |
| L9 | Low | Analytics takeaways | Ties and small samples mislead. "Call is the most common type: 1 of 3" when Call, Meeting and Task are equal. A 1-of-1 owner leads "Win rate by owner" with 100 %. | A takeaway that is true. | On a tie: "Call, Meeting and Task are tied at 1 each". For win rate, rank owners with at least 3 closed opportunities, or add "(1 closed)" to the takeaway. |
| L10 | Low | Analytics filter | (a) The spec's "Owner/team" filter is only "Owner"; there is no team option. (b) In Private mode, a User sees team-mates in the Owner list, and choosing one returns empty charts. (c) The range line reads "Close dates from 01.10.2026 – 31.10.2026". (d) The intro says "at each deal's FX rate" but does not state ADR-0005's open-pipeline rule (current month). | Spec 3.3; ADR-0005 transparency footer. | (a) Add team options for Managers and Admins (see Decisions). (b) In Private mode, list only the user. (c) "Close dates 01.10.2026 – 31.10.2026." (d) Use ADR-0005's line: "Converted to DKK at monthly rates (won and lost at close month; open pipeline at November 2026 rates). Where a month has no rate, the latest earlier month is used." |
| L11 | Low | Dashboard: page title | "Dashboard – CRM" is fine. The "Data as of" line wraps under the h1 at 390 px with a large gap. | Tidy. | Reduce the page-header gap below 640 px. |
| L12 | Low | Analytics grid | Cards in the same row stretch to the tallest. "Pipeline by stage" has about 200 px of empty space when the neighbouring table is open. | Tidy. | `.charts { align-items: start; }` |
| L13 | Low | Settings (increment-3 leftovers) | (a) Help texts are still not linked: the Edit team group ID help has no `aria-describedby`, and on Add team only the error is linked, not the help. (b) Messages still show months as "10.2026" (increment-3 L3a). (c) The FX edit page does not focus the form (increment-3 L3c). | Spec 7.4. | (a) Add the help ids to `aria-describedby`. (b) and (c) as in the increment-3 review. |

Severity counts: High 1 · Medium 5 · Low 13. Not verified in demo data: the analytics table badge "Latest rate used". In this data set, the only fallback record had a close date outside every period tested. The code path (`flagOf`) is correct by reading.

### Accessibility requirements specific to this feature (for the fixes)
- No page-level horizontal scroll on Dashboard or Analytics at 320–1439 px, for any role, with real row counts (H1). Wide tables scroll inside `.table-wrap`.
- A KPI tile's accessible name or description includes every FX exclusion or fallback that affects its value (M2).
- Chart axes show unique, correct tick labels (M3). The data table stays the full text alternative, and its cards keep column labels (M5).
- A filter never ignores user input silently (M4).
- Keep the following as they are: the `figure`/`figcaption` with takeaway, the canvas `role="img"` with `aria-label`, server-rendered tables, the `<details>` toggle and reduced motion.

## Decisions needing product-owner sign-off
1. **Analytics team filter (L10a):** the spec says "Owner/team". The build has Owner only. **Recommendation:** add "Team: Sales North" options for Managers (their team) and Admins (all teams). Managers reviewing team pipeline is a stated task (spec 0). Accepting Owner-only for now is also reasonable.
2. **Activities tile emphasis (L7):** lead with overdue when there is any overdue work, instead of "due today" as the spec wireframe shows. **Recommendation:** lead with overdue. This needs validation with real users (assumption A3).
3. Carried over from increment 3 and still open: in-use stage removal with a "Move N opportunities to [stage]" bulk step (the minimum is built), and the logo being PNG only (the copy is already updated in the build).

## Re-test after fixes
- H1: as User at 1024, 1280 and 1440 px, scrollWidth must equal clientWidth.
- M1: a tile value at 1280 px must not break inside a word.
- M2: tab to the Open pipeline tile with an exclusion present; the description must include the note.
- M3: tick labels on count charts must be unique integers.
- M4: preset period plus edited dates gives a custom range or a clear message.
- M5: analytics cards at 390 px show column labels.
- Re-run axe on Dashboard and Analytics for all three roles.
