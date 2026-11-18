# ADR-0005: Currency conversion with Admin-entered monthly FX rates to a base currency

- **Status:** Accepted for the approach (product owner decision in the plan: Admin enters the rates). Proposed for the conversion rules below.
- **Date:** 2026-11-18
- **One-way door:** No. The rates source can later be replaced by an ERP or ECB feed that writes the same table.

## Context
- Opportunities carry `Amount` and `CurrencyCode`.
- The dashboard and analytics show totals in the base currency (`currency.base` setting).
- The CFO wants rates controlled internally, not pulled from a third party.
- The figures are indicative pipeline numbers, not ledger postings. They must still be explainable and reproducible.

## Options considered
| Option | Pros | Cons |
|---|---|---|
| **A. Admin enters one rate per currency per month; conversion at query time** | Controlled and auditable. No external dependency. Changing a rate restates history consistently. | Manual monthly task. Missing months must be handled. |
| B. Daily rates from an external feed (ECB) | No manual work | New outbound integration and dependency. Rates may not match group policy rates. Needs product owner approval (CLAUDE.md §9). |
| C. Convert and store base amounts when an opportunity is saved | Fast reads | Values go stale when rates change. A base currency change needs a mass update. Two figures can disagree. |
| D. Show per-currency totals only | Simple | Fails the requirement. |

## Decision
Option A. The table is `crm.FxRates (BaseCurrency, CurrencyCode, RateMonth, Rate)` (DDL in overview §4.1).

- **Semantics:** 1 unit of `CurrencyCode` = `Rate` units of `BaseCurrency` for the calendar month `RateMonth`. `RateMonth` is the first day of the month. The base currency always has an implicit rate of 1 and is never stored.
- **Rate lookup:** `fx.ts` is pure and unit-tested. Conversion uses the rate of the **reference month**, or else the **latest earlier month**, with the current `BaseCurrency`.
  - Won and lost opportunities: reference month = month of `CloseDate`.
  - Open pipeline (open, weighted): reference month = **current month**, as a "today's value" view.
- **Missing rate:** if no rate exists for that month or any earlier month:
  - The amount is **excluded** from the total.
  - The tile or chart shows: "Excludes 3 opportunities in SEK: no exchange rate. Add rates in Settings > Exchange rates."
  - There is never a silent 1:1 conversion.
- **Computation:**
  - SQL aggregates `SUM(Amount)` per `(CurrencyCode, reference month)` under RLS, using exact decimal arithmetic.
  - `fx.ts` multiplies each subtotal by its rate and sums the results.
  - Rounding to 2 decimals happens only at the end (half away from zero).
  - JavaScript doubles are acceptable at this magnitude (under 10^13) for indicative totals. Tests assert results to the cent.
- **Base currency change:**
  - Rates are keyed by `BaseCurrency`, so old rates are kept but ignored. Admin must enter rates against the new base.
  - The Settings page warns: "Changing the base currency hides totals in other currencies until you add rates for {new base}."
- **Entry UI (`/settings/fx`):**
  - A month picker and one row per enabled non-base currency.
  - The rate is parsed with the locale and must satisfy `0 < rate ≤ 1,000,000`.
  - It accepts up to 8 decimals.
  - Each save is audited (`EntityType = 'fxrate'`, old and new value).
  - Admin-only, enforced by route `requireRole('Admin')` and by the `fn_AdminWrite` RLS block predicate.
- **Transparency:** chart and table footers state "Converted to DKK at monthly rates; open pipeline at {Month YYYY} rates."

## Consequences
- **Easier:**
  - Totals are reproducible from `FxRates` plus the opportunities.
  - Rates match group policy.
  - Changes are audited.
- **Harder:**
  - Admin must enter rates every month. Mitigation: the dashboard shows Admin a notice when the current month has no rate for an enabled currency.
- **Risks:**
  - A wrong rate entered by hand. Mitigations: audit log, and a preview showing the inverse rate on entry.
  - An excluded currency could understate totals. Mitigation: always shown explicitly.
- **Reversibility:** high. A future feed (ERP or ECB) inserts rows into the same table. That would be a new integration and needs product owner approval.
