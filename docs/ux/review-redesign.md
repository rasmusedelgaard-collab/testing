# UX review: UI redesign on the DAFA design system

Reviewer: UX · Date: 19.11.2026 · Spec: `docs/ux/ui-redesign.md` v1.0, DAFA design system (`docs/ux/design-system/`), ui-spec v0.3 where it still applies.
Build reviewed: branch `claude/tender-darwin-4quuzo`, HEAD `de392ce`. `dotnet/src` was not changed.

## Verdict: FAIL (one High issue)

The redesign is a real step up. It now looks like DAFA:
- the logo and rings are used well
- the blue sign-in field is strong
- type sizes give a clear hierarchy
- the components are consistent and the Dark theme is complete

axe-core found **no serious or critical issues** on 21 page templates × Light/Dark × 390/1024/1440 (126 runs).

**One High issue blocks release.** At <640 px, every dense table on the dashboard turns into overlapping text that cannot be read. The dashboard is the start page, and phones are a target. The fix is one CSS rule (H1). After that the build should pass. Five Medium issues should go in the same pass, or be signed off as spec changes (section "Spec changes I propose").

Visual quality in short:
- It is modern, calm and clearly on-brand. It is no longer "boring" in the generic-admin-template sense.
- Large mid-gray (`#d6d7d7`) panels make the dashboard read grey and heavy.
- The pipeline board, the most visible new feature, is dominated by its move forms.
- Both come from my spec, not from the build. I propose small spec changes below (M4, M5) for PO sign-off.

## Method
- **Rendering:** the real Razor views from the render tests (`PageRenderSmokeTests`, in-memory models, no database). Run with `CRM_RENDER_DUMP`. The HTML was served with the real `wwwroot` (CSS, JS, sprite, brand SVGs).
  - `theme.css` was a static stand-in with no accent override, which is the default/legacy-accent case.
  - Pages: dashboard (full and empty), the opportunities and activities lists, the filtered-empty list, the opportunity and account records, delete and stage confirms, the board (filled and empty), search (results, no results, empty query), Settings General, Pipeline stages and Visibility, sign-in, the 404, 409 and waking pages.
- **Browser:** Playwright 1.63, Chromium headless, viewports 390, 1024 and 1440 (height 900), `colorScheme` light and dark (system theme path), JS on.
- **Stress variants:** the test fixtures are tiny, so I changed the rendered DOM in the browser to check real-data cases:
  - six KPI tiles as in production
  - long opportunity and account names
  - five cards in each of four board columns
  - the six-tile grid at 1280, 1440, 1600, 1920 and 1440 with the sidebar collapsed
- **axe-core 4.x** on every page × theme × width (wcag2a/aa, 21a/aa, 22aa, best-practice).
- **Checks:** keyboard tab order on the dashboard, and a target-size probe (2.5.8).
- **Code review** of the reported deviations: `_Layout`, `_Flash`, `_SettingsLayout`, `_Timeline`, `Pipeline/Index`, `Detail`, `Dashboard/Index`, `Color.cs`, `BrandingEndpoints.ThemeCss`, `General.cshtml(.cs)`, `app.css`.
- **Not covered**, because there are no render fixtures:
  - Analytics charts
  - entity create/edit forms
  - test sign-in and the user picker
  - Settings FX, Picklists and Users & teams
  - real flash messages (code review only)
  - drawer and command bar behaviour (only the JS code was read)

  These should get a check on seeded Test data (implementation step 12).

## Developer's reported deviations

| # | Deviation | Assessment | Decision |
|---|---|---|---|
| D-a | KPI grid uses 6 columns only from 1600 px (spec: ≥1440) | **Justified.** The content is max 1360 px. At 1440 with the 240 px sidebar, six tiles would be 175 px wide and "24,1 mio. kr." and "17,9 mio. kr." overflow (screenshot `redesign-kpi-six-tiles-1440.png` shows the 3×2 result). Even at 1600 the tiles are 202 px and the value touches the edge (`redesign-kpi-six-tiles-1600.png`). Six columns are never comfortable. | **Accept**, with one refinement (L1): switch on the content width (container query), not the viewport. The spec will say "3 columns; 6 when the content area is ≥1300 px". |
| D-b | Seed accent `#1D4ED8` treated as "not set". Seed value left unchanged because migrations are checksummed | **Correct and matches decision D9** ("without touching production data"). `EffectiveAccent` ignores the legacy value, the default and any failing accent. Dark never gets the accent. General settings shows `#00456C` instead of the legacy value. | **Accept.** Two small leftovers in L2. |
| D-c | Flash position | Most pages render the flash directly under the page header, as the spec says: dashboard, lists, records, board, search, forms. Two exceptions: **Settings** (from `_SettingsLayout`, *above* the page `h1`) and the layout fallback on confirm/error pages (above the breadcrumb). | **Accept** the general approach. Fix Settings so the flash sits under the `h1` (L3). The fallback is fine. |
| D-d | Board accessible names: select is "Move to {name}" (spec said "Move {name} to") | **Better than the spec.** The visible label "Move to" stays at the start of the accessible name (WCAG 2.5.3 Label in Name). The button "Move {name}" is unchanged. | **Accept.** Spec text will be updated. |
| D-e | Timeline markup: `ol.timeline > li.timeline-group > h3 + ol.timeline-items > li` (spec: one flat list with `h3`s) | **Fine, arguably better.** Headings and lists nest correctly, screen readers announce the group size, and the connector line only runs inside a group. | **Accept.** |
| D-f | New empty-state copy | "No changes recorded yet." (activity history) and "No open opportunities yet. New opportunity" (dashboard) are clear and in our voice. "No open stages." (dashboard funnel) is accurate but leaves the user stuck. "Nothing here yet." is still used on the record Contacts/Opportunities tabs. | **Accept the first two.** Change the other two (L4). I will add all of them to spec section 9. |

## Findings

### High

| ID | Severity | Screen | Issue | Expected | Recommendation |
|---|---|---|---|---|---|
| H1 | High | Dashboard at <640 (both themes), and any `table.data.dense` with more than one column at <640 (record tab tables) | Funnel and "My open opportunities" collapse into overlapping text, and the footer runs into the table (`redesign-dashboard-light-390-table-overlap.png`). Cause: `table.data.dense tbody tr { height: 40px }` is more specific than the card-view reset `table.data tbody tr { height: auto }` in the `max-width: 639px` block. Rows stay 40 px while their content is 108–152 px. | Card view per 4.3: each row a block, no overlap. Reflow at 320 px (WCAG 1.4.10). | In the <640 block, use `table.data tbody tr, table.data.dense tbody tr { height: auto; }`. Add a render-test or e2e check at 390 that row heights are not fixed. |

### Medium

| ID | Severity | Screen | Issue | Expected | Recommendation |
|---|---|---|---|---|---|
| M1 | Medium | Waking page at 640–1279 px with JS, and whenever the user collapsed the sidebar | The DAFA logo disappears (`redesign-waking-light-1024-no-logo.png`). `html[data-sidebar=collapsed] .brand .logo-full { display:none }` also hits the `.brand` link on the waking page, which has no `.logo-mark`. The link stays in the tab order with no visible content, so focus lands on an invisible element. | Logo at the top of the waking page at every width (5.10). | Scope the rail rules to `.sidebar .brand`. Also add the rings ornament at ≥1024 (5.10), which is missing. |
| M2 | Medium | Opportunity record, Dark | Stage track: passed segments use `--ink` (white) and the current one uses `--action` (white in Dark), so they look the same. Only the bold label tells you where you are (`redesign-opportunity-dark-1024.png`). | The current stage stands out at a glance in both themes. | **Spec gap (mine).** In Dark, draw passed segments in `--dafa-gray-25` and the current one in white. Or give the current one `--chart-1` (ochre in Dark). Text and `aria-current` stay as they are. |
| M3 | Medium | Lists at <640 | The card view is a label/value grid (Name, Account, Stage …), with each card about 250 px tall. The Edit action is an icon only. A "Past close date" chip breaks the grid onto its own line (`redesign-opportunities-list-light-390.png`). | 4.3: line 1 the bold link; line 2 two key fields; line 3 chips and owner; actions as **text** links. | Build the 3-line card for <640. Show "Edit" as text. The list becomes 2–3× denser on phones. |
| M4 | Medium | Pipeline board | Each card is about 360 px tall and the move form is about 30 % of it. Long names wrap to 3 lines and every name is underlined. You see 2–3 cards per column per screen (`redesign-board-light-1440-long-names.png`). The page feels like a stack of forms, not a modern board. | The board is the "modern CRM" showcase (D1). You should scan 5–6 cards per column. | **Spec refinement (mine, needs a nod from the PO):** <ul><li>name bold, not underlined (underline on hover and focus)</li><li>one meta row "Account · EUR 240.000 · 15.12.2026"</li><li>owner as an avatar with a visually hidden name</li><li>the move control on one row (`[Negotiation v] [Move]`, with the visible label "Move to" kept small above it)</li><li>names clamped to 2 lines</li></ul> Keep the form, the names and the no-JS behaviour as they are. |
| M5 | Medium | Dashboard, record side panels, board columns (Light) | Every large block uses `--surface-panel` `#d6d7d7`, with white chips and tables on top. The page reads grey and heavy. The blue only appears on buttons. That is the main reason it can still feel "flat" to the PO (`redesign-dashboard-light-1440.png`). | Calm, but light and modern (principles 3 and 4). | **Spec refinement (mine):** <ul><li>keep `#d6d7d7` for the KPI cards only</li><li>use `--surface-subtle` `#eeeeee` for the My day, Pipeline-by-stage, My-open-opportunities and Details panels, and for board columns</li><li>make sparklines 40 px tall</li></ul> Contrast stays fine: ink on `#eee` is 6.8:1. Check this with the PO in the S4 preference test. |

### Low

| ID | Severity | Screen | Issue | Recommendation |
|---|---|---|---|---|
| L1 | Low | Dashboard KPI grid | The 6-column switch is tied to the viewport. At 1440 with the sidebar collapsed there is room (421 px tiles) but it stays at 3. Six-column tiles at 1600 can overflow with larger values ("124,1 mio. kr.", `nowrap`). | Use a container query on `main` (or `.tiles`): 6 columns when the inline size is ≥1300 px. Size `.tile-value` with `cqi` instead of `vw`. |
| L2 | Low | Settings > General | The field error example still says "Enter a colour like #1D4ED8." An Admin who deliberately enters `#1D4ED8` has it silently ignored. | Change the example to `#00456C`. Accept the silent case (the old blue is very unlikely to be a deliberate choice). Add it to the spec under D9. |
| L3 | Low | Settings pages | The flash renders above the `h1` (from `_SettingsLayout`), not under the header as on every other page. | Render `<partial name="_Flash" />` after the `h1` in each settings page, or give the settings layout a section. |
| L4 | Low | Dashboard funnel, record tabs | "No open stages." gives no next step. "Nothing here yet." is vague. | "No open stages. An administrator can add them in Settings > Pipeline stages." For tabs: "No contacts yet." / "No opportunities yet." (the New button is already above). |
| L5 | Low | Lists at ≥1024 with long names | The Amount ("EUR / 240.000,00") and Owner ("Anna / Holm") cells wrap (`redesign-opportunities-list-long-names-1440.png`). | `white-space: nowrap` on `td.num` and the owner cell. Let Name and Account wrap. |
| L6 | Low | Board column header | The probability chip has the same ground as the column, so it is not visible as a chip. The FX note appears twice (page alert and every column). | Give the chip `--surface` inside `.board-col`. Show the page-level note only, or only the column notes. |
| L7 | Low | Activities list | The record link (Subject) is not bold because bold only applies to `td:first-child a`, and the first column is now Done. | Mark the record-link cell with a class (e.g. `td.record`) and make that bold. |
| L8 | Low | Dashboard | The sparkline end dot is drawn in a stretched viewBox (`preserveAspectRatio=none`), so it becomes a 7×3 px ellipse. Link sizes in My day are mixed: "View all due" is 14 px and "View all overdue activities" is 17 px. Vertical gaps vary between 48 and 32 px. | Draw the dot as a separate un-stretched element (or drop it). Make the "View all …" links 14 px. Use `--space-xxl` between all dashboard rows. |
| L9 | Low | Sign-in | "Your session has ended" uses `.flash` (mint success bar) with an info icon. It should be an info alert (action bar). At 390 there is about 150 px of empty space above "Sign in". | Use `.alert` (info). Remove the forced vertical centring below 640. |
| L10 | Low | Copy | FX tile note reads "…not included: no FX rate Ask an administrator." (no full stop). Search reads "See all 1 opportunities". | Add ". " before the follow-up sentence. Use the singular, or hide "See all" when every hit is already shown. |
| L11 | Low | Search page, account record (axe, moderate) | `landmark-unique`: there are two unnamed `role=search` forms (top bar and page), and the record tab region and its section have the same name. | `aria-label="Search all records"` on the top-bar form and "Search" on the page form. Drop `aria-labelledby` from the tab `section` or rename it. |
| L12 | Low | Delete confirm | The alert repeats the `h1` question and is bold all the way through. It touches the `h1` with no gap. | Start the alert with the consequence. Only the first sentence is bold. Add `--space-md` above it. |

Accepted, not raised: the test banner outside a landmark (axe "region", known since increment 2), and the "Log activity" button appearing twice on records (in the header and in the Activity tab, as the spec wireframe shows).

## What works well (keep)
- **Brand:**
  - the logo with clear space
  - rings in the rail, the greeting and the sign-in field
  - the negative logo in Dark
  - bold Avenir/Arial headlines
  - square shapes, 8 px only on controls
  - no shadows or gradients
- **Hierarchy:** the 40 px greeting, then the KPI values at 28 bold, then 21 bold panel titles. On records the stage track and facts strip come before the detail.
- **Components:** stage pips, won/lost markers, warning bars, avatars as line circles, ringed icons. All carry text, and none use Unicode symbols.
- **Dark theme:** full parity. All text pairs pass, and focus rings are visible on every ground.
- **Accessibility basics:**
  - one `h1` per page
  - landmarks
  - skip link, sidebar, top bar and content in a sensible tab order
  - a 2 px focus ring on everything tested
  - unique board and timeline action names
  - region names on scrolling tables

## Spec changes I propose (v1.1, after this review)
1. Section 3.5 / 5.1: KPI grid "3 columns; 6 when the content area is ≥1300 px" (D-a, L1).
2. Section 4.16: "Move to {name}" accessible name (D-d), and the compact card (M4, needs PO nod).
3. Section 4.10: nested group markup (D-e).
4. Section 4.6 / 2.3: big panels on `--surface-subtle`, KPI cards on `--surface-panel` (M5, needs PO nod).
5. Section 5.4: Dark stage-track colours (M2).
6. Section 9: the new empty-state strings (D-f, L4).

## Screenshots (in `docs/ux/screenshots/`)
- `redesign-dashboard-light-1440.png`, `redesign-dashboard-dark-1440.png`: dashboard as rendered
- `redesign-dashboard-light-390-table-overlap.png`: H1
- `redesign-kpi-six-tiles-1440.png`, `redesign-kpi-six-tiles-1600.png`: six production tiles (D-a, L1)
- `redesign-board-light-1440-long-names.png`, `redesign-board-dark-390.png`: board (M4, L6)
- `redesign-opportunity-light-1440.png`, `redesign-opportunity-dark-1024.png`: record (M2)
- `redesign-opportunities-list-light-390.png`, `redesign-opportunities-list-long-names-1440.png`: lists (M3, L5)
- `redesign-signin-light-1440.png`: sign-in
- `redesign-waking-light-1024-no-logo.png`: M1
