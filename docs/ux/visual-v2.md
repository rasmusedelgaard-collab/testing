# Visual language v2 (soft blue, iteration 1: Dashboard and app shell)

Replaces the DAFA design-system look of `ui-redesign.md` for the CSS. Layout, routes, copy and RLS are unchanged. The DAFA logo is kept (recoloured copies in `wwwroot/img/brand`: `dafa-logo-softblue.svg`, `dafa-rings-softblue.svg`; originals untouched; white logo on dark). An Admin-uploaded logo still overrides it.

Rules: system font stack only (no font files, no web fonts, no Apple marks or artwork), no inline style or script, tokens first in `wwwroot/css/app.css`, no colour literals after `/* end of tokens */`. Enforced by `Architecture/DesignSystemTests.cs`.

## Tokens (Light, then Dark)
| Token | Light | Dark |
|---|---|---|
| `--page` / `--surface` / `--surface-panel` | #F5F7FA / #FFFFFF / #E9EDF3 | #1C1C1E / #2C2C2E / #3A3A3C |
| `--column-bg` (pipeline board columns) | #EDF1F7 | #242426 |
| `--tint` (selected, hover) | #EAF2FD | #1E3350 |
| `--ink` / `--ink-muted` | #1D1D1F / #5C6270 | #F5F5F7 / #AEAEB8 |
| `--action` (buttons, links; Admin accent) | #1A66CC (hover #1450A3) | #6AA9FF (on-action #0A1D38) |
| `--accent-soft` (sparkline, bars, fills only) | #4A90E2 / #5B9BF0 | #5B9BF0 |
| `--border-input` | #7C8798 (3:1) | #8E96A8 |
| `--danger` | #B3261E | #FF8A80 |
| Chips and avatars (bg / text) | blue #EAF2FD/#1A5FC4, green #E3F6EA/#1B6B3A, orange #FFF0DB/#8A4B00, red #FDE6E8/#A3202D, purple #EFE8FB/#5B35A3, teal #DDF4F1/#0F6B63, gray #ECEFF4/#3A3F4A | dark equivalents in the CSS |
| `--chart-1..6` | #3D85E0, #C97510, #1F9E90, #8A5CD0, #D9475A, #2E9E55 | #5B9BF0, #F0A04B, #3CC7B6, #B591F0, #F07178, #5FCB7F |

Type: 34px/700 page titles (28px on phones), 15px body, 14px tables, 13px captions, tabular numbers on money. Shape: cards 16px, board cards 14px, buttons and inputs 10px, chips fully rounded. Depth: `--shadow-1` 0 1px 2px / 0 4px 16px, `--shadow-2` for hover and menus. Frosted sidebar and top bar use `backdrop-filter` inside `@supports`, solid `--surface` otherwise. Motion: `--duration` 180ms, 0ms under `prefers-reduced-motion`.

The default Admin accent is #1A66CC, the AA-safe shade of the soft blue (#4A90E2 itself is 3.3:1 on white). The accent rule: at least 4.5:1 on white and on `--tint`. The accent applies to Light only.

## Component patterns (round 4 rollout)
The approved Dashboard look applied to every page. Screenshots: `screenshots/v2/rollout-*.png` (regenerate with `render.sh rollout rollout`). Routes, copy, RLS and the one-RunAsync-per-request rule are unchanged.

- **Page header.** Every `h1` is the iOS large title (34px/700, tight tracking, 28px on phones). Record pages add the key chips (stage chip with mini 4-bar progress, account, owner avatar) on the line below. Links in headers, tables and detail lists are quiet (no underline, underline on hover); panel links use the chevron style (`.more-link`).
- **Pipeline board.** Columns are rounded 16px on `--column-bg`. Column header: name and probability chip, then total (compact), weighted at the right, count underneath, and a 6px share bar. Cards (14px, hairline border, `--shadow-1`): name (2-line clamp, title), account (ellipsis, title), compact amount (full amount in the title), close-date chip (orange with a warning icon and a hidden "Past close date" when late), owner avatar. The move control is a pill select and a `Move` button on one row; its label is visually hidden. Drag and drop is unchanged. On phones the columns stack and a pill row (`.stage-jump`) jumps between them.
- **Record pages.** Stage track is one card with four pill segments (passed `--stage-passed`, current `--stage-current`). Key facts are a card of tiles on wide screens and an iOS grouped list on phones. Details is an iOS grouped list: label left, value right, hairline between rows. Tabs are the segmented control (full width on phones). The timeline keeps the coloured icon dots on a hairline spine, inside a card. A table inside a tab card has no second border.
- **List pages.** Filter bar: a pill search field with a magnifier and pill filters that carry their label inside (`Owner  All`). The table is a card with hairline rows and a sticky header (up to 1439px wide the card scrolls sideways instead of clipping, and the sticky header then stays with the card). Name cells are cut at 26ch and key text cells at 20ch with an ellipsis and a title. Pagination is pill buttons around a pill "Page x of y". At 390px each row is its own card (name, two key fields, chips and owner, then the action); the filters collapse behind a pill.
- **Forms and confirm pages.** Each `.form-group` is a grouped card with an uppercase section caption (iOS settings). Inputs and selects have 10px radius and the 3:1 input border. A field error is a red pastel pill with an icon under the label, the input gets a 2px danger border, and the error summary links to the fields. Confirm pages are one card under the breadcrumb.
- **Analytics.** The period is a segmented control made of radio inputs (works without JS; typing a date selects Custom). Owner and the dates are pill filters. Each chart is a white card with a 20px title and the takeaway line; the data table stays under it. Charts use `--chart-1` (soft blue) for single series.
- **Search and command bar.** Results are one card per group with hairline rows (icon, name, stage chip, detail, owner). The command list under the top-bar search is frosted (`backdrop-filter` inside `@supports`, solid otherwise) with 10px option rows.
- **Settings.** The settings navigation is an iOS grouped list with chevrons from 1280px, a scrolling pill row below that. Forms and previews are grouped cards; tables are card tables like the lists.
- **Sign-in and test user picker.** A centred 440px card on the soft-blue gradient with the recoloured logo (softblue on light, white on dark; an Admin logo replaces it). The user picker is an inset list: avatar, name, role and team, radio at the right, hairline inset to the text.
- **Error, 404, no-access and waking pages.** A centred card with the icon ring, title, message, reference and buttons. No-access and signed-out use the sign-in card.
- **Fixes found while rolling out.** `td.rec-link` replaces `td.record` (the record-page grid class made table name cells grid containers and misaligned rows). `data-autofocus` is now written only on the element that should take focus; the old `data-autofocus="@(cond ? \"\" : null)\"` form was rendered on every board card, so each load focused the first card. Same fix on the stages, picklists and test sign-in pages.
