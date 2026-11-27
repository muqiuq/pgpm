# PGPM – Build Plan

> Status: v1.0 (2026-11-27)
> Specification: [01-functional-overview.md](01-functional-overview.md) · [02-data-model.md](02-data-model.md) · [03-implementation-reference.md](03-implementation-reference.md) · [04-innovation-methodology.md](04-innovation-methodology.md)

## Status (2026-11-27)

| Phase | State |
|---|---|
| 0 Bootstrap | done |
| 1 Identity and rights | done |
| 2 Sync core | done, integrated (client and server aligned, e2e and contract tests) |
| 3 Items, lists, detail pages | done, integrated |
| 4 Configuration and method | done (server and client; packages `core`, `innovation-ai`) |
| 5 Maps | done (canvas, matrix, templates, wired to the local database) |
| 6 Directory and MCP | done, pulled forward after phase 2 (the dev-tenant check, P1, is by hand) |
| 7 Hardening and release 1.0 | in progress in three tracks: H1 MCP tools, System page, security review, performance, e2e robustness · H2 client polish and accessibility · H3 deploy scripts (`init`, `backup`, `restore`, `update`, `info`), the `deploy` test group, CI with caches and the image release on tags, README, [admin guide](admin-guide.md), [user guide](user-guide.md), [release notes](release-notes-1.0.md), version 1.0.0 |

Open before production: P2 (host, domain, TLS, proxy) and P3 (image name, registry and its secrets).

## 1. Approach

- **Walking skeleton first, sync core second.** Every feature in PGPM is
  offline-first and live; the sync engine (outbox, push/pull, live WebSocket,
  conflicts, sharing, evictions) is the riskiest part and everything depends on
  it. It is built in phase 2 with a single entity (notes) and then reused by
  every later feature, so no feature is ever built "online first".
- **Vertical slices**: each phase ends with something usable in the browser, a
  green `./run-tests.sh`, and a container that builds with `--locked-mode`.
- **Copy, then adapt**: code is taken from PNMTD / GGITSV / PLM as listed in
  doc 03 and renamed (`pnmt` → `pgpm`); deviations are recorded in doc 03 §15.
- **Docs stay the spec**: when the implementation has to differ, the doc is
  updated in the same change.

## 2. Prerequisites

| # | What | Needed by | Owner |
|---|---|---|---|
| P1 | Entra app registration for **dev** (doc 03 §5.5): redirect `http://localhost:7330/signin-oidc`, client secret, delegated `User.Read`, application `User.Read.All` + admin consent, app roles for bound groups | phase 1 (SSO), phase 6 (directory) | Philipp |
| P2 | Production host, domain, TLS, reverse proxy with WebSocket support (`Upgrade` headers, idle ≥ 60 s) | phase 7 (spike S3 earlier) | Philipp |
| P3 | Container image name (PNMTD: `uisach/pnmtd`) – proposal `uisach/pgpm` | phase 0 | Philipp |
| P4 | Dev port – proposal **7330** (PNMTD uses 7327) | phase 0 | – |
| P5 | Default sharing of new items (doc 01 §18.2 Q1) – proposal: creator's groups with *edit* | phase 3 | Philipp |

Nothing else blocks phase 0.

## 3. Phases

### Phase 0 – Bootstrap (walking skeleton)

**Scope**
- Repository layout of doc 03 §2.3: `PGPM.slnx`, `PGPM/`, `PGPM.Lib/`,
  `PGPM.WebUI/`, `PGPM.Tests/`, `PGPM.Contract.Tests/`, `deploy/`, `tools/`.
- From PNMTD: `global.json`, `Directory.Build.props`, `Directory.Packages.props`
  (trimmed to the packages of doc 03 §2.1 + `EFCore.NamingConventions`,
  `Microsoft.AspNetCore.Authentication.OpenIdConnect`), `dotnet-tools.json`,
  `.gitignore`, `.dockerignore`; `.editorconfig` after GGITSV/PLM (PNMTD has
  none, doc 03 §15 #19).
- Backend skeleton: `Program.cs` (CLI switch, feature registration, middleware
  order of doc 03 §3.1 incl. security headers/CSP and `UseWebSockets`),
  `PgpmDbContext` (snake_case, UTC converter), `DatabaseMigrationService`,
  design-time factory, `/healthz`, `ApiException` + ProblemDetails, OpenAPI
  generation into `PGPM.WebUI/src/api/openapi.json`.
- Frontend skeleton from PNMTD.WebUI: `package.json` (trimmed), `vite.config.ts`,
  `src/design/{preset.ts,base.css}`, `stores/preferences.ts`, `layouts/`
  (`AppLayout`, `AuthLayout`, `navigation.ts` with the PGPM menu), router + guard,
  `components/{PageHeader,TabNav,ListTable,FilterBar,InlineField,MoreMenu,EmptyState,ErrorState}.vue`,
  `composables/{useServerTable,useActions,useColumnWidths}.ts`, `api/client.ts`;
  empty pages for every menu entry.
- PWA: `vite-plugin-pwa` (`injectManifest`), `src/sw.ts` with the PLM behaviours
  (doc 03 §11.1), manifest, icons, update toast, install entry.
- Ops: `Dockerfile` (3 stages, Ubuntu runtime), `deploy/docker-compose.yml`,
  `start.sh` / `stop.sh` / `run-tests.sh` / `tools/{test-db.sh,npm.sh}` adapted
  from PNMTD, CI workflow (`.github/workflows`).

**Done when**
- `./start.sh` serves the app shell at `http://localhost:7330/`; it installs as a
  PWA and the shell opens offline.
- `./run-tests.sh` is green (one MSTest, one Vitest, one Playwright test).
- `docker build` succeeds with `--locked-mode` and `--network=none` build steps.

### Phase 1 – Identity and rights

**Scope**
- PNMTD: `users`, `groups`, `user_groups`, `group_rights`, `PasswordHasher`,
  `TotpService`, cookie session + `SessionCache` + `SessionRules` +
  `SessionGates`, CSRF, login rate limit, lockout, initial admin seed,
  `GuardLastAdminAsync`, CLI `pgpm users …`, `deploy/*.sh` wrappers.
- Rights catalogue of doc 03 §6.2 (`RequireRight`, policy provider, frontend
  `rights.ts`, `session.ts` `can()`/`allows()`, route `meta.right`).
- Admin UI: users, groups & rights; account page (password, TOTP, sessions).
- Audit: `audit_log` + `AuditChangesInterceptor` + endpoint attributes;
  **endpoint audit test** (anonymous allowlist + rights matrix).
- GGITSV: Microsoft SSO on PNMTD cookie auth (doc 03 §5.2), auto-provisioning,
  app roles → groups, default group, admin link/unlink, sign-in page with
  Microsoft button, `FakeEntra` tests.
- `persons` (minimal) with one person per user; `/auth/me` returns `personId`,
  `groupIds`, rights.

**Done when**
- Local login incl. TOTP, lockout and forced password change work; SSO works
  against FakeEntra in tests and the dev tenant (P1) manually.
- Endpoint audit test and the ported SSO scenarios are green.

### Phase 2 – Sync core (highest risk)

**Scope – server**
- `ISyncedEntity`, `SyncStampInterceptor`, `sync_counter`, `SyncTables` registry.
- `SyncWriter`: insert/patch/delete, patch whitelists, per-field conflicts,
  duplicate pairs, delete batches, `force` delete, `sync_mutations` idempotency
  (doc 03 §11.5).
- `AccessService` + `item_shares` / sharing endpoint, re-stamp, `sync_evictions`,
  `access_epoch` resets (doc 03 §6.3, §11.6).
- `POST /api/v1/sync` (push + pull pages + evictions), `sync_clients`.
- `LiveSyncHub`, `ChangeFeed`, `GET /api/v1/sync/live` with origin check, ack flow
  control, heartbeats, revalidation, close codes (doc 03 §11.4, §11.6).
- Trash: `delete_batches`, `GET /trash`, restore, purge; `TombstonePruner`.
- `change_log` interceptor; History API for one item.

**Scope – client**
- Dexie schema (data model §14), repos with outbox in the same transaction,
  outbox coalescing, rebase on incoming rows.
- Sync engine (doc 03 §11.7): leader tab (Web Lock), HTTP loop, live WebSocket,
  fallback polling, backoff, evictions, reset, wipe on logout / user change.
- `SyncIndicator`, `SyncDialog`, `ConflictResolver` (doc 03 §11.8, §12.6),
  `ShareDialog` (create + change), Trash view.
- First synced entities: **notes** (in `items`), `groups`, `persons`,
  `user_settings`.

**Done when**
- Two browsers editing notes see each other's changes in < 1 s.
- Offline edits sync after reconnect; the conflict dialog resolves field
  conflicts (mine / theirs / edit / all) and delete conflicts.
- Unsharing a note removes it from the other user's device live; restore from
  Trash brings it back everywhere.
- Server integration tests, WebSocket tests, the three-client convergence
  property test and the two-context Playwright tests (doc 03 §11.13) are green.

### Phase 3 – Items, lists, detail pages

**Scope**
- Ideas, leads, projects (fixed fields of doc 01 §8–§10), number counters / keys.
- Generic statuses (`statuses`, `status_changed_at`, `max_days` aging, overdue
  markers) with admin UI; seeded `core` statuses (temporary seed until phase 4
  packages).
- List pages with `useLocalTable` (search, filters, sort, column chooser, bulk
  actions), saved views (personal + shared), last list state.
- Detail pages: header with share/owner, tabs, inline editing, read-only mode by
  access.
- **Markdown editor and renderer** ported from PNMTD (doc 03 §18): Visual/Markdown
  modes, textarea fallback, editing page, uploads through the offline file
  pipeline, *Link to an item…*, Mermaid, drafts, conflict diff, *… is editing*.
- Assignments, persons page (manual persons, *Persons: Edit*), attachments
  (chunked upload, sha256, thumbnails, cover images, blob cache, PNMTD
  `FileStore`), item links + Links tab + *Learnings* view, History tab, global
  search (Ctrl/⌘K), idea/lead → project conversion (fields come in phase 4).

**Done when** all four item types are usable end-to-end, offline and live, with
sharing, attachments, links, history and saved views.

### Phase 4 – Configuration and method features (R1–R8)

**Scope**
- Sections (`applies_to`, `default_for`), `section_groups`, field definitions incl.
  **rating** and **score** (F1, F2), `field_values`, `item_sections`, field widgets,
  `score.ts` + `ScoreEvaluator.cs` + shared vectors (doc 03 §16).
- Project templates: allowed statuses, sections, defaults, default shares,
  milestones and **gates** (F4), client-side template application.
- Milestones, gates, DoD with *n/a* + note (F7), gate decisions with status
  suggestion, *Gate decisions* list; timebox date fields (F6).
- Conversion carries sections and values.
- Admin UIs: statuses, sections & fields (incl. score editor with validation),
  templates.
- **Configuration packages** (doc 03 §17): format, `config_keys`, import with dry
  run, export, `ConfigPackageSeeder`; built-in `core` and `innovation-ai`
  (doc 04 §4.2 + Appendix A checklists, written in English).

**Done when** the funnel walkthrough works with the seeded package: ideas →
assessment (playbook example gives feasibility 58/65) → *Ready for review* →
review-board decision (section rights enforced) → convert → *AI use case* project
with gates G0–G3 → gate decisions with status changes; re-importing a package
changes nothing.

### Phase 5 – Maps

**Scope**
- Map list, sharing, trash; canvas renderer (SVG, `foreignObject` cards, LOD,
  culling); `usePanZoom`, `useMoving`, `geometry` from PNMTD; selection,
  rubber band, resize, snap, minimap.
- Elements: item cards (multiple placements), milestones/gates, timeline,
  rectangle, ellipse, line/arrow with connectors, text, image.
- Layers, z-order (fractional keys), toolbar, library panel, add new/existing,
  side panel (PNMTD drawer), undo/redo, clipboard, keyboard, visibility filter,
  default view, personal map views, links overlay, deep links.
- **Map templates** (F8) incl. seeded templates; **matrix maps** (F3): layout,
  collision pass, tray, quadrant badges/column/filter.
- Live co-editing and per-map conflict grouping.

**Done when** two users co-edit a map live; a 1,000-element map stays fluid
(target 60 fps pan/zoom on a mid-range laptop, measured); the seeded matrix map
shows the assessed ideas in the right quadrants and cards move when ratings
change; maps work offline.

### Phase 6 – Directory and MCP

**Scope**
- `DirectorySyncTask` (Graph delta, whole tenant, doc 03 §8), *Microsoft
  integration* admin page, person ↔ user linking.
- MCP server (doc 03 §7): tokens, `McpTokenHandler`, capped rights and access,
  tool catalogue of §7.3, `mcp_log`, strikes, rate limits, account MCP card,
  admin MCP pages.

**Done when** the dev tenant syncs completely; an MCP client can search items,
create ideas with ratings, record a gate decision and add an item to a map, and
the changes appear live in the browser.

**Status (pulled forward after phase 2):** the directory sync, the token model, the
server, the admin and account pages are built (doc 03 §7.4, §8.3); the tools work
on notes (`search_items`, `get_item`, `create_note`, `update_item`,
`search_persons`, `list_config`). Phases 3–5 add their tools as specs (doc 03 §7.4
"Adding a tool"); the dev-tenant check (P1) is still by hand.

### Phase 7 – Hardening and release 1.0

**Scope**
- Security review (CSP, origin checks, CSRF, rate limits, file validation,
  access checks on every sync table and endpoint).
- Performance: 10,000 items in lists, 1,000-element maps, 50 concurrent live
  clients on one instance.
- Backup/restore (`deploy/backup.sh`, `restore.sh` incl. files), update flow
  (`app/version` broadcast), System page, accessibility pass, phone layout.
- Admin guide and a short user guide; production deployment (P2).

**Done when** the release checklist is complete and PGPM runs in production.

### Later (version 1.x)

KPI tracking (F9), funnel/ambition dashboard (F10), MCP assessment drafts (F11),
tags, export, import, presence on maps, push notifications (doc 01 §17).

## 4. Definition of done for every feature

- Works **offline and live** (data features) – covered by a Playwright case.
- Access is checked on the server (sync push/pull, REST, MCP); new endpoints are
  in the endpoint audit matrix.
- Changes appear in History / audit log where doc 01 says so.
- OpenAPI regenerated (`check:api` green); no warnings (`TreatWarningsAsErrors`).
- Light and dark theme; usable below 600 px.
- Docs updated if behaviour differs from the spec.

## 5. Test strategy

| Level | Tools | Focus |
|---|---|---|
| Unit (client) | Vitest | `score.ts`, `matrixLayout.ts`, fractional keys, outbox coalescing, rebase, `usePanZoom` |
| Unit (server) | MSTest | `SyncWriter` rules, `ScoreEvaluator`, access predicates, Graph mapping, package diff |
| Integration | MSTest + `WebApplicationFactory` + throwaway Postgres (`tools/test-db.sh`) | sync endpoint, live WebSocket (`CreateWebSocketClient`), sharing/evictions/reset, trash, SSO (`FakeEntra`), MCP |
| Property | MSTest | three-client convergence with random offline mutations |
| Contract | `PGPM.Contract.Tests` | running container: health, login, sync round trip |
| End-to-end | Playwright | offline mode, two contexts live, conflict dialog, funnel walkthrough, maps |

All groups run through `./run-tests.sh` (and CI), dotnet tests in a time zone east
and west of UTC as in PNMTD.

## 6. Risks

| Risk | Mitigation |
|---|---|
| Sync correctness (conflicts, re-stamp, evictions, resets) | built first (phase 2) on PLM's proven cursor logic; property test; whitelist round-trip test |
| WebSocket blocked by proxies or networks | HTTP polling fallback is always available; heartbeat + close codes; spike S3 |
| SVG performance with many `foreignObject` cards | spike S2 before phase 5; LOD and culling |
| Re-stamp cost on sharing changes of large maps | subtree update in one statement; measured in phase 7 |
| Single server instance | documented; `LISTEN/NOTIFY` path for scale-out |
| Browser storage eviction (iOS) | `navigator.storage.persist()`, install as PWA, 200 MB blob budget |
| Entra admin consent not available early | `FakeEntra` for development and tests; directory sync behind a flag |

## 7. Spikes

| # | Question | When | Time box |
|---|---|---|---|
| S1 | Dexie `liveQuery` + Vue with 10,000 items: list filtering and detail reactivity fast enough? | phase 0/1 | 1 day |
| S2 | SVG canvas with 1,000 `foreignObject` cards: pan/zoom frame rate, LOD threshold | before phase 5 (during phase 3) | 2 days |
| S3 | WebSocket through the planned reverse proxy and corporate network | phase 2 | ½ day |
| S4 | Editor chunks offline: Crepe, Monaco (+ workers) and Mermaid in the precache – size, first install time, editing in airplane mode | phase 3 | ½ day |

## 8. Phase 0 checklist (next session)

1. Create branch `phase-0-bootstrap`.
2. Copy and trim `global.json`, `Directory.Build.props`,
   `Directory.Packages.props`, `dotnet-tools.json`, `.editorconfig`,
   `.gitignore`, `.dockerignore` from PNMTD.
3. Create `PGPM.slnx` with `PGPM` (web), `PGPM.Lib` (classlib), `PGPM.Tests`
   (MSTest runner exe), `PGPM.Contract.Tests`.
4. `Program.cs`, `PgpmDbContext`, `DatabaseMigrationService`, first migration,
   `/healthz`, error handling, OpenAPI build output.
5. Create `PGPM.WebUI` from PNMTD.WebUI pieces (list in phase 0 scope), PGPM menu,
   empty pages, `vite-plugin-pwa` + `src/sw.ts` + manifest.
6. `Dockerfile`, `deploy/docker-compose.yml`, `start.sh`, `stop.sh`,
   `run-tests.sh`, `tools/test-db.sh`, `tools/npm.sh`, CI workflow.
7. One test per level; `./run-tests.sh` green; offline shell check in Playwright.
