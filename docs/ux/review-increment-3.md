# UX review: increment 3 (Admin settings) and re-test of increment 2 fixes

Reviewer: UX · Date: 18.11.2026 · Spec: `docs/ux/ui-spec.md` v0.3 (section 3.8 Settings, sections 2, 5, 6, 7) · Build reviewed: commit `c91f52d` (built `dist/`, demo mode, `NODE_ENV=test`, port 3999). Later commits `b428aa2` (logo PNG only, stage id checks) and `1ec0caf` (tests), and uncommitted dashboard/analytics work in the tree, were not assessed. Only the PNG-only change affects this review (see Decisions).

## Verdict: FAIL

**Increment 2 re-test: all 11 fixes pass.** Increment 3 has three High issues:
- The accent-colour check lets through colours that then fail contrast on the page background.
- The pipeline-stages error summary leaves out row errors and gives the wrong problem count.
- FX rates typed with the other locale's decimal separator are silently saved 10 or 10,000 times too large. This also affects amounts.

All three are small, local fixes. Once H1–H3 are fixed, this should be PASS WITH ISSUES.

Known deviations, accepted and not raised:
- team name max 80 (DB column)
- team deactivation takes effect at members' next sign-in
- no remove-logo action
- mobile push-down menu without a focus trap
- no 640–1023 px icon nav

## Method
- Playwright (Chromium headless, deviceScaleFactor 1) at 1280×900, 800×900 and 390×844 (touch, mobile). Signed in as Admin, plus User and Manager for the access check.
- axe-core 4.14 (loaded from the scratchpad, not added to the repo) on 30+ states: every settings page, every confirmation page, every error state, the accent colour at a borderline value, and the increment-2 pages. Tags: wcag2a/aa, wcag21a/aa, wcag22aa, best-practice.
- Flows run end to end:
  - General: name, accent colour; logo with no file, wrong type, more than 500 KB, valid SVG
  - Locale: switching da-DK ↔ en-GB
  - FX: empty, negative, duplicate, edit, delete; en-GB input
  - Stages: move up/down, add, remove an in-use stage, invalid save
  - Picklists: move, rename empty, add empty, add duplicate, deactivate (checked that the value is gone from the account form)
  - Visibility: switch with confirmation
  - Teams: add (empty, bad GUID, braces/upper case, duplicate group, 80 characters), edit, change group with confirmation, deactivate, reactivate
  - Users filter with no matches
- Keyboard: moving a stage with Enter and counting the tab stops back to the row, focus after each POST, error-summary links.

## Part 1: Re-test of increment 2 fixes

| ID | Result | Evidence |
|---|---|---|
| H1 Error summary focus | **Fixed** | After an empty submit on New opportunity, focus is on `#error-summary` ("Fix 4 problems to save"). Same on every settings form. |
| H2 List search label | **Fixed** | The list field is `id="f-q"` with its own label "Search". The top-bar box has exactly 1 label. |
| H3 Mark done name | **Fixed** | Name is "☐ Mark done: Call Acme" (visible text first). axe shows no label-content-name-mismatch. |
| M9 Avatar name | **Fixed** | "DA – account menu for Demo Admin". axe shows no serious issues on any page with default settings. |
| M1 Focus after action | **Fixed (variant)** | Focus goes to the flash ("Marked as done.", "Rate saved."), not the `h1`. The flash is announced and sits at the top of `main`, so this is accepted. Side effect on the stages editor: see M2. |
| M4 Won/lost confirmation | **Fixed** | Confirmation page "Mark Renewal 2027 as won? The stage changes to Closed won. The close date will be set to today." with [Mark as won] / Cancel. Lost includes the reason. |
| M5 Lost reason inline error | **Fixed** | Inline error "Choose why this was lost." with `aria-invalid="true"`, `aria-describedby="e-lostReason"`, and focus on the select. |
| M7 Cards < 640 px / 800 px | **Fixed for CRM lists** | At 390 px, rows are cards with `data-label` captions (Name, Account, Stage, Amount, ...). At 800 px the Opportunities page no longer scrolls (scrollWidth 800). Settings tables lack the labels and one still scrolls at 800 px: see M6. |
| L4 Date-time | **Fixed** | "18.11.2026 21:34" (da-DK) and "18/11/2026 21:34" (en-GB). |
| L5 Page title | **Fixed for records** | "Opportunity · Renewal 2027 – CRM". Settings titles: see L7. |
| L10 Breadcrumb current | **Fixed** | Last breadcrumb item is `<span aria-current="page">`. |

Still open from increment 2 and not part of this re-test: M2, M3, M6, M8 and L1–L3, L6–L9, L11, L13, L14. L2 (env banner outside a landmark) is the only axe finding on default pages (moderate, "region").

## Part 2: Increment 3, Admin settings

### What works well
- **Access control:** Settings is hidden from the nav for User and Manager, and `/settings/*` returns the 403 page with the spec copy.
- **Structure:**
  - The sub-nav is a `nav aria-label="Settings"` with `aria-current="page"`.
  - Each page has one `h1`.
  - Picklist tabs are links in their own labelled `nav`.
  - Every table has a caption and `th scope`.
- **Unique row-action names:**
  - "Edit EUR 11.2026" / "Delete EUR 11.2026"
  - "Edit Sales North" / "Deactivate Sales North" / "Reactivate Sales South"
  - "Rename Retail" / "Deactivate Retail"
  - "Move Proposal up"
  - "Remove stage Proposal"
- **Accent colour:**
  - Too-light colours are refused with the exact spec copy.
  - A malformed value gets "Enter a colour like #1D4ED8."
  - The theme is served as a validated stylesheet, so the CSP stays strict.
- **Logo:**
  - Errors use the spec copy "Logo must be PNG or SVG, 500 KB or smaller.", including for files over 500 KB.
  - The preview has alt "Current logo". The top-bar logo has `alt=""` next to the CRM name, which is correct.
- **Locale switch** changes dates, money and the preview everywhere ("DKK 1,234,567.89 · 18/11/2026").
- **FX rates:**
  - The rate definition is spelled out ("1 unit of the currency = X DKK").
  - "Missing" rows use text plus a warning line, not colour alone.
  - The duplicate rate gets an error and an "Edit the existing rate" link.
  - Delete has a confirmation that names the impact, and flashes "Rate saved." / "Rate deleted."
- **Stages:**
  - Move up/down buttons give a keyboard and single-pointer alternative to drag (2.5.7).
  - Won/Lost probability is forced to 100/0 in the editor.
  - Copy matches the spec: "You need exactly one Won stage and one Lost stage.", "Each stage needs a different name.", "Probability must be between 0 and 100."
  - The unsaved-changes note "Changes are not saved yet. Choose Save stages to keep them." is clear.
- **Picklists:** no hard delete. A deactivated value disappears from the account form at once, and the note explains the rule. Duplicate values are refused.
- **Visibility:** `fieldset`/legend and the radio copy match the spec word for word. Switching asks for confirmation with the spec copy. Saving the same mode skips the confirmation.
- **Teams:**
  - Add-team copy matches the spec (empty name, empty ID, GUID format, "This group is already linked to the team Sales North.").
  - Braces and upper case are normalised.
  - The help text "Find this in Microsoft Entra ID > Groups > the group > Object ID." is shown.
  - Flashes name the team. The change-group and deactivate confirmations match the spec, adjusted for the accepted next-sign-in behaviour.
  - Users are read-only. The no-team badge is icon plus text in warning colours, the summary line "1 user has no team or a team problem. [Show them]" links to the filter, and the GUID is monospaced and selectable.
- **axe:** zero serious or critical results on every settings page and state with the default accent colour.

### Issues

| ID | Severity | Screen | Issue | Expected | Recommendation |
|---|---|---|---|---|---|
| H1 | High | Settings > General (accent colour); then every page | The colour is only checked against white (`contrastWithWhite >= 4.5`). Links, sub-nav, sort headers and link-buttons sit on `--color-bg` #F9FAFB and table headers. A colour that just passes on white fails there. Example: #757575 is accepted ("Settings saved."), then axe reports **serious color-contrast 4.4:1** on every page (sub-nav, filters, sort links). The help text promises "4.5:1 against white", so the Admin has no way to know. WCAG 1.4.3. | Spec 3.8: the colour must work for text and buttons. Spec 8: zero serious axe issues. | Check against the **darkest light surface** the primary colour is drawn on: require `contrast(accent, #F9FAFB) >= 4.5`, about 4.73:1 against white. Keep the white check for white-on-primary buttons. Change the help text to "It must be dark enough to read on the page background (contrast 4.5:1)." Add a unit test with #757575 (refused) and #1D4ED8 (accepted). |
| H2 | High | Settings > Pipeline stages, invalid save | The summary shows only page-level problems. Example: a blank name, a duplicate name, probability 150 and two Won stages give "**Fix 1 problem to save** · You need exactly one Won stage and one Lost stage." The three row errors appear inline but are not in the summary and the count is wrong. Focus goes to the summary, so a screen-reader user hears that there is one problem. Cause: in `stagesPage`, `summary: extra.summary` overrides the `summaryOf(errors)` built by `render`. Inline row errors also have no `aria-describedby`, and the probability field gets no `aria-invalid`. | Spec 2: the summary lists a link to **each** field. The count is correct. | Pass `summary: [...summaryOf(errors), ...extra.summary]` (field errors first, as links to `#f-name0` etc.). Add `aria-describedby="e-name{i}"` / `e-prob{i}` and `aria-invalid` on both inputs. Prefix row errors with the row ("Row 3: Each stage needs a different name.") so the summary is readable out of context. |
| H3 | High | Settings > FX rates (also Opportunity amount, increment 2) | `parseDecimal` drops every grouping separator wherever it appears. With locale **en-GB**, typing "6,8" saved USD at **68.0000**, with the flash "Rate saved." With **da-DK**, "7.4590" would become 74.590. A wrong FX rate silently changes every dashboard and analytics total. This is a financial-data integrity problem (no silent wrong values), and staff switch between Danish and English number habits. The error example "7,4590" is also hard-coded for every locale. | Spec 6: grouping separators are optional, but input must be unambiguous and examples must match the locale. | Accept a grouping separator only in valid positions (groups of exactly 3 digits, only left of the decimal). Otherwise return the format error. With that rule, "6,8" in en-GB is refused with "Enter a rate, for example 7.4590." Build the example with the locale (`rateInput(7.459, locale)`). Apply the same parser to amounts. Add tests: en-GB "6,8" refused, "1,234.5" accepted; da-DK "7.4590" refused, "1.234,5" accepted. |
| M1 | Medium | FX rates, Add team, Edit team, Picklists (add and rename), Currency & locale | Inline errors are not linked to their fields:<br>• FX, Add team, Edit team, Add value: inputs get neither `aria-invalid` nor `aria-describedby`.<br>• Picklist rename: `aria-invalid` but no `aria-describedby`, and the error `span` has no id.<br>• Locale and team-edit errors also have no warning icon.<br>General does it right, so the pattern is inconsistent. A screen-reader user who follows a summary link lands on a field that is not announced as invalid, and the error text is not read. | Spec 2 / 7.4: inline error linked with `aria-describedby`, `aria-invalid="true"`, icon plus text. | Move the field markup into one partial (label, help, error with id, input with `aria-invalid` and `aria-describedby` listing help and error ids) and use it on all settings forms, as `settings-general.ejs` already does. Link existing help texts (base currency, group ID, accent colour) the same way. |
| M2 | Medium | Pipeline stages: Move up/down, Add stage, Remove | Each action reloads the page and focus jumps to the "Changes are not saved yet" note at the top. To move a stage two places, a keyboard user must Tab back to the row each time: 12 stops to reach row 4, more for lower rows. Same after picklist moves ("Settings saved."). Not a WCAG failure, but a lot of repeated effort for the main keyboard path to reordering (2.5.7 alternative). | Spec 7.2–7.3: logical focus; stage reorder by buttons is the keyboard alternative. | After a move, render the page with the moved row's button focused: pass `focusId`, e.g. `up-3` for the row's new position, and have `app.js` prefer `[data-autofocus]` over `.flash`. If the button is now disabled (moved to the top), focus the other one. Keep the note as `role="status"` without moving focus to it. Do the same for picklists (redirect to `#value-{id}`). |
| M3 | Medium | Pipeline stages: removing a stage that has opportunities | "Remove" appears on every row, including in-use stages and the only Won and Lost stages. Clicking it on "Proposal" (1 opportunity) hides the row. The error appears only at Save ("Move 1 opportunity out of "Proposal" before deleting it."), and the row stays missing from the editor, so the Admin has to reload to get it back and loses other unsaved edits. The copy says "deleting" where the button says "Remove". There is no way to move the opportunities from here. | Spec 3.8: "A stage in use can't be deleted until its opportunities move. Clicking Delete opens a dialog: Move 12 opportunities to [stage v] before deleting." | Preferred: for a stage with used > 0, link "Remove" to a confirmation page with a target-stage select ("Move 12 opportunities to [stage v], then remove Proposal") and an audited bulk move. Minimum: refuse the remove action at once, keep the row, show the error on that row, and link to `/opportunities?stage={id}`. Hide Remove on the only Won and only Lost stage. Use "remove" consistently in the copy. |
| M4 | Medium | General: logo upload errors | Logo errors ("Choose a logo file first.", type/size, unsafe SVG) appear only as a top flash `role="alert"`. The file input gets no inline error, no `aria-invalid` and no focus. Same pattern as increment-2 M5, which is now fixed. | Spec 2: errors use the form pattern. | Re-render (or redirect with a field error) so the message sits above `#f-logo` with `aria-invalid` / `aria-describedby`, and focus the input or the summary. Remove the `required` attribute, or keep it and accept the browser bubble, but the server copy must still show inline. |
| M5 | Medium | FX rates: Edit rate | Changing an existing rate saves at once. Only a muted note says that totals change. Delete does confirm. | Spec 3.8: "Changing or deleting a rate asks for confirmation: This changes totals on the dashboard and in analytics for November 2026. Continue?" | Reuse `settings-confirm` for edits where the rate changed, with the spec copy and month name. Or ask the product owner to accept the note (see Decisions). |
| M6 | Medium | Settings tables at < 640 px and at 800 px | (a) The increment-2 card CSS applies to every `table.data`, but settings cells have no `data-label`. Cards show unlabeled values: "4", "Active", "0" for Members and Opportunities, and the stage probability and type have no visible column name.<br>(b) At 390 px `/settings/users` scrolls horizontally (623 px): the Team filter `select` grows to the longest team name.<br>(c) At 800 px `/settings/stages` scrolls horizontally (900 px), so the Opportunities count and Remove are off-screen. | Spec 1: no page-level horizontal scroll; cards show labelled fields. WCAG 1.4.10 is met at 320 px except (b). | Add `data-label` to settings `td`s (Members, Status, Probability, Type, Opportunities, Order). Add `select { max-width: 100%; }`. For the stages table, either keep it a scrolling `table-wrap` (constrain the wrapper with `max-width: 100%; overflow-x: auto`) or stack Type radios inline so it fits in 576 px. |
| L1 | Low | General: accent colour | Plain text field "#1D4ED8" with no swatch. The spec asks for a picker. | Spec 3.8 "accent colour picker". | Add a native `<input type="color">` beside the text field, synced by JS. Keep the text field as the labelled fallback. Show a sample button and link in the chosen colour. |
| L2 | Low | Currency & locale | (a) "Preview (saved settings)" is not live while choosing. (b) The help "The base currency is always enabled." runs inline after the AED checkbox and wraps oddly. (c) Help texts are not linked with `aria-describedby`. (d) The base currency checkbox can be unticked, which then silently has no effect. | Spec 3.8 live preview. | (a) Update the preview with a small script on change (server preview as fallback). (b) Put the help on its own line under the legend. (c) Link it. (d) Render the base currency as checked plus the text "(base)", and tell the user if they untick it. |
| L3 | Low | FX rates | (a) Months appear as "11.2026" in messages ("A rate for EUR in 11.2026 already exists."). The spec uses "November 2026" in sentences. (b) "Missing" rows ignore the Currency filter. (c) "Edit EUR 11.2026" opens the edit form at the bottom without scrolling to it or focusing it. | Spec 3.8 copy. | (a) Use the long month name in sentences and keep 11.2026 in the table. (b) Apply the filter to missing rows. (c) Link to `/settings/fx/{id}/edit#add-rate` and focus the Rate field. |
| L4 | Low | Users & teams: deactivate / edit | (a) "Its 1 members will have no team..." needs pluralising. (b) Changing a team's group to one already linked to another team first shows the "Change group ... Continue?" confirmation and only then the duplicate error. | Error before confirmation. | (a) "Its 1 member" / "Its 8 members". (b) Check uniqueness before showing the confirmation. |
| L5 | Low | Users & teams: Users table | (a) A search with no match shows only the table headers. (b) Empty "Last sign-in" cells are blank. (c) The spec's [Open Entra admin centre] link is missing. | Spec 3.9 empty state; spec 6 "–" with hidden "Not set". | (a) "No users match these filters. [Clear filters]". (b) "–" plus visually hidden "Not set". (c) Add the link (`https://entra.microsoft.com`, opens in the same tab). |
| L6 | Low | Stages and Picklists: move buttons | Move buttons show only "↑" / "↓". Their names ("Move Finance up") are fine for screen readers, but voice-control and new users can't guess the name, and the spec shows [Move up]/[Move down]. At 1280 px the stage "%" sign also wraps under the probability input. | Spec 3.8. | Visible text "Up" / "Down" with a hidden row name ("Up<span class="visually-hidden">: move Finance up</span>"), or at least `title`. Keep the input and "%" on one line (`white-space: nowrap`). |
| L7 | Low | All settings pages | Titles are "General settings – CRM", "Picklist: Industry – CRM", "Edit Sales North – CRM". | Spec 1: "{Page} · {Record} – {CRM name}". | "Settings · General – CRM", "Settings · Picklists · Industry – CRM", "Settings · Edit team · Sales North – CRM". |
| L8 | Low | Visibility confirmation | "Change visibility: This changes what everyone can see immediately. Continue?" does not say which mode is being chosen. | Confirmations name the object (spec 4). | Title "Switch to Team visibility?" and button "Switch to Team". |
| L9 | Low | Checkboxes and radios at touch widths | Native 13×13 px controls (enabled currencies, stage type, visibility). Their labels are clickable, so the targets are usable, but they are small next to the 44 px fields. | Spec 5: 44 px on touch widths. | `accent-color` plus `width/height: 20px` and label padding to give at least a 44 px row height below 640 px. |

Severity counts: High 3 · Medium 6 · Low 9.

### Accessibility requirements specific to this feature (for the fixes)
- The accent colour must keep 4.5:1 for primary-coloured text on #F9FAFB and on white, and white text on the primary button must keep 4.5:1 (H1).
- Every settings form uses the shared field pattern: visible label, help and error linked with `aria-describedby`, `aria-invalid`, an error summary that links to every field with an error, and focus on the summary (H2, M1, M4).
- Reorder controls keep focus on the moved item (M2). No drag-only interaction.
- No page-level horizontal scroll at 320–1023 px on settings pages. Cards carry labels (M6).
- Numeric input is unambiguous for the configured locale (H3).

## Decisions needing product-owner sign-off
1. **Logo format:** commit `b428aa2` (not reviewed here) makes the logo PNG only for security. Spec 3.8 says PNG or SVG. **Recommendation: accept PNG only.** UX will update spec 3.8 copy to "Logo must be a PNG, 500 KB or smaller."
2. **In-use stage removal (M3):** build the spec's "Move N opportunities to [stage]" step (recommended: it saves Admins editing opportunities one by one), or accept "refuse with a link to the filtered opportunities list" for now.
3. **FX rate edit confirmation (M5):** add the confirmation (recommended, since edits change reported totals) or accept the on-form note.

## Re-test after fixes
H1–H3 are needed for PASS. Re-run axe with the accent colour set to #757575 (must be refused) and to a value that is just accepted, check every page for colour-contrast, then repeat the stages invalid-save and en-GB FX "6,8" checks. M1–M6 are recommended in the same pass.
