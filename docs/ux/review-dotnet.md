# UX review: .NET 10 port of the CRM UI (Crm.Web)

Reviewer: UX · Date: 19.11.2026 · Spec: `docs/ux/ui-spec.md` v0.3 · Reference: the Node views in `src/views` as approved in `review-increment-2.md`, `-3.md` and `-4.md`.

Build reviewed: branch `claude/tender-darwin-4quuzo` at `ba616d1`. While the review ran, `8861e93` landed; it changes one test file only, so the findings apply to it as well. `dotnet/src/Crm.Web` was not changed.

## Verdict: PASS (with issues)

- **Parity is very close.** Every Razor page matches its EJS view in structure, landmarks, headings, ids, `aria-*` wiring and copy.
  - `wwwroot` CSS and JS are byte-identical to `src/public`.
  - Every validation message in `validators.ts`, `settingsValidators.ts` and `entities.ts` exists word for word in the .NET code (checked by script).
  - All the increment-4 High and Medium fixes are present: H1 (dashboard grid `minmax(0,1fr)`), M1 (no breaking inside tile values), M2 (tile `aria-describedby` points to the FX notes), M3 (integer ticks), M4 (typed dates switch the period to Custom) and M5 (`data-label` on chart table cells).
- **No High issues.**
- **Four Medium issues should be fixed before release.** Three are in the new .NET-only stage-editor behaviour (the Enter key, the stale-list message and the type lock). One is in the waking-up page, where "Go back" goes to the dashboard.
- **Where .NET is better than Node:**
  - An absolute session timeout keeps `returnTo`.
  - Detail page titles follow spec 1 ("Opportunity · Acme renewal").
  - The FX exclusion note uses the spec copy.
  - The stages editor refuses a stale save, where Node silently overwrote it.

## Method
- **Code review:** every `.cshtml` in `Pages/`, the page models that produce copy (`Stages`, `Fx`, `Team`, `Users`, `Picklists`, `Index` (dashboard), `Analytics`, `Error`), `Services/NumberFormats.cs`, `Formatter.cs`, `LocaleProfile.cs`, `Charts.cs`, `Fx.cs`, `Data/AdminRepoConfig.cs` (stage save rules), `Auth/AuthSetup.cs`, `SessionValidator.cs` and `Program.cs` (routes, error pages, CSP). Each was compared side by side with the EJS view or TS route.
- **Running app:** built to the scratchpad and run in `AUTH_MODE=test-access-code` with no SQL Server. These pages render without a database: sign-in, signed-out, no-access, sign-in error, test code (correct and wrong code) and the 500 page.
  - Tools: Playwright with Chromium and axe-core 4.x (wcag2a/aa, 21a/aa, 22aa, best-practice) at 1280 and 390 px.
  - Results: no serious or critical issues. The only finding is the known moderate `region` result for the test banner (accepted in increment 2, L2). There is no horizontal scroll. Skip link and focus ring checked: 2 px #1D4ED8 with a 2 px offset. With `?reason=expired`, focus moves to the session-ended message.
- **Enter key in the stage editor:** reproduced in Chromium with a test page that has the same button order as `Stages.cshtml`, and the proposed fix was checked the same way (see M1).
- **Not run (they need the database):** Dashboard, Analytics, lists and forms, Settings. These are code review only. The tests in `dotnet/tests/Crm.Tests/Unit/PeriodsKpiTests.cs` confirm the compact money values: "DKK 2.4M", "2,4 mio. kr.", "120 t kr." and "DKK 1M" for 999,960.

## Checklist

| Area | Result | Notes |
|---|---|---|
| Layout, nav, skip link, landmarks, `aria-current` | Parity | `_Layout.cshtml` = `head.ejs` + `foot.ejs`. Settings link only for Admin. |
| Test banner | Parity | "TEST ENVIRONMENT — test data only", `role="note"`, shown only with `IsTestAccess`. There is no demo mode in .NET, so the demo banner is not needed. |
| Sign-in, picker, sign-out, no access, sign-in error | Parity | Copy matches spec 3.1. The picker lists "Name (Role, team or no team)" with the first user selected. Wrong code: 401 with an alert. Locked: 429. See L1 and L2. |
| Session expired | Better | `?reason=expired` keeps `returnTo` on GET (Node dropped it). See L3. |
| Error, 403, 404, 409 conflict, 413, 429 | Parity | Same copy as Node. The 403 for a failed antiforgery check behaves as in Node. |
| Waking up the database | Regression on POST | See M4 and L4. |
| Dashboard tiles and lists | Parity | Compact money is pinned per locale: en "DKK 2.4M", da "2,4 mio. kr." / "120 t kr.", percent "34%" / "34 %". The tile `title` and hidden full value are in place. Overdue leads with danger colour plus text. Owner column dropped. Empty states use the spec copy. |
| Analytics | Parity | Figures, captions, takeaways, canvas `role="img"` and label, `<details open>` table that JS collapses, `data-label` on cells, page FX note, range error with focus target. The chart JSON uses the default encoder, so it can't break out of the script element. |
| Entity list, detail, form, delete | Parity | Error summary, inline errors, `aria-invalid`, `aria-describedby`, "Not set" for empty values, unique "Mark done" names, Last modified footer. |
| Settings: General, Locale, FX, Picklists, Visibility, Users & teams | Parity | Includes the leftovers listed under "Carried over". |
| Settings: Pipeline stages | New behaviour | A version hash refuses stale saves, and the type of an in-use stage is locked. Both are right for data integrity, but the messages need work (M2, M3). The Enter key problem is M1. |
| WCAG 2.2 AA (the pages that ran) | Pass | axe: 0 serious or critical. Keyboard order, focus ring and reflow at 390 px are fine. |

## Issues

| ID | Severity | Screen | Issue | Expected | Recommendation |
|---|---|---|---|---|---|
| M1 | Medium | Settings > Pipeline stages (`Pages/Settings/Stages.cshtml`) | Enter in a stage name or probability field triggers the form's default button. That is the first submit button in DOM order: row 1's "Move up", which is always `disabled`. Measured in Chromium with the same button order: **Enter does nothing**, with no feedback. The developer saw "Move down" instead; in a browser that skips the disabled button, the next one is row 1's "Move down". Either way, Enter never means "Save stages", which is what users expect after typing in a form field. | Enter in a text field submits the form's main action (platform convention; spec 7.2: everything works with the keyboard alone). | Make Save the default button. Put a hidden copy of it as the **first element inside the `<form>`**: `<button type="submit" name="action" value="save" class="visually-hidden" tabindex="-1" aria-hidden="true">Save stages</button>`. Verified in Chromium: Enter in row 1 and row 2 both post `action=save`. It works without JS, is not tabbable, and is not announced, because the visible "Save stages" stays the real control. Do not use `hidden` or `display:none`, because some engines then skip implicit submission. Add an HTTP or e2e test: Enter in `#f-name1` posts `action=save`. |
| M2 | Medium | Pipeline stages: stale list (`AdminRepoConfig.cs:116` and `:133`) | (a) The copy "...Reload the page and try again." is a trap. The page on screen is a POST response, so F5 re-posts the same stale `ver` and gets the same error. The editor also keeps the stale `ver` through Add, Move and Remove (`version: posted`), so only a fresh GET clears it. (b) The message doesn't say whether the user's edits were kept. (c) The summary title "Fix 1 problem to save" suggests the user can fix it on this page, but they can't. (d) Two different messages describe the same state ("was changed by someone else since you opened this page" and "The stage list is out of date"). It may also have been the same Admin in another tab, not "someone else". | Errors say what to do in a way that works (spec 4). The way out is a single action. | Use one message: "The stage list changed after you opened this page, so your changes weren't saved. [Load the current stage list]". The link goes to `/settings/stages` (GET). Use it for both cases. Show it as a page-level alert (`flash-error`, `role="alert"`, focused) instead of inside "Fix N problems to save". Optional: show the user's unsaved names in a short list so they can re-enter them. |
| M3 | Medium | Pipeline stages: in-use type lock (`AdminRepoConfig.cs:140-147`) | (a) The rule is only found out at Save. The Open/Won/Lost radios of in-use stages look editable, and the help line only mentions removal. (b) The refusal is page-level, with no row number, no link and no inline error. The type radios have no `id`, so nothing can be linked. (c) Only the first offending stage is reported, so several changes take several round trips. (d) The same rule has two wordings: removal says "Can't remove "X": ..." in place, but at Save it says "Move 3 opportunities out of "X" before deleting it." That uses "deleting" while the button says "Remove". | Spec 7.4: inline error linked to the field, summary links to the field. Spec 2: prevent errors rather than report them. | (a) For a stage with `Used > 0`, show the type as text plus a hidden input, for example "Won (locked: 40 opportunities)". Or keep the radios and add help text with the same wording. Extend the help line: "A stage in use can't be removed or change type until its opportunities are moved to another stage." (b) Return the error with `Field = "type{i}"`. Give the first radio `id="f-type{i}"` and add `aria-describedby` on the `fieldset`. The existing `Summary` then prefixes "Row N:" and links to the field. (c) Collect every offending row. (d) Reuse the in-place wording at Save: "Can't remove "X": 3 opportunities use it. Move them to another stage first." |
| M4 | Medium | Waking up the database, POST variant (`Pages/Errors/Error.cshtml:15`) | The button reads "Go back" but links to `/`, the dashboard. The user who just pressed Save on a form is sent away from it. Their input survives only if they find the browser Back button themselves. (Node used `javascript:history.back()`, which the CSP `script-src 'self'` blocks, so neither version works. In Node it was a dead link; in .NET the label doesn't match where it goes.) | Spec 3.10: "Your changes weren't saved. Go back to the form to try again." with [Go back] returning to the form, where the browser usually restores the field values. | In `wake.js`, attach `click → history.back()` to `#wake-back` (allowed by the CSP because it comes from a script file). Without JS, set `href` to the same-origin path of the form page: the POST path's GET equivalent, or `Referer` if it is same-origin and passed through `ReturnTo.Safe`. If neither is available, label the button "Go to dashboard". |
| L1 | Low | Test sign-in, wrong code (`Pages/Auth/TestLogin.cshtml`) | The error "That access code is not correct." is a `role="alert"` paragraph. The `#code` field has no `aria-invalid` or `aria-describedby`, and focus stays on `body` (measured). | Spec 7.4 inline error pattern. | Give the error an `id="e-code"`, add `aria-invalid="true" aria-describedby="e-code"` on `#code`, and add `data-autofocus` on the field. Same as Node, but this is the first screen every tester sees. |
| L2 | Low | Test user picker | The hint "Run `dotnet Crm.Web.dll seed-test` first." is developer copy on a page testers see. | Plain language (spec 4). | "No test users yet. Ask the developer to load the test users." Keep the command in the log. |
| L3 | Low | Sign-in after the session is ended | (a) `reason=disabled` (user deactivated) shows the normal sign-in page with no explanation. (b) Idle timeout (ticket expiry) gives no `reason`, so the user sees no "Your session has ended" message. (c) The expired message does not say that unsaved input is lost (spec 3.9). | Spec 3.9 session expired copy. | (a) Show the no-access copy for `disabled`. (b) If feasible, set `reason=expired` when a session cookie was present but the ticket is gone. (c) "Your session has ended. Sign in again to continue. Anything you hadn't saved was lost." |
| L4 | Low | Waking up page, give-up state (`wwwroot/js/wake.js`) | After 90 s the alert is shown, but focus stays where it was. The action still reads "Try again now". The `Retry-After` value is 15 s; the spec says 5. | Spec 3.10: focus moves to the alert text; action [Try again]. | Add `giveUp.tabIndex = -1; giveUp.focus();` and change the button text to "Try again". Align `Retry-After` with the spec, or update the spec to 15 s (either is fine; the JS polls every 5 s regardless). |
| L5 | Low | Pipeline stages: unsaved note | "Changes are not saved yet. Choose Save stages to keep them." is muted grey text. It is easy to miss, and leaving the page loses the edits. | Warnings in warning style (spec 5). | Use `flash-warn` with the ⚠ icon. Keep `role="status"`. |
| L6 | Low | Picklists: failed rename (`Picklists.cshtml.cs:80`) | When a rename fails (empty or duplicate), the attempted text is put into the **"New value" add field** (`LabelValue`). The row's own field goes back to the old label. A user who then presses "Add value" gets the same error again. This is the same as Node. | Spec 7.4: input is kept where it was typed. | Keep `LabelValue` for `action == "add"` only. For rename, render the posted text in `#f-label{id}` for the failed row. |
| L7 | Low | Analytics charts (`wwwroot/js/charts.js`) | The percent axis always adds " %" ("34 %"), while en-GB tiles and tables show "34%". | Spec 6 percent per locale. | Use `Intl.NumberFormat(locale, { style: 'percent' })` on `v / 100`. |

Severity counts: High 0 · Medium 4 · Low 7.

## Carried over from the Node reviews (still open in both builds, not new)
These are at parity, so they are not counted above. Fix them when convenient.
- **inc-4 L1:** the column header "Deals" in "Won revenue by month" (`Charts.cs`), and the intro text "at each deal's FX rate" / "left out" in `Analytics.cshtml`. Spec 4 allows "deal" only in the KPI context line.
- **inc-4 L4:** Win rate with no closed deals repeats "No closed deals yet" in the hidden full value (`Dashboard/Index.cshtml.cs`).
- **inc-4 L6:** the three "View all" links on the dashboard share one name.
- **inc-4 L8:** x-axis labels are clipped at 390 px.
- **inc-4 L10c:** "Close dates from 01.10.2026 – 31.12.2026" ("from" is redundant).
- **inc-4 L12:** `.charts { align-items: start; }`.
- **inc-4 L13:**
  - (a) Help texts are not linked with `aria-describedby`: Add team group ID, Locale base currency, and the stage help.
  - (b) Months show as "10.2026" in FX messages (`Fx.cshtml.cs:52`) and ignore the locale.
  - (c) The FX edit page does not focus the form.
- **Other spec deviations:**
  - The 500 page has no [Try again] action.
  - Team name max length is 80; the spec says 100.
  - The connection info line on Users & teams is developer copy.

## Accessibility requirements for the fixes
- Enter in any stage field submits "Save stages". The hidden default button is not tabbable, not announced, and does not change the visible order (M1).
- Page-level save refusals in the stage editor are announced (`role="alert"`) and focused. Row-level refusals link to a real field id and are prefixed "Row N:" (M2, M3).
- Locked stage types are conveyed in text, not only by disabled styling (M3).
- On the waking-up page, "Go back" goes back to the form; the give-up alert takes focus (M4, L4).

## Decisions needing product-owner sign-off
1. **Type lock on in-use stages (M3).** The build blocks changing Open/Won/Lost for any stage that opportunities use. This protects reported revenue, but it means the Won stage can never be swapped once it has deals. **Recommendation:** accept the lock and make it visible up front (M3a). Revisit together with the open bulk "Move N opportunities to [stage]" decision from increment 3.
2. **Stale-save handling (M2).** The minimum is a clear message plus a link that reloads the list. **Recommendation:** the minimum now; showing the user's unsaved edits next to the fresh list is optional.

## Re-test after fixes
- M1: Enter in `#f-name0`, `#f-name1` and `#f-prob1` posts `action=save`. Tab order is unchanged and axe is clean on `/settings/stages`.
- M2: open the stages page in two tabs, save in one, save in the other. Expect the single message with a working "Load the current stage list" link, focused.
- M3: change the type of an in-use stage. Expect it to be shown as locked before Save, or, if forced, an inline row error linked from the summary.
- M4: force the waking state on a POST. "Go back" returns to the form (with JS), and the no-JS label matches where the link goes.
- Run axe on every Settings page and on Dashboard and Analytics for Admin, Manager and User at 390, 1024 and 1280 px against a seeded SQL Server. This review could not run them.
