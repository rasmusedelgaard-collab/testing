# CRM UI redesign: DAFA design system (v1.0)

Status: v1.0, draft for product-owner sign-off (section 12) · Owner: UX · Date: 19.11.2026
Applies to: `dotnet/src/Crm.Web` (Razor Pages, `wwwroot/css`, `wwwroot/js`).
Inputs: DAFA design system (`docs/ux/design-system/project/README.md`, `tokens.json`, `assets/`), UI spec v0.3 (`docs/ux/ui-spec.md`), UX review of the .NET build (`docs/ux/review-dotnet.md`), the product owner's brief ("needs a big UI/UX overhaul, it's looking very boring").

## How this document relates to ui-spec.md
This document is the **binding visual and interaction spec**. It replaces these parts of `ui-spec.md` v0.3:

| ui-spec.md v0.3 | Status |
|---|---|
| 1 Navigation and layout | **Replaced** by section 3 here |
| 2 Shared patterns: the ASCII layouts and visual details | **Replaced** by sections 4 and 5. The rules in section 2 stay (owner field, delete rights, edit rights, error summary behaviour, Last modified line). |
| 3.1–3.10 Pages: layout and visual parts | **Replaced** by section 5. Every rule, field, validation message and copy string in 3.1–3.10 stays exactly as written. |
| 4 UX copy rules | **Unchanged**. New strings for new views are in section 9. |
| 5 Design tokens | **Replaced** by section 2 |
| 6 Formatting | **Unchanged** (da-DK, en-GB, en-US) |
| 7 Accessibility | **Unchanged and extended** by section 7 |
| 8 Acceptance criteria, 9 Decisions | **Unchanged**. New criteria are in section 10. |

Nothing here changes a business rule, a role or RLS behaviour, a validation rule or the meaning of any copy. Where the redesign needs a backend change, it is marked **[B-n]** and listed in section 8.

### Users and the goal of the redesign
| Role | What the redesign must do for them |
|---|---|
| User (sales, ~daily) | See today's work first, log and close activities fast, move opportunities through the pipeline without opening a form |
| Manager | See team pipeline by stage at a glance (board and funnel), spot what is stuck or overdue |
| Admin | Same as Manager, plus calm, predictable Settings pages |
| Group CFO (product owner, decision-maker) | A dashboard that leads with the numbers that matter, with trend vs the previous period, that looks like DAFA |

Business goal (product owner): the CRM should look modern and on-brand, so people want to use it, without losing any function. Success measures (to agree with the product owner): (S1) a first-time user finds the pipeline board and moves an opportunity in under 30 s; (S2) logging an activity still takes 30 s or less; (S3) zero serious or critical axe issues in both themes; (S4) the product owner and 3–5 sales users rate the new UI clearly better than the current one in a short preference test.

Assumptions to validate with real users (add to A1–A6 in ui-spec 0):
- **A7** Sales users start their day from "what is due today", so "My day" belongs on the dashboard above the fold.
- **A8** A board view is used more than the opportunities list for daily pipeline work. Check with 3–5 users after release (count page views of `/pipeline` vs `/opportunities`).
- **A9** Dark theme is a "nice to have". Check usage before investing in more dark-only polish.
- **A10** Industry picklist values map cleanly to DAFA's three segments (wind energy, building materials, industry). Check with sales leads.

---

## 1. Design principles
1. **One colour does the work.** The page is monochrome: ink on white (or white on corporate gray). The `action` colour (logo blue in Light, white in Dark) marks everything you can act on, and nothing else. Segment colours, ochre and mint appear only inside data: charts, sparklines and the segment marker of an account.
2. **Lead with what matters.** Every page opens with the one thing its user came for: today's work and the money on the dashboard, the stage and value on an opportunity, the totals per stage on the board. Detail comes after.
3. **Calm density.** Tables and boards show a lot, but on quiet grounds, with one rule weight (1 px hairline), square corners and no shadows. Space and ground colour separate areas, not boxes and borders.
4. **Flat fields, not floating cards.** Following the guide, sections are set apart by changing the ground (white, `surface-subtle`, `surface-panel`), never by shadows or gradients. Only buttons and inputs have rounded corners (8 px).
5. **Works without JavaScript, better with it.** Every action is a link or a form that POSTs. JavaScript adds comfort only: sidebar collapse, command bar shortcut, drag-and-drop on the board, charts. Nothing breaks without it.
6. **Text carries meaning, colour supports it.** Status, stage, overdue, won/lost and trends are always written in words. Icons and colour markers repeat the meaning, they never carry it alone.
7. **Same words, same places.** Copy rules from ui-spec 4 apply. Page header, primary action, flash and filters are in the same place on every page.
8. **Brand rules are not decoration.** Bold headlines, Avenir Next, the logo with clear space, the rings as the only ornament, line icons in circles, no emoji, sentence case.

---

## 2. Design tokens

### 2.1 Where tokens come from
- **From `tokens.json`, unchanged:** the brand palette, `surface`, `surface-panel`, `ink`, `on-dark`, `hairline`, `action`, `on-action`, the font stacks, the type scale, the spacing scale, the radii.
- **Provisional (digital), added here:** tokens the guide does not define but an application needs. Each one is marked **P** in the tables, derived from the palette where possible, and listed for sign-off in section 12 (decision D2). If a provisional token is refused, the fallback in the table applies.

### 2.2 Brand palette (same values in both themes)
CSS names use the `--dafa-` prefix. Never use these directly in components; use the semantic tokens in 2.3. They exist so the semantic tokens and charts can reference them.

| CSS variable | Value | tokens.json |
|---|---|---|
| `--dafa-white` | `#ffffff` | white |
| `--dafa-gray-100` | `#505152` | logo-gray-100 |
| `--dafa-gray-75` | `#7f8181` | logo-gray-75 |
| `--dafa-gray-50` | `#abadae` | logo-gray-50 |
| `--dafa-gray-25` | `#d6d7d7` | logo-gray-25 |
| `--dafa-blue` | `#00456c` | logo-blue |
| `--dafa-wind-100` / `-75` / `-50` / `-25` | `#51657f` / `#7b859b` / `#a5aaba` / `#d1d2db` | wind-* |
| `--dafa-building-100` / `-75` / `-50` / `-25` | `#8e5550` / `#a77973` / `#c2a29d` / `#dfcfcb` | building-* |
| `--dafa-industry-100` / `-75` / `-50` / `-25` | `#4c6c6f` / `#789193` / `#a4b6b8` / `#d2dbdc` | industry-* |
| `--dafa-ochre` | `#d2a225` | ochre |
| `--dafa-mint` | `#54bbab` | mint |

### 2.3 Semantic colour tokens (Light / Dark)
Contrast figures were computed with the WCAG 2.x formula (scratch script, values rounded down). Normal text needs 4.5:1, large text and UI parts 3:1.

| CSS variable | Light | Dark | Source | Use | Contrast (checked) |
|---|---|---|---|---|---|
| `--surface` | `#ffffff` | `#505152` | surface | Page ground (main content), inputs in Light, cards on a panel | — |
| `--surface-subtle` **P** | `#eeeeee` | `#48494a` | 10 % tint of logo gray (Light); between surface and panel (Dark) | Sidebar ground, table row hover, quiet bands | ink 6.8:1 (L) · white 9.0:1 (D) |
| `--surface-panel` | `#d6d7d7` | `#434445` | surface-panel | Panels and cards set off from surface: KPI cards, key facts, My day, board columns, table header band, filter bar | ink 5.5:1 (L) · white 9.7:1 (D) |
| `--ink` | `#505152` | `#ffffff` | ink | All text and line icons | 7.9:1 on surface (both) |
| `--ink-muted` **P** | `#505152` | `#d6d7d7` | Light: same as ink (no lighter gray reaches 4.5:1 on the panel). Dark: logo-gray-25 | Secondary text (meta lines, help). In Light, hierarchy comes from size, not a paler gray. | 5.5:1 on surface, 6.7:1 on panel (D) |
| `--on-dark` | `#ffffff` | `#ffffff` | on-dark | Text and icons on fixed brand fills (logo blue, gray-100, segment-100) | ≥5.7:1 |
| `--hairline` | `#d6d7d7` | `#7f8181` | hairline | Decorative 1 px rules only. Never the only boundary of a control. | decorative |
| `--border-input` **P** | `#505152` | `#d6d7d7` | ink (L), logo-gray-25 (D) | Borders of inputs, selects, checkboxes, secondary buttons' neutral variant, menus | 7.9 / 5.5:1 (L on surface / panel) · 5.5 / 6.7:1 (D) |
| `--action` | `#00456c` | `#ffffff` | action. **Admin accent overrides Light only** (2.7) | Links, primary button fill, focus ring, current-nav bar, selected tab bar | 10.1 surface · 8.7 subtle · 7.0 panel (L) · ≥7.9 (D) |
| `--action-hover` **P** | `#003b5c` | `#d6d7d7` | action darkened 15 % (same rule as today's `Color.Darken`) / logo-gray-25 | Hover and pressed fill of primary buttons | on-action 11.8 (L) · 5.5 (D) |
| `--on-action` | `#ffffff` | `#505152` | on-action | Label on an action fill | 10.1 (L) · 7.9 (D) |
| `--focus` | `var(--action)` | `var(--action)` | action | 2 px focus ring | as action |
| `--danger` **P** | `#a4262c` | `#ffc2bb` | not in the guide | Error text, error borders, destructive buttons, overdue count | 7.2 surface · 6.2 subtle · 5.0 panel (L) · 5.8 subtle · 5.1 surface · 5.8 panel (D) |
| `--on-danger` **P** | `#ffffff` | `#505152` | — | Label on a danger fill | 7.2 (L) · 5.1 (D) |
| `--signal-warning` | `#d2a225` | `#d2a225` | ochre | 4 px bar and icon ring of warning alerts and badges. **Never text.** | decorative; meaning is in the text |
| `--signal-success` | `#54bbab` | `#54bbab` | mint | 4 px bar and icon ring of success alerts, "Won" marker. **Never text.** | decorative |
| `--signal-info` | `var(--action)` | `var(--action)` | action | Bar of info alerts | — |
| `--segment-wind` | `#51657f` | `#a5aaba` | wind-100 / wind-50 | Segment marker, segment series in charts | 3:1+ against surface in both themes |
| `--segment-building` | `#8e5550` | `#c2a29d` | building-100 / building-50 | as above | 3:1+ |
| `--segment-industry` | `#4c6c6f` | `#a4b6b8` | industry-100 / industry-50 | as above | 3:1+ |

Danger fallback if D2 refuses a red: errors in `--ink` with a 2 px ink border, the error icon and the word "Error:" before the message. This is accessible but users recognise red errors faster (heuristic: match real-world conventions), so the red is recommended.

Chart tokens are in section 6.

### 2.4 Typography
| CSS variable | Value | Use in the app |
|---|---|---|
| `--font-sans` | `"Avenir Next", "Avenir Next LT Pro", Arial, sans-serif` | Everything. No web font file is shipped (licence); installed Avenir Next is used where present, otherwise Arial. |
| `--weight-regular` / `--weight-bold` | `400` / `700` | Only these two weights. Replace every `600` in today's CSS with `700`. |
| `--text-display` | `56px / 1.07` | Not used in the app (marketing only) |
| `--text-headline` | `40px / 1.1` bold | Dashboard greeting at ≥1024; sign-in panel headline |
| `--text-title` | `28px / 1.14` bold | Page `h1`; KPI value |
| `--text-lead` | `21px / 1.19` | `h2` section titles (set **bold**), empty-state titles, intro lines |
| `--text-body` | `17px / 1.47` | Paragraphs, form inputs, detail values, record header meta |
| `--text-caption` | `14px / 1.43` | Tables, nav, buttons (bold), labels (bold), chips, help text, breadcrumbs, timeline meta |
| `--text-fine` | `12px / 1.33` | Non-essential only: footer copyright, keyboard hint "Ctrl K". Never for information a user needs. |

Rules: sentence case everywhere; headlines bold; `font-variant-numeric: tabular-nums` on `body`; numbers in tables right-aligned. Max line length for paragraphs 72ch. `h3` = 17px bold. At <640 the `h1` stays 28px and the greeting drops to 28px.

### 2.5 Spacing, size, shape, layering
| CSS variable | Value | Use |
|---|---|---|
| `--space-xxs` | `4px` | Icon-to-label gap |
| `--space-xs` | `8px` | Inside controls, chip padding, gap between buttons in a group |
| `--space-sm` | `12px` | Table cell horizontal padding, related lines |
| `--space-md` | `17px` | Form field gap, page side padding at <640 |
| `--space-lg` | `24px` | Panel padding, grid gutter, page side padding 640–1279 |
| `--space-xl` | `32px` | Block gaps, page side padding ≥1280 |
| `--space-xxl` | `48px` | Between the page header and a new major block (dashboard rows) |
| `--space-section` | `80px` | Sign-in and error pages only |
| `--radius-none` | `0` | Everything except the two below |
| `--radius-sm` | `8px` | Buttons, inputs, selects, textareas, the search field |
| `--radius-full` | `9999px` | Icon rings, avatars (an avatar is treated as an "icon in a circle", D5) |
| `--control-h` | `40px` (`44px` at <640 and `(pointer: coarse)`) | Buttons, inputs, selects |
| `--topbar-h` | `56px` | Top bar |
| `--sidebar-w` / `--sidebar-w-collapsed` | `240px` / `72px` | Sidebar |
| `--content-max` | `1360px` | Max width of `main` content |
| `--z-sticky` / `--z-topbar` / `--z-menu` / `--z-drawer` | `10` / `20` / `30` / `40` | Layering |
| `--duration` | `120ms` | Colour transitions only (hover). `0ms` under `prefers-reduced-motion`. |
| Shadows | **none** | Not even on menus. Menus get a 1 px `--border-input` border. |
| Gradients | **none** | — |

### 2.6 The CSS (binding names and values)
Put this at the top of `wwwroot/css/app.css` (rewritten; see section 11 for the order). The dark block is written twice: once for the system setting (unless the user chose Light) and once for a manual Dark choice. Keep the two blocks identical; a unit test or lint step should compare them.

```css
:root {
  /* brand palette (same in both themes) */
  --dafa-white:#ffffff; --dafa-gray-100:#505152; --dafa-gray-75:#7f8181; --dafa-gray-50:#abadae; --dafa-gray-25:#d6d7d7;
  --dafa-blue:#00456c;
  --dafa-wind-100:#51657f; --dafa-wind-75:#7b859b; --dafa-wind-50:#a5aaba; --dafa-wind-25:#d1d2db;
  --dafa-building-100:#8e5550; --dafa-building-75:#a77973; --dafa-building-50:#c2a29d; --dafa-building-25:#dfcfcb;
  --dafa-industry-100:#4c6c6f; --dafa-industry-75:#789193; --dafa-industry-50:#a4b6b8; --dafa-industry-25:#d2dbdc;
  --dafa-ochre:#d2a225; --dafa-mint:#54bbab;

  /* semantic: Light (default) */
  color-scheme: light;
  --surface:#ffffff; --surface-subtle:#eeeeee; --surface-panel:#d6d7d7;
  --ink:#505152; --ink-muted:#505152; --on-dark:#ffffff;
  --hairline:#d6d7d7; --border-input:#505152;
  --action:#00456c; --action-hover:#003b5c; --on-action:#ffffff; --focus:var(--action);
  --danger:#a4262c; --on-danger:#ffffff;
  --signal-warning:var(--dafa-ochre); --signal-success:var(--dafa-mint); --signal-info:var(--action);
  --segment-wind:var(--dafa-wind-100); --segment-building:var(--dafa-building-100); --segment-industry:var(--dafa-industry-100);
  --chart-1:var(--dafa-blue); --chart-2:var(--dafa-ochre); --chart-3:var(--dafa-mint); --chart-4:var(--dafa-building-100);
  --chart-grid:var(--hairline); --chart-axis:var(--ink); --chart-outline:var(--dafa-gray-100);
  --logo-light-display:inline; --logo-dark-display:none;

  /* type */
  --font-sans:"Avenir Next","Avenir Next LT Pro",Arial,sans-serif;
  --weight-regular:400; --weight-bold:700;
  --text-headline:40px; --lh-headline:1.1;
  --text-title:28px;    --lh-title:1.14;
  --text-lead:21px;     --lh-lead:1.19;
  --text-body:17px;     --lh-body:1.47;
  --text-caption:14px;  --lh-caption:1.43;
  --text-fine:12px;     --lh-fine:1.33;

  /* space, shape, size */
  --space-xxs:4px; --space-xs:8px; --space-sm:12px; --space-md:17px; --space-lg:24px; --space-xl:32px; --space-xxl:48px; --space-section:80px;
  --radius-none:0; --radius-sm:8px; --radius-full:9999px;
  --control-h:40px; --topbar-h:56px; --sidebar-w:240px; --sidebar-w-collapsed:72px; --content-max:1360px;
  --z-sticky:10; --z-topbar:20; --z-menu:30; --z-drawer:40;
  --duration:120ms;
}
@media (prefers-color-scheme: dark) {
  :root:not([data-theme="light"]) {
    color-scheme: dark;
    --surface:#505152; --surface-subtle:#48494a; --surface-panel:#434445;
    --ink:#ffffff; --ink-muted:#d6d7d7;
    --hairline:#7f8181; --border-input:#d6d7d7;
    --action:#ffffff; --action-hover:#d6d7d7; --on-action:#505152;
    --danger:#ffc2bb; --on-danger:#505152;
    --segment-wind:var(--dafa-wind-50); --segment-building:var(--dafa-building-50); --segment-industry:var(--dafa-industry-50);
    --chart-1:var(--dafa-ochre); --chart-2:var(--dafa-white); --chart-3:var(--dafa-mint); --chart-4:none;
    --chart-outline:transparent;
    --logo-light-display:none; --logo-dark-display:inline;
  }
}
:root[data-theme="dark"] { /* identical to the block above */ }
@media (max-width: 639px), (pointer: coarse) { :root { --control-h:44px; } }
@media (prefers-reduced-motion: reduce) { :root { --duration:0ms; } }
```

### 2.7 Theme switching, Admin accent and logos
- **System theme (no backend):** works through `prefers-color-scheme` alone.
- **Manual toggle [B-1] (cheap, recommended):** the user menu gets a "Theme" group with three submit buttons in one POST form: "Match system", "Light", "Dark". The server stores a `theme` cookie (`system|light|dark`, `SameSite=Lax`, `Secure`, `HttpOnly` not needed, 1 year, no personal data) and redirects back (`returnTo`, validated as today). `_Layout.cshtml` writes `data-theme="light|dark"` on `<html>` when the cookie is `light` or `dark`, and nothing for `system`. Works without JS; no flash of the wrong theme because the server renders the attribute.
- **Admin accent [B-2]:** `/css/theme.css` keeps its job but emits the new names, **for Light only**:
  `:root { --action: #XXXXXX; --action-hover: <Darken(#XXXXXX)>; }` and nothing for dark (Dark keeps white `action`, because no accent that passes in Light can reach 4.5:1 on the dark gray).
  Cascade order: `app.css` first, then `theme.css`. The Light override must not win over the dark blocks: emit it as `:root:not([data-theme="dark"]) { … }` inside `@media (prefers-color-scheme: light)`, **plus** `:root[data-theme="light"] { … }`. The developer verifies with a test that a dark page never gets the accent.
- **Accent validation [B-3]:** the accent is drawn on `surface` (white), `surface-subtle` (`#eeeeee`) and `surface-panel` (`#d6d7d7`). Change `Color.AccentContrast` to the minimum of white and `#D6D7D7` (today it checks white and `#F9FAFB`). Error copy stays: "This colour is too light for text and buttons. Choose a darker shade." Help text becomes: "Used for links and buttons in the Light theme. It must have a contrast of at least 4.5:1 against white and the light gray panels (#D6D7D7). The Dark theme always uses white." A saved accent that fails the new rule is ignored by `theme.css` (falls back to `#00456C`) and General settings shows the field error on load.
- **Default accent [B-12]:** the database default is `#1D4ED8` (old blue). Treat `#1D4ED8` as "not set" in `theme.css` (no override → DAFA blue), and change the seed default to `#00456C`. If the product owner prefers, a data migration changes the stored value from `#1D4ED8` to `#00456C` (needs PO approval as a production data change).
- **Logos:** copy `dafa-logo-primary-blue.svg`, `dafa-logo-negative.svg`, `dafa-rings-gray.svg`, `dafa-rings-white.svg` and the six segment icons into `wwwroot/img/brand/` unchanged (never redraw or recolour). The sidebar renders both the blue and the negative logo; CSS shows one: `.logo-light { display: var(--logo-light-display) }`, `.logo-dark { display: var(--logo-dark-display) }`. Both `<img>` have `alt=""`, the link has the accessible name "{CRM name}, go to dashboard".
- **Admin-uploaded logo:** if a logo is uploaded, it replaces the DAFA logo in both themes. In Dark it sits on a white plate (`background: #fff; padding: 8px`, square), because an uploaded PNG is usually made for white.

### 2.8 Mapping from today's CSS variables
So the developer can migrate page by page.

| Old (`app.css` today) | New |
|---|---|
| `--color-text` | `--ink` |
| `--color-text-muted` | `--ink-muted` |
| `--color-bg` | `--surface` (the page is now white; panels use `--surface-panel`) |
| `--color-surface` | `--surface` |
| `--color-border` | `--hairline` |
| `--color-border-input` | `--border-input` |
| `--color-primary` / `-hover` | `--action` / `--action-hover` |
| `--color-primary-subtle` | `--surface-subtle` (selection) |
| `--color-danger` | `--danger` |
| `--color-success*` | `--signal-success` (bar) + `--ink` (text) |
| `--color-warning-*` | `--signal-warning` (bar) + `--ink` (text) |
| `--color-nav-*` | removed: the sidebar uses `--surface-subtle`, `--ink`, `--action` |
| `--space-1..8` | `--space-xxs … --space-xxl` (4, 8, 12, 17, 24, 32, 48) |
| `--radius` (6px) | `--radius-sm` (8px) on buttons and inputs, `0` elsewhere |
| `--font` | `--font-sans` |

---

## 3. App shell

### 3.1 Structure (all signed-in pages)
```
<html data-theme?> <body>
  skip link "Skip to main content"
  [test banner, role=note]                         (only in test-access mode)
  <div class="app">                                 grid: sidebar | column
    <nav class="sidebar" aria-label="Main">          logo, primary nav, Settings, collapse toggle
    <div class="column">
      <header class="topbar">                       command bar, + New, user menu
      <main id="main" tabindex="-1">
        page header (breadcrumb, h1, meta, actions)
        flash region
        page content
      <footer class="app-footer">                   ©2026 DAFA A/S (fine print, ink-muted)
```
DOM order = tab order: skip link, sidebar, top bar, main. (Today the top bar comes first; moving the sidebar first keeps "nav, then tools, then content".)

### 3.2 Layout at 1440 (sidebar expanded)
```
+------------------+-----------------------------------------------------------------------------+
| [DAFA logo]      | [ (o) Search accounts, contacts, opportunities...   Ctrl K ]   [+ New v] (MH)| 56 sticky
|  CRM             +-----------------------------------------------------------------------------+
|                  |  Accounts / Nordvind Rotor A/S                         (breadcrumb, 14px)   |
| (o) Dashboard    |  Page title (28 bold)                         [Secondary] [Primary action]  |
| (o) Pipeline     |  meta line: chips · owner · key facts                                       |
| (o) Accounts     |  [flash region]                                                             |
|#(o) Opportunities|                                                                             |
| (o) Contacts     |  content, max 1360, padding 32                                              |
| (o) Activities   |                                                                             |
| (o) Analytics    |                                                                             |
|                  |                                                                             |
| ---------------- |                                                                             |
| (o) Settings*    |                                                                             |
| [<< Collapse]    |  ©2026 DAFA A/S                                                             |
+------------------+-----------------------------------------------------------------------------+
  240px, surface-subtle, 1px hairline right edge          * Admin only. # = current page
```

### 3.3 Layout at 1024 (rail)
```
+------+--------------------------------------------------------------------------+
| [o)) | [ (o) Search accounts, contacts, opportunities...  Ctrl K ] [+ New v] (MH)|
+------+--------------------------------------------------------------------------+
| (o)  |  Page title                                       [Secondary] [Primary]  |
| (o)  |                                                                          |
|#(o)  |  content, padding 24                                                     |
| (o)  |                                                                          |
| (o)  |                                                                          |
| --   |                                                                          |
| (o)  |                                                                          |
| [>>] |                                                                          |
+------+--------------------------------------------------------------------------+
  72px: rings mark instead of the logo, icons only. Label shown as a tooltip on hover and focus.
```

### 3.4 Layout at 390 (phone)
```
+--------------------------------------+
| [Menu] [o))]        [(o)] [+] (MH)   |  56 sticky: Menu, rings mark, search link, New, user
+--------------------------------------+
| Accounts / Nordvind…                 |
| Page title                           |
| [Primary action — full width]        |
| content, padding 17                  |
+--------------------------------------+
Menu opens the sidebar as a drawer over the page (full height, 280px, surface-subtle),
with a "Close menu" button first. Focus is trapped while open, Esc closes, focus returns to Menu.
```

### 3.5 Responsive rules
| Width | Sidebar | Top bar search | Page padding | Notes |
|---|---|---|---|---|
| ≥1280 | Expanded 240px. JS: user can collapse to 72px; choice kept in `localStorage` (`crm.sidebar`), applied by `boot.js` before paint | Field, max 560px | 32 | KPI grid 6 columns at ≥1440, 3 below |
| 1024–1279 | JS: collapsed rail by default (user can expand). No JS: expanded 240px | Field | 24 | |
| 640–1023 | JS: rail. No JS: expanded 200px | Field (shrinks) | 24 | Board scrolls horizontally |
| <640 | JS: hidden; drawer behind "Menu". No JS: nav shown as a list above the content (as today) | Icon link to `/search` | 17 | Tables become cards; filters behind "Filters" `<details>` |

Collapsed state is an enhancement: without JS there is no collapse button (it is created by `app.js`), so there is never a rail without visible labels.

### 3.6 Sidebar
- **Header:** logo link to `/`. Expanded: DAFA logo 112px wide (≈39px high), then the CRM name (`--text-caption`, bold) below it. Clear space: 24px on all sides of the logo, which covers the guide's "ring width / ring height" rule at this size. Collapsed: `dafa-rings-gray.svg` (Light) or `dafa-rings-white.svg` (Dark), 32px, the CRM name visually hidden. With an uploaded logo: the uploaded logo, max 160×48 (expanded) or 40×40 (collapsed), `object-fit: contain`.
- **Items** (in this order): Dashboard `/`, Pipeline `/pipeline` (new, section 5.6), Accounts, Opportunities, Contacts, Activities, Analytics; a 1px hairline; Settings (Admin only, hidden for others, as today). Order changes from today: Pipeline added; Opportunities moved above Contacts because it is used more (A8, to validate).
- **Item anatomy:** 44px high, padding 0 16, ringed icon 28px (4.6) + label `--text-caption`. Hover: ground `--surface-panel`. Current (`aria-current="page"`): ground `--surface`, label bold, 4px `--action` bar on the left. Focus: 2px `--focus` ring inset 2px.
- **Collapse toggle** (JS only, last item): button "Collapse sidebar" / "Expand sidebar", `aria-expanded`, `aria-controls="main-nav"`. Icon chevrons.
- **Rail tooltips:** the label stays in the DOM (visually hidden in rail mode) and is shown as a tooltip on `:hover` and `:focus-visible` to the right of the item: `--surface-panel` ground, ink text, 1px `--border-input` border, square. `app.js` hides it on Esc (WCAG 1.4.13). The link keeps its text label, so no `aria-label` is needed.
- **Drawer (<640, JS):** same content, 280px wide, full height, `--surface-subtle` ground with a 1px `--border-input` right edge (no scrim, no shadow). The rest of the page gets `inert` while open; a click outside or Esc closes it. No slide animation (instant open), so reduced motion needs no special case.

### 3.7 Top bar
- `--surface` ground, 1px `--hairline` bottom rule, sticky, height 56, padding 0 `--space-lg`.
- **Command bar / search (left, flex 1, max 560):** a GET form to `/search` (works without JS). Input 40px, radius 8, 1px `--border-input`, search icon inside on the left (decorative), placeholder "Search accounts, contacts, opportunities..." and a fine-print hint "Ctrl K" (⌘ K on macOS, chosen by JS; hidden without JS) at the right edge. Label "Search all records" (visually hidden, as today).
  - **JS enhancement (no new backend):** Ctrl+K / ⌘+K focuses the field from anywhere (a modifier shortcut, so it doesn't conflict with WCAG 2.1.4). While typing, a listbox under the field (combobox pattern, `aria-expanded`, `aria-activedescendant`) offers: first "Search all records for "{text}"" (Enter, default), then matching **commands**: "New account", "New contact", "New opportunity", "Log activity", "Go to Pipeline", "Go to Accounts", … "Go to Settings" (Admin only). Up/Down to move, Enter to go, Esc to close. Commands are a static list in `app.js` filtered by text.
  - **Optional later [B-11]:** live record results in the listbox need a JSON endpoint that reuses the existing list queries. Not in this release.
- **+ New (right):** `<details>` menu as today: "Account", "Contact", "Opportunity", "Activity". Summary is a secondary button "New" with a plus icon. Menu: `--surface`, 1px `--border-input`, square, items 40px.
- **User menu (far right):** avatar 36px as the `<summary>` (accessible name "{initials} – account menu for {name}", as today). Panel: name (bold), email, role chip, then **Theme** (B-1): a group "Theme" with three buttons "Match system", "Light", "Dark", the current one `aria-pressed="true"` and shown with a check icon; then "Sign out" (secondary button).
- **At <640:** "Menu" button (left, icon + text "Menu"), rings mark, search icon link to `/search` ("Search"), "+" New menu (accessible name "New"), avatar.

### 3.8 Page header pattern (every page)
```
Breadcrumb (detail, form, settings sub-pages only)        14px, ink, links underlined
[segment/type icon 48] H1 (28 bold)                [Quiet] [Secondary] [Primary]
Meta line: chips · avatar + owner · key fact · key fact      17px ink-muted
View switch or tabs (when the page has them)
```
- One `h1` per page. Actions are right-aligned on the same row from 640px; below 640 they wrap under the title, primary first and full width, secondary in a row.
- At most one primary button per header. Destructive actions never sit in the header row directly; they live in a "More" menu (record pages) or at the end of the form.
- Breadcrumb: `<nav aria-label="Breadcrumb">` with an `<ol>`; separator "/" drawn with CSS (`aria-hidden`); last item `aria-current="page"`, not a link. Long names truncate at 40ch with an ellipsis and the full name in the `h1`.
- Flash region sits directly under the header (section 4.13).
- Page `<title>`: unchanged ("{Page} · {Record name} – {CRM name}").

---

## 4. Components
Class names below are binding. Where today's class works, it is kept so tests and partials don't break.

### 4.1 Buttons (`.button`)
| Variant | Class | Light | Dark |
|---|---|---|---|
| Primary | `.button.primary` | `--action` fill, `--on-action` label | white fill, gray-100 label |
| Secondary | `.button.secondary` | `--surface` ground, 1px `--action` border, `--action` label | same with white |
| Quiet (was "link") | `.button.link` / `.button.quiet` | no ground or border, `--action` label, underlined | same |
| Danger | `.button.danger` | `--danger` fill, `--on-danger` label | `#ffc2bb` fill, gray-100 label |
| Danger secondary | `.button.danger.secondary` | 1px `--danger` border, `--danger` label | same |
| Icon | `.button.icon` | 40×40 (44 at touch), ringed icon, no label; **must** have `aria-label` | same |

- Size: height `--control-h`, padding 0 16, radius 8, `--text-caption` bold, icon 16px + 4px gap (optional, left of the label).
- States: **hover** primary → `--action-hover`; secondary/quiet → ground `--surface-subtle`. **Focus-visible** 2px `--focus` ring, 2px offset. **Active** same as hover. **Disabled** avoid; where unavoidable: `--surface-panel` ground, `--ink` label, no border, plus `aria-disabled="true"` and a visible reason next to it. **Busy** (form submit): label becomes "Saving..." (existing behaviour) and the button gets `aria-disabled="true"`.
- Order in groups: primary last on the right on desktop (`[Cancel] [Save]` reads as quiet → primary); on <640 the primary comes first and is full width. Form footers: `[Save]` primary + `Cancel` quiet link (as today).

### 4.2 Inputs, textareas, selects (`.field`)
```
Label (14 bold) (required)
Help text (14, ink-muted)                 id="h-x"
(!) Error message (14 bold, danger)       id="e-x"   only when invalid
[ input 40px, radius 8, 1px border-input ]
```
- Ground `--surface` in Light and `--surface-panel` in Dark (so the field reads as a field on the dark page). Text `--text-body` ink. Placeholder only for format examples, ink-muted; never as a label.
- **Hover:** border 1px → `--ink`. **Focus:** border `--action` plus 2px `--focus` outline at 0 offset (so the ring hugs the field). **Invalid** (`aria-invalid="true"`): 2px `--danger` border, error text above the input with the error icon. **Read-only values** are shown as text, not a disabled input (e.g. Probability). **Disabled:** avoid.
- Select: `appearance: none`, chevron from `/img/ui/chevron-down-{light|dark}.svg` as a background image (allowed by `img-src 'self'`), right padding 40.
- Checkbox and radio: native, 20×20, `accent-color: var(--action)`, label to the right, whole row 44px clickable. Radio groups in a `fieldset` (no border, legend 14 bold).
- Field width: by expected content, not 100 % (amount 16ch, postcode 10ch, date 18ch). Pairs share a row at ≥640: First/Last name, Amount/Currency, Due date/Due time, Postcode/City.
- Error summary (`.error-summary`): `--surface-panel` ground, 4px `--danger` left bar, ringed error icon, title "Fix 2 problems to save" (21 bold, `--danger`), list of links. Behaviour unchanged (focus, links to fields).

### 4.3 Tables (`table.data`)
- Wrapper `.table-wrap`. Dense but calm: row height 44 (40 for `table.data.dense` on dashboard and record tabs), cell padding 8 12, `--text-caption`, ink.
- **Header:** `--surface-panel` band, 14 bold, sticky: `position: sticky; top: var(--topbar-h); z-index: var(--z-sticky)`. Sticky only works if no ancestor scrolls, so `.table-wrap` has `overflow: visible` at ≥1024 (tables are designed to fit: long text wraps, no fixed widths) and `overflow-x: auto` with `tabindex="0"`, `role="region"` and an `aria-label` = caption at 640–1023 (header not sticky there). At <640 tables become cards (as today).
- **Rows:** 1px `--hairline` bottom rule; hover ground `--surface-subtle`. No zebra stripes.
- **First column** is the record link: `--action`, bold, underlined on hover and focus. Bold is the non-colour cue (in Dark, action and ink are both white).
- **Owner column:** avatar 24 + name (4.8).
- **Stage column:** stage chip (4.4). **Status, overdue, past close date:** badges (4.4).
- **Numbers:** right-aligned, tabular; the header of a number column is right-aligned too.
- **Sorting:** header link with `aria-sort` (as today) plus a sort icon (up/down/both, decorative).
- **Inline quick actions (last column, no header text; `<th>` contains visually hidden "Actions"):**
  - Activities: "Mark done" ringed-check icon button (POST form as today; the visible ☐/☑ characters are removed), accessible name "Mark 'Call Nordvind' as done".
  - All entities: "Edit" icon link, accessible name "Edit {record name}".
  - Always visible (not hover-only), so they work for touch and keyboard. Icons only at ≥640, text links in the card view <640.
- **Card view <640:** each row is a block on `--surface` with a 1px `--hairline` bottom rule (not a box): line 1 the link (bold), line 2 two key fields, line 3 chips/badges and owner; actions as text buttons at the end.
- Row count announcement stays (`role="status"`, "128 opportunities found").

### 4.4 Chips and badges (`.chip`)
All square (radius 0), height 24, padding 0 8, `--text-caption`, always text.

| Chip | Look | Example |
|---|---|---|
| Neutral `.chip` | `--surface-panel` ground (or `--surface` when on a panel), ink | Role: Admin · Lead source |
| Stage `.chip.stage` | neutral + **progress pips**: N small 6×6 squares, filled ink for passed and current open stages, outlined for later ones (decorative, `aria-hidden`) | `▪▪▪▫ Proposal` |
| Won `.chip.won` | neutral + 8×8 mint square marker | `■ Won` |
| Lost `.chip.lost` | neutral + 8×8 outlined square | `□ Lost` |
| Warning `.chip.warn` | neutral ground, 3px `--signal-warning` left bar, warning icon | Past close date · Uses latest rate |
| Danger `.chip.danger` | neutral ground, `--danger` text bold, error icon | 2 overdue |
| Segment `.chip.segment` | neutral + 8×8 square in `--segment-*` | `■ Wind energy` |
| Team status | as warning | No team — sees own records only |

The pips and markers are drawn with CSS (pseudo-elements or `<span>`s), never with Unicode characters.

### 4.5 Icons
- One small **bespoke line-icon set** in the guide's style (D4): 24-unit grid, 1.5px stroke, round caps and joins, no fills, `stroke="currentColor"`. One sprite file `/img/ui/icons.svg` with `<symbol id="i-…">`; used as `<svg class="icon" aria-hidden="true"><use href="/img/ui/icons.svg#i-call"/></svg>`. Same-origin, no inline style, so the CSP is fine.
- **Ringed** (`.icon-ring`): the glyph centred in a 1.5px circle (`--radius-full`). Sizes 28 (nav, timeline), 40 (record header, alerts), 56 (empty states). Used wherever an icon stands on its own.
- **Bare glyph** only inside a control next to text, or for control glyphs (chevrons, close, sort arrows, search).
- Set (ids): dashboard, pipeline, accounts, opportunities, contacts, activities, analytics, settings, search, plus, edit, delete, more, chevron-left, chevron-right, chevron-down, menu, close, call, meeting, task, note, change (history), check, warning, error, info, success, calendar, clock, trend-up, trend-down, trend-flat, filter, sort, sort-asc, sort-desc, drag, theme, signout, external.
- **Segment icons:** the six supplied files (`icon-*-gray.svg` for Light, `icon-*-white.svg` for Dark), never redrawn. They are already ringed.
- Replace every `⚠`, `☐`, `☑` and other Unicode symbol in today's views with icons (the review found ⚠ in errors, tiles and FX notes; some systems render it as emoji).

### 4.6 Cards and panels (`.panel`)
- `.panel`: `--surface-panel` ground, padding 24, square, no border, no shadow. Title `h2` 21 bold, optional right-aligned link ("View all …") on the title row.
- `.panel.plain`: `--surface` ground with a 1px `--hairline` frame. Used for charts (section 6) and wherever content needs white behind it.
- Panels never nest a panel of the same ground; inside a panel, rows and cards use `--surface`.

### 4.7 KPI card (`.kpi`)
```
+--------------------------------+
| Won this month           (link)|  label 14, ink
| 2,4 mio. kr.                   |  value 28 bold, nowrap, ink
| 5 deals                        |  context 14, ink-muted
| [^] Up 12% vs same days last m.|  trend 14 with ringed trend icon      [B-6]
| ‾‾\_/‾‾‾\__/‾‾‾                |  sparkline 100% x 32, last 12 months   [B-6]
| (!) Uses latest rate for 1 …   |  FX note (warning chip style), as today
+--------------------------------+
```
- `--surface-panel` ground, padding 17 24, square. The whole card is one link (as today) with the same accessible name and `aria-describedby` to FX notes (as today). Hover: ground `--surface-subtle`, value underlined. Focus: 2px ring around the card.
- Value colour is **ink**, not blue (today it is primary blue). The card is a link; the hover/focus states and the "View" affordance come from the ring and underline.
- **Trend line [B-6]:** "Up 12% vs same days last month" / "Down 8% vs same days last quarter" / equal: "No change vs same days last month" / previous period zero: "None in the same days last month". Win rate: "Up 4 pts vs previous 90 days". Percent formatted per locale. The exact comparison range (e.g. 01.10.2026–19.10.2026) is in the card's `title` and in a visually hidden span, so the visible line stays short. Icon trend-up/down/flat in ink; **no green/red** (monochrome; up isn't always good). Screen readers get the sentence as written.
  - Comparison periods: month-to-date vs the same days of the previous month; quarter-to-date vs the same days of the previous quarter; win rate last 90 d vs the 90 days before. No trend for Open pipeline and Weighted pipeline (no history of past pipeline exists; showing one would need snapshots, which is a new feature).
- **Sparkline [B-6]:** only on "Won this month" and "Won this quarter" (won revenue per month, last 12 months, base currency). Section 4.17.
- Activities due card: value "2 overdue" in `--danger` with the error icon, or "3 due today" in ink (as today, never colour alone).

### 4.8 Avatars (`.avatar`)
- Initials (first letter of the first two name parts, as today's code) in a circle: 1.5px `--ink` ring, transparent ground, initials bold, ink. Sizes 24 (tables, 11px text), 28 (board cards, timeline, 12px), 36 (user menu, 14px). This follows the "line icon in a circle" style (D5).
- Decorative (`aria-hidden="true"`) whenever the name is written next to it, which is the default. Where only the avatar is shown (none in this spec), it needs `role="img"` and `aria-label="Owner: {name}"`.
- Inactive owner: ring dashed, and the name keeps "(inactive)".

### 4.9 Tabs (`.tabs`)
Links, not JS tabs (as today). Row of links with 44px hit height, 14 bold for current, 14 regular for others, 3px `--action` bar under the current one, 1px `--hairline` rule under the row. Count in brackets: "Contacts (4)". On <640 the row scrolls horizontally inside a focusable region if it overflows.

**View switch** (`.view-switch`, e.g. "List | Board"): two adjacent links styled as a segmented pair: 40px, 1px `--border-input` border, radius 8 on the outer corners only; current: `--action` fill and `--on-action` label, `aria-current="page"`.

### 4.10 Timeline item (`.timeline`)
```
 (call)  Call: Discuss 2027 frame agreement                       [Mark done]
   |     Due today 14:00 · (AH) Anna Holm · Contact: Lars Berg
   |     Short description, max 3 lines, then "Show more" link to the activity
 (chg)   Stage changed from Needs analysis to Proposal
   |     18.11.2026 09:12 · Mette Holm
```
- `<ol class="timeline">`; each `<li>`: ringed icon 28 (type: call, meeting, task, note; "change" for history lines), a 1px `--hairline` vertical connector between icons, content to the right.
- Line 1: activity type + subject as a link (bold) for activities; plain sentence for history (from `DescribeHistory`, unchanged copy). Owner changes stay bold (as today).
- Line 2 (14, ink-muted): date (absolute, plus relative hint "in 3 days" / "2 days overdue" per ui-spec 6), avatar + owner, related record.
- Overdue: line 2 starts with the danger chip "2 days overdue". Done activities: check icon in the ring and "Done" in text.
- Quick action: "Mark done" / "Mark as not done" secondary button (small, POST, as today).
- Groups, in this order, each an `h3`: "Overdue", "Upcoming" (open activities, soonest first), then past activity and changes by month, newest first ("November 2026"). Notes and done activities are "past" by their due date or creation date.

### 4.11 Empty state (`.empty`)
```
          (ringed icon 56)
     No opportunities yet.                 21 bold
 Opportunities track deals from first contact to close.     17
          [New opportunity]                 primary
```
- Centred in its container, padding 48, `--surface` ground (or none inside a panel). Copy from ui-spec 3.9 (unchanged).
- Variants: **No records** (icon of the entity, primary action), **No results for filters** ("No opportunities match these filters." + [Clear filters] secondary), **All caught up** (check icon, "Nothing due. You're all caught up.", no action), **No data for period** (analytics icon, "No data for this period. Try a longer date range."), **No access to anything yet** is handled by the no-access page.
- Empty states inside panels (My day, record tabs) are compact: icon 28 inline + text, no padding block.

### 4.12 Alerts and flash (`.flash`, `.alert`)
```
|| (icon)  Opportunity saved.  [Undo]                               ||
4px bar   ringed icon 28   text 17 ink    optional link
```
- `--surface-panel` ground, 4px left bar, ringed icon, ink text. Square. Max width = content.
- Kinds: success (bar `--signal-success`, icon success), info (`--signal-info`, info), warning (`--signal-warning`, warning), error (`--danger`, error; first sentence bold).
- Roles unchanged: success/info `role="status"`, error `role="alert"`, warnings `role="status"` unless they block the task. Focus rules unchanged (`app.js`).
- Test environment banner: full-width band, `--surface-panel` ground with a 4px `--signal-warning` bottom bar, text bold "TEST ENVIRONMENT — test data only" (copy unchanged), `role="note"`.

### 4.13 Pagination (`.pagination`)
`Showing 1–25 of 128` (left, 14) · `[Previous] Page 2 of 6 [Next]` (right, secondary buttons with chevrons; disabled ends are omitted, as today) · rows per page stays in the filter form. On <640: stacked, buttons full width.

### 4.14 Filters bar (`.filters`)
```
[(o) Search name, account...] [Stage v] [Owner v] [Status v] [Apply]  Clear filters
Active: [Stage: Proposal x] [Owner: Anna Holm x]                        25 rows v
```
- A GET form on `--surface` (not a panel, so the field borders keep full contrast), labels visible above each control (14 bold).
- **Active filter chips** (new, UI only): one neutral chip per non-default filter, each a link to the same URL without that filter, accessible name "Remove filter Stage: Proposal". Built from the existing query object.
- No auto-submit on change (WCAG 3.2.2). [Apply] is a secondary button; "Clear filters" a quiet link.
- <640: the controls sit in `<details class="filters-toggle">` with summary "Filters (2)" (count of active filters); search stays visible above it.

### 4.15 Confirm pattern (no modals)
Destructive and high-impact confirmations are **pages** (as the existing Delete and Mark as won/lost confirm pages are). No `<dialog>`.
```
Accounts / Nordvind Rotor A/S / Delete
H1: Delete Nordvind Rotor A/S?
[panel, 4px danger bar] This also removes 4 contacts' link to this account. This cannot be undone.
[Delete account]  Cancel
```
- Max width 640. `h1` is the question. Consequences in an error-kind alert without `role="alert"` (it is the page content, not a late message). Danger button + quiet "Cancel" link back. Focus lands on the `h1` (normal page load).
- Same layout for: delete record, mark as won/lost confirmation, FX change/delete, team deactivate, visibility switch, stage removal.

### 4.16 Board column and card (`.board`, `.board-col`, `.deal-card`)
```
+-- Proposal ------------------------------+
| Proposal                    50 %          |  h2 17 bold · probability chip
| 14 opportunities                          |  14
| 3,2 mio. kr.  ·  1,6 mio. kr. weighted        |  17 bold · 14
| [====================-------]  (share)    |  decorative bar, chart-1
|-------------------------------------------|
| +---------------------------------------+ |
| | Blade root seals 2027          (link) | |  17 bold
| | Nordvind Rotor A/S                    | |  14
| | EUR 240.000,00        15.12.2026      | |  14 bold amount · close date
| | [Past close date]                     | |  warning chip, only if past
| | (AH) Anna Holm                        | |  avatar 28 + name 14
| | Move to [Negotiation v] [Move]        | |  POST form
| +---------------------------------------+ |
| ... up to 20 cards                        |
| Show all 25 in Proposal (link)            |
+-------------------------------------------+
```
- **Column:** `--surface-panel` ground, min 272px, max 320px, padding 12, header not sticky. Column is a `<section aria-labelledby>`; cards are an `<ol>`. Header totals are in the base currency with the FX notes (warning chips) when a fallback or exclusion applies (same copy as the dashboard tiles).
- **Card:** `--surface` ground, padding 12, square, 1px `--hairline` frame, gap 8 between cards. The name is the only link. Hover: frame `--border-input`. A card never changes colour by stage.
- **Move form:** label "Move to" (visually visible, 14) + select of the **open stages** in Settings order, current stage selected and suffixed "(current)" + [Move] secondary small button. Accessible names: select "Move Blade root seals 2027 to", button "Move Blade root seals 2027". Hidden `rowVer` for concurrency. POST `/opportunities/{id}/stage` [B-5]. Closing (Won/Lost) is **not** on the board: the card's link goes to the record, where "Close this opportunity" works as today (keeps the lost-reason and close-date rules in one place).
- **After a move:** redirect to `/pipeline#opp-{id}` with the flash "Moved Blade root seals 2027 to Negotiation." (`role="status"`), and focus on the moved card's link (`data-autofocus`).
- **Conflict:** the existing 409 page ("Someone else changed this record…").
- **Drag-and-drop (JS, optional):** cards get `draggable="true"` and a drag-handle icon. Dropping on a column sets the select and submits the same form. Drop target shows a 2px dashed `--action` outline. No keyboard drag (the form is the keyboard and single-pointer path, WCAG 2.5.7). Under reduced motion, no drag ghost animation.

### 4.17 Sparkline and funnel bars (server-rendered SVG)
The CSP forbids inline `style`, but SVG geometry attributes are fine. Render these on the server; they need no JS.
- **Sparkline:** `<svg class="spark" viewBox="0 0 120 32" preserveAspectRatio="none" aria-hidden="true">` with one `<polyline>` (`stroke: var(--chart-1)`, 1.5px, `vector-effect: non-scaling-stroke`, no fill) and a 3px dot on the last point. Baseline at zero; y-scale 0 to max. The meaning is in text: the card's accessible name already includes the value; add a visually hidden sentence "Last 12 months: highest in June 2026, 3,1 mio. kr." (computed server-side).
- **Bar (funnel, board column share):** `<svg class="bar" viewBox="0 0 100 8" preserveAspectRatio="none" aria-hidden="true"><rect width="{pct}" height="8"/></svg>` on a `--surface-subtle` track. `fill: var(--chart-1)`. Always next to the number it represents.

---

## 5. Pages
All copy not listed here is unchanged from ui-spec 3.x. Wireframes are 1440 unless stated. `(o)` = ringed icon, `(AH)` = avatar, `[ ]` = button, `[ v]` = select.

### 5.1 Dashboard `/`
```
+-----------------------------------------------------------------------------------------------+
| Good morning, Mette                                   [Log activity] [New opportunity]         |  surface;
| Thursday 19 November · Data as of 19.11.2026 09:14                         rings ornament ))  |  headline 40
+-----------------------------------------------------------------------------------------------+
| Open pipeline | Weighted      | Won this month | Won this quarter | Win rate (last 90 d) | Activities due |
| 24,1 mio. kr. | 9,0 mio. kr.  | 2,4 mio. kr.   | 7,9 mio. kr.     | 34 %                 | 2 overdue      |
| 66 opps       | amount × …    | 5 deals        | 14 deals         | 14 of 41             | 3 due today    |
|               |               | ^ Up 12% vs …  | v Down 3% vs …   | ^ Up 4 pts vs …      |                |
|               |               | /\_/\__/‾      | /\_/\__/‾        |                      |                |
+-----------------------------------------------------------------------------------------------+
+-- My day ------------------------------ View all due --+ +-- Open pipeline by stage ---- Open board --+
| Overdue (2)                                           | | Stage          Opps  Amount (DKK)          |
|  (o) Call: Follow up on sample     [Mark done]        | | Qualification   25   6,0 mio. [=========] |
|      2 days overdue · Opp: Blade root seals 2027       | | Needs analysis  18   7,2 mio. [==========]|
| Today (3)                                              | | Proposal        14   6,4 mio. [======== ] |
|  (o) Meeting 10:00: Site visit Gdansk  [Mark done]    | | Negotiation      9   4,5 mio. [=====    ] |
|  ...                                                   | | Amounts in DKK at this month's FX rates.   |
| Next 7 days (5)                                        | | (!) Uses latest rate for 1 currency        |
|  ...                                                   | +--------------------------------------------+
+--------------------------------------------------------+
+-- My open opportunities ------------------------------------------ View all my opportunities (12) --+
| Name                    | Account              | Stage          | Amount         | Close date       |
| Blade root seals 2027   | Nordvind Rotor A/S   | ▪▪▪▫ Proposal  | EUR 240.000,00 | 15.12.2026       |
| ...  (max 10, earliest close date first)                                                            |
+-----------------------------------------------------------------------------------------------------+
```
- **Greeting band:** `h1` "Good morning, Mette" (existing greeting logic), sub-line long date (locale) + "Data as of …" (existing). Quick actions: [Log activity] secondary → `/activities/new`, [New opportunity] primary. Ornament: `dafa-rings-gray.svg` (Light) / `-white` (Dark), 200px, running off the top-right corner of the band, `aria-hidden`, hidden below 1024. (D6: request the official outlined-rings ornament file; until then use the rings file.)
- **KPI row:** existing six tiles restyled as KPI cards (4.7). Grid: 6 columns ≥1440; 3 columns 640–1439; 2 columns <640. Trends and sparklines are [B-6].
- **My day (left, 5 of 12 columns at ≥1280; full width below):** the existing "Upcoming activities" data (Overdue, Today, Next 7 days; Calls, Meetings, Tasks; mine) shown as compact timeline items (4.10) instead of three tables. Each group heading shows its count; max 10 rows per group (as today); "View all" per group with unique names ("View all overdue activities", fixes carried-over L6). Panel title link "View all due" → `/activities?status=open&type=actionable&due=due`. Empty: "Nothing due. You're all caught up." with a check icon.
- **Open pipeline by stage (right, 7 of 12):** a real `<table>` (caption "Open pipeline by stage") built from the open-pipeline aggregate the dashboard already loads (no new query): stage (link to `/opportunities?status=open&stage={id}`), count, amount in base currency (compact, full value in `title` and visually hidden), and a decorative bar (4.17). Footer line "Amounts in DKK at this month's FX rates." plus FX notes as on the tile. Panel title link "Open board" → `/pipeline`.
- **My open opportunities (full width):** existing table (Owner column dropped, as today), stage chip, 10 rows, "View all my opportunities (12)".
- **Order on <640:** greeting, KPI (2 columns), My day, funnel, my open opportunities.

### 5.2 Analytics `/analytics`
```
Analytics                                                           (h1)
+-- filters (surface) ------------------------------------------------------------+
| Period [This quarter v]  From [01.10.2026]  To [31.12.2026]  Owner [All I can see v] [Apply] |
+------------------------------------------------------------------------------------+
Close dates 01.10.2026 – 31.12.2026. Changing the dates switches the period to Custom.
Amounts are in DKK at each opportunity's FX rate (Settings > FX rates).
+-- Pipeline by stage ------------------------+ +-- Won revenue by month ------------------+
| Proposal holds the most value: 6,4 mio. kr. | | Best month: June, 3,1 mio. kr.           |
| [bar chart, chart-1]                        | | [line chart, chart-1]                     |
| > Show as table                             | | > Show as table                           |
+---------------------------------------------+ +------------------------------------------+
+-- Win rate by owner ------------------------+ +-- Activities by type ---------------------+
+-- Opportunities by lead source (full width) -------------------------------------------+
```
- Charts in `.panel.plain` figures (white in Light, so ochre/mint pass with outlines; section 6). Figcaption: title `h2` 21 bold, takeaway 17 ink-muted. Chart height 300 (240 at <640).
- Filters as 4.14 (no active-filter chips here; the range line says it). Range error stays an inline error with focus target (existing).
- The intro copy fixes carried-over inc-4 L1 and L10c: "Close dates 01.10.2026 – 31.12.2026." (no "from") and "at each opportunity's FX rate" (not "deal's").
- Data tables: `<details>` as today; the summary looks like a quiet button with a chevron.
- Layout: 2 columns ≥1024 (`align-items: start`, carried-over L12), 1 column below; the fifth chart spans both columns.

### 5.3 List pages (Accounts, Contacts, Opportunities, Activities)
```
Opportunities (128)                                   [List|Board]   [New opportunity]
[(o) Search name, account...] [Stage v] [Owner v] [Status: Open v] [Apply] Clear filters
Active: [Owner: Anna Holm x]                                                 Rows [25 v]
128 opportunities found                                                      (role=status)
+------------------------------------------------------------------------------------------------+
| Name ^              | Account            | Stage            | Amount ⇅      | Close date | Owner          |   |
|---------------------+--------------------+------------------+---------------+------------+----------------+---|
| Blade root seals 27 | Nordvind Rotor A/S | ▪▪▪▫ Proposal    | EUR 240.000,00| 15.12.2026 | (AH) Anna Holm | ✎ |
| Facade gaskets PL   | Baltyk Fasady sp.  | ▪▫▫▫ Qualificat. | PLN …         | 02.11.2026 | (TN) Tomasz N. | ✎ |
|                     |                    |                  |               | [Past close date]           |   |
+------------------------------------------------------------------------------------------------+
Showing 1–25 of 128                                  [< Previous]  Page 1 of 6  [Next >]
```
- Columns and filters unchanged per entity (ui-spec 3.4–3.7). New visual elements only:
  - Accounts: Industry column shows the **segment chip** when the industry value maps to a segment [B-8], otherwise plain text. Open pipeline right-aligned.
  - Contacts: Email as a `mailto:` link (it is text today) — UI only.
  - Opportunities: Stage chip with pips; status Won/Lost chips when the Status filter shows closed ones; "Past close date" warning chip. View switch "List | Board" (Board → `/pipeline` with the same owner filter).
  - Activities: Done column becomes the leading "Mark done" icon button; Type shows the ringed type icon 24 + label; Due date shows "Overdue" danger chip when past and open.
- Empty states per 4.11.

### 5.4 Record detail pages
Shared layout (≥1024): header across the top, then two columns: **main 8/12** (tabs with the timeline as the default tab) and **side 4/12** (key facts, then context panels). <1024: one column; key facts come **before** the tabs.

**Opportunity**
```
Opportunities / Blade root seals 2027
Blade root seals 2027                                  [Log activity] [Edit] [More v]
▪▪▪▫ Proposal · Nordvind Rotor A/S · (AH) Anna Holm                     (More: Delete opportunity)
+-----------------------------------------------------------------------------------------------+
| [Qualification]—[Needs analysis]—[ Proposal (current) ]—[Negotiation]          stage track    |
+-----------------------------------------------------------------------------------------------+
| Amount            | Weighted amount   | Close date                 | Probability             |
| EUR 240.000,00    | EUR 120.000,00    | 15.12.2026 · in 26 days    | 50 %                    |   panel, 4 facts
+-----------------------------------------------------------------------------------------------+
+-- main ---------------------------------------------------+ +-- side --------------------------+
| Activity (9)                                              | | Details                          |
| -----------                                               | | Account        Nordvind Rotor A/S|
| [Log activity]                                            | | Primary contact Lars Berg        |
| Overdue                                                   | | Lead source    Event             |
|  (o) Task: Send revised quote          [Mark done]        | | Currency       EUR               |
| Upcoming                                                  | +----------------------------------+
|  (o) Meeting: Technical review 26.11.  [Mark done]        | +-- Close this opportunity --------+
| November 2026                                             | | [Mark as won]                    |
|  (o) Stage changed from Needs analysis to Proposal        | | Lost reason (required to mark as |
|  (o) Call: Intro with purchasing       Done               | | lost) [— choose — v]             |
| ...                                                       | | [Mark as lost]                   |
+-----------------------------------------------------------+ +----------------------------------+
Created 01.10.2026 by Anna Holm · Last modified by Mette Holm, 18.11.2026 14:02
```
- **Header:** `h1` record name; meta line: stage chip, account link, owner avatar + name. Actions: [Log activity] **primary** (→ `/activities/new?opportunityId=…`, existing prefill) and [Edit] secondary, on opportunity, contact and account pages, because logging is the most frequent task (S2). "More" menu (`<details>`) holds "Delete opportunity" (danger text), shown only when `CanDelete` (rule unchanged; if empty, no "More").
- **Stage track:** `<ol class="stage-track" aria-label="Stage">` of open stages in Settings order; each a 8px bar segment with the label below (14). Passed: ink fill; current: `--action` fill, label bold, `aria-current="step"`, visually hidden "(current stage)"; later: `--surface-panel` fill. Won: all segments ink and a won chip "Won" after the track; Lost: the track is replaced by the lost chip and "Lost reason: Price". At <640 only the current label is shown under the bar ("Stage 3 of 4: Proposal").
- **Facts strip:** Amount, Weighted amount, Close date (+ relative hint, + "Past close date" chip), Probability. `--surface-panel`, 4 columns (2 at <640). Values 21 bold.
- **Main tabs:** "Activity (n)" (default, replaces the Activities tab and the History section: related activities + audit history merged into one timeline, 4.10 [B-7]) — and for accounts/contacts, the existing "Contacts (n)" / "Opportunities (n)" tabs with their tables and "New …" buttons as today. The "Nothing here yet." text becomes a compact empty state ("No activity yet. [Log activity]").
- **Side:** "Details" panel (`dl`, label 14 ink-muted above value 17; "Not set" rule unchanged), then "Close this opportunity" panel for open opportunities (existing forms, restyled). Lost-reason error stays inline (existing).
- **Footer:** "Created … · Last modified by …" (existing copy), 14 ink-muted.

**Account:** header with the segment icon (supplied file, 48px) when the industry maps to a segment [B-8]; meta: segment chip, city + country, website link, owner. Facts strip: Open opportunities (count from the loaded related list), Contacts (count), Last activity date (from the loaded related activities; "–" when none). No money total on the account page (it would need FX conversion per row; not in scope). Side: Details (industry, website, phone, address, notes).
**Contact:** header with an avatar 40 (initials); meta: job title, account link, owner. Quick links under the name: email (`mailto:`), phone (`tel:`). Facts: none (strip omitted). Side: Details.
**Activity:** header with the ringed type icon 40; meta: status chip (Open/Done), due date, related record link, owner. Header action: [Mark as done] primary (existing POST), [Edit] secondary, More → Delete. No tabs; main column shows Description (full) and the History timeline; side shows Details.

### 5.5 Forms (create and edit, all entities and settings)
```
Opportunities / New opportunity
New opportunity                                                             (h1)
[error summary, if any]
+-- form, max 720, on surface --------------------------------------------+
| Basics                                                     (h2 17 bold)  |
|  Name (required)               [______________________________]          |
|  Account (required)            [Nordvind Rotor A/S          v]           |
|  Primary contact               [— none —                    v]           |
| Value and timing                                                         |
|  Stage (required)              [Proposal (50%)              v]           |
|  Amount (required)  [ 240.000,00 ]   Currency (required) [EUR v]         |
|  Close date (required)  [15.12.2026]                                     |
| Source and ownership                                                     |
|  Lead source [Event v]   Lost reason (required if …) [— none — v]        |
|  Owner (required) [Anna Holm v]   You can choose people in your team.    |
+--------------------------------------------------------------------------+
[Save]  Cancel
```
- Labels above fields (as today); one column with paired fields (4.2). Field order unchanged.
- Visual grouping with `h2`s (17 bold, 1px hairline above) — no `fieldset` unless it is a real group (radio groups, "Related to (link at least one)"). Groups:
  - Account: "Company" (Name, Website, Phone, Industry), "Address" (Street, Postcode+City, Country code), "Notes and ownership" (Notes, Owner).
  - Contact: "Person" (First+Last name, Job title), "Contact details" (Email, Phone, Mobile), "Company and ownership" (Account, Owner).
  - Opportunity: as drawn.
  - Activity: "Activity" (Type radios as a segmented row of radio buttons with icons, Subject), "Related to (link at least one)" (fieldset, existing), "Timing" (Due date+time, Done), "Details" (Description, Owner).
- Footer: [Save] primary + "Cancel" quiet link, left-aligned under the form (not sticky).
- Settings forms use the same field styles.

### 5.6 Pipeline board `/pipeline` (new view)
```
Pipeline                                                   [List|Board]  [New opportunity]
Owner [All I can see v]  [Apply]      Open pipeline 24,1 mio. kr. · 66 opportunities
+-- Qualification ----+ +-- Needs analysis --+ +-- Proposal --------+ +-- Negotiation -----+
| 10 %                | | 25 %               | | 50 %               | | 75 %               |
| 25 opportunities    | | 18 opportunities   | | 14 opportunities   | | 9 opportunities    |
| 6,0 mio. kr.        | | 7,2 mio. kr.       | | 6,4 mio. kr.       | | 4,5 mio. kr.       |
| 0,6 mio. kr. weighted   | | 1,8 mio. kr. weighted  | | 3,2 mio. kr. weighted  | | 3,4 mio. kr. weighted  |
| [card]              | | [card]             | | [card]             | | [card]             |
| [card]              | | [card]             | | [card]             | | [card]             |
| ...                 | | ...                | | ...                | | ...                |
| Show all 25         | |                    | |                    | |                    |
+---------------------+ +--------------------+ +--------------------+ +--------------------+
Closed opportunities are in the list: View won and lost opportunities
```
- Nav item "Pipeline", `aria-current` set. `h1` "Pipeline".
- **Data [B-5]:** for each **open** stage (Settings order): the existing list query with `status=open&stage={id}`, sorted by close date ascending, page size 20; column count, total and weighted from the existing open-pipeline aggregate grouped by stage (converted as the dashboard does). Owner filter = the existing owner filter of the opportunities list (same options, same RLS). No new SQL. **New:** POST `/opportunities/{id}/stage` (stage-only update: RLS, owner and team rules unchanged, `rowVer` concurrency, audit row, flash).
- **Board container:** `<div class="board" role="region" aria-label="Pipeline board" tabindex="0">`, horizontal scroll when columns don't fit (more than 4 open stages, or <1280). Columns 272–320px, gap 12.
- **Empty column:** compact empty state "No opportunities in this stage."
- **Empty board** (no open opportunities): full empty state "No open opportunities yet." + [New opportunity].
- **<640:** no horizontal board. Columns stack as sections; above them a jump list of stage links "Qualification (25) · Needs analysis (18) · …" (in-page anchors). Cards full width.
- **No JS:** fully works (forms). **JS:** drag-and-drop (4.16).

### 5.7 Search `/search` (replaces the placeholder page)
```
Search                                                                  (h1)
[ (o) nordvind                                       ] [Search]
Results for "nordvind"
Accounts (2)                                        See all 2 accounts
  Nordvind Rotor A/S · Esbjerg, DK · (AH) Anna Holm
  Nordvind Service GmbH · Bremen, DE · (KS) Katrin Seidel
Contacts (3) …   Opportunities (5) …   Activities (1) …
```
- **Data [B-4]:** for each entity, the existing list query with `q={text}`, page size 5, default sort; count from the same query. No new SQL. Groups in nav order; groups with 0 results are left out; "See all N {plural}" → `/{entity}?q={text}`.
- Empty query: only the form and the hint "Search names, accounts, emails and subjects." No results: empty state "No records match "xyz". Check the spelling or try a shorter word." `role="status"` line "10 results found".

### 5.8 Settings (Admin only)
```
Settings                                                                (h1 on every settings page: section name)
+-- settings nav (200) --+ +-- content (max 880) -----------------------------------------------+
| General                | | General                                                            |
| Currency & locale      | | [form fields]                                                      |
| FX rates               | | Brand preview:  [Light sample]  [Dark sample]                      |
|#Pipeline stages        | |                                                                    |
| Picklists              | |                                                                    |
| Visibility             | |                                                                    |
| Users & teams          | |                                                                    |
+------------------------+ +--------------------------------------------------------------------+
```
- `_SettingsNav` becomes a vertical list at ≥1024 (`--surface-subtle` ground, same item style as the sidebar without icons), and a wrapping row of tab links below 1024. Labels unchanged.
- **General:** CRM name, Accent colour (text field plus a 40×40 swatch preview, help text per 2.7), Logo upload (as today). New **brand preview** (UI only): two small samples rendered with the current values (primary button, link, current nav item) on Light and Dark grounds, so the Admin sees that Dark keeps white.
- **Currency & locale:** unchanged fields; the live preview line in a `.panel`.
- **FX rates:** filter bar, table (Currency, Month, Rate, Last changed, actions), "Missing" rows use the warning chip "Missing", add-rate form below in a panel. Confirmations as 4.15.
- **Pipeline stages:** editor table; type lock shown as text "Won (locked: 40 opportunities)" (review M3); Move up/down as icon buttons with unique names; Save stages primary at the bottom; unsaved note as a warning alert (review L5).
- **Picklists:** list chooser as tabs; values table with inline rename field, Move up/down, Deactivate/Reactivate.
- **Visibility:** two **radio cards** (each a `label` wrapping the radio, `--surface-panel`, 1px `--border-input`, the checked one gets a 2px `--action` border) with the existing copy; help line below; confirm page (4.15).
- **Users & teams:** Teams table + add form in a panel; Users table with filters; "No team" warning chip; summary line "3 users have no team. [Show them]" as a warning alert.

### 5.9 Sign-in, signed out, no access, sign-in error, test sign-in, test user picker
```
1440:
+-------------------------------------------+------------------------------------------------+
| logo-blue field (100% colour block)       |  surface                                       |
|                                           |                                                |
|   [DAFA negative logo 160]                |   Sign in                       (28 bold)      |
|                                           |   Sign in with your work account.              |
|   Close to the markets –                  |   [ Sign in with Microsoft ]  (primary, 44)    |
|   close to you            (40 bold white) |                                                |
|                                    ))))   |                                                |
|   rings-white running off bottom-right    |   ©2026 DAFA A/S             (fine print)      |
+-------------------------------------------+------------------------------------------------+
390: the blue field becomes a 160px band on top with the logo only; the form below.
```
- No sidebar or top bar (signed out). The blue field is the fixed brand colour `--dafa-blue` in both themes with `--on-dark` text (10.1:1). The tagline is an approved one, verbatim, as a `<p>` (not a heading; the page `h1` is "Sign in").
- Form side follows the theme. Messages (expired, disabled, SSO error, no access, signed out) are alerts (4.12) above the button, copy unchanged.
- **Test sign-in:** same layout; access-code field per 4.2, error inline (review L1 already fixed in code). Test banner stays above everything.
- **Test user picker:** radio cards in a list, each: avatar 36, name (bold), "Role · Team" (14). First selected (as today). [Sign in] primary.
- If an Admin uploaded a logo, the blue field shows the uploaded logo on a white plate instead of the DAFA logo, and no tagline (the tagline belongs to DAFA).

### 5.10 Error and waking pages
```
+----------------------------------------------------------------------------------+
|                                                                      ))))  rings  |
|      (ringed icon 56: error / search / clock)                                     |
|      We can't find that page or record.                     (28 bold)             |
|      It may have been deleted, or you may not have access.  (17)                  |
|      Reference: 7f3a…                                       (14 ink-muted)        |
|      [Go to dashboard]  [Search]                                                  |
+----------------------------------------------------------------------------------+
```
- Signed-in errors (403, 404, 409, 413, 429, 500) render inside the app shell; content block max 560, left-aligned, 80px top padding. Copy and actions unchanged (ui-spec 3.9); 500 gets [Try again] (carried-over deviation).
- **Waking up the database:** renders without the database, so without the sidebar: centred block on `--surface` with the DAFA logo (or default branding) at the top, `h1` "Waking up the database", copy unchanged, the indeterminate bar (8px, `--surface-panel` track, `--action` runner, square ends, static under reduced motion), status line, [Try again now] primary 44px. Give-up alert as 4.12 error.
- Rings ornament (2.7) top-right on error and waking pages at ≥1024, `aria-hidden`.

---

## 6. Charts

### 6.1 Palette and order
| Order | Light (on white chart ground) | Dark (on `#505152` chart ground) | Use |
|---|---|---|---|
| `--chart-1` | logo blue `#00456c` (10.1:1) | ochre `#d2a225` (3.3:1) | Every single-series chart, sparklines, funnel bars |
| `--chart-2` | ochre `#d2a225` (2.3:1 → needs outline) | white `#ffffff` (7.9:1) | Second series |
| `--chart-3` | mint `#54bbab` (2.3:1 → needs outline) | mint `#54bbab` (3.4:1) | Third series |
| `--chart-4` | building-100 `#8e5550` (5.8:1) | — (use a dashed white line or hatched bars) | Fourth series (Light only) |
| Segment series | `--segment-wind/-building/-industry` | same tokens (50 % tints in Dark) | Only when the series **are** segments |

- **Colour-blind check** (simulated protanopia, deuteranopia, tritanopia; CIE Lab distance): Light 1–3 minimum ΔE 45, Light 1–4 minimum ΔE 27; Dark 1–3 minimum ΔE 28. Adding a fourth colour in Dark drops below ΔE 6, hence patterns.
- **More than 3 (Dark) or 4 (Light) series:** don't add colours. Group the rest into "Other", or use small multiples.
- **Outline rule:** any series below 3:1 against the chart ground gets a 1px `--chart-outline` border (bars) or an outlined point marker (lines). In Dark all series reach 3:1, so `--chart-outline` is transparent.
- All current charts are single-series, so in practice every chart uses `--chart-1`.

### 6.2 Chart.js configuration (`wwwroot/js/charts.js`)
- Read colours at render time with `getComputedStyle(document.documentElement).getPropertyValue('--chart-1')` etc. (allowed by the CSP). Listen to `matchMedia('(prefers-color-scheme: dark)')` changes and re-create charts; the manual toggle reloads the page anyway.
- Font: `Chart.defaults.font.family = getComputedStyle(body).fontFamily`, size 14, colour `--chart-axis`.
- Grid lines: `--chart-grid`, 1px, no border on the plot area; x-axis grid off for bar charts.
- Bars: `borderRadius: 0`, `maxBarThickness: 40`, `categoryPercentage: 0.7`. Lines: width 2, points radius 4 with `--surface` fill and `--chart-1` 2px border, `tension: 0`. No area fills, no gradients.
- Axes start at zero (as today). Value ticks use the existing formatters (compact money, percent per locale, integer counts).
- Tooltip: `--surface-panel` background, `--ink` text, 1px `--border-input` border, `cornerRadius: 0`, no caret shadow.
- Legend: hidden for single series; for multi-series, a legend above the plot with square markers, and data tables always available.
- Animation off under reduced motion (as today); otherwise `duration: 200`.
- `x` tick labels: `autoSkip` with `maxRotation: 0`; at <640 use the short month labels (fixes carried-over inc-4 L8).

### 6.3 Non-Chart.js visuals
Sparklines and funnel bars are server SVG (4.17) using `--chart-1`. They never carry information that isn't also in text.

---

## 7. Accessibility (WCAG 2.2 AA, both themes)
Everything in ui-spec 7 still applies. Specific to this redesign:
1. **Contrast:** all text pairs in 2.3 meet 4.5:1 in both themes; action and focus reach ≥7:1 on every ground. Ochre, mint and segment-100 colours are never used for text. Hairlines are decorative; every control has a ≥3:1 boundary (`--border-input`, or a filled ground).
2. **Links:** in running text, links are underlined. Standalone links (table first column, card titles, KPI cards, nav) are bold or in a clearly interactive component; in Dark the action colour equals the ink colour, so never rely on colour to show a link.
3. **Focus:** 2px `--focus` ring, 2px offset, on every interactive element, visible on every ground in both themes. Not hidden by the sticky top bar or sticky table headers (`scroll-padding-top: calc(var(--topbar-h) + 48px)` on list pages so a focused row isn't under the sticky header; 2.4.11).
4. **Landmarks:** sidebar `nav[aria-label=Main]`, `header` (top bar), `main`, `footer`; settings nav `nav[aria-label=Settings]`; breadcrumb `nav[aria-label=Breadcrumb]`; board `role=region`.
5. **Sidebar:** collapse button with `aria-expanded`; rail tooltips dismissible with Esc and hoverable (1.4.13); drawer traps focus, Esc closes, focus returns.
6. **Command bar:** ARIA 1.2 combobox with listbox; works as a plain search form without JS; shortcut uses a modifier key (2.1.4).
7. **Board:** the move form is the keyboard and single-pointer path (2.5.7); drag is extra. Each select/button pair has a unique accessible name. After a move, the flash announces it and focus returns to the card.
8. **Charts and visuals:** canvas `role="img"` + `aria-label` summary, data table in HTML (unchanged). Sparklines and bars are `aria-hidden` with the information in text.
9. **Status without colour:** stage pips, won/lost markers, warning bars and trend icons are decorative; the text says it.
10. **Target size:** 24×24 minimum everywhere (2.5.8); our controls are 40–44px. Icon buttons in table rows are 40×40 with 8px spacing.
11. **Reflow:** 320px wide, 200 % and 400 % zoom work: the sidebar becomes the drawer, tables become cards, the board stacks.
12. **Theme:** both themes are tested; `color-scheme` set so native controls (date pickers, scrollbars) follow the theme. Forced-colours mode (Windows high contrast): buttons, chips and the current nav item keep a visible border (`@media (forced-colors: active)` adds `border: 1px solid CanvasText` where meaning comes from a ground colour).
13. **Motion:** no animation beyond colour transitions; none under reduced motion.
14. **Testing:** axe on every page template in Light and Dark, at 390, 1024 and 1440, for Admin, Manager and User; keyboard-only run of "move an opportunity on the board", "log an activity from a record", "mark done from My day"; NVDA + Firefox spot check of dashboard, board and an opportunity record.

---

## 8. Backend impact (all other changes are views, CSS, JS and static files)
| ID | Change | New query? |
|---|---|---|
| B-1 | Theme preference: POST endpoint setting a `theme` cookie; layout reads it | No DB |
| B-2 | `/css/theme.css` emits `--action` / `--action-hover`, Light only | No |
| B-3 | Accent contrast check against white and `#D6D7D7`; help text | No |
| B-4 | Search page model: existing list query per entity with `q`, page size 5 | Existing queries |
| B-5 | Pipeline board page model (existing list query per open stage + existing open-pipeline aggregate) and **new POST `/opportunities/{id}/stage`** (stage-only update with RLS, rowVer, audit, flash) | Existing queries; **one new write endpoint** |
| B-6 | Dashboard trends: existing opportunity aggregate called with the previous-period ranges; won revenue per month for 12 months (same aggregate as the analytics "Won revenue by month" chart) | Existing queries, new parameters |
| B-7 | Record timeline: merge the already-loaded related activities and history into one view model; make the activities tab the default | No (data already loaded) |
| B-8 | Segment: return the industry picklist value's `Code` (`wind`, `building`, `industry`) with accounts in the list and detail queries; demo data sets the codes | **Small change to existing SELECTs**, no schema change |
| B-9 | Static assets: brand files, icon sprite, chevrons in `wwwroot/img` | No |
| B-10 | Account facts (open opportunities count, contacts count, last activity date) from already-loaded related lists | No |
| B-11 | (Later, optional) JSON quick-search endpoint for live results in the command bar | Existing queries |
| B-12 | Default accent: `theme.css` treats the old default `#1D4ED8` as not set (DAFA blue shows); seed default becomes `#00456C`. A data migration of the stored value is optional and needs PO approval | No |

Not included because they would be new features: pipeline history/snapshots (trend on open pipeline), saved views, per-card currency conversion, editing segment codes in Settings > Picklists, notifications.

## 9. New and changed UI copy
| Where | Copy |
|---|---|
| Sidebar | "Pipeline" · "Collapse sidebar" / "Expand sidebar" · logo link name "{CRM name}, go to dashboard" |
| Top bar | Placeholder "Search accounts, contacts, opportunities..." · hint "Ctrl K" / "⌘ K" · "New" · listbox first option "Search all records for "{text}"" · commands "New account", "New contact", "New opportunity", "Log activity", "Go to {page}" |
| User menu | "Theme" · "Match system" · "Light" · "Dark" |
| Dashboard | "My day" · "View all due" · "View all overdue activities" / "View all activities due today" / "View all activities in the next 7 days" · "Open pipeline by stage" · "Open board" · "Amounts in {base} at this month's FX rates." · trend "Up {n}% vs same days last month" (or "last quarter") / "Down {n}% vs …" / "No change vs …" / "None in the same days last month" (or "last quarter") / "Up {n} pts vs previous 90 days" / "Down {n} pts vs previous 90 days" / "No change vs previous 90 days" · sparkline text "Last 12 months: highest in {month}, {amount}." |
| Board | `h1` "Pipeline" · "{n} opportunities" / "1 opportunity" · "{amount} weighted" · "Move to" · "Move" · "(current)" · "Show all {n} in {stage}" · "No opportunities in this stage." · "Closed opportunities are in the list: View won and lost opportunities" · flash "Moved {name} to {stage}." |
| Lists | "Active:" · chip name "Remove filter {label}: {value}" · "Filters ({n})" · "Edit {name}" · view switch "List" / "Board" |
| Records | Tab "Activity ({n})" · groups "Overdue", "Upcoming", "{Month year}" · "No activity yet." · "Show more" · "More" · "Stage {i} of {n}: {stage}" · "(current stage)" · facts "Open opportunities", "Contacts", "Last activity" |
| Search | "Results for "{text}"" · "See all {n} {plural}" · "Search names, accounts, emails and subjects." · "No records match "{text}". Check the spelling or try a shorter word." · "{n} results found" |
| Settings > General | Accent help text (2.7) · "Brand preview" · "Light" · "Dark" |
| Footer | "©2026 DAFA A/S" (year from the server clock) |

"Deal" stays only in the existing KPI context lines ("5 deals").

## 10. UI acceptance criteria (in addition to ui-spec 8)
1. Every token in 2.6 exists in `app.css` with the exact name and value; the two dark blocks are identical; no colour literal outside the token block except in brand SVG files.
2. No `box-shadow`, no gradient, no `border-radius` other than 8px on buttons/inputs and full circles on icon rings and avatars (lint or grep check).
3. Only weights 400 and 700; the font stack is exactly `"Avenir Next", "Avenir Next LT Pro", Arial, sans-serif`.
4. Light and Dark both pass axe with zero serious/critical issues on every template at 390/1024/1440; the theme follows the OS, and the manual choice overrides it and survives sign-out.
5. An Admin accent changes links, buttons and focus in Light only; Dark stays white. An accent below 4.5:1 on `#D6D7D7` is refused with the existing message.
6. Sidebar: expanded at ≥1280, rail at 640–1279 (JS), drawer at <640; without JS the nav is always fully labelled. The DAFA logo shows (blue in Light, negative in Dark) unless a logo is uploaded.
7. Board: every open opportunity the user can see is on the board (or behind "Show all"), column totals equal the dashboard funnel, a move works with JS off, writes an audit row, refuses a stale `rowVer` with the 409 page, and respects RLS and owner rules exactly as the edit form does.
8. Dashboard: My day shows the same items as today's Upcoming activities; trends and sparklines match the numbers in Analytics for the same ranges.
9. Record pages: the Activity tab shows related activities and history in one timeline, in the order of 4.10; "Last modified" footer unchanged.
10. No Unicode symbol is used as an icon (⚠ ☐ ☑ ▲ etc.); no emoji anywhere.
11. All existing e2e tests pass after selector updates; no copy listed as unchanged in ui-spec has changed.

## 11. Implementation order
Each step ships on its own and leaves the app consistent. UX reviews after steps 3, 5, 7 and 10.

| # | Step | Contents | Depends on |
|---|---|---|---|
| 1 | **Foundations** | Rewrite `app.css` top: tokens (2.6) with system dark mode, base type, links, focus, buttons, inputs, alerts, chips; copy brand assets and build the icon sprite (B-9); replace Unicode symbols; `theme.css` new variable names (B-2), accent rule (B-3) and default (B-12) | — |
| 2 | **App shell** | Sidebar (expanded/rail/drawer), top bar with search form and menus, page header, breadcrumbs, footer, test banner; `app.js` collapse + tooltips + drawer; `boot.js` reads `crm.sidebar` | 1 |
| 3 | **Lists** | Tables (sticky header, avatars, chips, quick actions), filters bar with active chips, pagination, empty states, card view <640 — all four entity lists | 2 |
| 4 | **Records and forms** | Record header, stage track, facts strip, side panels, timeline (B-7, B-10), "More" menu; form grouping and paired fields; confirm pages | 3 |
| 5 | **Dashboard** | Greeting band, KPI cards, My day, funnel table, my open opportunities. Then trends and sparklines (B-6) as a follow-up commit | 3 |
| 6 | **Pipeline board** | `/pipeline`, nav item, view switch, stage-move endpoint (B-5), then drag-and-drop | 3, 4 |
| 7 | **Analytics and charts** | Chart panels, Chart.js styling from tokens, theme re-render | 1 |
| 8 | **Settings** | Settings layout and nav, General brand preview, radio cards, stage editor fixes from review M1–M3/L5 if not done | 2 |
| 9 | **Signed-out and system pages** | Sign-in split layout, test sign-in, picker, signed out, no access, errors, waking | 1 |
| 10 | **Search and command bar** | `/search` results (B-4), command bar listbox and Ctrl K | 2 |
| 11 | **Manual theme toggle** | B-1 cookie endpoint, user-menu Theme group, `data-theme` | 1 |
| 12 | **Segment data and demo data** | B-8 industry codes; load the demo dataset (`docs/ux/demo-data-brief.md`) on Test; final UX review on seeded data in both themes | 3 |

## 12. Decisions needing product-owner sign-off
| ID | Decision | Recommendation |
|---|---|---|
| D1 | Pipeline board as a new nav item "Pipeline" (with Opportunities above Contacts in the nav) | Yes. It is the single most visible "modern CRM" upgrade and needs one small write endpoint. |
| D2 | Provisional digital tokens not in the guide: `surface-subtle`, `ink-muted` (Dark), `border-input`, `action-hover`, `danger`/`on-danger` (a red for errors) | Accept all. Without a red, errors are harder to spot; it appears only on errors and destructive buttons. |
| D3 | Admin accent applies to the Light theme only; Dark always uses white, and the contrast check moves to white + `#D6D7D7` | Accept. Any accent dark enough for Light can't work on the dark gray. |
| D4 | A small bespoke line-icon set in the guide's style for navigation and actions (the guide only supplies three segment icons) | Accept, and ask the brand owner to review the sprite once drawn. |
| D5 | Avatars as initials in a line circle (`radius-full`, which the guide reserves for icon circles) | Accept; it reads as part of the icon family. Alternative: square initials blocks. |
| D6 | Rings ornament on the dashboard greeting, sign-in and error pages, using the supplied rings files until the brand owner provides the official outlined-rings ornament | Accept, and request the ornament file. |
| D7 | Trends only for Won this month, Won this quarter and Win rate (no history exists for pipeline trends) | Accept. A pipeline snapshot is a separate feature decision. |
| D8 | Map Industry picklist values to DAFA segments through the existing `Code` field, set by data (not editable in Settings in this release) | Accept for now; add a "Segment" choice in Picklists later if Admins need it. |
| D9 | Default accent: treat the old `#1D4ED8` as "not set" so DAFA blue shows without touching production data | Accept (no data migration needed). |

## 13. Open questions and validation
- Does DAFA have an Avenir Next web licence? If yes, self-host the WOFF2 files under `wwwroot/fonts` (CSP `font-src 'self'` must be added) — that's the only way users without the font installed see Avenir. Until then most users will see Arial.
- Brand owner review of: dark theme values, `surface-subtle`, the danger red, the icon sprite, avatar style.
- After release: 5 short sessions with sales users (Wind, Building materials, Industry) on the dashboard, board and record pages; measure S1–S4 and check A7–A10.
