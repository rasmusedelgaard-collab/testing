# UX review: increment 2 (layout, sign-in, Accounts, Contacts, Opportunities, Activities)

Reviewer: UX · Date: 18.11.2026 · Spec: `docs/ux/ui-spec.md` v0.3 · Build reviewed: commit `df13cb8` (built `dist/`, demo mode, port 3999). Uncommitted changes that appeared in the working tree during the review (branding in `head.ejs` / `app.css`) were not assessed.

## Verdict: FAIL

Three High issues, all small to fix: the error summary does not get focus, the list-page search field has no accessible name (duplicate `id="q"`), and the "Mark done" buttons fail Label in Name (WCAG 2.5.3, level A). The spec's acceptance criterion "axe shows zero serious or critical issues" is not met. Once H1–H3 and M9 are fixed this should be PASS WITH ISSUES. The two keyboard-only journeys in the acceptance criteria ("create opportunity" and "mark activity done") **can be completed**.

Known deviations accepted for now and not raised: no "Open pipeline" column on Accounts (increment 4), plain `<select>` pickers limited to 500, Mark as won/lost inline on the detail page.

## Method
- Playwright (Chromium headless, deviceScaleFactor 1) at 1280×900, 800, 390×844 (touch) and 320 px. Signed in as Admin and User (demo).
- axe-core 4.x (installed temporarily with `--no-save`) on 24 page states: sign-in, dashboard, list/detail/new/edit/delete for all four entities, 404, filtered-empty list, search. Tag sets: wcag2a/aa, wcag21a/aa, wcag22aa, best-practice.
- Keyboard-only walk-throughs: sign-in, tab order on the shell, skip link, create opportunity (including two failed submits), mark activity done, drawer open/Esc at 390 px.
- Validation copy for every form compared word for word with spec sections 3.4–3.7.
- Edit conflict reproduced with two tabs; delete, won/lost, sign-out flows run end to end.

## What works well
- Skip link is the first focusable element, is visible on focus and moves to `main`.
- Visible 2px focus ring on every tab stop (primary blue on light, `#93C5FD` on the dark bar and nav). `scroll-padding-top` is set for the sticky bar.
- Landmarks (`header`, `nav[aria-label=Main]`, `main`, breadcrumb/pagination/related `nav`, record `footer`), one `h1` per page, `lang="en"`.
- axe: **no contrast violations** on any page. Tokens match spec section 5.
- Forms: labels sit above fields with "(required)" in text. Inline errors use an icon and text, `aria-invalid="true"` and `aria-describedby`. Error summary title "Fix N problems to save" with working links to each field. Input is kept on error. Error copy matches the spec exactly (name, account, amount, negative amount, close date, real date, website, phone, email, last name, subject, "Link this activity to an account, contact or opportunity.").
- Tables: caption (visually hidden), `th scope="col"`, sortable headers are links with `aria-sort`. Amounts are right-aligned with tabular figures, money and dates in da-DK format.
- Flash after save "Opportunity saved." / "Account deleted." / "Marked as done." uses `role="status"`. Error flash uses `role="alert"`.
- Owner field: Admin sees "Name (Team)". User sees only team-mates plus the help text "You can choose people in your team."
- Delete confirmation page names the record and the consequence, with a danger "Delete account" button and a Cancel link. 404 and signed-out copy match the spec.
- Mark as won sets the close date to today and says so ("Opportunity marked as won. Close date set to today.").
- At 390 px the Menu button (44 px high) has `aria-expanded`/`aria-controls`. Esc closes the nav and returns focus to the button. No page-level horizontal scroll at 390 or 320 px (only inside tables).

## Issues

| ID | Severity | Screen | Issue | Expected | Recommendation |
|---|---|---|---|---|---|
| H1 | High | All forms (new/edit for 4 entities) | After a failed submit, focus stays on `body`. The summary has `role="alert"` and `tabindex="-1"` but is never focused. An alert present at page load is not reliably announced, so screen-reader users get no feedback. | Spec 2 / 7.3: the summary receives focus. | In `app.js`: `var s = document.getElementById('error-summary'); if (s) s.focus();` (or `autofocus` handling). Keep `tabindex="-1"`. |
| H2 | High | Accounts, Contacts, Opportunities, Activities lists | The list filter "Search" input has `id="q"`, and so does the global search in the top bar. `<label for="q">Search</label>` attaches to the top-bar box (which then has two labels), so the list search field has **no accessible name** (`searchbox` with no name). Clicking the "Search" label focuses the wrong field. WCAG 1.3.1 / 4.1.2. | Every input has its own visible `<label for>`. | Give the list field a unique id, e.g. `id="f-q"` with `name="q"` kept, and update its label. |
| H3 | High | Activities list, Related records tab (account/contact/opportunity detail) | Visible text "☐ Mark done" / "☑ Done", but the accessible name is "Mark 'Call Acme' as done" / "… as not done". The visible words are not in the name, so a voice-control user saying "click Mark done" fails. WCAG 2.5.3 (level A); axe "serious" (label-content-name-mismatch). | Name contains the visible label. | Drop the `aria-label`. Use visible text plus hidden context: `Mark done<span class="visually-hidden">: Call Acme</span>` and `Done<span class="visually-hidden">: Call Acme, mark as not done</span>`. Or make the name start with the visible text ("Mark done: Call Acme"). |
| M1 | Medium | All pages after save / mark done / delete / won-lost | After every POST-redirect, focus starts at the top of the document. After "Mark done" a keyboard user has to tab through about 15 stops to get back to the list. Spec 7.3 says focus goes to the new page's `h1` after a successful save. | Focus on `h1` (or on the flash) after a successful action. | When the page has a `.flash`, focus the `h1` (`tabindex="-1"`). For mark done, also consider redirecting back to the row (`#activity-123`) and focusing the next "Mark done" button. |
| M2 | Medium | Activities list / detail | The flash "Marked as done." has no [Undo]. Users can only undo from the Done filter. | Spec 3.7: "Marked as done. [Undo]". | Add an Undo button (POST to the not-done endpoint) inside the flash. |
| M3 | Medium | New/Edit account | Saving a second "Acme A/S" gives no duplicate warning. | Spec 3.4: non-blocking warning "An account named Acme A/S already exists. [View it]". | Show the warning on the re-rendered form (or on the detail page after save) with a link to the existing record. |
| M4 | Medium | Opportunity detail | Mark as won/lost runs on one click with no confirmation, even though it changes the stage and may move the close date to today. (Placing the buttons inline is accepted; the missing confirmation is not covered by that deviation.) | Spec 3.6: moving to Won/Lost asks for confirmation. | Confirmation page or `<dialog>`: "Mark Southern deal as won? Close date will be set to today." Product owner can choose to accept this instead (see Decisions). |
| M5 | Medium | Opportunity detail, Mark as lost | With no reason chosen, the error appears only as a top flash "Choose why this was lost." The Lost reason select gets no inline error, no `aria-invalid` and no focus. | Same error pattern as forms. | Re-render with an inline error linked by `aria-describedby`, `aria-invalid="true"`, and focus on the select (or the error). |
| M6 | Medium | Edit conflict page | The conflict page explains the problem well but says "Your edits were not saved." The user's input is lost and the current values are not shown. They have to open the record and retype (WCAG 3.3.7 spirit; spec 3.9 "Show the current values"). | Show current values and keep the user's input. | Re-render the edit form with the current values, the conflict message as the alert, and a "Your change / Current value" list for the fields that differ. |
| M7 | Medium | All lists at < 640 px; Opportunities at 640–1023 px | Below 640 px the lists stay wide tables in a horizontal scroller (Opportunities is 632 px inside 358 px). Amount and Close date are off-screen with no cue. At 800 px the Opportunities page itself scrolls horizontally (848 px). The nav does not collapse to icons at 640–1023 px. | Spec 1 / 2: cards below 640 px; icon nav at 640–1023 px. | CSS card layout for `table.data` below 640 px (`display:block` rows, `data-label` on cells). At tablet widths either collapse the nav or let the table area scroll without widening the page. |
| M8 | Medium | Opportunity detail | No stage progress bar. | Spec 3.6: `<ol>` of stages with the current step `aria-current="step"`. | Add the `<ol>` above Details. Show past/current/future stages with text and shape, not colour alone. |
| M9 | Medium | All signed-in pages (top bar) | The account menu `<summary>` shows initials "DA" but is named "Account menu for Demo Admin". axe "serious" (label-content-name-mismatch) on every page, which blocks the "zero serious" criterion. | Name contains the visible text, or the visible text is not a label. | Name it "DA – account menu for Demo Admin", or show the first name next to the avatar and include it in the name. |
| L1 | Low | Mobile drawer (< 640 px) | The drawer is an in-flow disclosure that pushes content down. Focus stays on Menu, and the next tabs go to CRM, + New and the avatar before reaching the nav items. There is no focus trap. This is not a WCAG failure for a non-modal disclosure, but it differs from the spec ("drawer that traps focus"). | Spec 1. | Recommended: keep the disclosure, no trap (simpler), and move the `nav` in the DOM (or the Menu button) so the first Tab after opening reaches "Dashboard". UX will update the spec. |
| L2 | Low | All pages | The demo/test environment banner sits outside any landmark (axe "region", moderate). | All content in landmarks. | Move it inside `header` or give it `role="region" aria-label="Environment"`. |
| L3 | Low | New/Edit activity | Due date and time stay visible when Type = Note. Only the label says "(not used for notes)". | Spec 3.7: hidden for Note. | Hide them with JS when Note is selected. Without JS the current label is a fine fallback. |
| L4 | Low | Detail pages, history | Date-time shows "18.11.2026, 21.22" (Intl default for da-DK). The spec example is "18.11.2026 14:02". | Spec 6. | Use `timeStyle:'short'` with `hourCycle:'h23'` and join date and time with a space, or accept the Intl output (decide once for all locales). |
| L5 | Low | All pages | Page title is "{Record} – CRM", e.g. "Acme A/S – CRM". | Spec 1: "{Page} · {Record name} – {CRM name}", e.g. "Account · Acme A/S – CRM". | Prefix the entity name. Helps tab and history recognition. |
| L6 | Low | Opportunity form | Amount has no example or hint, so users only learn the format from the error. | Spec 6: placeholder/example matches locale. | Help text "For example 12.500,00", linked with `aria-describedby`. |
| L7 | Low | Contact form | Phone and Mobile have no `autocomplete`. Email has it. | Spec 7.4. | `autocomplete="tel"`. Not a 1.3.5 failure, since these are not the user's own details. |
| L8 | Low | Lists (no data), Related records tabs | The empty state has no explanatory line. Related tabs say "Nothing here yet." | Spec 3.9: "No opportunities yet." / "Opportunities track deals from first contact to close." | Add the one-line description per entity. Tabs: "No activities for this account yet." |
| L9 | Low | Lists | The sorted column shows no visible direction (only `aria-sort`). Sighted users can't tell the current sort. | Spec 2: "Name ^v". | Add an arrow icon (`aria-hidden`) next to the sorted header. |
| L10 | Low | Detail pages | The breadcrumb's last item has no `aria-current="page"`. | Breadcrumb pattern. | Wrap it in `<span aria-current="page">`. |
| L11 | Low | Lists | The "Past close date" badge uses 12 px text. | Spec 5: 12 px never carries essential info. | Use 14 px. |
| L12 | Low | Forms and filters at touch widths | Buttons and inputs are 40 px high at < 640 px. | Spec 5: 44 px on touch widths. | `min-height: 44px` in the < 640 px media query. Still meets WCAG 2.5.8. |
| L13 | Low | Forms | No "Saving..." disabled state on submit and no unsaved-changes warning (JS only). | Spec 2 / 3.9. | Small progressive enhancement in `app.js`. |
| L14 | Low | Top bar | Global search and Analytics lead to the "built in a later increment" page, and global search is hidden below 640 px. | Expected for this increment. | Either hide them until built, or keep them and accept. Make the search reachable on mobile once built. |

Severity counts: High 3 · Medium 9 · Low 14.

## Keyboard-only journeys
- **Create opportunity:** Tab to "New opportunity" → form → Save with empty fields → summary with 4 errors, links move focus to fields → fill (selects with arrow keys, native date) → Save → detail page with "Opportunity saved." **Completed.** Friction: H1 (no focus on summary), M1 (focus at top after save).
- **Mark activity done:** Activities list → Tab to "Mark done" (24 px high, focus visible) → Enter → "Marked as done." **Completed.** Friction: H3, M1, M2.

## Decisions needing product-owner sign-off
1. **Won/lost confirmation (M4):** add a confirmation step (recommended, since it changes stage and close date) or accept one-click with the clear flash as now.
2. **Mobile nav (L1):** accept the push-down disclosure without a focus trap (recommended) instead of a modal drawer. UX will update spec section 1.
3. **Date-time format (L4):** use "18.11.2026 14:02" (recommended, matches the spec and Excel habits) or accept the Intl default "18.11.2026, 14.02".

## Re-test after fixes
H1–H3 and M9 are needed for PASS. Re-run axe on the same 24 states, expecting zero serious or critical results, and repeat both keyboard journeys.
