# WordOS documentation

Distilled from the seven source documents in `../WordOS Decumentation/` (read in full).
Read in this order:

| File | Read it when |
|------|--------------|
| [`08-FINAL-SPECIFICATION.md`](08-FINAL-SPECIFICATION.md) | **You want to know what the application does.** The finished behaviour, section by section. |
| [`00-PROJECT-PLAN.md`](00-PROJECT-PLAN.md) | You need the product, the architecture and the nine non-negotiable rules. |
| [`01-PHASES.md`](01-PHASES.md) | You need the build order and what "done" means for each phase. |
| [`02-PROGRESS.md`](02-PROGRESS.md) | **Always.** The live state: what is built, what is deferred, what is next. |
| [`03-DECISIONS.md`](03-DECISIONS.md) | You hit a judgement call or a contradiction between documents. |
| [`04-DATA-MODEL.md`](04-DATA-MODEL.md) | You are building the database or a model. |
| [`05-API-CONTRACT.md`](05-API-CONTRACT.md) | You are building or calling an endpoint. |

The source documents remain authoritative for **intent**. Where they conflict, the resolution is
recorded as an ADR in `03-DECISIONS.md` — never silently.

`08-FINAL-SPECIFICATION.md` is authoritative for **behaviour**: `00-PROJECT-PLAN.md` and
`01-PHASES.md` describe what was planned and in which order, and the plan was followed, but where
an intention and the built behaviour differ it is the specification and the ADR behind it that
describe the product.
