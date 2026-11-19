# Demo data brief: a realistic sample that makes the CRM look alive

Status: v1.0 · Owner: UX · Date: 19.11.2026 · For: developer (script), QA (checks) · Related: `docs/ux/ui-redesign.md` (B-8, section 11 step 12)

## Purpose
The redesigned dashboard, board, analytics and record timelines only show their value with believable data. Today's test seed has users and teams but no business records, so every chart is empty. This brief says what a demo dataset must contain. It's for the **Test** environment and product-owner demos only.

## Relation to the demo seeder in progress
A developer is building a deterministic generator (`dotnet/src/Crm.Web/Services/Demo/`, not yet committed when this brief was written). It already follows the safety rules below (invented names, `example.com`, 555-01xx phone numbers, dates relative to today, test-oid range). **Its own catalogue of names and users is fine; the names and people in this brief are examples, not a replacement.** Use this brief as the checklist the generated data must pass. The checks that need attention most:
- industry values that map to the three segments through the picklist `Code` (`wind`, `building`, `industry`), needed for segment chips (redesign B-8);
- enough closed opportunities per owner for "Win rate by owner" (5+ each in the last 12 months);
- My day data for the persona used in demos (overdue, today with times, next 7 days);
- previous-period data for the KPI trends (last 90 days vs the 90 before; month and quarter to date vs the same days before);
- stage and owner history rows for record timelines;
- a no-team user and an inactive owner (the test-user seed has a no-team user; an inactive owner of closed records is still missing).

## Hard rules
- **Fictitious only.** No real company, product, project or person data. Every company name below is invented. Before loading, check each name against public registers (CVR for DK, Bolagsverket/allabolag for SE, Handelsregister for DE, KRS for PL, Registro Imprese for IT, OpenCorporates for US and CN). Replace any name that matches a real company. Don't use the names of DAFA's real customers, competitors or partners, even altered.
- **Safe contact data.** Emails use `@example.com`, `@example.net` or `@example.org` only (reserved by RFC 2606). Websites `https://{slug}.example.com`. Leave phone numbers empty unless you use reserved fictional ranges (UK `+44 7700 900xxx`, US `+1 555 01xx`). No real street addresses: invented street names, real city names are fine.
- **Never in production.** A separate seed command or script, idempotent, with a guard that refuses to run unless the app runs in test-access mode (same pattern as `db/seed/test-users.sql`). Include a reset script that removes only demo records.
- **Through the rules, not around them.** Records must respect RLS, owners and teams as if users had entered them, and the audit trail must be consistent (created/modified by and at, history rows for stage and owner changes). Use past `CreatedAt` values to match the history.
- **Today is the anchor.** Generate dates relative to the load date, so "due today", "overdue" and "this month" stay true whenever the demo is reloaded. The numbers below assume 19.11.2026.

## Settings
| Setting | Value |
|---|---|
| CRM name | DAFA CRM |
| Logo | none uploaded (so the DAFA logo shows) |
| Accent | not set / `#00456C` |
| Locale, timezone | da-DK, Europe/Copenhagen (also demo once in en-GB) |
| Base currency | DKK |
| Enabled currencies | DKK, EUR, SEK, USD (edge variant adds NOK) |
| Fiscal year start | January |
| Visibility | Team |
| Pipeline stages | the defaults: Qualification 10 %, Needs analysis 25 %, Proposal 50 %, Negotiation 75 %, Closed won, Closed lost |
| Industry picklist | add "Wind energy" (Code `wind`), "Building materials" (Code `building`), "Industry" (Code `industry`); keep "Other"; deactivate the generic defaults (Manufacturing, Technology, …) so they don't appear in forms |
| Lead source, Lost reason | the defaults |

## Users and teams (example cast, all fictitious)
Minimum: 2–3 teams, 8+ users including an Admin who owns nothing, one Manager per team, one no-team User and one inactive User who owns closed records. Example:

Three teams, linked to fake Entra group IDs in the same format as the test seed (`00000000-0000-4000-8000-…`). Users appear in the test user picker.

| Name | Role | Team | Market | Notes |
|---|---|---|---|---|
| Henrik Dalsgaard | Admin | none | Group | Owns no records; sees everything |
| Mette Kjærgaard | Manager | Nordics | DK, SE | **Demo persona**: owns records and activities so "My day" is full |
| Anders Holm | User | Nordics | DK | |
| Sofie Lund | User | Nordics | DK | |
| Erik Sandberg | User | Nordics | SE | |
| Katrin Weiß | Manager | Central Europe | DE | |
| Jonas Becker | User | Central Europe | DE | |
| Agnieszka Nowak | User | Central Europe | PL | |
| Luca Ferrari | User | Central Europe | IT | |
| Daniel Brooks | Manager | Americas & Asia | US | |
| Emily Carter | User | Americas & Asia | US | |
| Wei Zhang | User | Americas & Asia | CN | |
| Lise Bech | User | **no team** | DK | Shows the "No team" badge; sees only her own records |
| Peter Juhl | User | Nordics | DK | **Inactive** (left); still owns 3 closed opportunities, shows "(inactive)" |

Emails: `firstname.lastname@example.com` (ASCII only: `katrin.weiss`, `mette.kjaergaard`).

## Accounts: 40–60
Split by segment: Wind energy ~40 %, Building materials ~35 %, Industry ~25 %. Country mix: DK 25 %, DE 20 %, SE 15 %, PL 12 %, US 12 %, IT 8 %, CN 8 %. Each account is owned by a user from the team for its market.

Example names (invented; check them as described above):

| Segment | Name | City, country |
|---|---|---|
| Wind energy | Brisvik Rotor A/S | Esbjerg, DK |
| Wind energy | Nordlys Bladeworks ApS | Aalborg, DK |
| Wind energy | Kvarnö Vindteknik AB | Göteborg, SE |
| Wind energy | Windhafen Komponenten GmbH | Bremerhaven, DE |
| Wind energy | Elbmarsch Turbinenbau GmbH | Hamburg, DE |
| Wind energy | Bałtyk Łopaty sp. z o.o. | Szczecin, PL |
| Wind energy | Prairie Gust Composites Inc. | Grand Forks, US |
| Wind energy | Lone Mesa Wind Services LLC | Amarillo, US |
| Wind energy | Qinglan Wind Components Co., Ltd. | Tianjin, CN |
| Wind energy | Maestrale Pale Eoliche S.r.l. | Taranto, IT |
| Building materials | Fugemester Byg A/S | Vejle, DK |
| Building materials | Tegltårn Facader ApS | Odense, DK |
| Building materials | Skogsglim Fönster AB | Växjö, SE |
| Building materials | Mälarbygg Tätning AB | Västerås, SE |
| Building materials | Rheinkante Fassadentechnik GmbH | Köln, DE |
| Building materials | Hausmark Fenstersysteme GmbH | Bielefeld, DE |
| Building materials | Wiślana Stolarka Okienna sp. z o.o. | Kraków, PL |
| Building materials | Cedarvale Building Products Inc. | Charlotte, US |
| Building materials | Pietrafina Serramenti S.p.A. | Verona, IT |
| Industry | Hjulmager Industri A/S | Herning, DK |
| Industry | Fjordvang Maskinfabrik A/S | Randers, DK |
| Industry | Järnvik Pump & Ventil AB | Örebro, SE |
| Industry | Sauerwerk Dichtungstechnik GmbH | Stuttgart, DE |
| Industry | Śląskie Zakłady Akustyczne sp. z o.o. | Katowice, PL |
| Industry | Ironpine Enclosures Corp. | Milwaukee, US |
| Industry | Valtorre Macchine S.r.l. | Brescia, IT |
| Industry | Jinghai Railcraft Components Co., Ltd. | Tianjin, CN |

Fill the rest the same way. Include **edge cases**: one name of 100+ characters, one account with no contacts, one with no opportunities, one with 12+ opportunities (a key account), names with æ ø å ö ä ß ł ś ż, one account with notes of ~1,500 characters, one with no industry.

## Contacts: 100–150
1–4 per account (a key account has 8). Job titles that fit the segments: Purchasing manager, Technical buyer, R&D engineer, Plant manager, Quality engineer, Project manager, Sourcing director, Design engineer. Edge cases: a contact with no first name, a contact with no account, a very long job title.

## Opportunities: 120 minimum, about 300 recommended
The proportions below matter more than the totals. With 120, keep at least 40 open and 80 closed, and scale the counts down.
Names describe a sealing or protection need, not a real project: "Blade root seals 2027", "Nacelle noise insulation", "Facade gasket frame agreement", "Window profile seals – new series", "Machine enclosure foam", "Rail door seals retrofit", "Tower cable protection", "HVAC damping pads".

**History (closed), about 230, spread over at least 14 months, 18 recommended** (May 2025 → today), so "last 12 months", the previous period and the previous 90 days all have data:
- About 80 won and 150 lost → win rate around 34 % in the last 90 days and around 30 % in the 90 days before (so the trend shows "Up").
- Won revenue per month between DKK 1.5 and 3.5 mio., with peaks in June and December. Month to date (1–19 November) higher than 1–19 October; quarter to date a little lower than the same days of last quarter, so both trend directions appear.
- Lost reasons: Price 35 %, Competitor 25 %, No decision 20 %, Timing 12 %, No budget 5 %, Other 3 %.
- Every owner except the Admin has at least 5 closed opportunities in the last 12 months (the "Win rate by owner" chart needs them); Peter Juhl (inactive) has 3.

**Open pipeline, about 70, a funnel shape:**
| Stage | Count | Notes |
|---|---|---|
| Qualification | 25 | |
| Needs analysis | 18 | |
| Proposal | 14 | |
| Negotiation | 9 | |

- About 6 with a close date in the past ("Past close date"), about 10 closing this month, the rest over the next 9 months.
- The demo persona owns about 12 open opportunities across all stages.
- One opportunity with an amount of 0, one very large (top 1 % of value), one with no primary contact.

**Amounts and currencies** (base DKK): DK accounts in DKK, SE in SEK, DE/IT/PL in EUR, US and CN in USD. Rough value mix: DKK 30 %, EUR 40 %, SEK 12 %, USD 18 %. Ranges by segment (in the record currency's equivalent): Wind energy 300,000–6,000,000 DKK; Building materials 100,000–2,500,000 DKK; Industry 50,000–1,500,000 DKK. Use round-ish amounts with some decimals (e.g. EUR 48.750,00).

**Lead sources:** Referral 25 %, Event 20 %, Website 20 %, Outbound 20 %, Partner 15 %.

**History rows:** for about 40 % of opportunities, 1–4 stage changes over time, and for about 10 % an owner change, so record timelines show "Stage changed from … to …" and "Owner changed from … to …".

## Activities: 250 minimum, about 900 recommended
- **Past, done:** 2–6 per opportunity and some on accounts and contacts only. Types: Call 40 %, Meeting 25 %, Task 25 %, Note 10 % (notes have no due date).
- **Demo persona (Mette Kjærgaard), open:** 2 overdue (1 and 5 days), 3 due today (two with times, e.g. 10:00 and 14:30, one without), 5 in the next 7 days. Mix of calls, meetings and tasks; linked to her opportunities, contacts and accounts.
- **Every other active user, open:** 0–3 overdue, 1–4 due today, 2–6 in the next 7 days.
- Edge cases: a subject of 160 characters, a description of ~3,000 characters, an activity linked only to a contact, a done activity due in the future.

## FX rates (illustrative, not market data)
One rate per month for EUR, SEK and USD from May 2025 to November 2026, "1 unit = X DKK":
- EUR 7.4550–7.4650 (small movement),
- SEK 0.6600–0.7100,
- USD 6.3500–7.0500.

Two variants:
- **Clean (for product-owner demos):** every month present, so no FX notes appear.
- **Edge (for QA):** SEK for November 2026 missing (the "Uses latest rate for 1 currency" note appears) and NOK enabled with 2 open opportunities in NOK and no NOK rates at all (the "2 opportunities in NOK not included: no FX rate" note appears, with "Add FX rates" for Admins).

## What "alive" should look like (acceptance checks)
After loading the clean variant, signed in as the demo persona (a Manager who owns records) and as the Admin:
1. All six KPI cards show non-zero values; Won this month, Won this quarter and Win rate show a trend line; the two Won cards show a 12-month sparkline with visible variation.
2. My day shows Overdue (2), Today (3) and Next 7 days (5) for the demo persona.
3. The funnel and the board show four populated columns with decreasing counts; at least one card shows "Past close date".
4. All five Analytics charts have data for "This quarter", "This year" and "Last 12 months".
5. Account lists show all three segment chips; record pages show timelines with activities and stage changes.
6. The no-team user sees only their own records; the inactive owner still shows by name with "(inactive)" on their records; Users & teams shows the "No team" badge and one inactive user.
7. Switching the locale to en-GB changes every number and date; nothing is hard-coded.
