# WordOS Admin

**Product Intelligence & Learning Analytics** — the Arabic (RTL) admin website for WordOS.
Decision record: `docs/03-DECISIONS.md` ADR-125. API: `docs/05-API-CONTRACT.md` §Admin intelligence.

```bash
npm install
npm run dev        # http://localhost:5180/admin/ — proxies /api to the local API (./wordos start)
npm run dev:mock   # http://localhost:5181/admin/ — no backend, invented data, bannered
npm run build      # typecheck + production bundle in dist/
```

In production the API serves `dist/` at `/admin` (the Dockerfile builds it).

| Path | What |
|---|---|
| `src/api/` | `AdminApi` contract, `HttpAdminApi` (real, with token refresh) and `MockAdminApi` |
| `src/pages/` | the nine sections and their drill-downs |
| `src/components/charts/` | hand-built SVG charts — line, columns, horizontal bars, donut, table view |
| `src/lib/filters.tsx` | global filters, kept in the URL |
| `src/styles.css` | every token, light and dark |

Flow: **Overview → Analysis → Investigation → User 360 → Timeline / Event.**
No figure is computed here; the site formats and draws what `/api/admin/intel` returns.
