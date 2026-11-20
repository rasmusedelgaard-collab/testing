# Visual language v2 (soft blue, iteration 1: Dashboard and app shell)

Replaces the DAFA design-system look of `ui-redesign.md` for the CSS. Layout, routes, copy and RLS are unchanged. The DAFA logo is kept (recoloured copies in `wwwroot/img/brand`: `dafa-logo-softblue.svg`, `dafa-rings-softblue.svg`; originals untouched; white logo on dark). An Admin-uploaded logo still overrides it.

Rules: system font stack only (no font files, no web fonts, no Apple marks or artwork), no inline style or script, tokens first in `wwwroot/css/app.css`, no colour literals after `/* end of tokens */`. Enforced by `Architecture/DesignSystemTests.cs`.

## Tokens (Light, then Dark)
| Token | Light | Dark |
|---|---|---|
| `--page` / `--surface` / `--surface-panel` | #F5F7FA / #FFFFFF / #E9EDF3 | #1C1C1E / #2C2C2E / #3A3A3C |
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
