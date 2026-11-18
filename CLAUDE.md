# Project: [Project name]

> Template: replace everything in [brackets]. Delete lines that don't apply. Keep this file short —
> every agent reads it in every session.

## 1. Purpose
- **What we are building:** [One or two sentences: the system and the problem it solves]
- **Users:** [Who uses it, e.g. finance controllers in 12 subsidiaries, ~50 users]
- **Business goal / success measure:** [e.g. cut month-end close by 2 days; replace manual Excel consolidation]
- **Product owner (final decisions):** [Name / role]

## 2. Scope
- **In scope (this phase):** [Key capabilities]
- **Out of scope:** [What we will explicitly not build now]
- **Hard deadline / milestones:** [Date or "none"]

## 3. Technology stack
- **Language / framework:** [e.g. TypeScript, Next.js / Python, FastAPI]
- **Database:** [e.g. PostgreSQL]
- **Hosting / cloud:** [e.g. Azure, Cloudflare, on-prem]
- **Authentication:** [e.g. Microsoft Entra ID SSO — never build our own login]
- **Integrations:** [e.g. ERP, Power BI, bank APIs]
- **Approved for new dependencies:** only well-maintained, widely used libraries with permissive licences
  (MIT, Apache-2.0, BSD). Anything else needs architect recommendation and product owner approval.

## 4. Commands
| Task | Command |
|---|---|
| Install | `[e.g. npm install]` |
| Run locally | `[e.g. npm run dev]` |
| Unit tests | `[e.g. npm test]` |
| End-to-end tests | `[e.g. npm run e2e -- --trace off]` |
| Lint / format | `[e.g. npm run lint]` |
| Build | `[e.g. npm run build]` |

## 5. Repository structure
- `src/` – application code (developer only)
- `tests/` – unit and integration tests; `e2e/` – end-to-end tests (QA)
- `docs/adr/`, `docs/architecture/` – design decisions (architect)
- `docs/ux/` – user flows, UI specs, prototypes (UX designer)
- `infra/`, `.github/workflows/` – infrastructure and pipelines (DevOps)

## 6. Coding conventions
- Follow the existing style; formatter and linter decide, not personal preference.
- Clear names; small functions; comments explain *why*, not *what*.
- All external input is validated; errors are handled explicitly, never swallowed.
- No secrets, keys or customer data in code, tests, logs or git history. Use environment variables / secret store.
- Every new feature or fix has automated tests.
- Small, focused commits with clear messages: `type: short description` (feat, fix, docs, test, chore, refactor).

## 7. Definition of done
A task is done only when all of the following are true:
- [ ] Acceptance criteria met and verified
- [ ] Unit tests added; full test suite passes
- [ ] QA verdict PASS (no open Critical/High defects)
- [ ] Security verdict PASS (no open Critical/High findings)
- [ ] UX review PASS (user-facing changes only)
- [ ] CI pipeline green
- [ ] Relevant docs / ADRs updated

## 8. Non-functional requirements
- **Security:** OWASP Top 10 addressed; least-privilege access; all traffic over HTTPS.
- **Data protection:** [GDPR applies — personal data minimised, retention defined, EU hosting: yes/no]
- **Financial data:** [e.g. full audit trail on changes to financial figures; no silent overwrites; reconciles to source]
- **Access control:** [Roles, e.g. viewer / preparer / approver; segregation of duties where relevant]
- **Performance:** [e.g. key pages < 2 s; month-end batch < 30 min]
- **Availability:** [e.g. business hours, CET; critical during close days 1–5]
- **Accessibility:** WCAG 2.2 AA.
- **Language / locale:** [e.g. UI in English; Danish number and date formats; multi-currency: DKK, EUR, …]

## 9. Approvals required from the product owner
- Production deployments and production data migrations
- Architecture decisions that are hard to reverse (database, hosting, core frameworks, vendors)
- Scope changes, new paid services or new third-party data sharing
- Accepting any open Critical/High defect or security finding

## 10. Environments
| Environment | Purpose | URL | Deploys |
|---|---|---|---|
| Dev | Development | [url] | Automatic on merge to `main` |
| Test | QA / user acceptance | [url] | Automatic on release candidate |
| Prod | Live | [url] | Manual, product owner approval only |

## 11. Known decisions and constraints
- [e.g. ADR-0001: PostgreSQL chosen over SQL Server — see docs/adr/0001-database.md]
- [e.g. Must integrate with existing ERP; no changes to the ERP side]
