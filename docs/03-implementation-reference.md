# PGPM – Implementation Reference

> Status: v0.2 (2026-11-26) – live sync (WebSocket), object sharing, user-resolved
> conflicts, trash, innovation features R1–R8 (doc 04)
> Related: [01-functional-overview.md](01-functional-overview.md) · [02-data-model.md](02-data-model.md) · [04-innovation-methodology.md](04-innovation-methodology.md) · [05-build-plan.md](05-build-plan.md)

This document records **what PGPM takes over from existing projects**, where it
lives there, and how it has to be adapted. It is the "copy list" for the
implementation.

| Tag | Source | Path | Taken for |
|---|---|---|---|
| **[PNMTD]** | PNMT | `~/source/PNMTD` | tech stack, repo layout, UI design, users, groups, rights, local login, MCP token model, files, map canvas, side panel, API conventions, tests, deployment |
| **[GGITSV]** | GGITSV | `~/source/ggitsv` | Microsoft SSO (Entra ID, OIDC), Entra app roles → groups, worker/signal pattern, fake IdP for tests |
| **[PLM]** | PLM | `~/source/plm` | PWA, service worker, IndexedDB, background sync protocol, attachment upload/caching, drafts – **without** the E2E encryption |
| **[NEW]** | – | – | designed for PGPM (directory sync via Graph, multi-user sync visibility, infinite-canvas editing) |

Paths below are relative to the respective project root.

---

## 1. Overview: which piece comes from where

| Concern | Source | Key files |
|---|---|---|
| .NET/Vue stack, central packages, lock files | PNMTD | `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `PNMTD.WebUI/package.json` |
| Hosting, middleware order, CLI mode | PNMTD | `PNMTD/Program.cs` |
| EF Core, migrations at startup, custom migrations | PNMTD | `PNMTD/Data/PnmtdDbContext.cs`, `PNMTD/Data/Migrations/`, `DatabaseMigrationService` |
| Users, password hashing, cookie session, CSRF, TOTP, lockout | PNMTD | `PNMTD/Auth/*`, `PNMTD.Lib/Security/{PasswordHasher,TotpService}.cs` |
| Groups and rights (area/level) | PNMTD | `PNMTD/Auth/Rights/{Rights,RequireRight}.cs`, `PNMTD/Auth/GroupService.cs` |
| MCP server + token model | PNMTD | `PNMTD/Mcp/*`, `PNMTD/Tools/*`, `docs/mcp-plan.md` |
| API conventions, errors, OpenAPI → TS | PNMTD | `PNMTD/Api/ApiSetup.cs`, `PNMTD/Api/ApiException.cs`, `PNMTD.WebUI/src/api/client.ts` |
| Audit log + field-diff interceptor | PNMTD | `PNMTD/Audit/*`, `AuditChangesInterceptor` |
| File storage on disk | PNMTD | `PNMTD/Files/FileStore.cs`, `PNMTD/Api/V1/FilesController.cs` |
| UI design system, layout, navigation | PNMTD | `PNMTD.WebUI/src/design/{preset.ts,base.css}`, `src/layouts/{AppLayout.vue,navigation.ts}` |
| Tables, filter bar, detail pages, inline editing | PNMTD | `src/components/{ListTable,FilterBar,PageHeader,TabNav,InlineField,MoreMenu}.vue`, `src/composables/{useServerTable,useActions}.ts` |
| Map canvas: pan/zoom, drag, side drawer | PNMTD | `src/composables/usePanZoom.ts`, `src/components/graph/{useMoving,geometry,focus}.ts`, `src/views/network/{MapTab,DevicePanel}.vue`, `src/views/admin/responsibilities/overview/*` |
| Microsoft SSO | GGITSV | `src/Ggitsv.Api/Authentication/*`, `src/Ggitsv.Infrastructure/Identity/MicrosoftSignInService.cs` |
| Entra app role → group membership | GGITSV | `MicrosoftSignInService.cs:145-169`, `src/Ggitsv.Domain/Groups/Group.cs` |
| Sign-in page with Microsoft button | GGITSV | `frontend/ggitsv-web/src/features/auth/SignInPage.vue`, `src/api/auth.ts` |
| Background worker + wake-up signal | GGITSV / PNMTD | `ServiceRequestWorker.cs`, `ServiceRequestSignal.cs` / `Mcp/McpTokenExpiryTask.cs` |
| Fake Entra for tests | GGITSV | `tests/Ggitsv.Api.Tests/Support/FakeEntra.cs` |
| Service worker, manifest, install prompt | PLM | `public/sw.js`, `src/Plm.Api/Http/Manifest.cs`, `public/js/ui/install.js` |
| IndexedDB schema + migrations | PLM | `public/js/db.js` |
| Sync protocol (seq cursor, push+pull in one call) | PLM | `public/js/sync.js`, `src/Plm.Core/Sync/{SyncService,SequenceAllocator}.cs`, `docs/19-client-server-communication.md` |
| Attachment upload (chunked, sha256), on-demand cache | PLM | `public/js/files.js`, `src/Plm.Api/Actions/BlobActions.cs` |
| Drafts / unsaved changes | PLM + PNMTD | `public/js/drafts.js`, `docs/13-unsaved-changes.md` / `components/markdown/EditingPage.vue` |
| Markdown editor (Visual + Markdown), renderer, diagrams, uploads | PNMTD | `PNMTD.WebUI/src/components/markdown/*`, `docs/locations-knowledge-plan.md` §5 (§18) |
| Directory sync via Microsoft Graph | NEW | – (no project has it; GGITSV only calls Graph `/me`) |
| Live sync channel (WebSocket) | NEW | – (PLM polls on purpose; PNMTD has no live channel) |
| Object sharing per group, trash/restore, user-resolved sync conflicts | NEW | – |
| Score fields, matrix maps, gates, configuration packages | NEW | – (method: doc 04) |

---

## 2. Tech stack and repository layout [PNMTD]

### 2.1 Backend

| Item | Value (as in PNMTD) |
|---|---|
| SDK | .NET 10 (`global.json`: `10.0.100`, `rollForward: latestFeature`, `"test": {"runner": "Microsoft.Testing.Platform"}`) |
| Build props | `net10.0`, `LangVersion latest`, `Nullable enable`, `ImplicitUsings`, `Deterministic`, `RestorePackagesWithLockFile=true` (Docker restores `--locked-mode`), **`TreatWarningsAsErrors=true`** from day one (PNMTD still carries a `NoWarn` list for its pre-1.0 code) |
| Packages | central (`ManagePackageVersionsCentrally`, `CentralPackageTransitivePinningEnabled`), Microsoft packages on 10.0.x |
| Web | ASP.NET Core controllers, `Microsoft.AspNetCore.OpenApi` 10.0.x, `Microsoft.Extensions.ApiDescription.Server` (writes `openapi.json` at build) |
| DB | PostgreSQL 18, EF Core 10.0.x, `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3, code-first migrations, `dotnet-ef` as local tool |
| MCP | `ModelContextProtocol.AspNetCore` 2.4.0 |
| OIDC | `Microsoft.AspNetCore.Authentication.OpenIdConnect` 10.0.x **[GGITSV]** |
| Other | Markdig (Markdown → HTML for MCP/export), MailKit (later: notifications) |
| Logging | console + debug providers (no Serilog), business events in DB `audit_log` |
| Tests | MSTest 4.x (runner mode, `OutputType Exe`), `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.Extensions.TimeProvider.Testing` |

Additions for PGPM: `EFCore.NamingConventions` (snake_case, as in GGITSV/PLM –
see §15), `pg_trgm` extension for title search.

### 2.2 Frontend

| Item | Value (as in PNMTD `PNMTD.WebUI/package.json`) |
|---|---|
| Framework | Vue 3.5 `<script setup lang="ts">`, TypeScript 5.9 strict, `vue-tsc` |
| Build | Vite 8, `@vitejs/plugin-vue` |
| Components | PrimeVue 4.5.5 styled mode, `@primeuix/themes` Aura preset, PrimeIcons 7 |
| State | Pinia 4 (client state), `@tanstack/vue-query` 5 (online-only server state) |
| Router | vue-router 5, `createWebHistory`, lazy routes |
| API client | `openapi-fetch` + `openapi-typescript` (types generated from backend `openapi.json`) |
| Markdown | Milkdown Crepe 7 (visual), Monaco 0.57 (source, diff), Mermaid 12 (diagrams), unified/remark/rehype with `rehype-sanitize` and `rehype-highlight` (rendering), DOMPurify – all lazy chunks (§18) |
| Fonts | `@fontsource-variable/inter`, `@fontsource-variable/jetbrains-mono` (self-hosted, CSP `'self'`) |
| Sanitising | `dompurify` for rendered Markdown |
| Tests | Vitest 5 + jsdom + `@vue/test-utils`, Playwright (chromium 1440×900, `workers: 1`), ESLint 10, Prettier |

Additions for PGPM:

| Package | Why |
|---|---|
| `dexie` 4 | IndexedDB wrapper with typed tables, compound indexes and `liveQuery` (reactive reads). PLM hand-writes IndexedDB (`public/js/db.js`); in a TypeScript/Vue code base Dexie gives the same model with less code and makes every list/map reactive to sync. The PLM rules (additive migrations, `onblocked` handling, `navigator.storage.persist()`) still apply. |
| `vite-plugin-pwa` (`injectManifest` mode) + `workbox-precaching`, `workbox-routing` | Generates the precache list from the Vite build. PLM maintains `SHELL` by hand and needed a lint (`tools/lint-sw.mjs`) after the list drifted by 13 files; with hashed Vite assets a generated manifest is the only sane option. The service worker logic itself follows PLM (§11.1). |
| `fractional-indexing` | z-order and sort keys (data model §10). |
| `uuid` (v7) | Client-side ids. |

### 2.3 Repository layout

```
pgpm/
├── PGPM/                     ASP.NET Core host, one project with feature folders (PNMTD style)
│   ├── Api/V1/               controllers + Contracts/ (DTO records)
│   ├── Auth/                 local login, sessions, rights, Microsoft SSO, AccessService (sharing)
│   ├── Audit/                audit_log, change_log, interceptors
│   ├── Cli/                  `pgpm users|groups|db …` (no web host)
│   ├── Config/               statuses, sections, fields, templates
│   ├── Data/                 DbContext, Migrations/, CustomMigrations/
│   ├── Directory/            Graph client + directory sync task (phase 6; namespace not `PGPM.Directory`, see §15 #23)
│   ├── Persons/              persons (`PGPM.Persons`): the user ↔ person link, manual persons
│   ├── Files/                FileStore, uploads, cleanup tasks
│   ├── Hosting/              middleware, security headers, static SPA
│   ├── Items/                notes, ideas, leads, projects, milestones
│   ├── Maps/                 maps, layers, elements, sharing
│   ├── Mcp/  Tools/          MCP server, token model, tool catalogue
│   ├── Packages/             configuration packages (import/export, built-in JSON)
│   └── Sync/                 sync endpoint, live WebSocket hub, SyncWriter, pull, evictions, trash
├── PGPM.Lib/                 PasswordHasher, TotpService, small shared helpers
├── PGPM.WebUI/               Vue app incl. src/sw.ts
├── PGPM.Tests/               MSTest + WebApplicationFactory (TestApp)
├── PGPM.Contract.Tests/      HTTP + Npgsql against a running container
├── deploy/                   docker-compose.yml, init.sh, backup.sh, … (PNMTD scripts)
├── spec/                     test vectors shared by the TypeScript and C# tests (score-vectors.json)
├── tools/                    test-db.sh, npm.sh (npm in a container)
├── docs/
├── Directory.Build.props  Directory.Packages.props  global.json  dotnet-tools.json
├── PGPM.slnx  Dockerfile  start.sh  stop.sh  run-tests.sh
```

### 2.4 Dev scripts, Docker, deploy

Copy from PNMTD and rename:

- `start.sh`: Podman by default (`ENGINE=docker`), builds `pgpm:dev`, runs
  Postgres + app on `http://localhost:7330/`; `WEB=dev` adds Vite HMR in a node
  container (port 5173); `SEED=1` demo data once a seed command exists (phase 3);
  settings from `dev.env` (0600, generated on first run with the DB password and
  `Auth__InitialAdmin__Password`). Rule: node never runs on the host
  (`tools/npm.sh`, volumes `pgpm-web-node-modules`, `pgpm-npm-cache`).
- `run-tests.sh`: groups `dotnet, web, contract, e2e, shell` (dotnet first: its
  Debug build writes `openapi.json`, which `web` checks the TS types against;
  `deploy` joins with the `deploy/*.sh` scripts), `--only`, `--list`,
  `step NAME cmd…` helper logging to `.tmpcheck/`; dotnet tests against a
  throwaway Postgres (`tools/test-db.sh`, port 55432, fsync off – a PostgreSQL
  another project already runs on that port, e.g. PNMTD's `pnmtd-test-pg`, is
  used as it is, the tests create databases of their own), run once in a time
  zone east and once west of UTC (`PGPM_TEST_TZ` and `TZ`). `contract` and `e2e`
  start stacks of their own through `start.sh` (`tools/contract.sh` on port 7332,
  `tools/e2e.sh` on 7331, Playwright in its image inside the app container's
  network so `localhost` is a secure context for the service worker).
- `Dockerfile`: 3 stages – `node:24-slim` (`npm ci --ignore-scripts`,
  `RUN --network=none npm run build`), `dotnet/sdk:10.0` (`restore --locked-mode`,
  copy `dist` to `wwwroot/`, publish with `--network=none`), `dotnet/aspnet:10.0`
  (Ubuntu – **not Alpine**: GGITSV found a musl/Podman DNS issue resolving
  `login.microsoftonline.com`), `USER app`, `/var/lib/pgpm/{keys,files}`, CLI
  wrapper `/usr/local/bin/pgpm`, `EXPOSE 8080`, `HEALTHCHECK` on `/healthz`.
- `deploy/docker-compose.yml`: `db` (postgres:18-alpine, `pg_isready`) + `pgpm`
  (`read_only: true`, tmpfs `/tmp`, `cap_drop: [ALL]`, `no-new-privileges`),
  volumes for pgdata, Data-Protection keys, and a bind mount for files.
  `deploy/*.sh` (PNMTD's, sharing `lib.sh`): `init.sh` (`.env` with generated secrets, the files folder for uid 1654),
  `backup.sh` (one archive `pgpm-<stamp>.tar`: `manifest.txt`, `SHA256SUMS`, `database.pgdump`, `keys.tar.gz`,
  `files.tar`; `--keep`), `restore.sh` (verifies the archive first; into an existing or a fresh stack; raises every
  user's `access_epoch`, so every device resets and none keeps rows or a cursor the database lost), `update.sh` (pull,
  backup, `up -d --wait`, `/healthz`, the release before and after), `info.sh`, and the user wrappers around
  `docker compose exec pgpm pgpm users …`. Tested by the `deploy` group (`tools/deploy-test.sh`, project
  `pgpm-deploytest`, port 7390). Reference for operators: `deploy/README.md`, `docs/admin-guide.md`.

PGPM is served at `/` (PNMTD serves its UI under `/pnmt-admin/`; a PWA is
simpler at the root scope).

---

## 3. Backend conventions [PNMTD]

### 3.1 Program.cs

- `Main`: if `args[0]` is a CLI command (`db`, `users`, `groups`, `directory`),
  run `Cli.CommandLine.Run` without a web host, else `RunApi`.
- Each feature registers via an extension method (`AddPgpmAuth`, `AddFiles`,
  `AddAudit`, `AddSync`, `McpSetup.AddPgpmMcp`, …).
- Hosted services are skipped when the entry assembly is `GetDocument.Insider`
  (OpenAPI generation at build).
- Middleware order (PNMTD):
  1. `UseForwardedHeaders` → security headers (CSP) → `UseApiErrors` → static SPA
  2. `UseRouting` → plain 404s for unknown `/api` → `UseRateLimiter` → `UseMcpOriginCheck`
  3. `UseWhen(path is not /mcp) → UseAuthentication` → `UseAuthorization`
     (fallback policy = authenticated user, deny by default) → `UseSessionGates`. The app marks its pipeline as having
     authentication (`HostingSetup.AuthenticationMiddlewareSet`): a `UseAuthentication` inside `UseWhen` doesn't count
     for `WebApplication`, which then put one of its own before everything – the static files included, where a stale
     cookie (the service worker's precache, sent before a password change) was answered with its deletion and wiped the
     fresh session (found at the phase 3 integration; `LoginTests.AStaleCookieOnTheFilesOfTheUiIsLeftAlone`).
  4. `/healthz` → `MapControllers` → `MapPgpmMcp` → SPA fallback (`index.html`)

CSP additions for PGPM (PNMTD allows only `'self'`): `worker-src 'self'`,
`manifest-src 'self'`, `img-src 'self' blob:` (images and thumbnails are shown
from IndexedDB via `URL.createObjectURL`; no `data:` – the phase-0 shell needs
none). Keep `assetsInlineLimit: 0` in `vite.config.ts`. The live channel is a
same-origin WebSocket (`wss://<host>/api/v1/sync/live`, `app.UseWebSockets()`
with `KeepAliveInterval = 25 s`); add the `wss://<host>` origin to `connect-src`
explicitly (older Safari versions don't match `ws(s)` with `'self'`): taken from
`PublicUrl` when set, else from the request's own host (`PGPM/Hosting/HostingSetup.cs`).
The UI's CSP goes on every response but `/api` and `/healthz` (API CSP
`default-src 'none'` + `no-store`). `Strict-Transport-Security: max-age=31536000` goes on every response of an https
request and, with an https `PublicUrl`, on all of them (the browser saw the proxy's https; docs/06 S-04). The
reverse proxy must forward `Upgrade`/`Connection` headers and allow idle
connections ≥ 60 s.

### 3.2 Database

- `AddDbContextFactory<PgpmDbContext>`; controllers get a scoped context,
  background tasks use the factory.
- `DatabaseMigrationService` is the **first** hosted service: refuses a DB that a
  newer version migrated, applies EF migrations, then runs `ICustomMigration`
  classes (found by reflection, `ShouldRun`/`Run`) – e.g. seeding default
  statuses, the default layer for old maps.
- `PgpmDesignTimeDbContextFactory` so `dotnet ef migrations add` never connects.
- All `DateTime` through a UTC converter into `timestamptz`; enums as
  lower-case text by convention (`LowerCaseEnumConverter`, data model §1 – the
  CHECK constraint comes with each table's migration); lazy loading off.
- snake_case also renames the migrations history's columns
  (`"__EFMigrationsHistory"(migration_id, product_version)`).
- Guid keys: Npgsql (EF 9+) generates UUID v7 on the client by default;
  `PGPM.Lib/Helper/Ids` (`New()`, `IsVersion7()`) for code and the sync push's
  id check.
- A Postgres unique violation becomes 409 `duplicate_key`.

### 3.3 API

- Routes `api/v1/<plural-kebab>`, action sub-resources
  (`POST /api/v1/items/{id}/convert`).
- Controllers inherit `V1ControllerBase` (`[ApiController]`,
  `[ApiExceptionFilter]`, `[CsrfProtection]`).
- Mutating actions carry `[Audit(AuditEvents.X)]` or `[NotAudited("reason")]`.
- DTOs in `Api/V1/Contracts/`, `sealed record` responses, request classes with
  writable fields only; entities are never returned.
- Paging `Paged<T>(Items, Page, PageSize, TotalCount)` – only needed for
  online-only lists (history, audit log, MCP log, users).
- JSON: web defaults (camelCase), `JsonStringEnumConverter`, `NumberHandling.Strict`.
- Errors: `ApiException(status, code, detail, errors?, extensions?)`, helpers
  `NotFound`, `Conflict`, `Invalid(field, msg)`, `MissingRight(area, level)`;
  `ApiValidator().Check(cond, "field", "msg").ThrowIfInvalid()` → 400
  `validation_failed` with per-field errors. Output: ProblemDetails with a stable
  `code`.
- OpenAPI: `AddOpenApi("v1")` (only `api/v1/`), transformers for camelCase query
  params and multipart bodies; build writes `PGPM.WebUI/src/api/openapi.json`;
  `npm run gen:api` → `schema.d.ts`; `check:api` fails if out of date.

Most PGPM data does **not** go through REST CRUD endpoints but through the sync
endpoint and the live channel (§11). REST endpoints exist for: auth, account,
admin (users, groups, rights, configuration, configuration packages, directory),
sharing (`PUT /api/v1/items/{id}/shares`, `PUT /api/v1/maps/{id}/shares`,
`POST /api/v1/items/{id}/owner`), trash (`GET /api/v1/trash`,
`POST /api/v1/trash/{batchId}/restore`, `DELETE /api/v1/trash/{batchId}`), files,
history, MCP token management, system diagnostics.

**Phases 3–5 endpoints (as built).** Reads of configuration come from the sync; every write goes through a `SyncWrite`
(source `admin`), so devices get it live. Errors are ProblemDetails with `code`; validation is 400 `validation_failed`
with `errors` by field.

| Endpoint | Needs | Body → answer |
|---|---|---|
| `POST /api/v1/config/statuses` | Configuration: Manage | `{itemType: idea\|lead\|project, name (1–100), color #rrggbb (stored lower case), icon? (`pi-xxx`), category: open\|active\|closed, maxDays? (1–36,500), isDefault?}` → `StatusItem {id, itemType, name, color, icon, category, sortOrder, isDefault, maxDays, isArchived, rev}` (200); the first status of a type, or `isDefault: true`, becomes the default (the old one isn't any more); 400 `validation_failed` with `errors.{itemType,name,color,icon,category,maxDays}`; 409 `duplicate_name` (per type, non-archived, ignoring case; a `detail`, no `errors`) |
| `PUT /api/v1/config/statuses/{id}` | 〃 | the same; `itemType` may be sent but not changed (400) → `StatusItem`; 409 `default_required` for `isDefault: false` on the default, `status_archived` to make an archived one the default |
| `POST /api/v1/config/statuses/{id}/archive` · `/unarchive` | 〃 | → `StatusItem`; 409 `default_status` for the default |
| `PUT /api/v1/config/statuses/order` | 〃 | `{itemType, ids: [all of the type]}` → `[StatusItem]` (sort orders 10, 20, …) |
| `POST /api/v1/config/sections` · `PUT …/{id}` | 〃 | `{name, description?, appliesTo: [idea, project], defaultFor ⊆ appliesTo, collapsedByDefault?, access?: [{groupId, level: view\|edit}]}` (`access` null on PUT: unchanged) → `SectionItem {id, …, access: [{groupId, groupName, level}], rev}`; a change of who may *view* bumps the affected users' `access_epoch` (reset), the row is re-stamped for `canEdit`; 409 `section_in_template` when `project` is dropped while templates use it |
| `POST /api/v1/config/sections/{id}/archive` · `/unarchive`, `PUT …/sections/order {ids}` | 〃 | → `SectionItem` / `[SectionItem]` |
| `GET /api/v1/config/sections` · `GET …/{id}` | 〃 | → `[SectionItem]` (archived ones too, in order) / `SectionItem` with `access: [{groupId, groupName, level}]` – the groups aren't synced (`section_groups` is server-only), the section dialog reads them here (phases 4–5 integration); 404 for an unknown id |
| `POST /api/v1/config/fields` · `PUT …/{id}` | 〃 | `{sectionId, key (create only, ^[a-z][a-z0-9_]{1,62}$, unique → 409 duplicate_key), label, helpText?, type (create only), config {…}, required?, defaultValue?}` → `FieldItem {id, sectionId, key, label, helpText, type, config, required, defaultValue, sortOrder, isArchived, rev}`; config checked by type, scores by `ScoreConfigValidator` (codes in `errors.config`: `cycle`, `input_max_required`, …), also every score that uses the field; another key/type on PUT → 400 |
| `POST /api/v1/config/fields/{id}/archive` · `/unarchive` | 〃 | → `FieldItem`; 409 `used_by_score` |
| `GET /api/v1/config/fields/{id}/usage` | 〃 | → `{id, key, values, items, scores: [keys], templates}` |
| `DELETE /api/v1/config/fields/{id}?confirm=<values>` | 〃 | → 204; the field, its values and template defaults tombstoned in one `other` batch; 409 `confirmation_required` (+ `values`) without the right count, 409 `used_by_score` |
| `PUT /api/v1/config/fields/order` | 〃 | `{sectionId, ids}` → `[FieldItem]` |
| `POST /api/v1/config/templates` · `PUT …/{id}` | 〃 | `{name, description?, color?, icon?, defaultStatusId?, statusIds: [], sectionIds: [] (in order), fieldDefaults: [{fieldId, value}], defaultShares: [{groupId, access}], milestones: [{id?, kind, title, description?, offsetDays?, dod: [""], onGoStatusId?, onNoGoStatusId?}] (in order)}` – every list complete → `TemplateDetail` (the same with ids and the milestones' fractional `sortOrder`); a milestone with the id of an existing one is updated, others created, missing ones tombstoned |
| `POST /api/v1/config/templates/{id}/archive` · `/unarchive`, `PUT …/templates/order {ids}` | 〃 | → `TemplateDetail` / `[TemplateDetail]` |
| `POST /api/v1/config/roles` · `PUT …/{id}` · `DELETE …/{id}` | 〃 | `{name, description?, sortOrder?}` → `RoleItem {id, name, description, sortOrder, isArchived, rev}` / 204; 409 `duplicate_name` |
| `GET /api/v1/config/packages` | 〃 | → `[{name, title, description, version, builtIn, imported: {version, importedAt, importedBy, importedByName} \| null}]` |
| `POST /api/v1/config/packages/dry-run` | 〃 | `{package: {…} \| builtIn: "core", overwrite?: [keys]}` → `{name, version, entries: [{key, entity, name, action: create\|update\|unchanged\|conflict, adopted, changed: [members]}], counts {create, update, unchanged, conflict}, needsAdmin, isAdmin}`; 400 `invalid_package` with `problems: [""]` |
| `POST /api/v1/config/packages/apply` | 〃 (+ admin when `needsAdmin`: 403 `admin_required`) | the same body → `{diff, created, updated, skipped}` (skipped = conflicts not overwritten) |
| `GET /api/v1/config/packages/export?package=` | 〃 | → a package document (all configuration, or what that package brought) |
| `PUT /api/v1/maps/{id}/shares` | manage on the map | `[{groupId, access}]` → `[{id, groupId, groupName, access}]` (re-stamp, evictions `{kind: "map"}`) |
| `POST /api/v1/maps/{id}/owner` | manage on the map | `{userId}` → 204 |
| `PUT /api/v1/files/{id}/chunks/{n}` | edit on the file's item/map | raw bytes (≤ 4 MiB) → `{received}` (§10.2) |
| `POST /api/v1/files/{id}/commit` | 〃 | `{sha256, size}` → `{id, status: "available", sha256, sizeBytes, contentType, width, height, fileName, rev, seq}` |
| `GET /api/v1/files/{id}/{name}` | read on the file's item/map | the bytes |
| `GET /api/v1/system` (phase 7) | System: View | → `SystemStatusItem {instance {name, version, startedAt, uptimeSeconds, environment, publicUrl, backgroundTasks}, database {sizeBytes, serverVersion}, files {storedBytes, storedFiles, pendingUploads, freeBytes, minFreeBytes, problem}, sync {head, tombstoneHorizon, clients, liveConnections, liveUsers, pending, conflicts, liveEnabled, maxConnectionsPerUser}, clients: [{id, userId, userName, appVersion, userAgent, lastSeenAt, cursor, behind, pending, conflicts, live}] (newest 500), tasks: [{key, name, summary, lastRunAt, intervalMinutes, stale}] (§9), directory {enabled, configured, lastStatus, lastFinishedAt, lastError}, mcp {enabled, tokensOn, calls24h, errors24h}}` – *Administration → System* (`views/admin/SystemView.vue`, refreshed every 15 s) |
| `DELETE /api/v1/system/clients/{id}` | System: View | → 204: the device's `sync_clients` row deleted (its next sync resets, §11.6), its live connections get `reset` + 4409 at once; audit `system.client_reset` (subject: the device's user); 404 for an unknown device |

The Trash lists maps too (`rootKind: "map"`, `rootType: canvas\|matrix`, `title` = the map's name).

*Phases 4–5 integration (verified against the clients):* the package dry run's `entity` is the importer's singular
(`group` … `matrix preset`); `invalid_package` (400) carries `problems: [""]` at the top level; `admin_required` is 403;
the server stamps a milestone's `completedAt` / `decidedAt` / `decidedBy` and does not enforce the DoD for *done* (the
client asks and the status patch is the History's record); `maps` takes a patch of `isTemplate` with *Configuration:
Manage* only (else `missing_right`); `preview` is bytea, base64 on the wire, ≤ 256 KB (`validation_failed` above),
**last writer wins** and is left out of the History (§15 #70); a `files` insert whose SHA-256 and size match an
available file the user can read is available at once on any item or map (map image copies); `map_shares` inserts are
accepted only in the batch that inserts their map (`missing_access` later); `PUT /api/v1/maps/{id}/shares` re-stamps the
map and its rows and evicts users who lost access (`kind: "map"`). The contract tests cover a section/field/template
round trip and a map's sharing round trip (`ConfigContractTests`, `MapsContractTests`).

### 3.4 Audit

PNMTD's `AuditChangesInterceptor` (EF `SaveChangesInterceptor`) with entity
attributes `[Audited]`, `[AuditSecret]`, `[AuditIgnore]`, `[AuditText]` records
field diffs into `audit_log`. PGPM uses:

- the same interceptor for **admin entities** (users, groups, rights, statuses,
  sections, fields, templates, map shares) → `audit_log`;
- a sibling interceptor for **synced entities** → `change_log` (data model §12),
  so History is filled identically for sync pushes, MCP tool calls and admin
  actions;
- `SyncStampInterceptor` (new) assigning `seq`/`rev`/`updated_*` (§11.5).

As PNMTD: one entry per write request (`AuditFilter` opens an `AuditScope` around every action with `[Audit]`; the
interceptor adds the entry to the request's first `SaveChanges`, so it commits with the change); services name the
event they record (`AuditLog.Record`), and refusals are recorded by `AuditDeniedHandler` (outcome `denied`, at most 60
a minute per user). PGPM's shape (docs/02 §12, §15 #25): events are dotted lower case (`auth.login_succeeded`,
`user.provisioned`, `membership.added`, `group.rights_changed` – `AuditEvents`, labelled by `AuditCatalog`); the
actor is `user`, `entra` (a Microsoft sign-in's provisioning and app-role memberships), `system` with `actor_ref`
`cli` or `seed`, or `mcp` (phase 6); PNMTD's extra columns (username, outcome, target name, request, browser,
changes) live in the `detail` jsonb.

### 3.5 Tests [PNMTD]

- `TestApp : WebApplicationFactory<Program>` with a fake `TestSignIn` scheme
  driven by headers (`Client(TestUsers.Member)`, `ClientWithRights("Projects:Create,Maps:Create")`); the cookie
  scheme forwards a request with the header to it (`ForwardDefaultSelector`), so one app serves header clients and
  real cookie sessions (`Browser`, PNMTD's) alike. The test users (admin, a Members and a Viewers member, one without
  groups) are seeded once per run.
- **Endpoint audit test** (`PNMTD.Tests/Hosting/EndpointAuditTests.cs`): an
  allowlist of anonymous endpoints and a matrix of every v1 endpoint's required
  right – a new endpoint fails the tests until it is added. Copy as-is.
- Sync tests: two simulated clients against one server (offline edits,
  conflicting patches, rights revoked mid-way, tombstone horizon).
- `FakeEntra` from GGITSV (§5.2).
- Playwright e2e incl. offline mode (`context.setOffline(true)`).
- Measurements are opt-in (docs/07): the live feed's load test (`PGPM_LOAD=1 dotnet test … --filter TestCategory=Load`),
  the list's timings (`tools/npm.sh run bench`), the map's frame times and the 10,000-notes first sync
  (`E2E_PERF=1 tools/e2e.sh perf.spec.ts`). Every e2e spec leaves shared configuration as it found it (`statuses.spec.ts`
  restores order and default in `afterEach`), so the specs after the first admin's (`identity.spec.ts`, §15 #69) pass in
  any order and again on a used stack.

---

## 4. Users, groups, local login [PNMTD]

Taken over unchanged except for names (`pnmt` → `pgpm`).

### 4.1 Passwords

`PNMTD.Lib/Security/PasswordHasher.cs`: PBKDF2-HMAC-SHA256, **210,000
iterations**, 16-byte salt, 32-byte key, format
`pbkdf2-sha256$<iter>$<saltB64>$<keyB64>`, `FixedTimeEquals`, `NeedsRehash` on
login, `DummyHash` verification for unknown usernames (no timing oracle).
Policy: 10–256 characters, no composition rules. `SecurePasswordGenerator`: 20
chars without look-alikes. Temporary passwords valid 7 days, must be changed.

### 4.2 Session cookie

`PNMTD/Auth/AuthSetup.cs:60-164` – cookie auth only, no JWT:

```csharp
.AddCookie(o => {
    o.Cookie.Name = "pgpm.auth"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Strict;
    o.ExpireTimeSpan = TimeSpan.FromDays(cfg.GetValue("Auth:SessionDays", 7)); o.SlidingExpiration = true;
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; }; // never redirect
    o.Events.OnValidatePrincipal = SessionCache.ValidateAsync;
})
```

- `Secure` as the request came – and **always** once `PublicUrl` is https (session, `pgpm.2fa`, `XSRF-TOKEN`, the SSO's
  correlation and nonce cookies; `AuthSetup.SecureCookies`), so a reverse proxy without the `Proxy` setting still gets
  Secure cookies; the antiforgery cookie token stays as the request came (the framework refuses `Always` on plain http) –
  §15 #85, docs/06 S-03.
- Data-Protection key ring persisted to `/var/lib/pgpm/keys`, application name `PGPM`.
- Claims (`SessionClaims.Create`): user id, name, `pgpm:display_name`,
  `pgpm:stamp`, `pgpm:signed_in`, `pgpm:totp_at`, **new** `pgpm:auth_method` (`password` \| `microsoft`).
- Rights are **not** in the cookie: `SessionCache.ValidateAsync` loads stamp,
  active flag, rights and group ids per request through `IMemoryCache` (1 min TTL), rejects
  on stamp mismatch, inactive user or the end of `Auth:LoginDays` (401 `login_expired`), adds `pgpm:admin` /
  `pgpm:right` / **new** `pgpm:group` claims (`User.GroupIds()` – the sharing checks of phase 2 read them).
  `SessionCache.Forget(userId)` on group/right changes.
- `last_seen_at` written at most every 5 minutes (also by sync requests – keeps
  the MCP read token alive while the user works offline-first).
- Session rules (`Auth/SessionRules.cs`): `Auth:LoginDays=30` (re-authenticate at
  least this often), `Auth:TotpDays=1`, `Auth:ResetHour=4`. `SessionGates`
  middleware answers other `/api/v1` calls with 403 `must_change_password` \|
  `totp_setup_required` \| `totp_due`. **Microsoft sessions skip the password and
  TOTP gates** (GGITSV `UserAccess.SessionStateFor`), MFA is Entra's job.

### 4.3 CSRF, rate limiting, lockout

- Double-submit: antiforgery cookie `pgpm.antiforgery`, readable `XSRF-TOKEN`
  cookie echoed as `X-XSRF-TOKEN` header on every POST/PUT/PATCH/DELETE
  (`[CsrfProtection]`; `[SkipCsrf]` only on login steps). The sync endpoint is a
  POST and needs the header too.
- Login rate limit: fixed window, 10/min/IP (`[EnableRateLimiting("login")]`).
- Lockout: 5 failures → 5, 10, 20, 40, max 60 minutes (`AuthService.LockoutFor`).
- Optional **[GGITSV]**: `Auth:LocalLoginNetworks` (CIDR list) – local password
  login only from these networks (else 403 `local_login_not_allowed`, audit `auth.login_refused`); Microsoft SSO
  from anywhere. Startup check: refuse a config where nobody can sign in (`none` without `Entra:Enabled`), and SSO
  switched on without its tenant, client id or secret.

### 4.4 TOTP

RFC 6238, SHA1, 6 digits, 30 s, ±1 step (`PNMTD.Lib/Security/TotpService.cs`),
replay protection via `totp_last_used_step`, secret encrypted with Data
Protection (`PGPM.Totp.v1`), policy `Auth:TotpPolicy = Optional |
RequiredForAdmins | Required` (applies to local accounts only). QR rendered
client-side (`qrcode`) on a `<canvas>` – PNMTD's `data:` image is not allowed by PGPM's CSP (§15 #24). An account
without a password (Microsoft only) has no PGPM TOTP: setup answers 409 `password_not_set`.

### 4.5 Endpoints

| Endpoint | Notes |
|---|---|
| `POST /api/v1/auth/login` | `{next:"done"\|"totp", me}` (`me` as `/auth/me` when done); for `totp` a 5-min `pgpm.2fa` cookie (path `/api/v1/auth`); 401 `invalid_credentials`, 429 `locked_out`, 403 `local_login_not_allowed` |
| `POST /api/v1/auth/login/totp`, `POST /auth/totp/daily`, `POST /auth/logout` | |
| `GET /api/v1/auth/me` | `{user: {id, username, displayName, email, isAdmin, rights: {Area: Level}, groupIds, personId, isTotpEnabled, hasPassword, hasMicrosoftAccount}, flags, session: {totpDue, signedInUntil, authMethod}}`, issues XSRF cookie; kept for an offline start (phase 1: `localStorage` `pgpm.session` with the rights catalogue; phase 2 moves it into IndexedDB `meta`, §15 #22) |
| `GET /api/v1/auth/options` | `{microsoft: bool, local: bool}` **[GGITSV]** – `local` is whether `Auth:LocalLoginNetworks` allows the caller's address |
| `GET /api/v1/auth/sso/microsoft?returnTo=` | SSO start **[GGITSV]** |
| `/api/v1/account/{password, sessions/revoke, totp/*, access, mcp/*}` | own account |
| `/api/v1/users`, `/{id}/reset-password`, `/{id}/reset-totp`, `/{id}/unlock`, `/{id}/access`, `/{id}/microsoft-account` | admin only (`[RequireAdmin]`) |
| `/api/v1/groups`, `/{id}`, `PUT /{id}/members`, `PUT /{id}/rights` | admin only; `PUT /{id}` without `rights` leaves them |
| `GET /api/v1/rights` | the catalogue (areas, levels in order, what each allows), every signed-in user |
| `GET /api/v1/audit`, `GET /api/v1/audit/events` | *Audit log: View*; paged, newest first; filters `event`, `prefix` (`auth`, `user`, …), `actor`, `outcome`, `userId` (by or about), `subjectId`, `from`/`to`, `search` |

### 4.6 Bootstrap and recovery

- `UserAccounts.SeedAsync`: empty `users` table → create
  `Auth:InitialAdmin:Username` (default `admin`) with
  `Auth:InitialAdmin:Password` or log a generated one once,
  `must_change_password = true`.
- `GuardLastAdminAsync`: 409 `last_admin` when deleting/demoting/deactivating the
  last active admin.
- CLI: `pgpm users list|add|reset-password|reset-totp|unlock|set-admin`, `pgpm groups list`, wrapped by
  `deploy/{add-user,list-users,reset-password,reset-totp}.sh` (`docker compose exec pgpm pgpm …`).
- `start.sh` writes `Auth__InitialAdmin__Username=admin` and a generated password into `dev.env` and prints both.

### 4.7 Groups

`groups`, `group_rights (group_id, area, level)`, `user_groups` (data model §3).
`GroupService`: CRUD, `SetMembersAsync`, `AccessAsync` (effective rights + which
group grants them). Seed groups with fixed GUIDs (`GroupEntity.MembersId`, `ViewersId`): **Members** and
**Viewers** (§6.2), by the custom migration `SeedGroups` once per database (key_values marker `seed.groups`, so a
group an admin deleted stays deleted; phase 4's `core` package takes them over).

---

## 5. Microsoft SSO [GGITSV]

### 5.1 Flow

Server-side OpenID Connect against Entra ID with the plain ASP.NET Core handler –
**no MSAL.js, no Microsoft.Identity.Web**. Authorization code + PKCE,
confidential client (client secret), single tenant.

`src/Ggitsv.Api/Authentication/MicrosoftSignIn.cs:36-76`:

```csharp
options.Authority = $"https://login.microsoftonline.com/{entra.TenantId}/v2.0";
options.ResponseType = OpenIdConnectResponseType.Code;
options.UsePkce = true;
options.ResponseMode = OpenIdConnectResponseMode.Query;  // top-level GET → cookies can be SameSite=Lax
options.CallbackPath = "/signin-oidc";
options.Scope.Clear(); // openid, profile, email, User.Read
options.SaveTokens = false; options.GetClaimsFromUserInfoEndpoint = false; options.MapInboundClaims = false;
// correlation/nonce cookies renamed pgpm.oidc.*, SameSite=Lax, HttpOnly
```

Events (lines 83-118):
- `OnRedirectToIdentityProvider`: `RedirectUri = new Uri(PublicUrl, "/signin-oidc")`
  (correct behind the reverse proxy).
- `OnTokenResponseReceived`: access token into `HttpContext.Items` (this request
  only, for Graph `/me`).
- `OnTicketReceived`: `MicrosoftSignInService.CompleteAsync`.
- `OnAccessDenied` / `OnRemoteFailure`: audit entry, redirect to
  `/sign-in?ssoError=cancelled|failed`.

Start: `GET /api/v1/auth/sso/microsoft?returnTo=/path` →
`Challenge(new AuthenticationProperties { RedirectUri = ReturnPaths.Normalize(returnTo) }, "Microsoft")`;
404 `microsoft_sign_in_disabled` when off. `ReturnPaths.cs`: only local paths, no
`//`, no `/\` (open-redirect protection). When SSO is disabled the `Microsoft`
scheme is removed at startup (`IAuthenticationSchemeProvider.RemoveScheme`) so
`/signin-oidc` isn't handled.

### 5.2 Adapting it onto PNMTD's cookie auth

GGITSV signs in through ASP.NET Identity (`SignInManager`, `user_logins`).
PGPM has PNMTD's own cookie auth, so:

1. `OnTicketReceived` → `CompleteAsync(principal)` → on success
   `HttpContext.SignInAsync("pgpm.auth", SessionClaims.Create(user, authMethod: "microsoft"))`,
   then `context.HandleResponse(); Response.Redirect(returnTo)`.
2. The PNMTD session cookie is `SameSite=Strict`. After the cross-site redirect
   chain from Microsoft the very first navigation does not carry it – fine,
   because the SPA shell is public and the first `GET /auth/me` is a same-site
   fetch. Keep the shell public; do not put auth on `index.html`.
3. `SessionGates` must treat `pgpm:auth_method = microsoft` as "password and TOTP
   satisfied" (GGITSV `UserAccess.SessionStateFor`). `Auth:LoginDays` still
   applies → a Microsoft user re-does SSO at least every 30 days.
4. Exclude `/signin-oidc` and `/api/v1/auth/sso/*` from the service worker.

### 5.3 Claims → user

`MicrosoftClaims.cs` reads raw claims `oid`, `tid`, `preferred_username`, `name`,
`email`, `roles` into `ExternalIdentity`; missing `oid`/`tid` → fail.

`src/Ggitsv.Infrastructure/Identity/MicrosoftSignInService.cs:33-99`, one
transaction:

1. **Match by (tid, oid) only** – never by e-mail or name.
   PGPM: `users.microsoft_tenant_id/microsoft_object_id`.
2. Unknown account → **auto-provision** (`Entra:AutoProvision`, default on):
   username = first valid of `preferred_username`, `email`, oid. If that username
   already exists → refuse with `account-conflict` (an admin links it via
   `PUT /api/v1/users/{id}/microsoft-account {objectId, tenantId?}`). Audit
   `user.provisioned` (source `entra`).
3. `is_active = false` → refuse (`deactivated`).
4. **Entra app roles → groups** (lines 145-169): every group with an
   `entra_app_role` gets its membership set from the token's `roles` claim at
   each sign-in; other groups untouched; `is_admin` never set from Entra;
   manual edits of such memberships → 409 `membership_managed_by_entra`.
5. **New for PGPM**: if the user has no group after step 4 and
   `Entra:DefaultGroupId` is set → add to that group.
6. **New for PGPM**: link the person: `persons` row with the same (tid, oid) →
   `persons.user_id = user.id` (created by the directory sync, §8; if it doesn't
   exist yet, create it from the claims).
7. Graph `/me` (`MicrosoftDirectoryReader.cs`, named `HttpClient`
   `microsoft-graph`, base `https://graph.microsoft.com/v1.0/`, 10 s timeout,
   delegated token, `me?$select=userPrincipalName,mail`) → `microsoft_user_name`,
   `directory_read_at`. Failure is logged and never blocks sign-in.
8. `last_login_at` updated, session issued, redirect to `returnTo` or
   `/sign-in?ssoError=deactivated|account-conflict|no-account|cancelled|failed` (`no-account`: unknown account while
   `Entra:AutoProvision` is off). A manual person the user had becomes the account's Microsoft person when the
   directory has none yet (it keeps its assignments); otherwise the directory person is linked and the manual one
   let go (stays, without a user).

Logout: `POST /api/v1/auth/logout` ends the PGPM session only – **no Entra
single sign-out** (document it in the UI).

### 5.4 Frontend [GGITSV]

`features/auth/SignInPage.vue:42-57,137-143` → PGPM `views/auth/SignInView.vue`
inside PNMTD's `AuthLayout`: a plain `<a href>` button "Sign in with Microsoft"
to `microsoftSignInUrl(returnTo)` (full navigation, not fetch), map
`?ssoError=` to messages, collapse the local form when Microsoft is available
(`GET /auth/options`). Admin: `UserMicrosoftAccount.vue` (link/unlink).

### 5.5 Entra app registration (one registration for SSO and directory sync)

| Setting | Value |
|---|---|
| Supported accounts | single tenant |
| Platform | Web, redirect URI `https://<host>/signin-oidc` (dev: `http://localhost:<port>/signin-oidc`) |
| Credentials | client secret (later: certificate) |
| Delegated permissions | `openid`, `profile`, `email`, `User.Read` (no admin consent needed) |
| Application permissions | `User.Read.All` (directory sync, admin consent) |
| App roles | one per PGPM group to be managed by Entra, e.g. `PGPM.MEMBERS` |
| Enterprise app | "Assignment required" = yes, if only assigned users may sign in |

### 5.6 Tests

`tests/Ggitsv.Api.Tests/Support/FakeEntra.cs`: static `OpenIdConnectConfiguration`
with own RSA key injected via `PostConfigure<OpenIdConnectOptions>("Microsoft", …)`
(`ConfigurationManager` + `Backchannel` handler), fakes Graph `/me`. Port the
12 scenarios of `MicrosoftSignInTests.cs` (PKCE, provisioning, app roles, Graph
failure, deactivated, conflict, cancel) to MSTest, and add: default group,
person linking.

---

## 6. Rights [PNMTD]

### 6.1 Model

`PNMTD/Auth/Rights/Rights.cs`: admin flag + **one level per area**, no roles,
no generic ACLs. Each area has its own ordered level list (`RightsCatalog.Areas`
with a description per level); a higher level includes the lower ones
(`AreaInfo.Rank`); string form `"Area:Level"`; effective level = highest level
of any of the user's groups (`UserRights.Combine`); `is_admin` meets every
right. Catalogue served at `GET /api/v1/rights`.

Enforcement (`Auth/Rights/RequireRight.cs`): `[RequireRight(Area.X, Level.Y)]`,
`[RequireAnyRight(…)]`, `[RequireAdmin]` as `AuthorizeAttribute` subclasses with
policy names like `right:Projects:Create`; `RightPolicyProvider` builds and caches
policies; `RightHandler` checks `UserRights.Of(principal)`. Refusal: 403
`{code:"missing_right", area, level}`, logged by `AuditDeniedHandler`. Checks the
attributes can't express: `User.Has(area, level)` /
`throw ApiException.MissingRight(area, level)` in services – this is what the
sync push uses per mutation.

Frontend: `src/api/rights.ts` (level order per area, `holds()`, `meets()`),
`stores/session.ts` `can(area, level)` / `allows(right)`, route
`meta: { right: ['Projects','Edit'] }` checked by `router/guard.ts` `decide()`,
`navigation.ts` entries with `right`, buttons `v-if="session.can(…)"`.
Offline the rights come from the cached `/auth/me`.

### 6.2 PGPM rights catalogue

Rights are **capabilities** – what a user may *do*. What a user may *see* is
decided by sharing (§6.3, data model §4).

| Area | Levels (low → high) | Meaning |
|---|---|---|
| `Notes`, `Ideas`, `Leads`, `Projects` | `Create` | create items of the type (creator becomes owner); converting needs `Projects:Create` |
| `Maps` | `Create` | create maps |
| `Persons` | `Edit` | manual persons, notes on persons (everybody can read the directory) |
| `Configuration` | `Manage` | statuses, sections, fields, templates, map templates, shared saved views, configuration packages |
| `Directory` | `Manage` | Microsoft integration settings, run directory sync now |
| `Mcp` | `Use` < `View` < `Manage` | Use: own tokens · View: MCP log of all users · Manage: switch off others' tokens |
| `AuditLog` | `View` | |
| `System` | `View` | sync clients, live connections, storage, versions |

Users, groups and rights themselves: **admin only** (as in PNMTD).

Seed groups (`core` package): **Members** (`Notes/Ideas/Leads/Projects:Create`,
`Maps:Create`, `Mcp:Use`), **Viewers** (no rights – they see what is shared with
them). Package `innovation-ai`: **Innovation team** (`Notes/Ideas/Projects:Create`,
`Maps:Create`), **Review board** and **Compliance** (no rights; they get *edit* on
their sections via `section_groups`).

Rights don't change visibility, so a rights change needs no sync reset – only
`SessionCache.Forget(user)` and a live `session/rights_changed` message so the
client refetches `/auth/me`.

### 6.3 Object sharing [NEW]

PNMTD has no per-object ACL (only special cases like `Knowledge/MachineAccess.cs`).
PGPM shares items and maps with groups (decision 2026-11-26 #1, data model §4):

- **`AccessService`** (scoped per request and per live connection; group ids of
  the user loaded once via `SessionCache`):
  - `ItemAccess(id)`, `MapAccess(id)` → `none | read | edit | manage`;
  - `ReadableItems()` / `ReadableMaps()` – `IQueryable` predicates:
    `is_admin OR owner_user_id = @me OR EXISTS (SELECT 1 FROM item_shares s WHERE s.item_id = i.id AND s.group_id = ANY(@groups) AND NOT s.deleted)`;
  - `VisibleSectionIds()`, `EditableSectionIds()` (`section_groups`).
- Used by the sync pull (visibility), the sync push (per mutation: *edit* on the
  item for item rows and children, *manage* for item delete, section edit for
  `field_values`, map *edit* for layers/elements, item *read* for placing a card),
  MCP tools and REST endpoints (sharing, trash, history).
- Refusal: mutation `rejected` with `code: "missing_access"` and the required
  level (sync), 403 ProblemDetails `missing_access` (REST).
- **Sharing endpoint** `PUT /api/v1/items/{id}/shares` (full list
  `[{groupId, access}]`, needs *manage*): diff → share rows via `SyncWriter` →
  **re-stamp** the item subtree → `sync_evictions` for users who lost access
  entirely (members of removed/lowered groups minus users who still have access)
  → audit `item.shares_changed`, change_log `share`. Same for maps.
- **Initial shares offline**: the create dialog inserts `item_shares` rows in the
  same outbox batch as the item; the server accepts share inserts only for items
  created in the same batch by the same user.
- **Owner transfer** `POST /api/v1/items/{id}/owner {userId}` (*manage*):
  re-stamp; eviction for the old owner if no access remains.
- Every user has a person: `UserAccounts.CreateAsync` creates a manual person for
  local users; SSO links or creates the directory person (§5.3). `/auth/me`
  returns `personId` and `groupIds`.
- Indexes: `item_shares (group_id, item_id) WHERE NOT deleted`,
  `map_shares (group_id, map_id) WHERE NOT deleted`, `items (owner_user_id)`.

## 7. MCP [PNMTD]

### 7.1 Token model (copy)

`PNMTD/Mcp/*`, `docs/mcp-plan.md` §4-6. Table `mcp_tokens` (data model §3).

- **Two tokens per user**: read and write. Value
  `pgpm_ro_` / `pgpm_rw_` + base64url(32 random bytes) (43 chars). Only the
  SHA-256 hex hash is stored; the server ignores the prefix and takes the kind
  from the matched row. Value shown once (`Cache-Control: no-store`).

  ```csharp
  public static string New(McpTokenKind kind) => (kind == Write ? "pgpm_rw_" : "pgpm_ro_")
      + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
  public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
  ```

- Endpoints `/api/v1/account/mcp`: `POST {read|write}/on`, `…/off`,
  `…/regenerate`, `POST/DELETE write/delete-window`. Admin `/api/v1/mcp`:
  `tokens/{id}/off` (with reason), revoke-all, `log`, `status`, `catalog`.
- **On** only if (`McpTokenState.Of`, evaluated per request): `Mcp:Enabled`, not
  blocked, `on_since` set, user active, `on_stamp == security_stamp` (password,
  2FA, sign-out-everywhere kill both tokens), write token: expires
  `on_since + Mcp:WriteHours (12)`; read token: expires
  `max(last_seen_at, on_since) + Mcp:ReadDaysAfterOnline (7)`.
- Write token requires 2FA (`WriteRequiresTotp`). **PGPM adaptation**: satisfied
  by TOTP for local accounts **or** by a linked Microsoft account (MFA enforced by
  Entra).
- `McpTokenExpiryTask` every 5 min records ends + audit entries (requests check
  state themselves). Strikes: 3 refused calls in 24 h → blocked + admin alert
  (`McpStrikes.cs`).

### 7.2 Hosting (copy)

`McpSetup.cs`:

```csharp
services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, McpTokenHandler>("McpToken", null); // never default
services.AddAuthorizationBuilder().AddPolicy("mcp", p => p.AddAuthenticationSchemes("McpToken").RequireAuthenticatedUser());
services.AddMcpServer(o => { o.ServerInfo = new Implementation { Name = "pgpm", … }; o.ServerInstructions = McpInstructions.Text; })
    .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
    .WithTools(ToolCatalog.All.Select(t => (McpServerTool)new CatalogTool(t)))
    .WithRequestFilters(f => { f.AddListToolsFilter(McpCalls.ListFilter); f.AddCallToolFilter(McpCalls.CallFilter); });
app.MapMcp("/mcp").RequireAuthorization("mcp");   // only when Mcp:Enabled
```

- Streamable HTTP, stateless, `/mcp`. Cookie authentication is not run for
  `/mcp`; no `/api/v1` policy accepts the `McpToken` scheme (test both directions).
- `UseMcpOriginCheck`: 403 when `Origin` ≠ host (DNS rebinding).
- `McpTokenHandler`: `Authorization: Bearer`; unknown → 401 + `WWW-Authenticate`;
  known but off → 403 with an explanatory JSON body.
- `McpPrincipal.Create`: user id/name, `pgpm:mcp_token`, `pgpm:mcp_kind`, **capped**
  rights as `pgpm:right` claims, never `pgpm:admin` – existing services work
  unchanged with `User.Has(…)`.
- **Capped rights** (`McpRights.cs`): min of (user level; write `Ceiling`; read
  `ReadOnlyCap`). PGPM write ceiling: `Notes/Ideas/Leads/Projects:Create`,
  `Maps:Create`; object access is capped at *edit* (no sharing changes, no
  deletes, no owner transfer). No admin/config areas.
- Tools (`Tools/ToolSpec.cs`, `Tools/ToolCatalog.cs`):

  ```csharp
  public sealed record ToolSpec(string Name, string Title, string Description, JsonObject InputSchema,
      Area? Area, Level Level, ToolKind Kind, Func<JsonElement, ToolContext, PgpmDbContext, CancellationToken, Task<object>> Run)
  { AuditEvents, Destructive, Idempotent, … }
  public enum ToolKind { Read, Write, Action, Delete, Ask }
  public static bool KindAllows(ToolSpec t, McpTokenKind k) => k == Write || t.Kind is ToolKind.Read or ToolKind.Ask;
  ```

  `CatalogTool : McpServerTool` maps kind → annotations (`ReadOnlyHint`,
  `DestructiveHint`). `ListFilter` hides tools the token can't use. `CallFilter`:
  existence/kind/rights (else denial + strike) → rate limit `McpLimits` (120
  calls, 20 writes per minute per token) → delete window for `Delete` tools
  (atomic `ExecuteUpdate` on `delete_count` while `delete_until > now`; 10 min,
  max 20). Invocation in the request DI scope, writes wrapped in an `AuditScope`
  (`Actor=Mcp`, `ActorRef=token:{id}:rw|ro`), errors as `isError` results
  `{error, message}`, every call in `mcp_log` (redacted args, gzipped result,
  correlation id).
- UI: Account → Security → MCP card (`views/account/McpCard.vue`), Admin → MCP
  (`views/admin/mcp/`).

### 7.3 PGPM specifics [NEW]

- **All MCP writes go through `SyncWriter`** (§11.5) – same validation, access,
  section rights, `seq` stamping and `change_log` (source `mcp`); connected
  clients see the change live.
- Access: the token's user via `AccessService`; read tokens are capped at *read*,
  write tokens at *edit*. Section visibility applies – tools never return or
  accept values of fields the user can't see/edit.
- Concurrency: write tools accept an optional `expectedRev`; on mismatch they
  return `changed_meanwhile` with the current values so the assistant re-reads.
  Without `expectedRev` the change is applied as a normal patch whose `old`
  values are the ones the tool read in the same call.
- Scores are returned computed (same spec as the client, §16).
- Tool catalogue v1 (as built in phase 7; items by id or key `I-12`, maps and milestones by id, persons by id, e-mail or `me`,
  statuses and templates by id or name, fields by key or label – of the sections the user sees):

  | Tool | Arguments | Kind | Requires |
  |---|---|---|---|
  | `search_items` | `type`, `text`, `status`, `owner` (`me`/user id), `person`, `sharedWith` (group), `fields: [{field, min, max}]` (numbers, ratings, a select's points, scores – computed), `map` + `quadrant` (`tl`/`tr`/`bl`/`br` of a matrix map), `limit`, `cursor` | Read | item *read* |
  | `get_item` | `id` → the item with status, template, origin/conversion, shares, `persons`, `links` (out, and in from readable items), `attachments`, `sections` (visible ones: fields, values, option labels, computed scores, `canEdit`), `milestones` (gates with decision, suggestions, DoD criteria, persons) | Read | item *read* |
  | `list_config` | `include` ⊆ `groups`, `itemTypes`, `statuses`, `sections` (visible, with fields incl. score definitions, `canEdit`), `templates` (statuses, sections, milestones, DoD, default shares), `roles` | Read | – |
  | `search_persons` | `text`, `userType`, `source`, `includeInactive`, paging | Read | – |
  | `list_maps` | `kind`, `text`, `templates`, paging → access (capped), element count | Read | map *read* |
  | `get_map` | `id`, `limit` → layers, element counts, elements (cards with their items – an unreadable one only its id –, texts, milestones); matrix maps: axes, quadrants, `cards` (values, `position` 0–1 per axis, world `point`, quadrant and label), `tray` | Read | map *read* |
  | `create_note`, `create_idea`, `create_lead` | `title`, `description`, `color`, the type's fields (note `pinned`; lead `organisation`, `value`, `currency`, `probability`, `decisionDate`, `nextAction`, `nextActionDue`, `source`), `status`, `fields` (ideas), `share_with: [{group, access read\|edit}]` | Write | type `Create` |
  | `create_project` | as above with `startDate`, `endDate`, `template` (applied as the client does: allowed/default status, sections, field defaults, milestones and gates due start + offset with DoD and status suggestions, default shares – at most edit – unless `share_with`), `fields` | Write | `Projects:Create` |
  | `update_item` | `id`, `title`, `description`, `color`, type fields, `status`, `fields` (null removes a value; the field's section is added to the item), `expectedRev` | Write | item *edit*, section edit |
  | `assign_person`, `unassign_person` | `item` or `milestone`, `person`, `role` (a gate's default *Review board*) | Write | item *edit* |
  | `link_items`, `unlink_items` | `from`, `to`, `relation` (`contributes_to`, `learning_of`, `depends_on`, `related`) | Write | *edit* on source, *read* on target |
  | `add_milestone` | `project`, `kind`, `title`, `description`, `dueDate`, `dod: [""]`, `onGoStatus`, `onNoGoStatus` | Write | item *edit* |
  | `update_milestone` | `id`, `title`, `description`, `dueDate`, `status`, `color`, `onGoStatus`, `onNoGoStatus`, `expectedRev` → `dodOpen` | Write | item *edit* |
  | `set_dod_item` | `id`, or `milestone` + `text` (found by its text, else added), `state` (`open`/`done`/`na` – n/a needs a note), `note` | Write | item *edit* |
  | `record_gate_decision` | `id`, `decision`, `note`, `applyStatus` (the suggestion: go → on-go, no-go → on-no-go, allowed by the template) or `status` → `suggestedStatus`, `statusChanged` | Write | item *edit* |
  | `add_item_to_map` | `map`, `item`, `x`+`y` or `near` (an element: to its right) or the map's centre, `layer` (default the top visible, unlocked one) – canvas maps only | Write | map *edit*, item *read* |
  | `convert_to_project` | `id` (idea/lead), `title`, `template`, `status`, `startDate` – as the client converts (shares at most edit, cover and attachments sharing the stored bytes, sections and values for projects the user may edit, persons, outgoing links to readable items, a *related* link back, source → *Selected*/*Won*); `already_converted` once done | Action | source *edit* + `Projects:Create` |

  Delete, sharing and owner tools are not offered in v1.

### 7.4 Implementation (phase 6, pulled forward)

**Server** (`PGPM/Mcp/`): `McpEntities` (`mcp_tokens`, `mcp_log`; `ended_why` and `outcome` are text with a CHECK –
`user_inactive`, `rate_limited`), `McpOptions`, `McpTokenValues`, `McpTokenService` (+ `McpTokenState`, `McpTokenUser`,
`McpCaller`), `McpRights` (write ceiling `Notes/Ideas/Leads/Projects/Maps: Create`, read cap empty – reading needs no right –,
`AccessCap`: read → *read*, write → *edit*), `McpPrincipal`, `McpTokenHandler`, `McpCalls` (`CatalogTool`, `ListFilter`,
`CallFilter`, `InvokeAsync`), `McpLimits` + `McpStrikes`, `McpLog`, `McpTokenExpiryTask`, `McpSetup` (`AddPgpmMcp`,
`UseMcpOriginCheck`, `MapPgpmMcp`, `McpInstructions`). Tools in `PGPM/Mcp/Tools/` (namespace `PGPM.Mcp.Tools`): `ToolSpec`,
`ToolArgs`, `ToolContext` (+ `ToolPaging`), `ToolResults`, `ToolCatalog`, `ToolWrites`, `ToolConfig`, `ItemKinds`, `ItemTools` (+
`ItemViews`), `ItemDetails`, `ItemSetup`, `PersonTools`, `LinkTools`, `MilestoneTools`, `MapTools` (+ `MatrixView`), `ConvertTools`,
`LookupTools` (+ `ConfigParts`); the catalogue is complete in phase 7 (§7.3). Endpoints: `AccountMcpController` (`/api/v1/account/mcp`: `GET`; `POST {read|write}/on|off|regenerate` – *on* and
*regenerate* need `Mcp:Use`, *off* never; the value only in that response, `no-store`), `McpAdminController` (`/api/v1/mcp`:
`catalog` for everyone, `status`, `log`, `tokens` with `Mcp:View`, `log/{id}` with `Mcp:View` or one's own, `tokens/{id}/off`
with a reason and `tokens/revoke-all` with `Mcp:Manage`).

- **Capped access**: `Viewer.MaxAccess` – `AccessService.ItemAccessAsync` never answers more than it –, set by `Viewer.OfAsync`
  for an MCP principal, whose `IsAdmin` is the user's flag (an admin's token sees everything, capped) while its rights never
  carry `pgpm:admin`. So every service and table hook (`ItemsTable.AuthorizePatch` needs *edit*, deletes and sharing need
  *manage*) is capped without knowing MCP.
- **Writes**: `ToolContext.WriteAsync` opens one `SyncWriter` unit with the source `mcp`; tools apply ordinary
  `SyncMutationIn`s (`ToolContext.ApplyAsync`: `rejected` → the tool's error with the table's code, `conflict` →
  `changed_meanwhile`). `create_*` inserts the item and its `share_with` shares in one batch (the shares' same-batch rule);
  `update_item` re-reads the row inside the unit (the counter's lock), refuses a stale `expectedRev` with `changed_meanwhile` and
  `current`, and patches with `old` = the values it read. Each write tool also writes an audit entry (`mcp.item_created`,
  `mcp.item_updated`, actor `mcp`, `actor_ref token:{id}:rw`) through an `AuditScope`; the change log has the fields with source
  `mcp`; the MCP log entry, the audit entry and the change-log rows share the request's correlation id (shown together in
  *Administration → MCP → Log*).
- **Capped map access** (phase 7): `AccessService.MapAccessAsync` is capped by `Viewer.MaxAccess` as items are – a read token never
  edits a map, a write token never manages one (it had not been: no map tool existed before).
- **What the user sees** (phase 7, `ToolConfig`, read once per call): the statuses, the sections the user may view and which of them
  they may edit, the fields of those sections. Values are read only for those fields, scores are computed by `ScoreEvaluator` from
  them (an input of a hidden section makes the score null, as on the device), matrix maps place items by them (`MatrixView`: the
  client's `numericValue`, `quadrantOf`, `axisPosition`, `matrixPoint`; the nudging apart is the device's), and `fields` arguments
  resolve only to them (`not_found` otherwise; `missing_access` for a section the user may not edit, before anything is written).
- **Server-side set-up** (phase 7, `ItemSetup`): `create_idea`/`create_project`/`convert_to_project` do in their write unit what
  the client's `setUp`/`fillDefaults`/`addMilestone` do in the create dialog's batch – the item, its shares, its sections, values
  (given or carried, then the template's defaults, then the fields' own; only in editable sections, never scores, archived fields,
  empty or invalid values), milestones (`FractionalKey.After`, the npm package's `generateKeyBetween(key, null)`) and DoD criteria –
  all in one batch. Every write call is all or nothing (an exception rolls the unit back) except the conversion's copied links,
  which go in as `ToolWrites.TryInsertAsync` (a refused one stays behind, as the device's outbox entry would).
- **Audit events** (phase 7): `mcp.item_updated` for the item tools (persons, links, milestones, DoD, gate decisions – the target is
  the item or project), `mcp.item_converted` (target the new project), `mcp.map_changed` (`add_item_to_map`, target the map).
- **Isolation**: cookie authentication is not run for `/mcp`; the `mcp` policy names only the `McpToken` scheme; `/mcp` gets the
  API's CSP and `no-store`. Tested both ways (a token on `/api/v1` → 401, a session cookie or the tests' sign-in on `/mcp` → 401).
- **Token states**: `on`, `off`, `no_value`, `expired`, `blocked`, `stamp`, `inactive`, `totp` (write token without TOTP and
  without a Microsoft link), `no_right` (the user lost `Mcp:Use`: refused, but it stays on and works again with the right),
  `mcp_off`.

**Adding a tool in a later phase** – the catalog is generic, a tool is a spec:

1. Write a `ToolSpec` (name in snake case, title, description, `ToolArgs.Schema(...)`, the right it needs as `Area`/`Level`
   (null for access-only tools) plus `Requires` for a second right, `ToolKind`, `AuditEvents` for writes) and its
   `Run(args, ctx, db, ct)`. Read through `ctx.Access(db)` (sharing, capped); write through `ctx.WriteAsync(db, write =>
   ToolContext.ApplyAsync(write, mutation, ct))` – never directly. Set `ctx.Summary` / `ctx.Target(...)` and, for writes,
   `AuditLog.Target/Summary`.
2. Add a new `mcp.*` audit event to `AuditEvents` and `AuditCatalog` if none fits.
3. Add the list to `ToolCatalog.All` (one `…Tools.All` per area: `ItemTools`, `PersonTools`, `LinkTools`, `MilestoneTools`,
   `MapTools`, `ConvertTools` – `convert_to_project` is an `Action` with `Requires = [(Projects, Create)]` –, `LookupTools`).
   Build mutations with `ToolWrites` (`InsertAsync`, `PatchAsync` with `old` from the row read inside the unit, `DeleteAsync`),
   read configuration through `ctx.ConfigAsync(db, ct)` (never a hidden section's fields).
4. New item types need no new tools: add an `ItemKind` (fields as `ItemField`s with their wire key, `HasStatus`) to
   `ItemKinds.All`; `create_idea` / `create_lead` / `create_project` appear once `ItemsTable.Creatable` contains the type, and
   `search_items`' type and status filters, `update_item`'s fields and `list_config`'s item types follow. More detail for
   `get_item` (persons, links, attachments, fields with scores, milestones) is an `ItemDetailPart` in `ItemDetails.Parts`;
   more configuration for `list_config` (statuses, sections, fields, templates) a `ConfigPart` in `ConfigParts.All`.
5. `McpServerTests.TheCatalogKeepsItsOwnRules` checks names, schemas and audit events; add a test of the tool over the SDK
   client (`McpKit.ClientAsync`).

**Web**: Account → Security → `McpCard.vue` (per token: switch, state, value once with copy, regenerate, last use, expiry, how to
connect a client to `/mcp`), *Administration → MCP* (`views/admin/mcp/`: Status, Tokens with *Switch off…* and *Revoke all…*,
Log with filters and the entry's audit and History, Tools from the catalog).

## 8. Persons directory sync via Microsoft Graph [NEW]

No source project has this. GGITSV only reads `/me` at sign-in with a delegated
token, and its "people" list is just active local users
(`ServiceCatalog.ListPeopleAsync`).

### 8.1 Design

Scope: **the whole tenant** (decision 2026-11-26 #5) – all user accounts, members
and guests (guests are kept with `user_type = Guest` and can be filtered).

- **App-only** auth (client credentials) with the same app registration:
  `POST https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token`,
  `grant_type=client_credentials`, `scope=https://graph.microsoft.com/.default`;
  application permission `User.Read.All` (admin consent). Raw `HttpClient` like
  GGITSV's `MicrosoftDirectoryReader` (no Graph SDK); token cached until 5 min
  before expiry.
- **Delta query** for incremental runs:

  ```
  GET https://graph.microsoft.com/v1.0/users/delta
      ?$select=id,displayName,givenName,surname,mail,userPrincipalName,jobTitle,
               department,companyName,businessPhones,mobilePhone,accountEnabled,userType
  ```

  Follow `@odata.nextLink` until `@odata.deltaLink`; store it in
  `directory_sync_state.delta_link`.
  - `@removed` or `accountEnabled = false` → `is_active = false`.
  - **410 Gone** (deltaLink expired) → full run.
  - **429/503** → honour `Retry-After`, continue from the current nextLink.
- Full run every `Directory:FullSyncDays` (7): persons not seen → inactive.
- **Upsert key**: (`entra_tenant_id`, `entra_object_id`) – same identity key as
  SSO. E-mail lower-cased; `display_name` falls back to UPN. If a user with the
  same (tid, oid) exists, `persons.user_id` is set.
- All writes through `SyncWriter` (source `directory`) → only changed persons get
  a new `seq` and are pushed live.
- Result in `directory_sync_state`, `key_values` (`LAST_DIRECTORY_SYNC`), audit on
  failure; shown in *Administration → Microsoft integration*.

### 8.2 Background task

PNMTD/GGITSV pattern: singleton `DirectorySyncTask : BackgroundService`,
registered `AddSingleton<DirectorySyncTask>()` +
`AddHostedService(sp => sp.GetRequiredService<DirectorySyncTask>())`, loop with
a GGITSV signal (`Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = DropWrite })`)
so *Run now* (`POST /api/v1/directory/sync`, `Directory:Manage`) wakes it early:

```csharp
protected override async Task ExecuteAsync(CancellationToken stop)
{
    while (!stop.IsCancellationRequested)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            if (await IsDueAsync(scope, stop)) await RunAsync(scope, stop);
        }
        catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Directory sync failed"); }
        await signal.WaitAsync(TimeSpan.FromMinutes(5), stop);   // re-check due-ness every 5 min
    }
}
```

`BackgroundTasks:Enabled=false` disables it in tests (PNMTD).

### 8.3 Implementation (phase 6, pulled forward)

`PGPM/Persons/DirectorySync/` (namespace `PGPM.Persons.DirectorySync`, never `PGPM.Directory`, §15 #23):
`GraphTokenCache` (client credentials, cached per tenant and client until 5 min before expiry, forgotten on a 401),
`GraphDirectoryClient` (HTTP client `microsoft-directory`, absolute addresses; pages handed on one by one; 429/503 waited out per
`Retry-After` – seconds or date, 10 s when missing, at most 5 min, 8 times per page – and the same page asked again; 410 →
`DeltaExpiredException`), `DirectorySyncService` (the run), `DirectorySyncTask` (the loop and the signal),
`DirectorySyncStateEntity`, `DirectorySyncSetup`. `DirectoryController`: `GET /api/v1/directory/status` and
`POST /api/v1/directory/sync[?full=true]` (202, the task wakes; 409 `directory_off`), both `Directory:Manage`.

- **Configuration**: `Directory:Enabled` plus `Entra:TenantId`, `ClientId`, `ClientSecret`; `Entra:Enabled` (the SSO) is not
  needed – a directory without SSO works.
- **One write unit per page** (`SyncWriter.BeginAsync` with source `directory`, `ChangeContext` without a user, so `created_by`/
  `updated_by` stay empty): the counter's lock is never held across a Graph call. The job sets the persons' properties
  directly inside the unit – a client's patch of a Microsoft person's directory fields is refused by `PersonsTable` –, so the
  stamp, the change log (source `directory`) and the live feed are the sync's own. EF only marks a person modified when a value
  differs, and `directory_synced_at` is set only then: an unchanged person keeps its seq.
- **Fields**: a property missing from a page is unchanged (delta pages may carry only what changed), a null clears it; mail
  lower-cased; display name → UPN → e-mail; the first business phone; `userType` `Member`/`Guest` (others null);
  `accountEnabled` decides `is_active`; `@removed` → inactive, never deleted.
- **Modes**: full when there is no delta link, `Directory:FullSyncDays` after the last full run, after a 410, or asked for
  (`?full=true`); a full run reads from scratch and makes every active Microsoft person of the tenant it didn't see inactive.
  The delta link is stored only after a complete run; a failed run leaves it, and the next run starts from it again.
- **Linking**: a person whose (tenant, object id) is a user's Microsoft identity gets `user_id`; a user's manual person is
  converted into the directory person when the directory brings it first (keeps its assignments, as at a sign-in), otherwise
  the manual person lets go (`user_id = null`) – every user keeps exactly one person. Audit `person.linked` (actor_ref
  `directory`).
- **Result**: `directory_sync_state` (with the mode, the seen and linked counts, the last full run and the last success),
  `key_values` `task.directory_sync`, audit `directory.sync_failed` for a failure (the error as Microsoft said it, never the
  secret), *Run now* is `directory.sync_requested`.
- **Tests**: `PGPM.Tests/Support/FakeGraph.cs` (login + `/users/delta` with tenants per test, paging, delta tokens, removals,
  410, throttling, refused tokens) and `DirectorySync/DirectorySyncTests.cs`.
- **Web**: *Administration → Microsoft integration* (`views/admin/MicrosoftView.vue`): the SSO settings (read-only), the sync's
  settings, next runs, the last run with its counts and error, the persons by kind, *Run now* and *Full run*.

---

## 9. Background tasks

| Task | Interval | Source | Purpose |
|---|---|---|---|
| `DatabaseMigrationService` | startup | PNMTD | migrations + custom migrations |
| `ConfigPackageSeeder` | startup | NEW | first start: import built-in packages `core` and `innovation-ai` (§17) |
| `LiveSyncHub` | continuous | NEW | owns WebSocket connections, change feed, heartbeats (§11.6) |
| `McpTokenExpiryTask` | 5 min | PNMTD | record token ends |
| `DirectorySyncTask` | 6 h (+ run now) | NEW | §8 |
| `FileCleanupTask` | 1 h | PNMTD + PLM | stale uploads (24 h), bytes no row names (purged files), `file_uploads` temp files, `embedded` files unreferenced for 30 days (§10.3) |
| `FileCheckTask` | 1 h | PNMTD | the folder, free disk space (`Files:MinFreeMb`), missing or changed files |
| `TombstonePruner` | 1 h | PLM | delete tombstones of `other` delete batches older than `Sync:TombstoneDays` (90) and rows of purged batches; update `sync_state.tombstone_horizon_seq`. Trash batches are never pruned automatically. |
| `LogPruner` | daily | PNMTD | `mcp_log` (10,000), `sync_mutations` (30 d), `sync_evictions` (90 d), `change_log` map moves (90 d) |

All: `BackgroundService` + `PeriodicTimer` or signal, own DI scope per run,
catch-and-log, task health timestamp in `key_values`.

*Administration → System* (phase 7, `GET /api/v1/system`, *System: View*) lists every `task.*` key with its last run and
summary, *overdue* when the background tasks are on and the last run is older than twice the task's interval (plus the
start's minute): tombstone pruner 1 h, log pruner 1 d, file cleanup 1 h, file check 1 h, MCP token expiry 5 min, directory
sync `Directory:IntervalHours` (listed only while it is on or has run). A failed run writes no key, so it shows as overdue.

Health keys (phase 2 and 6): `task.tombstone_pruner`, `task.log_pruner`, `task.mcp_token_expiry`, `task.directory_sync` (value:
the last run's summary). `McpTokenExpiryTask` and `DirectorySyncTask` are singletons registered always and hosted with the
background tasks, so the tests run them by hand (`RunAsync`, `StartAsync`). The MCP log is also trimmed every 25 writes.

## 10. Files and attachments [PNMTD storage + PLM transfer]

### 10.1 Storage (PNMTD `Files/FileStore.cs`)

- Disk below `Files:Path` (`/var/lib/pgpm/files`, host bind mount), path
  `yyyy/MM/{id}-{slug}.{ext}`, files 0640, folders 0750. DB holds metadata only
  (`files`).
- Write path: stream into `.tmp/` while hashing and counting, cut off at the
  limit, images verified by magic bytes (PNG/JPEG/GIF/WebP) and metadata stripped
  (`ImageCheck`), atomic `File.Move` into place, then DB row; on DB failure delete
  the file.
- `Locate()` rejects paths outside the root and symlinks.
- Images (PNMTD `docs/locations-knowledge-plan.md` §5.5): type from the bytes
  only, at most 10,000 px a side and 40 megapixels, metadata removed without
  re-encoding (JPEG APP1/APP13/comments, PNG text/eXIf/tIME chunks, WebP
  EXIF/XMP, GIF comments); HEIC, AVIF and SVG are refused as images (can be
  attached instead).
- Limits: `Files:MaxAttachmentBytes` 25 MB, `Files:MaxImageBytes` 2 MB (the
  browser prepares images first, §18.4),
  `Files:BlockedExtensions` (`exe msi dll scr com bat cmd ps1 vbs js jar hta lnk`,
  incl. double extensions), `Files:UploadsPerHour` 200.

### 10.2 Transfer (PLM `public/js/files.js`, `BlobActions.cs`)

PNMTD uploads the raw body in one request (XHR for progress). PGPM needs uploads
that are queued offline and survive restarts, so it uses PLM's chunked protocol
(plaintext):

| Endpoint | Body | Answer |
|---|---|---|
| `PUT /api/v1/files/{id}/chunks/{n}` | raw bytes, 4 MiB (chunk 0 truncates/starts) | `{received}` |
| `POST /api/v1/files/{id}/commit` | `{sha256, size}` | 200 – server verifies sha256 + size, moves file into place, sets `files.status = available` (new `seq`) |
| `GET /api/v1/files/{id}/{name}` | – | bytes; images inline, others `attachment`; Range support, ETag = sha256, `Cache-Control: private, max-age=31536000, immutable` (PNMTD) |

Client flow:

1. `attachFile()`: create `files` row (`status: pending`) in the outbox **and**
   store bytes in IndexedDB `blobs`; thumbnail (≤ 32 KB WebP) generated for images.
2. The metadata row syncs like any row; the bytes are uploaded by
   `flushPendingUploads()` at the start of every sync run (PLM), chunk by chunk.
3. Other clients get the metadata through the pull. Bytes:
   - `cover` and `map_image` files are **prefetched** (maps must render offline);
   - `attachment` files are downloaded **on demand** (`fileBlob()`), verified
     against sha256, cached.
4. Cache budget 200 MB (`enforceCacheBudget()`), oldest downloads evicted first,
   never evict unuploaded bytes. Offline + not cached → "Not available offline".

PLM pitfall carried over as a to-do: uploads restart from chunk 0 after an
interruption; resuming from `received` is a cheap improvement (PLM
docs/19 §5.3).

### 10.3 As built (phase 3)

`PGPM/Files/`: `FileStore` (the folder, PNMTD's), `ImageCheck` and `FileNames` (copied from PNMTD), `FilesTable` (the sync
descriptor), `FileService` (chunks, commit, download), `FileCleanup`/`FileCheck` (+ tasks), `FilesOptions` (Files:*).

- **The row** (sync, edit on its item or map): `{itemId | mapId, purpose, fileName, contentType, sizeBytes, sha256 (hex),
  width?, height?, thumbnail? (base64, ≤ 32 KB)}`; `status` is the server's (`pending`), `storagePath` never sent. Covers,
  attachments and embedded images belong to items, map images to maps; image purposes need PNG, JPEG, GIF or WebP; the
  name's (double) extension must not be blocked; `sizeBytes` ≤ the purpose's limit (`Files:MaxImageBytes` for images,
  `Files:MaxAttachmentBytes` else). Patchable: `fileName`, `thumbnail`. **Shared bytes:** an insert whose SHA-256 and size
  are those of an available file the user can read is `available` at once (same stored bytes, no upload) – conversions and
  copies need no upload; the client sees `status: "available"` in the insert's row.
- **Chunks** `PUT /api/v1/files/{id}/chunks/{n}`: raw body, ≤ 4 MiB (413 `too_large`), the total ≤ the row's `sizeBytes`
  (413); chunk 0 starts the upload over, every other must be the next one (409 `chunk_out_of_order` with `expected`; the
  client restarts from 0). 409 `file_available` when stored already, 429 `too_many_uploads` (`Files:UploadsPerHour`,
  counted at chunk 0), 503 `file_storage_unavailable`. A row the user can't see is 404, one they only read 403
  `missing_access`. `file_uploads.next_chunk` keeps the order.
- **Commit** `POST …/commit {sha256, size}`: size and hash must be those received and the row's (409 `size_mismatch` /
  `sha256_mismatch` – the upload is then discarded, 409 `upload_missing` for a commit without one); a blocked name is 422
  `blocked_type`. Images (cover, map image, embedded) are checked by their bytes (415 `not_an_image`; 413 `too_large` over
  10,000 px a side or 40 MP) and their metadata stripped **without re-encoding**: the stored bytes may then differ from
  the uploaded ones – the answer and the row carry the stored `sha256` and `sizeBytes`, the detected `contentType`, `width`,
  `height`, and a name with the type's extension (`Screenshot` → `Screenshot.png`; the answer's `fileName`, phase 3
  integration). The row becomes `available` (new rev, delivered live). A commit of a stored file answers with it (a
  retry).
- **Download** `GET …/{id}/{name}`: read access; 404 `file_pending` before the commit. Images inline as their type,
  everything else `application/octet-stream` + `attachment`; ranges (206); `ETag` = `"<sha256>"`; `Cache-Control: private,
  max-age=31536000, immutable`.
- **Cleanup** (`FileCleanupTask`, hourly): uploads older than 24 h (and leftovers in `.tmp/`); stored files no row names
  (purged rows, pruned tombstones) older than an hour; `embedded` files no text of their item names (Markdig over the
  description, the `markdown` field values and the milestones' descriptions – links, images, autolinks, reference
  definitions to `/api/v1/files/{id}/…`) for 30 days, counted from their creation (`files.unreferenced_since`), are
  tombstoned through a `SyncWrite` (source `system`, an `other` batch). **Check** (`FileCheckTask`, hourly): the folder,
  `Files:MinFreeMb`, every available file's bytes and size; problems logged and in the task's health entry.

**Client as built (phase 3).** `local/files.ts` `FilePipeline` (app instance `appFiles` in `local/boot.ts`, the
calls in `api/files.ts` over `fetchApi` – raw bodies, outside the generated client). Checks (blocked extensions incl.
double ones, attachments ≤ 25 MB, images through PNMTD's preparation to ≤ 2 MB), sha256, dimensions and the thumbnail
(canvas WebP ≤ 32 KB, base64; none where the browser can't encode WebP) happen before **one** repository transaction
that writes the `files` row and the `blobs` bytes (an ArrayBuffer: older Safari versions lost Blobs, and Dexie's
`update()` turns an ArrayBuffer into a plain object – blob rows are changed with get + put). The engine's `afterRun`
hook (after every successful run of the leader) calls `flushPendingUploads()`, then `prefetch()`: pending rows whose
insert the server accepted (`_local.serverRev` set) go up in 4 MiB chunks; the next chunk number is kept after each
one, so an interrupted upload **resumes** (the to-do above is done); a chunk past 0 refused with 409 or a wrong
`received` starts over from chunk 0 once, so does a 409/422 of the commit; the commit's row is applied at once.
Network errors and 5xx stop the run and keep the state; any other 4xx is kept on the blob as *Failed* (with *Retry*).
The budget (200 MB) takes the sizes from the `files` rows and the order from the `cachedAt` index (bytes are never
read for it): uploaded or downloaded bytes go oldest first, on-demand attachments before prefetched images, never
unuploaded bytes; prefetch (`cover`, `map_image`, `embedded`) only fills the budget by evicting attachments. Markdown
attachment links are not fetched on sight (the resolver answers the server URL online, null offline); images resolve
through `fileBlob()` (sha256 checked) to blob: URLs. Removing a cover deletes its file only when it belongs to the
item and no other local item shows it; otherwise only `imageFileId` is cleared. A removed, never-synced file goes with
its bytes; other bytes go once the server confirmed the delete.

*Integrated with the server (phase 3 integration):* a chunk past 0 refused with 409 (`chunk_out_of_order`, the server
lost or never had the upload) or a wrong `received` starts over from chunk 0 once; so does a 409 of the commit
(`size_mismatch`, `sha256_mismatch` – the server discarded the upload –, `upload_missing`); 422 `blocked_type`, 415
`not_an_image` and 413 `too_large` are final (*Failed* with the server's `detail`, *Retry*). 409 `file_available` (the
bytes are stored already: a commit whose answer was lost, or bytes the server shared at the insert) counts as uploaded.
503 `file_storage_unavailable`, 429 `too_many_uploads` and 5xx stop the run and keep the state. 507 `file_storage_full`
(phase 7, §15 #86) is neither: the file stays queued as *waiting* (`blobs.waiting` = the reason and the time to try again)
for the sync's longest error backoff (5 min) – no request on every sync run meanwhile, then the pipeline's own timer
tries it again, and so does *Retry*; an accepted chunk ends the waiting. The run goes on with the next file (the reserve
depends on the size: a smaller one may still fit). The attachment list says *Server storage full* (tooltip *The server's
file storage is full – ask an administrator.*, with *Retry*), a toast says it once per file (§15 #88). The commit's answer is
only part of the row: it is laid over the row as the server had it (item, purpose, thumbnail stay; a newer row from the
live channel wins by rev). When the commit stripped an image's metadata (the answer's `sha256` differs from the
uploaded one), the stored bytes are downloaded at once (checked against the new hash) and **replace** the uploader's
local ones, so every device holds the same bytes; should that fail, the uploaded bytes stay (the same picture) until
evicted. A download refused with 404 `file_pending` is *Not uploaded yet*, other 404/403 *gone*. An insert whose bytes
the server shares arrives as `available`: nothing is uploaded (§12.8, conversion).

---

## 11. Offline, PWA and synchronization [PLM, adapted]

### 11.1 Service worker

PLM `public/sw.js` behaviours, implemented as `PGPM.WebUI/src/sw.ts` with
`vite-plugin-pwa` (`strategies: 'injectManifest'`, `injectRegister: false`):

- **Precache** = `self.__WB_MANIFEST` (generated: all hashed JS/CSS, the woff2
  fonts, the manifest and its icons, `index.html` – not PrimeIcons' svg/ttf/eot
  fallback fonts). `precache()` + `cleanupOutdatedCaches()` + `clientsClaim()`;
  the precached files are served by a route of PGPM's own (not
  `precacheAndRoute`), so navigations take the race below first and the bypass
  rules (`src/pwa/swRules.ts`) apply to every route. Install must fail if the shell can't
  be cached (PLM `CRITICAL` rule – Workbox precache does this by default).
  The lazy editor chunks (Crepe, Monaco incl. its workers, Mermaid) are part of
  the precache so Markdown editing works offline, plus Monaco's icon font
  (`codicon-*.ttf`, ttf only); `maximumFileSizeToCacheInBytes`
  raised to 10 MB (Monaco's chunk exceeds Workbox's 2 MB default).
- **Navigation**: network raced against 4 s (`PAGE_ANSWER_MS`); timeout or 5xx →
  cached `index.html` (PLM `navigate()`, L348). Late/5xx bodies are cancelled.
- **Never handled by the SW**: `/api/*` (incl. sync and SSO start), `/mcp`,
  `/signin-oidc`, `/healthz`, `cache: 'no-store'` requests. Attachments are
  served from IndexedDB by the app, not via SW caching.
- **Update**: on `updatefound` + `installed` + existing controller → PrimeVue
  toast "New version available – Reload" → `postMessage('skip-waiting')` →
  reload on `controllerchange` (PLM `registerServiceWorker()`, `app.js` L524).
  Registration with `{ scope: '/', updateViaCache: 'none' }`.
- **Background Sync**: when the outbox is non-empty and a sync fails with
  offline, `registration.sync.register('pgpm-sync')`; the SW forwards
  `{type:'pgpm-sync'}` to open pages (PLM L411).
- Server headers: `index.html` and `sw.js` `Cache-Control: no-cache`; hashed
  assets `immutable`.

PLM lesson kept in mind: never derive the shell cache name from the version
(PLM's `plm-${version}` deleted the full cache from a stale worker) – Workbox
precache uses one cache with per-URL revisions.

### 11.2 Manifest and install

- Static manifest from `vite-plugin-pwa` (PLM generates it only because of its
  gate token): `id: "/"`, `start_url: "/"`, `display: "standalone"`,
  `launch_handler.client_mode: "focus-existing"`, `theme_color: "#3b59a5"`,
  icons 192/512/maskable-512/svg (generated by `tools/make-icons.py`, stdlib
  Python, from the logo's geometry; `LogoMark.vue` draws the same), `shortcuts`
  *New note* (`/notes?new=1`), *New idea* (`/ideas?new=1`), *Maps* (phase 7, §12.10). A global router guard
  (`views/items/createRequest.ts`) takes `new=1` off a list's URL and leaves the request for the list, which opens its
  create dialog (or says the rights don't allow it). With `focus-existing` a shortcut focuses an open window and hands
  its URL to `window.launchQueue` (`pwa/launch.ts`, Chromium), which routes there; the launch that loaded the page is
  skipped.
  Later: `share_target` → create a note from shared text/URL (PLM `js/share.js`).
- Install prompt: PLM `public/js/ui/install.js` (catch `beforeinstallprompt`,
  `state()`, `promptInstall()`, manual steps for iOS/Firefox) → offered in the
  user menu.
- `navigator.storage.persist()` after login (PLM `requestPersistence()`).

### 11.3 Local database

Dexie schema = data model §14 (one store per synced table + `outbox`, `conflicts`, `blobs`,
`drafts`, `meta`). Rules from PLM `db.js`:

- Additive versioned migrations only; backfills inside the upgrade transaction.
- `onblocked` → "PGPM is open in another tab with an older version – close it";
  `versionchange` → close and reload.
- `wipeLocalData()` on logout / user change / reset (delete DB + caches, keep
  service worker).
- Bookkeeping fields stripped before push (PLM `withoutBookkeeping`).
- Writes go through repositories (`src/local/repo/*.ts`) that update the row and
  append the outbox entry **in one Dexie transaction**, then emit a debounced
  sync trigger.
- **One sync runner across tabs**: `navigator.locks.request('pgpm-sync', …)` (PLM
  has no multi-tab coordination; Dexie's `liveQuery` updates the other tabs).

### 11.4 Transports: HTTP sync and live WebSocket

PLM syncs by polling only (docs/19 §7 rejects WebSocket/SSE for its
single-user case). PGPM is collaborative and must spread changes fast while
online (decision 2026-11-26 #10), so it has **two transports with one data
format**:

| Transport | Direction | Used for |
|---|---|---|
| `POST /api/v1/sync` (cookie + `X-XSRF-TOKEN`, `no-store`) | push (client → server) and pull pages (server → client) | **all writes**; catch-up after start/offline; fallback polling while the live channel is down |
| `GET /api/v1/sync/live` (WebSocket) | server → client | live delivery of changes, evictions, resets and session events while online |

Writes always go over HTTP: one write path, CSRF-protected, idempotent through
`mutationId`, easy to retry. The WebSocket carries no state-changing client
messages (only `hello`, `ack`, `pong`), so it adds no CSRF surface.

**HTTP sync**

```jsonc
// request
{
  "clientId": "0193…", "appVersion": "1.0.0",
  "since": 18234,                 // cursor; 0 = full
  "accessEpoch": 7,
  "limit": 500,                   // rows per table per page (1–1000)
  "mutations": [ /* ≤ 200 outbox entries, data model §14.1 */ ],
  "resolved": [ { "table": "items", "id": "…", "field": "title", "resolution": "theirs" } ],
  "pull": true,                   // false while the live channel is connected
  "pending": 12, "conflicts": 0   // diagnostics
}
// response
{
  "head": 18301, "cursor": 18301, "more": false, "reset": false, "accessEpoch": 7,
  "results": [
    { "mutationId": "…", "status": "accepted", "id": "…", "rev": 5, "seq": 18290 },
    { "mutationId": "…", "status": "accepted", "id": "…", "mergedInto": "…" },        // duplicate pair
    { "mutationId": "…", "status": "partial",  "id": "…", "rev": 6, "seq": 18291, "conflictFields": ["title"] },
    { "mutationId": "…", "status": "conflict", "kind": "deleted_by_other", "current": { /* tombstone */ } },
    { "mutationId": "…", "status": "conflict", "kind": "changed_since_delete", "current": { /* row */ } },
    { "mutationId": "…", "status": "rejected", "code": "missing_access", "message": "…", "current": { /* row or null */ } }
  ],
  "conflicts": [
    { "mutationId": "…", "table": "items", "id": "…", "field": "title",
      "theirs": "Website 2027", "by": "Anna Muster", "at": "2026-11-26T09:10:12Z", "rev": 6 }
  ],
  "changes":   { "items": [ /* rows */ ], "map_elements": [ { "id": "…", "seq": 18299, "rev": 2, "deleted": true } ] },
  "evictions": [ { "kind": "item", "id": "…" } ]
}
```

**Live channel** `wss://<host>/api/v1/sync/live`

- Handshake: same origin, session cookie, **`Origin` must equal the host**
  (cross-site WebSocket hijacking; same check as PNMTD `UseMcpOriginCheck`),
  session validated through `SessionCache` like any request.
- JSON text frames:

  | From | Message | Meaning |
  |---|---|---|
  | client | `{"type":"hello","clientId","since","accessEpoch","appVersion"}` | start the feed at the cursor |
  | server | `{"type":"welcome","head"}` | |
  | server | `{"type":"changes","changes":{…},"evictions":[…],"cursor":N,"more":bool}` | same shapes as HTTP |
  | client | `{"type":"ack","cursor":N}` | applied up to N – the server sends the next batch only after the ack (flow control) |
  | server | `{"type":"reset"}` | access epoch changed or cursor below tombstone horizon → client runs an HTTP sync with reset |
  | server | `{"type":"session","state":"rights_changed"\|"expired"\|"revoked"}` | refetch `/auth/me` / sign in again |
  | server | `{"type":"app","version":"1.3.0"}` | new version deployed → update prompt |
  | server | `{"type":"editing","itemId","target","user","since"}` / `{"type":"editing_end",…}` | *… is editing* notice (§18.6) |
  | both | `{"type":"ping"}` / `{"type":"pong"}` | every 25 s; two missed → reconnect |

- Close codes: `4401` session invalid, `4403` origin, `4409` reset required,
  `4429` too many connections (max. 10 per user).

**Wire format** (phase 2, the contract between `PGPM/Sync` and `PGPM.WebUI/src/local`):

- **Tables** are named by their snake_case table name: `groups`, `persons`, `items`, `item_shares`, `user_settings`
  (phase 3+ add more; `changes` lists them in registry order, parents first). **Rows** are the data-model columns as
  **camelCase** properties (`owner_user_id` → `ownerUserId`): uuids lower-case strings, timestamps ISO-8601 UTC
  (`2026-11-27T09:12:00.123400Z`, always six fractional digits – what PostgreSQL keeps, sortable as text; read in any
  ISO form, with an offset converted to UTC, without one taken as UTC), `date` as `yyyy-MM-dd`, `numeric` as a JSON number (`1234.50`),
  enums lower case (`note`, `manual`, `read`), `text[]` as an array, jsonb as JSON, bytea as base64. Every row has
  `seq`, `rev`, `deleted`, `deleteBatchId`, `createdAt`, `createdBy`, `updatedAt`, `updatedBy`; `items` also the computed
  `access` (`read` | `edit` | `manage`). Never delivered: `groups.normalizedName`, `groups.entraAppRole`. A deleted row is
  `{"id", "seq", "rev", "deleted": true}` (as the `current` of a result also with `deleteBatchId`, for *Restore*).
- **Applying a page**: evictions first (delete the root and every row whose `itemId` is it), then the rows: a tombstone
  deletes, a row replaces the local one when `rev ≥` the local `rev` (a re-stamp has the same rev and a new `access`),
  then pending outbox patches are re-applied. The cursor moves to `cursor` (not to the highest seq seen). Rows of tables
  the client doesn't know yet are ignored.
- **Mutations** as `PGPM.WebUI/src/local/outbox.ts` types them: `{mutationId, batch, table, id, op, baseRev, changes,
  row, clientChangedAt, force?}`. `changes` keys are wire keys or dotted members of a jsonb column (`value.viewport`);
  `{"old": …}` without `"new"` removes a jsonb member (a column without `new` is set to null). An insert's `row` holds
  the camelCase columns; server-owned ones are ignored (`items`: `number`, `ownerUserId`, `statusChangedAt`,
  `originItemId`, `convertedToItemId`; `persons`: `source`, `entra*`, `directorySyncedAt`, `userId`; `user_settings`:
  `userId`) and so are the synced-row columns and unknown keys (`access`).
- **Results**: `accepted` `{id, rev, seq}` (+ `mergedInto` for a duplicate pair – `id` is the client's, `mergedInto` the
  row it became); `partial` `{id, rev, seq, conflictFields}`; `conflict` with `kind` `field` (every key conflicted or was
  a no-op: nothing applied; `conflictFields`, `current`), `deleted_by_other` (`current` = tombstone with
  `deleteBatchId`) or `changed_since_delete` (`current` = row); `rejected` `{code, message, errors?, current?}` – no
  `current` means: drop the local row. Codes: `missing_access`, `missing_right`, `parent_missing`, `validation_failed`
  (`errors` by wire key), `not_patchable`, `duplicate_id`, `not_supported`. A row the user can't see (or that doesn't
  exist) is `missing_access` without `current`. Every conflicting field is also in `conflicts[]` (`theirs`, `by` = the
  display name of the last change-log entry of that field, `at`, `rev`).
- **Resolutions**: send `resolved: [{table, id, field, resolution}]` (`mine` | `theirs` | `edited`) for every decided
  field – `field: null` for the delete kinds (the whole row); each becomes a `conflict_resolved` History entry of a row
  the user may see, anything else is ignored; *Keep mine* / *Edit* additionally push a normal patch (`old` = theirs).
- **Duplicate pairs** (phase 2): `item_shares (itemId, groupId)` → `mergedInto`; `user_settings (userId, key)` → the
  insert becomes a patch of `value` with `old` = the existing value (the later device wins, never a conflict).
- **Phase 2 writes**: `items` inserts of `type: note` only (others `not_supported`, needs `Notes:Create`), patches with
  *edit*, deletes with *manage*; `item_shares` inserts only for an item inserted by the same user in the same `batch`
  (later changes: `PUT /api/v1/items/{id}/shares`), no patches or deletes; `persons` manual ones with `Persons:Edit`
  (a Microsoft person: `notes` only; a user's own person: not `displayName`/`email`; a user's person is never deleted);
  `user_settings` own rows; `groups` read-only.
- **Reset**: `reset: true` comes with `cursor: 0`, no rows, the current `accessEpoch`; mutations of that request were
  processed (their results are valid). Push the rest, wipe the synced stores, pull from 0 with the new epoch.
- **Live**: `hello {clientId, since, accessEpoch, appVersion}` after an HTTP sync; `welcome {head}`; the open *… is
  editing* notices of readable items; then `changes {changes, evictions, cursor, more}` pages, each to be acknowledged with
  `ack {cursor}` (the next one only after it; 30 s without → close `1008`). `editing {itemId, target, userId, user (display
  name), since}` / `editing_end {itemId, target, userId, user}` – never about one's own edits. `session {state}`:
  `revoked` (deactivated, signed out everywhere – then close 4401), `expired` (the 30-day sign-in – 4401),
  `rights_changed` (refetch `/auth/me`). `reset` is followed by close 4409. A wrong or missing `Origin` is refused with
  HTTP 403 before the upgrade (a browser sees 1006; `4403` is not sent). `app {version}` and close 1001 at a deploy.
  The server's `ping` every 25 s is answered with `pong`; the client's `ping` gets a `pong`. A browser refused with 403
  (a wrong `PublicUrl`) sees close 1006 and keeps reconnecting with backoff while it polls over HTTP.
- **Phases 3–5 tables** (in this order in `changes`): `groups`, `persons`, `statuses`, `sections`, `field_definitions`,
  `project_templates`, `template_statuses`, `template_sections`, `template_field_defaults`, `template_milestones`,
  `assignment_roles`, `saved_views`, `items`, `item_shares`, `maps`, `map_shares`, `files`, `item_sections`, `field_values`,
  `milestones`, `dod_items`, `assignments`, `item_links`, `map_layers`, `map_elements`, `user_settings`. Rows as before
  (camelCase columns of docs/02); enums lower case with underscores where the data model has them (`no_go`,
  `in_progress`, `map_image`, `contributes_to`); jsonb columns as JSON (`field_values.value` exactly docs/02 §7.5,
  `config`, `style`, `settings`, `matrix`, `definition`, `dod`, `defaultShares`); `files.thumbnail` and `maps.preview` base64.
  Computed: `access` on `items` and `maps` (a map template is `read` for everybody, `manage` with *Configuration: Manage*),
  `canEdit` on `sections` (the section's own rule, docs/02 §7.3 – combine it with the item's access). Never delivered:
  `files.storagePath`, `files.unreferencedSince`; sections a user may not view, their `field_definitions`, their
  `field_values`, `item_sections` and `template_field_defaults`.
- **Phases 3–5 writes** (sync): configuration tables (`statuses` … `assignment_roles`) are read-only (`not_supported`).
  `items` of every type with the type's *Create* right; ideas, leads, projects need a status of their type (none sent: the
  template's default, else the type's), not archived, a project's within its template's allowed set; server-owned:
  `number`, `ownerUserId`, `statusChangedAt`, `convertedToItemId`; `originItemId` only on a project's insert (an idea or lead
  the user reads; with edit on it the server sets its `convertedToItemId`); `imageFileId` an image file of the item.
  Children need edit on their item (`parent_missing` when it is gone or invisible): `assignments` (item xor milestone; a
  person once – duplicates `mergedInto`), `item_links` (edit on the source, read on the target; duplicates `mergedInto`; no
  patches), `item_sections` (the section applies to the type; duplicates `mergedInto`), `field_values` (the field's section
  visible and editable, the value of its type – see `FieldValueRules`; scores never; a duplicate pair becomes a patch from
  null, which may conflict), `milestones` (projects only; `decision` and status suggestions only for gates; server-owned
  `completedAt`, `decidedAt`, `decidedBy`), `dod_items` (`na` needs a `note`; server-owned `stateChangedBy/At`), `files`
  (§10.3). `saved_views`: own views; `isShared` (setting it, editing or deleting a shared view) needs *Configuration: Manage*;
  owner server-owned. `maps`: *Maps: Create*, owner server-owned, `isTemplate` needs *Configuration: Manage*, `defaultView`
  manage, delete manage (to the Trash, root `map`); `kind` and `templateSourceId` insert only; a matrix map has its
  `matrix`, a canvas none. `map_shares` as `item_shares` (only with a new map in the same batch). `map_layers` and
  `map_elements` with edit on the map; a map keeps one layer; elements: x/y finite within ±1,000,000 (rounded to two
  decimals), width/height > 0, `z` and `sortOrder` fractional keys, references by kind (an item card's item readable – none
  on matrix maps –, a milestone's or timeline's project readable, an image's file a `map_image` of the same map, connector
  ends elements of the map), `points` only for lines. Deleting an item tombstones its cards on all maps and its incoming
  links; a restore brings them back while their map exists.
- **REST** (online): `PUT /api/v1/items/{id}/shares` `[{groupId, access}]` → the new list `[{id, groupId, groupName,
  access}]`; `POST /api/v1/items/{id}/owner {userId}` → 204; `GET /api/v1/trash?page&pageSize` → `{items: [{batchId,
  rootKind: "item", rootId, rootType: "note", title, deletedAt, deletedBy, deletedByName}], page, pageSize,
  totalCount}`; `POST /api/v1/trash/{batchId}/restore` → `{batchId, restored, skipped}`; `DELETE /api/v1/trash/{batchId}`
  (admin) → 204 (the list also takes `search` – in the title – and `sort` = `-deletedAt` (default), `deletedAt`,
  `title`, `-title`); `GET /api/v1/history?itemId&page&pageSize` → `{items: [{id, at, userId, userName, source, entity,
  entityId, action, changes, clientChangedAt}], …}` – without the entries of `field_values` and `item_sections` of
  sections the user may not view, as the pull leaves out their rows (docs/06 S-01); `POST` / `DELETE /api/v1/editing
  {itemId, target}` (edit access; the `DELETE` carries the body too) → 204. Errors are ProblemDetails with `code` (`missing_access` + `level`, `item_deleted`).

```jsonc
// POST /api/v1/sync – a note with its initial share, offline-created
{ "clientId": "0193a0c2-…", "appVersion": "0.1.0", "since": 41, "accessEpoch": 1, "pull": true,
  "mutations": [
    { "mutationId": "0193a0c3-…01", "batch": "0193a0c3-…00", "table": "items", "id": "0193a0c3-…10", "op": "insert",
      "baseRev": null, "changes": null, "clientChangedAt": "2026-11-27T09:12:00Z",
      "row": { "id": "0193a0c3-…10", "type": "note", "title": "Kick-off", "description": "Agenda", "pinned": false, "tags": [] } },
    { "mutationId": "0193a0c3-…02", "batch": "0193a0c3-…00", "table": "item_shares", "id": "0193a0c3-…20", "op": "insert",
      "baseRev": null, "changes": null, "clientChangedAt": "2026-11-27T09:12:00Z",
      "row": { "id": "0193a0c3-…20", "itemId": "0193a0c3-…10", "groupId": "01970000-0000-7000-8000-000000000001", "access": "edit" } } ],
  "resolved": [] }
// → 200
{ "head": 44, "cursor": 44, "more": false, "reset": false, "accessEpoch": 1,
  "results": [ { "mutationId": "0193a0c3-…01", "status": "accepted", "id": "0193a0c3-…10", "rev": 1, "seq": 42 },
               { "mutationId": "0193a0c3-…02", "status": "accepted", "id": "0193a0c3-…20", "rev": 1, "seq": 43 } ],
  "conflicts": [],
  "changes": {
    "items": [ { "id": "0193a0c3-…10", "type": "note", "number": 7, "title": "Kick-off", "description": "Agenda", "color": null,
      "imageFileId": null, "ownerUserId": "0193…a1", "statusId": null, "statusChangedAt": null, "tags": [], "originItemId": null,
      "convertedToItemId": null, "pinned": false, "leadOrganisation": null, "leadValue": null, "leadCurrency": null,
      "leadProbability": null, "leadDecisionDate": null, "leadNextAction": null, "leadNextActionDue": null, "leadSource": null,
      "projectTemplateId": null, "projectStartDate": null, "projectEndDate": null, "seq": 42, "rev": 1, "deleted": false,
      "deleteBatchId": null, "createdAt": "2026-11-27T09:12:01.204311Z", "createdBy": "0193…a1",
      "updatedAt": "2026-11-27T09:12:01.204311Z", "updatedBy": "0193…a1", "access": "manage" } ],
    "item_shares": [ { "id": "0193a0c3-…20", "itemId": "0193a0c3-…10", "groupId": "01970000-0000-7000-8000-000000000001",
      "access": "edit", "seq": 43, "rev": 1, "deleted": false, "deleteBatchId": null, "createdAt": "…", "createdBy": "0193…a1",
      "updatedAt": "…", "updatedBy": "0193…a1" } ],
    "persons": [ { "id": "0193…", "seq": 44, "rev": 1, "deleted": true } ] },
  "evictions": [] }
// live, server → client (after the user's colleague edited the title)
{ "type": "changes", "cursor": 45, "more": false, "evictions": [],
  "changes": { "items": [ { "id": "0193a0c3-…10", "title": "Kick-off 2027", "rev": 2, "seq": 45, "access": "edit", "…": "…" } ] } }
// client → server
{ "type": "ack", "cursor": 45 }
```

### 11.5 Server: push (`SyncWriter`)

Based on PLM `SyncService.PushAsync` + `SequenceAllocator`, adapted to shared
data and user-resolved conflicts:

1. One transaction per request. `SET LOCAL lock_timeout = '10s'`, then
   `SELECT value FROM sync_counter WHERE id = 1 FOR UPDATE` (global row).
   Timeout → 503 `busy`, client retries with backoff.
2. Per mutation, in order, inside a **savepoint** (a refused mutation never aborts
   the batch):
   - Known `mutationId` → replay the stored result.
   - **insert**: v7 id, unknown; rights and access (`AccessService`: type
     `Create` for items – owner = current user; *edit* on the parent for children;
     map *edit* for layers/elements; item *read* for cards; shares only for an
     item created in the same batch); parent exists and is visible
     (`parent_missing`); validation (`ApiValidator`); server-owned fields
     (`number`, `status_changed_at`, `storage_path`, …) set by the server.
     **Duplicate unique pair**: `assignments`, `item_sections`, `item_links` →
     `accepted` with `mergedInto` = existing id (client rewrites its references);
     `field_values` → converted into a patch `{value: {old: null, new: …}}`
     against the existing row (may become a conflict).
   - **patch**: row exists (tombstone → `conflict/deleted_by_other`); every key in
     the table's **patchable whitelist**; access per row and section. Per key:
     current = `old` → apply; current = `new` → no-op; otherwise **conflict** – the
     key is **not applied** and reported with *theirs* and who/when (last
     `change_log` entry of that field). Non-conflicting keys are applied
     (`partial`). Keys a table names *last writer wins* (`SyncTable.LastWriterWins`:
     `maps.preview`) skip the `old` check – the later patch wins, never a conflict
     (§15 #70). Status changes: status must belong to the type (and template's
     allowed set); server sets `status_changed_at`.
   - **delete**: *manage* on items, *edit* for children and map rows. If the row
     changed since `baseRev` → `conflict/changed_since_delete` unless the
     mutation carries `force: true` (user chose *Delete anyway*). Soft delete with
     cascade in a new `delete_batches` row (`item`, `map` or `other`).
3. Every changed row gets `seq = ++counter`, `rev + 1`, `updated_*`
   (`SyncStampInterceptor`); `change_log` (incl. `conflict` and
   `conflict_resolved` entries from `resolved` and resolution patches);
   result stored in `sync_mutations`.
4. Commit → `ChangeFeed.Publish(head)` → live connections deliver (§11.6).
5. Pull (§11.6) if `pull = true` and no reset.

Patchable whitelist (examples): `items` – title, description, color,
image_file_id, status_id, pinned, lead_*, project_start/end_date (not type,
number, owner, origin, template); `map_elements` – layer_id, z, x, y, width,
height, text, points, start/end_element_id, `style.*`, `config.*`;
`milestones` – everything except item_id and kind; `dod_items` – text, state,
note, sort_order. A test pushes every whitelisted key and reads it back (PLM
pitfall: silently ignored fields).

The same `SyncWriter` API is used by MCP tools, admin endpoints, restore and the
directory job.

**Implementation (phase 2).** `PGPM/Sync/`: `ISyncedEntity` + `SyncedEntity` (the columns), `SyncStampInterceptor`
(seq/rev/`*_by`; a save outside a transaction gets one of its own so the counter's lock lasts until commit),
`ChangeLogInterceptor` (`create`/`update`/`delete`/`restore` rows of tables with `History`), `SyncCommitInterceptor`
(publishes the head after any commit that took seqs – sync, admin, seeds, sign-in), `SyncCounter` (`FOR UPDATE`, `SET
LOCAL lock_timeout` via `set_config`, 503 `busy`), `SyncTables` + one `SyncTable<T>` descriptor per table (wire name =
table name, `Visible`, `Patchable` incl. `col.*` jsonb paths, `ServerOwned`, `Hidden`, `Parents` with
`DeletedWithParent`, `SamePair` + `DuplicateMerge`, hooks `AuthorizeInsert/Patch/Delete`, `PrepareInsert`, `AfterPatch`,
`Validate`), `SyncWriter`/`SyncWrite` (one locked transaction, a savepoint per mutation, `sync_mutations` replay,
`change_log` `conflict`/`conflict_resolved`/`share` entries, delete batches), `SyncService` (`POST /sync`), `SyncReader`
(pull), `TrashService`, `TombstonePruner`/`LogPruner`; `PGPM/Sharing/` `AccessService` (+ `Viewer`) and
`SharingService`; `PGPM/Sync/Live/` `LiveSyncHub`, `LiveConnection`, `EditingRegistry`. Descriptors: `GroupsTable`
(`Sync/SyncTables.cs`), `PersonsTable` (`Persons/`), `ItemsTable` and `ItemSharesTable` (`Items/ItemTables.cs`),
`UserSettingsTable` (`Settings/`); phases 3–5: `Config/ConfigTables.cs` (configuration, saved views),
`Items/ItemChildTables.cs`, `Files/FilesTable.cs`, `Maps/MapTables.cs`; `ValidateAsync` for the checks that read other rows,
`ItemOf(db, row)`/`MapOf` for the History's `item_id`/`map_id`. A new table = entity (`: SyncedEntity`) + model configuration (`Synced(e)`) +
migration + descriptor in `SyncTables.All` after its parents; `SyncRegistryTests` fails until the descriptor exists
and round-trips every whitelisted key. A patch whose keys all conflict (or are no-ops) is `conflict` with `kind:
"field"`; an insert that fails a database constraint is `rejected` (`validation_failed`, unique: `duplicate_id`).

### 11.6 Server: pull, live feed, access changes

**Pull** (PLM `SyncService.PullAsync`):

- `REPEATABLE READ`; for each synced table
  `WHERE seq > :since AND <visibility> ORDER BY seq LIMIT :limit` (visibility
  from `AccessService` predicates, data model §11.5), plus
  `sync_evictions WHERE user_id = @me AND seq > :since`.
- `more` = any table filled its page. Cursor: if `more`, the lowest of the highest
  `seq` among the full tables; otherwise `head` (PLM `nextCursor()`). All synced
  tables and `sync_evictions` come from one registry (`SyncTables.All`); a test
  asserts every `ISyncedEntity` is registered.
- Deleted rows as `{id, seq, rev, deleted: true}`; computed columns `access`,
  `can_edit` filled per user; `sync_clients` updated.

**Live feed** (`LiveSyncHub`, singleton hosted service):

- One `LiveConnection` per WebSocket (user, client id, cursor, awaiting-ack flag,
  own DI scope + `AccessService`).
- `ChangeFeed.Publish(head)` (in-process signal after every commit, any source)
  wakes all connections whose cursor < head; each runs the pull above (debounce
  25 ms), sends `changes`, waits for `ack`, loops while `more`.
- Never more than one unacknowledged batch per connection; no ack within 30 s →
  close (client falls back to HTTP).
- Revalidation every 60 s and on `SessionCache.Forget(user)`: inactive or stamp
  changed → `session/expired` + close 4401; `access_epoch` changed → `reset`;
  rights changed → `session/rights_changed`.
- On deploy: `app/version` broadcast before shutdown; clients reconnect.
- **Phase 7**: the pull reads the head, the horizon and every table's highest seq in one statement first and leaves out
  the tables (and the evictions) without anything above the cursor – the same answer (`SyncReaderTests` compares it
  with reading every table from every cursor), a fraction of the queries (§15 #80, docs/07 §1).
- One server instance (as PNMTD/GGITSV). For scale-out the in-process
  `ChangeFeed` becomes Postgres `LISTEN/NOTIFY` (payload = head) – nothing else
  changes.

**Access changes** (data model §11.5):

| Change | Handling |
|---|---|
| share added or raised (item/map) | **re-stamp** root + subtree: inside the locked transaction `UPDATE … SET seq = :base + row_number()`, counter += count; `rev` unchanged |
| share removed or lowered | re-stamp + `sync_evictions` for users who lost access entirely |
| owner transferred | re-stamp + eviction for the old owner if no access remains |
| group membership, admin flag, `section_groups` | `users.access_epoch += 1` for affected users → `reset` |
| cursor < `sync_state.tombstone_horizon_seq` (after purge) | `reset` |
| `sync_clients` row deleted by an admin | `reset` |

**Trash and restore** (REST, online): `GET /api/v1/trash` (batches of roots the
user may manage), `POST /api/v1/trash/{batchId}/restore` → `SyncWriter`
un-deletes the batch rows (new `seq`, `rev + 1`, skips rows whose parent is gone
and duplicate pairs) → live delivery; `DELETE /api/v1/trash/{batchId}` (admin)
purges rows and files and raises the tombstone horizon.

**Implementation notes (phase 2).** The tombstone horizon is the highest seq ever physically removed (purge, pruned
tombstones, pruned evictions). A device resets when its cursor is below it **and** the horizon rose since the device last
pulled from 0 (`sync_clients.horizon_seq`) – otherwise a fresh device paging through its first sync would reset forever
after any purge. Deleting an item keeps its `item_shares` (they decide who sees the tombstone and may restore it); a purge
removes them. Deleting a group leaves a tombstone (a query filter hides it from everything but the sync), removes its
memberships and rights, tombstones its shares in an `other` batch and bumps its members' epochs. Group membership and
admin-flag changes bump `users.access_epoch` (`UserAccounts`, `GroupService`, `MicrosoftSignInService`) and call
`SessionCache.Forget`, whose `Forgotten` event makes the user's live connections check their session at once. Restoring
share rows re-stamps their items. The pruners run with the background tasks (`BackgroundTasks:Enabled`); the live hub
always (its shutdown sends `app`).

### 11.7 Client: sync engine

`src/local/sync.ts`, ported from PLM `sync.js` (`sync()` L346):

- **Leader tab**: Web Lock `pgpm-sync`; the leader owns the HTTP loop and the
  WebSocket; other tabs are passive and update through Dexie `liveQuery`. When
  the leader closes, another tab takes the lock.
- **Start**: HTTP sync (push outbox in batches of ≤ 200, pull pages until
  `!more`) → open WebSocket → `hello` with the cursor → **live**.
- **Live**: a completed local operation → debounce 150 ms → HTTP push with
  `pull: false`; incoming `changes` → apply → `ack`.
- **Live channel down**: reconnect with backoff 1 s, 2 s, 5 s, 10 s, 30 s (max);
  meanwhile HTTP sync every 30 s (5 min when hidden) and 1.5 s after local
  changes. Other triggers: `online`, `visibilitychange → visible`, service worker
  `pgpm-sync` message. HTTP error backoff 5 s → 15 s → 60 s → 5 min.
- **Applying rows**: replace when `incoming.rev ≥ local.serverRev` (re-stamps
  have the same rev and replace too, updating `access`), then **re-apply pending
  outbox patches** (rebase). **Evictions**: delete the root and all descendants
  (parent indexes), cached blobs, drafts and outbox entries for them; toast
  "Access to … was removed".
- **Results**: `accepted` → remove outbox entry (`mergedInto` → rewrite ids in
  stores and outbox); `partial` / `conflict` → remove entry, create conflict
  entries (data model §14.2), local row keeps the server value; `rejected` →
  restore `current` or delete the local insert with its children, toast with
  reason.
- `reset` → push remaining outbox, wipe synced stores, keep `conflicts`,
  pull from 0.
- `sync()` returns a result and never throws (PLM). Timeouts: 15 s + body time;
  sync 60 s; a timeout counts as offline.
- **Target**: p95 < 1 s from a completed change on client A to its display on
  client B (both live, same region).

**Client implementation (phase 2)** – `PGPM.WebUI/src/local/`:

| Module | Role |
|---|---|
| `tables.ts` | registry of the synced tables: wire name, Dexie indexes, `parents` (eviction cascade), `writes` (`all` / `insert` / `none`), `root` (eviction kind), `serverOwned`, `defaults`, `since`; `dbVersion` |
| `db.ts` | `PgpmDb` (Dexie, schema from the registry), `localDb()`, `wipeLocalData()` with before/after hooks, `claimLocalData(userId)`, `databaseBlocked` |
| `repo.ts` | `repo.create / patch / remove / transaction` – row + coalesced outbox entry in one Dexie transaction |
| `apply.ts` | fresh rows, evictions, results, reset (inside the caller's transaction) |
| `sync.ts` / `live.ts` / `leader.ts` | `SyncEngine` (status, `start`, `stop`, `localChange`, `syncNow`, `sync`, `wake`), the WebSocket, the Web Lock |
| `resolve.ts` | the conflict decisions; `boot.ts` wires engine, repo and conflicts to the session |

Decisions taken while building it:

- **Bookkeeping** lives in `_local = {serverRev, pending, base}` on the stored row and is stripped
  (`withoutLocal`) before anything leaves. `base` is the server's row while entries are pending:
  results and fresh rows are rebased from it (`local = rebase(base, pending)`), so a held-back key
  falls back to the server's value without a refetch. A tombstone keeps the known members (title)
  under `deleted: true` for the conflict dialog.
- **Sent entries are never coalesced**: a push stamps `sentAt` on the entries it takes (≤ 200, in
  `seq` order); later local changes become entries of their own, because the server may already have
  stored a result under the sent `mutationId`. A lost answer is re-sent with the same ids.
- **Cursor** = the maximum of what the HTTP pull and the live feed applied (both are idempotent);
  only a reset sets it back to 0. A reset pushes what is left first (as long as the server answers),
  then empties the synced stores (outbox, conflicts, drafts and blobs stay – whatever is still in the
  outbox goes out afterwards and is rebased on the re-pulled rows), adopts the response's
  `accessEpoch` and pulls from 0, then reopens the live channel.
- **Results**: `partial` and `conflict` of kind `field` are both field conflicts (`conflictFields` /
  `conflicts[]`); one conflict entry per row and kind, fields merged. `deleted_by_other` restores from
  `current.deleteBatchId` – the trash list is searched for the root when the server leaves it out. A `conflict` of kind `field` carries the server's row (`current`), which becomes
  the local row as it is (nothing of the patch was applied); a `partial` is rebased from the base. A page applies its
  evictions first, then its rows: the server only delivers rows the user may see now, so a root evicted and delivered
  again in one page (unshared and shared again) stays.
- **Session**: a sign-out wipes (`session.logout()`); a 401 or close 4401 only stops the engine and
  keeps the data, so the same user's pending changes go out after the next sign-in; a sign-in of
  another user wipes first (`meta.userId`).
- **Tabs**: BroadcastChannel `pgpm-sync` – the leader broadcasts its status (followers ask on start);
  followers forward `changed` and `sync-now`; `editing` notices are relayed. Without Web Locks every
  tab leads.
- **Close codes**: 4401 → session refresh, no reconnect; 4403 → polling only (`liveRefused`; PGPM's server answers a
  wrong origin with HTTP 403 instead, see §11.4);
  4409 → HTTP sync with reset; 4429 → reconnect after the longest backoff. A socket silent for two
  heartbeats is closed (4000) and treated like a dropped one (catch-up over HTTP, reconnect).
- **Integration (phase 2)**: a `reset` message and the close 4409 after it are one reset (one pull from 0); after a
  reset the session reloads `/auth/me` (the groups changed, and no `rights_changed` comes). The browser's `offline`
  event closes the live socket and shows *Offline* (DevTools' and Playwright's offline mode leave open WebSockets
  alone, a sleeping laptop's socket may linger); `online` catches up over HTTP and reopens it.
- Toasts: once per run "n sync conflicts", one per refused change ("Change undone: reason"), one per
  eviction ("Access to … was removed"). Background Sync is requested when a sync fails offline with
  pending entries.
- Tests: `src/test/fakeSyncServer.ts` mirrors PGPM's server in memory (push rules and their codes, visibility,
  re-stamp, evictions, epochs, trash, live channel with ack flow control) for `sync.test.ts`.

### 11.8 Conflict resolution (client) [NEW]

Decision 2026-11-26 #10: no silent last-writer-wins.

- Conflicts are stored in IndexedDB `conflicts` and survive reloads; the sync
  indicator shows the count; a toast appears once per sync run.
- `ConflictResolver.vue` (PrimeVue `Dialog`): objects grouped (map elements per
  map); per field *mine* / *theirs* (who, when); per object **Keep mine**,
  **Keep theirs**, **Edit**; footer **Keep mine for all**, **Keep theirs for all**.
- *Keep mine* → new patch `{field: {old: theirs, new: mine}}` (if theirs changed
  again meanwhile, it conflicts again and the dialog shows the newer value).
  *Keep theirs* → drop the entry, report `resolved` with the next sync.
  *Edit* → field editor prefilled (Markdown: side-by-side) → patch.
- `deleted_by_other` → *Restore with my changes* (`POST /trash/{batch}/restore`,
  then re-push) or *Discard*. `changed_since_delete` → *Delete anyway* (re-push
  with `force: true`) or *Keep it*.
- Logout with unresolved conflicts warns like pending changes.
- Implementation (`components/sync/ConflictResolver.vue`, `local/resolve.ts`): every decision runs in
  one repository transaction with the removal of the entry and its `resolved[]` record (meta
  `resolved`, sent with the next request; `field: null` for the delete kinds). *Keep mine* of a
  `deleted_by_other` is the restore, of a `changed_since_delete` the forced delete; "for all" goes on
  after a failure (a restore offline) and names what failed. *Edit* of a Markdown field opens
  `/notes/:id/edit?conflict=<id>` with the editor's conflict view; saving there resolves as *edited*,
  *Take theirs* as *theirs*. *Restore with my changes* is disabled offline and without *manage* access (the
  Trash's rule; the tombstone keeps the `access` the device knew).

### 11.9 What is dropped from PLM (E2EE)

Not ported: `crypto/*` (`keys.js`, `box.js`, `quick.js`, `sending.js`,
`assistant.js`), lock screen/PIN/passphrase/idle auto-lock (`ui/lock.js`), the
`isUnlocked()` guards in `sync`/`scheduleSync`, `key_epoch` / `WrongKeyError`,
the `envelopes` store, re-seal repair branches in `collectPush`, server columns
`wrapped_dek`, `iv`, `ciphertext`, `key_epoch`, `meta_ct`, session fields
`kdf_params`, `wrapped_mk_*`. Rows are plain typed data; file bytes are sent
as-is with sha256 over the plaintext. Local data is **not encrypted at rest**
(decision 2026-11-26 #9); logout wipes it.

### 11.10 Also not taken from PLM

- Generic opaque server tables → typed tables (rights, sections, MCP, Graph).
- Per-user sequence counter → one global counter.
- Object-level `base_rev` check with "conflict copies" → per-field detection,
  user decides (§11.8).
- Code-defined types with JS field schemas (PLM docs/09) → admin-defined fields
  incl. rating and score types. PLM's renderer idea is reused: one
  `fieldWidget(field, value, onChange)` switch and one `renderValue`; unknown
  option ids shown as "(removed option)"; field keys never renamed.
- Version snapshots (`versions.js`) → server-side `change_log` (online History).
- Polling only (5 min) → live WebSocket feed + polling fallback.
- The outbox is the **source of pushes** (PLM collects from `dirty` indexes).

### 11.11 Drafts / unsaved changes [PLM + PNMTD]

The Markdown editor brings PNMTD's own draft handling (§18.5). For create
dialogs (inline edits commit immediately):

- PLM `drafts.js`: draft in IndexedDB `drafts` (`draft:<id>`, `draft:new:<type>`)
  on 800 ms debounce; `flushDraft()` on `visibilitychange → hidden`, `pagehide`,
  `beforeunload` with a synchronous `localStorage` mirror (≤ 512 KB) promoted on
  boot; existing-object drafts restored automatically, new-object drafts offered;
  drafts older than 30 days swept; one `stopEditing()` removes all listeners.
- PNMTD `EditingPage.vue`: `dirty` prop, `onBeforeRouteLeave` + `beforeunload`
  confirm, Ctrl/⌘S saves, drawer folds to the rail.
- The draft stores its base `rev`; if the row changed meanwhile (visible live),
  the editor shows "changed by X since you started" before saving.

### 11.12 PLM pitfalls and how PGPM avoids them

| PLM pitfall | PGPM |
|---|---|
| Unbounded push (whole backlog, 24 MiB limit) | ≤ 200 mutations per request, loop |
| "Local wins" result pushed only on the next run | server decides per field in the same request; user resolves real conflicts |
| Every table must be in `nextCursor` | registry `SyncTables.All` + test |
| Tombstones pruned while a client is offline | trash never auto-purged; horizon → reset |
| Hand-maintained precache list drifts | generated by `vite-plugin-pwa` |
| IndexedDB upgrade blocked by another tab | `onblocked` message + `versionchange` close/reload |
| Server silently ignores some patched fields | explicit whitelist + round-trip test |
| No multi-tab coordination | Web Lock leader tab |

### 11.13 Sync tests

- **Server integration** (`WebApplicationFactory`, throwaway Postgres): push/pull
  round trips; same-field conflict → `partial`/`conflict`; delete vs edit both
  ways; duplicate pairs merged; share added → re-stamped rows delivered; share
  removed → eviction; membership change → reset; purge → horizon reset;
  idempotent replay of a `mutationId`.
- **Live channel**: `TestServer.CreateWebSocketClient()`; change by client A
  arrives at B; ack flow control; origin check; session revocation closes 4401.
- **Convergence property test**: three simulated clients with random offline
  mutation sequences; after resolving all conflicts with *theirs*, every local
  store equals the server state.
- **Playwright** (`PGPM.WebUI/e2e/sync.spec.ts`): two browser contexts, change visible on the other < 1 s;
  offline edit → reconnect → conflict dialog → resolve. As built: each test creates a team of its own through the admin
  API (a group with Notes: Create, users whose temporary passwords are changed); scenarios (a) live < 1 s, measured
  from the click on one page to the text on the other by the browser's clock, (b) field conflict → *Keep mine* /
  *Keep theirs*, (c) delete vs edit both ways, (d) unsharing evicts live, (e) a membership change resets (live and with
  pending changes), (f) Trash restore, (g) sign-out wipe and another user on the same browser, (h) two tabs, one
  socket, (i) *… is editing*, (j) offline after the first sync, (k) the indicator's states. tools/e2e.sh raises
  `Auth:LoginRateLimit:PermitLimit` for its stack (the tests sign in dozens of users from one address).
- **Contract** (`PGPM.Contract.Tests/SyncContractTests.cs`, against the image): the first admin's password change
  (`FirstAdmin`, once per run), a group and two users, a note with its share in one batch, the wire format (names,
  microsecond timestamps, `access`), the live channel (403 for another origin, `hello`/`welcome`, `ping`/`pong`,
  `changes` + `ack`), a `conflict` of kind `field` with `current` and `conflicts[]`, `resolved[]`, `missing_access`
  without `current`, an eviction live.

## 12. Frontend UI [PNMTD]

### 12.1 Design system (copy)

- `src/main.ts`: `app.use(PrimeVue, { theme: { preset: PgpmPreset, options: { darkModeSelector: '.pgpm-dark', cssLayer: false } }, ripple: false })`,
  `ToastService`, `ConfirmationService`, `Tooltip` directive.
- `src/design/preset.ts`: `definePreset(Aura, …)`, brand `#3b59a5` →
  `primary: palette(brandColor)`, light surfaces slate, dark surfaces zinc
  (primary `{primary.300}` in dark), dense paddings (form fields
  `0.625rem/0.4375rem`, DataTable cells `0.3125rem 0.625rem`), higher contrast
  helpers `filled()`, `tinted()`.
- `src/design/base.css`: root `font-size: 14px`, `--pgpm-font` Inter Variable,
  `--pgpm-mono` JetBrains Mono, spacing `--pgpm-space-1/2/3/4/6` (4/8/12/16/24
  px), `--pgpm-header-height: 48px`, `--pgpm-page-header-height: 40px`,
  `--pgpm-drawer-width: 232px`, `--pgpm-rail-width: 56px`, semantic colors
  (`--pgpm-bg`, `--pgpm-panel`, `--pgpm-border`, `--pgpm-text`, `--pgpm-muted`,
  `--pgpm-ok/fail/warn/none(-soft)`), **note colors**
  `--pgpm-note-yellow|green|blue|pink|purple|grey` (basis of the item color
  palette), utility classes `.panel`, `.panel-header`, `.panel-body`, `.facts`,
  `.form`, `.field`, `.field-error`, `.two-columns`, `.muted`, `.ellipsis`,
  `prefers-reduced-motion` kill switch.
- Dark mode: `stores/preferences.ts` (`theme: system|light|dark`, localStorage
  `pgpm.preferences`; also `drawerCollapsed`, `navFolds`, `tableWidths`,
  `holdRail()`).
- Icons: PrimeIcons only, each menu icon used once.
- Principles (`docs/webui-migration-plan.md` §5): compact (14 px, 32 px rows), one
  40 px header line per page, no banner cards, destructive actions in the ⋯
  menu, two columns from 1200 px, breakpoints 600/960/1200/1600, tables become
  lists below 600 px, light/dark following the system.

### 12.2 Layout and navigation (copy)

- `App.vue`: `AuthLayout` for `meta.layout === 'auth'`, else `AppLayout`; global
  `<Toast position="bottom-right">`, undo toast group, `<ConfirmDialog/>`.
- `layouts/AppLayout.vue`: left drawer 232 px (brand, collapsible groups with
  uppercase labels, 34 px items, active = `--p-highlight-background`, badges, footer
  with identity/version), folds to a 56 px rail on desktop, always rail < 960 px,
  overlay < 600 px. App bar 48 px: hamburger, instance name, centred
  `GlobalSearch` (Ctrl/⌘K), **sync indicator** (replaces PNMTD's "Updated …" +
  refresh button), theme toggle, avatar menu.
- `layouts/navigation.ts`: `NavGroup[]` with `NavItem { label, icon, to, name, right? }`,
  `visibleNavigation()` filters by right and drops empty groups. PGPM groups:
  *Work* (Maps, Projects, Leads, Ideas, Notes, Trash), *Directory* (Persons),
  *Administration* (folded).
- Router: `createWebHistory`, lazy routes, `RouteMeta { layout?, public?, right?, title? }`,
  guard in `router/guard.ts`; tabs as path params (`/projects/:id/:tab(overview|milestones|history)?`);
  list state in the URL (`?q&sort&…`).

### 12.3 Tables: local instead of server-side [PNMTD adapted]

PNMTD's `useServerTable` (`composables/useServerTable.ts`) pages, sorts and
filters on the server. PGPM data is local, so add `useLocalTable<T>` with the
**same return shape** (`items, total, page, pageSize, sort, search, params,
tableProps, onPage, onSort, goTo`) backed by a Dexie `liveQuery` + in-memory
filter/sort – then `ListTable.vue`, `FilterBar.vue`, `useColumnWidths` +
`ColumnEdge.vue` are reused unchanged:

- Sort strings `"field"` / `"-field"`, search debounced 250 ms, filter/search change
  → page 1, URL sync, `rowsPerPageOptions: [25,50,100,200]`.
- Search: normalised (lower-case, diacritics removed) title + description +
  text field values; if volumes grow, port PLM's search worker
  (`js/search.js`, `search.worker.js`).
- `ListTable.vue` props `table`, `what`, `emptyText?`, `rowClass?`, `selectable?`,
  `fixed?`, slots default (`<Column>`s), `#mobile`, `#expansion`.
- Column chooser and per-user list state → `user_settings` (`list:<type>`).
- `useServerTable` stays for online-only lists (history, audit log, users).

### 12.4 Detail pages (copy)

- `PageHeader.vue` (breadcrumbs, h1, `#status`, `#actions`), `TabNav.vue`
  (RouterLink tabs, 2 px primary underline), `MoreMenu.vue` (⋯ with destructive
  actions), `.panel` + `.facts` blocks, `.two-columns`.
- **Inline editing** (`InlineField.vue`, PNMTD's default on detail pages): value as
  text, click → editor with Save/Discard, Enter saves (Ctrl+Enter in textarea),
  Esc discards, validation error stays under the editor. PGPM: `save` writes a
  patch to the local repo (instant, offline) instead of a `PUT`. Types extended
  with the dynamic field types (`fieldWidget` switch, §11.9).
- Description and Markdown fields: rendered with `MarkdownView`; *Edit* routes to
  the `MarkdownEditor` on an `EditingPage` (§18).
- Create: small dialog (title, template for projects) → creates locally →
  navigates to the detail page.
- `useActions.ts` (`run`, `done`, `ask`, `busy`), `useUndo` toast with Undo button.

### 12.5 Data access in the frontend [NEW]

| Data | Access |
|---|---|
| Everything synced (items, maps, persons, configuration, saved views) | `src/local/repo/*` (reads via `liveQuery` → `useLive()` composable returning a Vue `ref`; writes via `create/patch/remove`, which update the row and append the outbox entry in one Dexie transaction) |
| Live channel | native browser `WebSocket` in `src/local/live.ts` (no library); reconnect and heartbeat logic there |
| Online-only (auth, admin, sharing changes, trash, history, MCP, diagnostics) | `openapi-fetch` client (`src/api/client.ts`, CSRF middleware, typed `ApiError`) + vue-query (staleTime 10 s, no retry on 4xx), as in PNMTD |

Online-only views show an "Available online only" empty state when offline.

Phase 2: `useLive(query, initial, key?)` (`local/useLive.ts`) answers `{value, loaded, error, refresh}` (while the sync
pulls, its re-runs come in bursts: §12.10);
`useLocalTable` (`composables/useLocalTable.ts`) returns the shape of `useServerTable` (typed as
`TableSource<T>` in `composables/tableSource.ts`, which `ListTable.vue` takes). The phase 2 endpoints
(sync, sharing, trash, history, editing) are in `openapi.json`; `api/sync.ts` calls them through the openapi-fetch
client on the generated types (`JsonObject` rows as `{[key]: unknown}`), typing strictly only what the client sends
(the generated request types have every member optional) – the hand-written `api/raw.ts` is gone. The live channel's
messages, which OpenAPI doesn't describe, are typed in `local/live.ts`.

### 12.6 Sync indicator, sharing, trash [NEW]

- `components/sync/SyncIndicator.vue` in the app bar: *live* (green dot),
  *syncing*, *offline* (grey), pending count, conflict count (orange); click →
  `SyncDialog.vue` (live status, cursor, last sync, pending list, *Sync now*,
  conflicts tab with `ConflictResolver.vue`, §11.8).
- `components/share/ShareDialog.vue`: groups with read/edit/manage (create
  dialog: local insert; later: `PUT …/shares`, online only), owner transfer.
- `views/trash/TrashView.vue`: `useServerTable` over `GET /api/v1/trash`, restore,
  purge (admin).
- Phase 2 as built: the indicator shows *Live*, *Syncing*, *Online* (HTTP only), *Offline*,
  *Connecting*, *Signed out*, with the pending count and an orange conflict count; a click opens the
  dialog on the conflicts when there are any. The Share dialog shows the sharing read-only below
  *manage* and disables it offline; the owner is picked from the persons that are users. The create
  dialog's *Share with* is preselected with the creator's groups at *edit* (docs/01 §18.2 default);
  the item and its shares are one batch. Sign-out with pending changes or conflicts asks first.

### 12.7 Fields, ratings, scores, statuses, gates [NEW]

- `src/domain/fields/fieldWidget.ts` + `renderValue.ts` (PLM idea): one switch
  over field types; new widgets `RatingInput.vue` (0–5 buttons + *n/a*),
  `ScoreBadge.vue` ("58 / 65", optional color thresholds); select options show
  their points as a muted suffix.
- `src/domain/score.ts` implements §16; memoised per item; recomputed through
  `liveQuery` when inputs change.
- Sections rendered in `item_sections` order; `can_edit = false` disables inputs;
  hidden sections never arrive.
- `StatusBadge.vue` with *overdue* marker (`max_days`, `status_changed_at`, timebox
  date fields).
- `GateDecision.vue`: decision buttons, note, suggested status change (confirm).
- Saved views: selector in `FilterBar`'s `#end` slot; `useLocalTable` takes a
  view `definition`.
- As built in phase 4: §12.9.

### 12.8 Items, persons, statuses, search (phase 3 client, as built)

- **Local database v2** (`local/tables.ts`): every synced table of the data model is registered now – statuses,
  assignments, item links, files, sections, field definitions/values, item sections, templates and their children,
  saved views, assignment roles, milestones, DoD items, maps, map shares/layers/elements – with their indexes and
  parents (eviction cascade: an item takes its shares, assignments, links, files, field values, sections and
  milestones, a milestone its DoD items and assignments, a map its shares, layers, elements and files), so phases 4 and
  5 need no Dexie version. `items` got the index `originItemId` (`revised: 2`, the version-1 schema kept under
  `before`, so the upgrade adds it). Read-only for clients: statuses, groups, sections, field definitions, templates
  and their children, assignment roles. `src/test/fakeSyncServer.ts` mirrors all of them as the server's table
  descriptors have them (checked at the phase 3 integration): the patchable whitelists (`key.*` for jsonb members),
  server-owned members ignored on insert (`ownerUserId`, `number`, `status` of files, the milestones' and DoD items'
  stamps, which it sets), a Microsoft person's notes only and a user's person without name and e-mail, a parent that is
  gone or invisible as `parent_missing`, the items' default status, origin and cover (`imageFileId`) checks, shared views
  for *Configuration: Manage*, visibility through the item or map, duplicate pairs merged, an item's children deleted in
  its batch – plus the file and statuses endpoints as a `fetch` (`fetchFor(userId)`) with the server's codes, answers
  (`StatusItem`, the commit's partial row), PNG metadata stripping and shared bytes.
- **Dexie zones**: the repository's operations answer Dexie promises (`inTransaction()`); a helper taking a
  `RepoTransaction` that ends by awaiting another async helper answers through `inTransaction` too. Dexie keeps its
  transaction zone across one native `await` only – otherwise the caller's next write lands outside the transaction
  and it commits early (`PrematureCommitError`).
- **Generic item views** (`views/items/`, `domain/itemTypes.ts`): one list (`ItemsView` → `ItemList`, keyed per
  type), page (`ItemView`, tabs `/<list>/:id/<tab>`: overview · milestones (projects; phase 4 placeholder) · links ·
  maps (phase 5 placeholder) · history) and editing page (`ItemEditView`, `<type>-edit`) for notes, ideas, leads and
  projects; routes `notes|ideas|leads|projects`, `note|idea|lead|project`. The conflict dialog's *Edit* of a
  description opens the editing page of the item's type. `ItemOverview` shows the description (*Edit*), a
  `#sections` slot for phase 4, attachments (`AttachmentsPanel`), and beside it the fixed fields inline (title, status
  `Select` with `StatusBadge`, colour – `ColorPicker`, 12 presets + hex –, pinned, the lead's fields with value +
  currency, probability, dates, next action highlighted when due, the project's dates, template and origin), the cover
  (`CoverImage`), the persons (`AssignmentsPanel`) and the facts. `InlineField` got `type: 'date'` and a `format` prop.
  Validation mirrors the server (title 1–300, currency `^[A-Z]{3}$`, probability 0–100, end ≥ start).
- **Lists** (`views/items/ItemList.vue`, `itemList.ts`) over `useLocalTable` with `multiSort`: the sort is a
  comma-separated string (`status,-updated_at`) in the ref and the URL; PrimeVue adds a column with Ctrl/⌘-click, not
  Shift as docs/01 §4.3 says (its meta key is fixed). Filters use the saved-view vocabulary of §17 with ids
  (`statusIds`, `categories`, `templateIds`, `groupIds`, `assignedToMe` via `session.personId`, `overdue` = longer in
  the status than `max_days` or a lead's past next-action date, never in a closed status, `hasLink`; `fields` kept and
  ignored until phase 4) plus `ownerIds`, `personIds`, `colors`, created/modified day ranges, `pinned`, `mine`,
  `hideClosed`; quick filters on the bar, the rest in a *Filters* popover. Columns use the `COLUMNS` ids of
  `tools/check-packages.py`; unknown ids (`field:…`) are dropped; key and title always show. `list:<type>` keeps
  `{viewId, search, filters, sort[], columns, pageSize}`; `?view=<id>` applies a view. Saved views
  (`local/savedViews.ts`, `SavedViewMenu` in the bar's `#end`): own and shared views (shared first), *All …*, *Save as
  new view…* (shared needs Configuration: Manage), *Update*, *Rename…*, *Delete…* for the owner of a personal view; a
  shared one – anybody's, the packages' seeded ones too – for everybody with Configuration: Manage (docs/02 §7.8, as the
  server checks it); a changed state shows *(modified)*. Bulk actions on the selected rows (assign a
  person with a role, status, colour, delete) run in one outbox batch each; rows without *edit* (*manage* to delete)
  are skipped and counted in the toast. *Add to map* comes with phase 5.
- **Create dialog** (`ItemCreateDialog`): title, the type's default status (changeable), *Share with* preselected with
  the creator's groups at *edit*; projects are blank with a `#template` slot for phase 4. Item and shares in one batch
  (`local/items.ts` `createItem`), works offline.
- **Assignments** (`AssignmentsPanel`, `PersonPicker`, `RoleInput`): type-ahead over the synced persons (inactive ones
  not offered, still shown where assigned), once per item (assigning again changes the role), role free text with the
  `assignment_roles` suggestions (docs/01 §4.1's defaults until phase 4 syncs some), order by fractional
  `sortOrder`. *Assigned to me* uses `/auth/me`'s `personId`.
- **Links tab** (`ItemLinks`): outgoing and incoming links by relation (*contributes to* / *contributed to by*, *is
  learning of* / *has learning*, *depends on* / *is needed by*, *related to*), the contributing items by their status's
  order (the funnel); an item not on the device shows as *Restricted*. *Add link…* (relation, then `ItemPicker`, a
  local search) needs *edit* on the link's source – for an inverse relation that is the picked item. Duplicates are not
  created (the server would merge them anyway). The seeded *Learnings* view works through `hasLink`.
- **Conversion** idea/lead → project (`convertToProject`, `ConvertDialog`), on the device in one batch, offline too:
  the project gets title (editable), description, colour, the source's shares (inserted with the project, as the create
  dialog does), assignments and outgoing links, `originItemId` = the source, and a `related` link back to the source.
  **Cover and attachments are copied** (phase 3 integration; docs/01 §8): new `files` rows of the project with the
  source file's name, type, size and SHA-256 – the server shares the stored bytes of an available file the user can read
  (§10.3), so the copy is `available` at once and nothing is uploaded; bytes on this device are copied along (an upload
  still waiting goes up for the copy too); a file whose bytes are only on another device and not uploaded yet stays on
  the source. The project's `imageFileId` is set by a patch after the cover copy's insert (the server takes only an
  image file of the item itself – the earlier *by reference* cover was refused). Since phase 7 (§12.10) the
  `embedded` images the description and the carried Markdown values show are copied too, and those texts'
  `/api/v1/files/{id}/…` links (attachment chips included) point at the copies. The source moves to *Selected* (ideas) /
  *Won* (leads) when such a status exists. The client never patches `convertedToItemId` (server-owned): the server
  accepts `originItemId` with read access to the origin and sets the origin's `convertedToItemId` when the user may edit
  it – the client converts only with edit (the source's status changes too); until the server's row arrives the source
  counts as converted through the local project naming it (index `originItemId`). Phase 4 adds the template, sections
  and field values (§12.9).
- **Persons, statuses, search** (`views/persons/`, `views/admin/StatusesView.vue`, `components/GlobalSearch.vue`,
  `local/search.ts`): the persons list (`list:person`) searches name, e-mail, job title, department and company, filters
  source, members/guests and department and shows active persons unless *With inactive*; *New person* (Persons: Edit)
  creates a manual person offline. The person page shows every field, the linked user and *All assignments* (item or
  milestone with the role, *Restricted* for items not on the device); *Edit* changes a manual person (not a user's name
  and e-mail), a Microsoft person's notes only; manual persons can be deactivated. Statuses: one list per type
  (`?type=`) over the synced rows; *New*, edit (colour presets + hex, icon `pi-…` with preview, category, default, max.
  days), up/down (`PUT …/order` with every id of the type), *Set as default*, archive/unarchive (not the default) through
  `api/config.ts` (the generated client, typed `StatusItem` answers), each followed by `engine.syncNow()`, disabled
  offline (*Available online only*). Errors show under their field: `validation_failed`'s `errors`, and the conflicts
  `duplicate_name` (name), `default_required` / `status_archived` (default) with the server's `detail`. Global search: local items and active persons, every word must occur, without
  diacritics; key, then title prefix, title, description; grouped *Items* / *Persons* (8 each) in an ARIA
  combobox/listbox with ↑/↓/Enter/Esc; another page closes it, a change of the page's query (a list writing its search
  to the URL) does not.
- **Integration notes (phase 3).** PrimeVue's `Select` takes its accessible name from the selected option unless
  `aria-labelledby` names its label (a `<label for>` doesn't reach its combobox): the status selects of the create,
  convert, bulk and status dialogs do so. The e2e specs wait for the service worker (`offlineReady`) before going
  offline – pages not opened yet load from the precache only then; `localRows(page, store)` reads the local database.

### 12.9 Configuration and method features (phase 4 client, as built)

- **Configuration on the device** (`local/catalogue.ts`): `useCatalogue()` (sections and field definitions the user
  sees, archived ones too, by id/key/section, plus score.ts's `defsByKey`) and `useTemplates()` (templates with their
  allowed statuses, sections, field defaults and milestones), both live; `loadCatalogue`/`loadTemplates` inside a
  repository transaction. Row types for every configuration and milestone table in `local/rows.ts`; `milestones` and
  `dod_items` have their server-owned members (`completedAt`, `decidedAt`/`decidedBy`, `stateChangedBy`/`At`) and
  defaults in `local/tables.ts` (no Dexie version).
- **Field widgets**: `domain/fields/fieldWidget.ts` (one switch: which widget, draft ⇄ stored value, `valueError` = the
  server's `FieldValueRules` plus *required*) and `renderValue.ts` (texts, option parts with colour and points, unknown
  ids as *(removed option)*, "4 / 5" / *n/a*, "58 / 65", sort and search values, `timeboxPassed`). Components in
  `components/fields/`: `FieldEditor` (texts, numbers, dates, switches through `InlineField`; select and multiselect as
  PrimeVue Select/MultiSelect saved as picked, options with colour dot and muted points, archived options offered only
  where chosen; persons by `PersonPicker`, single fields replaced; date-time with `datetime-local`; Markdown rendered with
  *Edit* → `<type>-field-edit` `/<type>/:id/fields/:key/edit`, `FieldEditView` with `MarkdownEditor` mode `field`, draft
  key `items:<id>:field.<key>`, saved as a `value` patch with `old` = the text when editing began), `RatingInput` (one
  button per value + *n/a*, a click on the chosen one clears, labels as tooltips), `ScoreBadge` (*incomplete* while an
  input is missing, optional thresholds), `FieldValue` (read-only, list cells).
- **Sections on ideas and projects** (`local/fields.ts`, `ItemSections` + `SectionPanel` in the Overview's `#sections`
  slot): `item_sections` order (integers, new ones last + 10), open/collapsed from `collapsedByDefault` until toggled on
  the page, scores also in the section header, sub-headings from `config.subheading`, archived fields hidden, a lock where
  `canEdit = false` (inputs read-only; the server refuses such values anyway), a conflict marker per field value (opens
  the conflict dialog). *Add section* (applying to the type, not archived; fills the fields' default values) and
  *Remove section* (tombstones the row, values stay). One value row per field: `setFieldValue` inserts once, then
  patches `value` (null clears; a younger duplicate row of two offline devices loses to the older one locally, as the
  server merges it). Scores recomputed live from `useItemFields` (never stored).
- **Creating items** (`local/templates.ts`, `ItemCreateDialog`, `TemplatePicker`): ideas get the sections *default for
  ideas*, blank projects those *default for projects*, a project from a template – picked in the dialog, *Blank project*
  first – its sections, the template's field defaults (else the field's own `defaultValue`; values of sections the user
  may not edit are left out), its milestones and gates with their DoD (`due = start + offsetDays`, the start date asked in
  the dialog, default today; gate status suggestions copied), all in the item's batch, offline. The status picker shows
  the template's allowed statuses with its default preselected; *Share with* is preselected with the template's
  `defaultShares` (else the creator's groups). A project's status select on its page is restricted the same way.
- **Conversion** asks for a template too (and its start date when it has milestones); the project gets, besides phase
  3's, the source's sections that apply to projects and the values of every field whose section applies to projects
  (shown or hidden; same field = same value, overriding template defaults), except in sections the user may not edit.
- **Lists**: a `field:<key>` column for every field of the sections that apply to the type (scores too; `FieldValue`
  cells, a timebox date passed marked), sorted by `sortValueOf` (select: option order, rating *n/a* below 0, incomplete
  scores last); field values are searched (texts, option labels, person names); `fields` filters applied (`eq`, `in`,
  `contains`, `range`, `set`, `unset`; a field the user doesn't see matches only `unset`), edited in the *Filters* panel
  (`FieldFilters`); `overdue` and the status cell include passed timebox dates (not in a closed status) – the seeded
  *Running experiments* view works with status, `days_in_status` and `field:timebox_end`. `registerColumns()` is the hook
  for phase 5's *quadrant* column. The projects list links *Gate decisions*.
- **Milestones tab** (`views/items/milestones/`, `local/milestones.ts`, `domain/milestones.ts`): list in fractional
  order (up/down) and an SVG mini timeline (gates as diamonds, milestones as dots, done filled, overdue red, today dashed,
  undated ones counted); create dialog (kind, title, due date); per card inline title, status, due date + overdue,
  colour, responsible persons (milestone assignments, `itemId` null, once per milestone, default role *Review board* for
  gates), description (*Edit* → `/projects/:id/milestones/:mid/edit`, mode `field`, draft `milestones:<id>:description`),
  DoD checklist (open/done/n·a; n/a without a note opens an inline note first; the note is kept on state changes;
  progress = done ÷ (all − n/a) shown "3 / 5"; none or only n/a counts as complete). *Done* is free with a complete DoD;
  otherwise – and for a gate without decision – a confirm lists what is missing and says the change is in the History
  (the status patch is the record; the card then shows *Done without a complete DoD / a decision*). `GateDecision`: pick
  pending/go/no-go/iterate/pivot/park, note, *Record decision* (one patch); after go / no-go it offers *Move project to
  …* when the template allows that status, it isn't archived and the project isn't in it – the project moves only on
  confirmation.
- **Gate decisions** (`/gates`, `views/gates/`): every gate of the readable projects on the device; columns of
  `COLUMNS.gate` (`status` is the gate's own), filters `decisions`, `templateIds`, `groupIds`, `assignedToMe`, `overdue`,
  search over gate title and project key/title, default sort `-decided_at` (a decision not synced yet by its `updatedAt`),
  `list:gate` state, saved views of `itemType: 'gate'` (`savedViews.ts` `ViewType`), URL `?decision&me&overdue&view`;
  a row opens the project's Milestones tab.
- **Admin pages** (*Configuration: Manage*, online only, each change followed by `engine.syncNow()`; reads from the
  synced rows): *Sections & Fields* (`views/admin/sections/`: sections in order with up/down – every id sent –, archive,
  section dialog with applies to / default for / collapsed / access per group; fields per section with up/down, archive,
  delete after `…/usage` – blocked while a score uses it –, field dialog: key suggested from the label and locked after
  create, type locked, sub-heading, required, per-type settings, options editor (ids from labels, locked once saved;
  saved options can only be archived; colours, points), score editor (inputs from the numeric fields with their maxima,
  op, n/a mode, max, caps, scale, normalise, decimals; `validateScoreConfig` before sending, the server's
  `errors.config` codes as sentences, "shown as … / max"), default value); *Project Templates* (list with order and
  archive; `/admin/templates/new`, `/admin/templates/:id` – a page sending the whole `TemplateInput`; existing milestones
  keep their ids; removing a section drops its defaults; gate suggestions among the allowed statuses); *Assignment
  roles* (`/admin/roles`, a tab next to *Statuses*, so the menu is unchanged); *Configuration packages* (built-in list
  with import state, upload a JSON, dry run → diff grouped by entity, conflicts first, unchanged hidden by default,
  *Overwrite* per conflict or all → dry run again → *Apply* (disabled while `needsAdmin && !isAdmin`), result toast;
  export downloads `<name>.pgpm.json`).
- **Section groups**: the section dialog reads them through `GET /api/v1/config/sections/{id}` (§3.3, added in the
  phases 4–5 integration; `section_groups` isn't synced). When that fails (offline in between, an error) the dialog says
  it can't read them and offers *Leave the groups as they are* (sends `access: null`) or *Replace them*.
- **Integrated with the server (phases 4–5 integration)**: every configuration call goes through the generated client
  (`api/config.ts`, statuses included). A conflict is named by what it belongs to (`apply.ts` `describeRow`): a field
  value as "<item> · <field>", a DoD item by its text, other rows of an item or milestone by its title; the conflict
  dialog heads a map's group with the map's name. A **bulk status change** skips projects whose template doesn't allow
  the status (the server would refuse them; the toast says why). `GateDecision` keeps the decision being chosen when the
  milestone row arrives again unchanged (any sync of the project used to reset it). Creating an idea right after the
  first sign-in waits for the statuses (the dialog's *Create* does nothing without one – the e2e funnel waits for the
  form to be complete). e2e `funnel.spec.ts` against the stack with `core` + `innovation-ai` seeded: an idea assessed
  to 58 / 65, *Ready for review*, the *Review board decision* read-only for the innovation team (and refused by the
  server from her device) and decided by a board member, live at hers, converted with *AI use case* (assessment carried,
  gates G0–G3), gate decisions moving the project; the built-in packages' dry runs then report no change.

### 12.10 Phase 7 client hardening (H2, as built)

- **First sync** (`stores/sync.ts` `firstSync`): true while the engine runs (not *stopped* / *signed out*) and this
  device never finished a sync – meta `lastSyncAt` (a live query, refreshed after a wipe) and the engine's status both
  empty. Then the statuses, templates, sections and groups may not be here yet, so every create dialog that depends
  on them – *New note/idea/lead/project* (`ItemCreateDialog`), the map's *Create new* (`PlaceDialog`), *New map* except
  *Duplicate* (`NewMapDialog`), *Convert to project* – shows `FirstSyncNotice` (*Preparing your data… (first sync)*;
  offline: *Waiting for a connection to finish it.*) and keeps *Create* disabled; the item, persons, gates and maps
  lists show it as their empty state (`ListTable`'s new `#empty` slot) and the sync indicator says *First sync* while
  pulling. The admin hint *There are no … statuses yet* appears only after the first sync.
- **Accessibility.** PrimeVue's `Select` names its combobox after the selected value unless `aria-labelledby` names its
  label (§12.8), so every Select and MultiSelect with a visible label has it (`<label id="X-label" for="X">` +
  `aria-labelledby`; `FieldValueInput` takes its parent's `${inputId}-label`); unlabelled ones (filters, the share list,
  the conflict editor's switch and text area, the sync dialog's view switch) have an `aria-label`. Icons and badges
  that carry an `aria-label` have `role="img"` (ARIA 1.2 forbids naming generic `<i>`/`<span>`). The conflict markers in
  an item's and a map's header are buttons (were clickable Tags, unreachable by keyboard). Focus: a global
  `:where(a, button, summary, [tabindex]:not([tabindex='-1'])):focus-visible` ring (no specificity: every component's
  own ring wins), the map canvas draws one inside its edge. The phone's navigation overlay takes the focus when it
  opens, Escape closes it, the focus returns to the menu button. Mobile list rows whose content is a link
  (`ListTable` `rowLinks`) are no second tab stop. Contrast: the maps list's conflict icon uses `--pgpm-warn`
  (orange-500 was 2.8:1), highlighted code comments surface-600 (was under 4.5:1 on the code background). PrimeVue's
  Dialog returns the focus to its opener itself. `e2e/a11y.spec.ts` runs axe-core (`@axe-core/playwright` 4.14.0,
  MPL-2.0, dev only) with the WCAG 2.1 A/AA tags on sign-in, the lists, the *New idea* dialog, an idea's tabs, the maps
  list, a map with its side panel and library and the admin pages (not *System*), in light and dark, plus the lists, the
  navigation overlay, an idea and a map at 390 × 844 (also: nothing scrolls sideways).
- **Phone (< 600 px).** The app bar's search is an icon that opens the global search in the bar (`GlobalSearch`
  `autofocus`, `done` folds it); the maps list is a list of two-line rows with a small preview; the map page has
  icon-only header buttons, `100dvh` (the browser's bars don't hide the zoom controls) and padding like every page; the
  canvas' filter bar folds into a *Filter (n)* button, the toolbar is compact; the library is a sheet over the bottom
  of the map (touch can't drag: *+* adds); the side panel is full width (§13.3). Item lists keep their `#mobile` rows,
  detail pages their single column below 1200 px, dialogs `min(…, 100vw)`.
- **Print.** `preferences.ts` drops the dark class from `beforeprint` to `afterprint` (dark text on paper); `base.css`
  hides toasts, tooltips and every control (PrimeVue buttons, toggles, the inline fields' pencils, `.no-print`), the
  shell hides the drawer and app bar, `PageHeader` its actions (the title wraps), `TabNav` the tabs; MarkdownView,
  CodeBlock and Mermaid keep their own print rules.
- **PWA**: the manifest's shortcuts *New note* / *New idea* and the launch queue (§11.2); *Install app* was in the user
  menu already.
- **Lists during a pull** (follow-up, docs/07 §2.3): while the engine pulls (`status.syncing`, in every tab – boot.ts
  hands it to `setLivePulling`), the `useLive` queries re-run together in bursts: a change holds a query back, and the
  held ones run at once when the last burst has been over for 500 ms – or for twice its length, if longer – and right
  after the pull. Otherwise every change re-runs a query at once, as before. Dexie still calls the query on a change;
  then `useLive` reads nothing (it answers the last run's result if Dexie dropped it for a change during that run, so a
  list still shows the pull's progress; that subscription observes nothing any more) and subscribes anew with the
  burst – one real run that observes again. A key change and `refresh()` read at once. A burst's end is taken after
  the task its answers render in (§15 #89).
- **Conversion** copies the embedded images the description and the carried Markdown values show (`filesToCopy`: the
  copies' ids are chosen first, so the project's insert carries the rewritten text – no extra patch) and rewrites their
  `/api/v1/files/{id}/…` links, attachment chips included (`rewriteFileLinks`, relative or absolute); an embedded image
  no text shows isn't copied (the server's cleanup would drop it, §10.3).

## 13. Maps [PNMTD canvas pieces + NEW editing]

### 13.1 Rendering

PNMTD renders all maps as **plain Vue SVG** – no Cytoscape/vis-network/Konva
(`docs/network-map-plan.md` §14 explains why). PGPM keeps that:

- `<svg>` → `<g :transform="translate(x y) scale(k)">` world group from
  `usePanZoom` → one `<g>` per visible layer (sorted by `sort_order`) → elements
  sorted by `z` then `id`.
- Item cards, text and shape labels: `<foreignObject>` with HTML (PNMTD overview
  notes: `<rect class="paper">` + `<foreignObject>` with rendered Markdown,
  `boards/boardMarkdown.ts`). Card badges: status (+ overdue), persons, milestone
  progress, configured scores (`maps.settings.cardBadges`).
- **Level of detail** (`src/maps/culling.ts`): from zoom 0.45 everything is drawn;
  below it cards render as a colored rect + title in plain SVG (no
  `<foreignObject>`); below 0.3 (14 px text under 4.5 px) as the rect only, and
  texts/labels under 24 px are left out. Less detail is applied at once while
  zooming out, more detail only once the view has settled (180 ms without a
  change), so a zoom gesture doesn't switch hundreds of cards back and forth.
- **Viewport culling** from 300 elements: only elements touching a region – the
  visible box grown by 50 % of its size on every side – are rendered (the
  selection always). The region is renewed when the view leaves it, and after
  the view settled when the zoom changed by more than 1.6×; panning inside it
  re-renders nothing but the world group's transform. Frames, card models and
  line geometries keep their object identity while unchanged, so a drag
  re-renders only the moved elements and their lines.
- **Spike S2** (2026-11-27; `/dev/map?map=stress&bench=1`: 1,000 elements –
  about 600 cards, 300 shapes and texts, 100 connected lines – driven by
  requestAnimationFrame; Vite dev build, headless Chromium 158 in a Linux
  container on an Apple-silicon Mac, software raster, 1440 × 900). With LOD and
  culling every pan and zoom phase holds the 60 fps cap (slowest frame
  16.8 ms); uncapped a frame takes 1.8–3.1 ms (p50) panning and 2.9–6.7 ms
  zooming, against 3.5–7.5 / 7.9–11 ms with both off. With 4× CPU throttling (a
  slow laptop) pans stay at 56–60 fps and zooms at 43–54 fps (p95 33 ms), where
  without them zooms drop to 22–24 fps (p50 50 ms) and the whole-map pan to
  26 fps. What remains are one-off hitches when the region is renewed or the LOD
  threshold is crossed (up to 150 ms throttled, under 20 ms unthrottled).
- Images: `<image href="blob:…">` from IndexedDB, thumbnail first.
- An item may have **several cards** on one map; all cards of an item re-render
  from the same item row. `?focus=item:<id>` selects all of them and fits the view.

### 13.2 Pan, zoom, drag (copy)

- `composables/usePanZoom.ts` (pure functions, vitests): view `{x, y, k}`,
  `MIN_ZOOM 0.1`, `MAX_ZOOM 4`, wheel `factor = exp(-clamp(deltaY, ±60) * 0.003)`
  around the pointer (`zoomAround`), background drag pans with pointer capture,
  two-pointer pinch, `zoomBy(1.25|0.8)`, `fit(box)` (padding 32, max 1.5),
  `centre(point)`, `toWorld(e)`.
- `components/graph/useMoving.ts`: `begin(e, ids)` stops propagation, move counts
  after > 2 units, `release()` → click selects, Escape restores, arrow keys nudge
  8 (Shift 40). PGPM extends it to multi-selection, rubber-band select, resize
  handles and snap-to-grid.
- `components/graph/geometry.ts` (`edgePath`, `boundsOf`) → extended with border
  intersection for connectors (rect, rounded rect, ellipse, diamond),
  point-in-shape, bounds of a set, rubber-band hit test (drag right: contained,
  left: touched) and snap-to-grid.
- `components/graph/focus.ts`: `?focus=item:<id>` deep link.
- In PGPM these are `src/maps/panZoom.ts`, `moving.ts` (+ optional snap of the
  dragged selection), `geometry.ts` and `focus.ts` – pure functions with their
  vitests; `matrixLayout.ts` (§13.5) sits next to them.
- Gestures (PGPM): with the Select tool a drag on the background pans and
  Shift/Ctrl/⌘ + drag draws the rubber band; the Hand tool pans everywhere;
  Space + drag and the middle button pan. A resize handle with Shift keeps the
  aspect ratio; the grid snaps the moving edges.
- Overlays: zoom +/−/Fit bottom-right, legend bottom-left (PNMTD); toolbar (left),
  layer panel, filter bar (top), minimap (PGPM).
- The map view folds the app drawer to the rail (`preferences.holdRail()`) and uses
  the full height below the app bar.

### 13.3 Side panel (copy)

`views/network/MapTab.vue:353`:

```html
<Drawer v-model:visible="open" position="right" :modal="false" :dismissable="false" class="map-drawer">
  <template #container="{ closeCallback }">
    <ElementPanel :selection="shown" @select="…" @close="closeCallback" />
  </template>
</Drawer>
```

- Non-modal, not dismissable by outside click, clicking another element swaps
  the content; `open = computed(() => selected !== null)`; a separate `shown` ref
  keeps the last selection visible while sliding out.
- Width `min(30rem, 100vw)`, `100vw` below 600 px.
- A background click closes it; *Map properties* (nothing selected) opens from
  the toolbar's cog. The item quick edit is the host's `#item-panel` slot above
  a read-only summary (status, persons, scores, description, milestones, links,
  "appears n× on this map" with *Select all*); `#milestone-panel` and
  `#map-sharing` likewise.
- Layout from `views/network/DevicePanel.vue`: header (2.75rem color avatar with
  type icon, h2 title, status badge, close), actions row, scrolling body with
  stacked `<section>`s – no tabs. Immediately saved color picker as in PNMTD
  `OverviewPanel.vue`.

### 13.4 Editing [NEW]

PNMTD maps only move nodes and save positions explicitly via a save bar. PGPM is
a real editor and offline-first:

- **Data source** (`src/maps/dataSource.ts`): the canvas, the matrix map and the
  side panel read and write through `MapDataSource` only – reactive rows (map,
  layers, elements, items, statuses, milestones, DoD, links, assignments,
  persons, field definitions/values, access, the personal view settings), a file
  URL resolver, `newId`, and `create/patch/remove/batch` with the outbox's
  `{key: {old, new}}` changes on the three map tables. `createMemorySource` holds
  it in memory (tests, `/dev/map`); `createLocalSource` implements it over the repo (§13.7). Placing
  tools ask the host through events (`requestItem` → *Create new* / *Add
  existing*, `requestMilestone`, `requestProject`, `requestImage`) whose payload
  has `place(id)`.
- **No save bar**: every completed operation (drag end, resize end, style change,
  text edit) writes patches to the local repo immediately; during a drag only Vue
  state changes; one patch per element on `pointerup`; the live sync spreads it.
- **Remote changes during interaction**: incoming changes to an element the user
  is dragging are applied after the drag ends (then the local patch's `old` no
  longer matches → conflict only if the same property changed).
- **Undo/redo** (`src/maps/history.ts`): per-map command stack (session only, 200
  steps); a command is a list of `{table, id, before, after}`; undo writes the
  inverse patches. Undoing a
  deletion creates the row again **under a new id** (the server never takes an id
  twice) and remembers the alias, so later steps and references (a line's
  `start_element_id`) follow it; a step's writes are one batch, rows created
  before the patches that refer to them.
- **z-order / layers**: fractional keys (`generateKeyBetween`).
- **Connectors**: `line` elements with `start_element_id`/`end_element_id`;
  endpoints computed from the targets' borders; stored `points` are the fallback.
  A connected line isn't dragged itself – it follows its elements; its ends are
  dragged onto an element (connect) or anywhere (free). Deleting an element
  turns the lines attached to it into free ends where they were drawn.
- **Links overlay**: *Show links* draws `item_links` between cards of linked items
  (all card pairs, dashed, not stored; nearest pair only when an item has several
  cards).
- **Timeline / milestones**: derived from the project's milestones; gates drawn
  as diamonds, colored by decision.
- **Clipboard**: JSON (`{"pgpm":"elements", …}`) via the copy/cut/paste events;
  plain text pastes as a text element, a pasted image goes to `requestImage`;
  paste creates new element ids; item cards reference the same items (another
  card).
- **Keyboard**: Delete, Ctrl/⌘ Z / Shift Z (and Y), C / X / V / D / A, ] / [
  (+Shift), arrows, Space-pan, Esc, `F` fit, + / − zoom.
- **Read-only maps**: tools hidden, `useMoving` disabled, side panel read-only.
- **Export** (later): export-mode SVG (cards as plain SVG text, because
  `<foreignObject>` taints the canvas in Safari) → PNG.

### 13.5 Matrix maps [NEW]

- `maps.kind = 'matrix'`, configuration in `maps.matrix` (data model §9).
- `src/maps/matrixLayout.ts`: items matching `matrix.filter` (local query) →
  x/y from the configured fields (`score.ts` for scores) → world coordinates
  (fixed 2000 × 1400 world box, padding) → small collision pass (spiral nudge,
  deterministic by item id) → cards. Items with missing values → tray *Not yet
  assessed*.
- Axes, split lines and quadrant labels are rendered as a fixed background layer;
  free elements and layers work as on canvases.
- Cards are not draggable; the side panel edits values and the card moves live.
  They are drawn as a virtual layer below the free elements (ids
  `matrix:<item id>`, no rows), moving with a 0.45 s transform transition.
- Quadrant of an item for a given matrix map: `quadrantOf(x, y, matrix)` → `tl` |
  `tr` | `bl` | `br` with its label (the map's or the default) – used for card
  badges, list column/filter and MCP `search_items`. Right/top is an axis' high
  side (towards `max`; `max < min` reverses an axis); a value on the split line
  counts as high; `split` defaults to the middle of `min` and `max`.
- Layout details (`layoutMatrix`): world 2000 × 1400, padding 100 (values beyond
  `min`/`max` sit on the plot's edge); standard card 180 × 56; bubble size scales
  the card's area with the value (×0.8 for none/≤ 0 … ×1.6 for the largest);
  when cards would cover more than 35 % of the plot, all shrink alike; cards are
  placed in id order, each at the free spot nearest its values (a sunflower
  spiral stretched to the card's shape, then a grid over the world), at least
  6 units apart and inside the world box; the tray keeps the caller's order.

### 13.6 Map templates [NEW]

- `maps.is_template = true` (*Configuration: Manage*); delivered to all users.
- *New map from template* runs on the client: copy layers and non-item elements
  with new ids (fractional keys kept), `template_source_id` set; works offline.
- Seeded by the `innovation-ai` package: *Impact field canvas*, *Crazy 8s*,
  *Customer journey*, *Use-case funnel*.

### 13.7 Phase 5 client (as built)

- **Local data source** (`maps/localSource.ts`, `createLocalSource(mapId, {db, repo, files, objectUrls})`): the
  `MapDataSource` over the repository. The device stores rows camelCase (as synced), the map UI reads snake_case:
  `maps/mapRows.ts` renames a row's own members only (jsonb content stays), a patch key's first segment
  (`default_view.types` → `defaultView.types`). Live queries: the map, its layers, its elements, all statuses, all
  field definitions, `user_settings` `map:<id>`, and one *related* query keyed by what the elements refer to (item,
  project and milestone ids) plus a matrix' filter – re-subscribed only when that key changes. It reads the referenced
  items and placed milestones' projects, on a matrix map the filter's candidates (types via the `type` index,
  statuses, templates, `groupIds` through `item_shares`), the projects' milestones, their DoD items, links with both
  ends among the items, assignments of items and milestones, their persons, the items' field values.
  **Reconciliation** (`Reconciler`): a row keeps its object while `seq`, `rev` and `deleted` are the same and nothing
  is pending (a re-stamp – computed `access` – changes `seq`); a pending local change (same `seq`/`rev`) is compared by
  content; an answer that changed nothing returns the previous array. **Writes**: refused with read access
  (`MapWriteError`); `create` refuses an existing id and maps, `remove` an unknown row and maps (as the memory source);
  a patch's `{old, new}` go to the repository as given (`old` decides the server's conflict check; keys whose old and
  new are equal are left out). **`batch(fn)`** calls `fn` at once and queues every write made until it ends (its
  promise settles) into **one** repository transaction, in order; a write that fails there (a row gone) fails alone,
  the others are stored; `fn` must not await its own writes (they settle with the batch); a batch inside a batch joins
  it. **View settings** are published at once and written in order (one row, patched afterwards). **Files**:
  `fileUrl` answers `blob:` URLs the source creates – the row's thumbnail when asked, else the local bytes, else a
  download through the file pipeline (`fileBlob`) – and otherwise waits (a live query on the row and `blobs`) until
  they arrive; `dispose()` ends the queries and revokes its URLs (also when the creating scope ends). `writes` counts
  write operations (a batch once), `loaded` says the map, layers and elements were read once. Tests: the memory
  source's behaviour tests are shared (`maps/dataSourceBehaviour.ts`, run by both), plus reads, matrix candidates,
  reconciliation, batches, view settings, files, and two devices against the fake server (live move, offline
  conflict per map, unsharing evicts) in `localSource.sync.test.ts`.
- **Map operations** (`local/maps.ts`, inside repository transactions, offline): `createMap` (a canvas with layer
  *Base* and `{grid: true, snap: false, gridSize: 20}`, a matrix with its own `matrix`, or from a template/preset via
  `instantiateTemplate` + `mapFromTemplate`) with its `map_shares` in the same batch; `duplicateMap` (settings,
  layers, every element with new ids and connections – cards, milestones, timelines only of items on the device, the
  server would refuse others; lines to dropped ones become free ends); `addCardToMap` (*Add to map…*: the middle of
  the map's elements, the topmost visible unlocked layer, on top); `placementsOf`. **Copying a map image**: a map image
  belongs to its map (the server checks `fileId` is a `map_image` of the same map), so a copy (template, duplicate)
  inserts a new `files` row with the same SHA-256 and size (the server takes known bytes at once, docs/03 §10.3) and
  copies the local bytes, if any (`uploaded` when the original was available); an image whose file isn't on the device
  is left out. Rows go in reference order (files and connected elements before what refers to them). Helpers keep
  the Dexie zone (`inTransaction`; `mapContent` awaits Dexie promises only).
- **Maps list** (`views/maps/MapsView.vue`, `/maps`, tabs *Maps* · *Templates* via `?tab=templates`): preview, name
  and description, kind, *Read only*, owner, modified; *New map* (Maps: Create) → *Canvas map*, *Matrix map* (from a
  preset – the *Prioritization matrix* first – or *Own axes*: two number/rating/score fields with their ranges, split in
  the middle, default quadrant labels, ideas and projects), *From template…*; per map *Open*, *New map from this
  template*, *Rename…* (edit), *Duplicate…* (Maps: Create; *Share with* preselected with the original's sharing),
  *Share…* (`ShareDialog` with `mapId`: `PUT /api/v1/maps/{id}/shares`, `POST …/owner`), *Mark/Unmark as template*
  (Configuration: Manage and edit; a marked matrix map becomes a preset), *Delete…* (manage, to the Trash). New maps
  preselect *Share with* as items do (the creator's groups at *edit*).
- **Preview** (`maps/preview.ts`): not an SVG snapshot (`<foreignObject>` cards and CSS variables don't survive a
  canvas, Safari taints it) but a schematic drawn on a 320 × 200 canvas from the rows – cards in their colours
  (palette names resolved to their light-mode hex), shapes with fill and stroke, lines, text as bars, images and
  milestones as boxes, a matrix map's quadrants and computed cards – as WebP (PNG where the browser can't encode WebP;
  the list sniffs the type), base64 in `maps.preview`. Written when leaving a map (route change, unmount) if the user
  can edit, is online, and changed something during the visit (or the map has none). Two devices writing a preview
  at once are no conflict: the server lets the later patch win (`LastWriterWins`, §15 #70; the fake server too), so the
  client's former auto-settling (`previewConflicts.ts`) is gone.
- **Map page** (`views/maps/MapView.vue`, `/maps/:id`, `props: true`): holds the rail, makes the source (with the
  app's file pipeline), keys `MapCanvas`/`MatrixMap` by kind and id, and recreates the source when the id changes.
  Header: back, kind icon, name, *Template*, *Read only*, the map's conflicts (opens the sync dialog), *Library*
  (canvas), *Matrix settings* (matrix, manage), *Share · …*. Requests (`mapRequests.ts`, `PlaceDialog.vue`): an item
  tool → *Create new* (title, status – the type's default –, *Share with* preselected; item and shares one batch, then
  `place`) and *Add existing* (local search; items on the map marked *on the map · n×*); the milestone tool → a
  project, then its milestones and gates; the timeline tool → a project; the image tool → the pasted file or a file
  chooser (hidden input; *cancel* cancels), stored with `attachFile({mapId}, file, 'map_image')`, placed at its size
  (at most 480 × 360). Slots: `#item-panel` → `ItemQuickEdit` (title, status, colour, image – `CoverImage` –,
  persons – `AssignmentsPanel` –, and *Values*: the card badges and a matrix' axes and size, a score with its inputs,
  ratings as 0…max + n/a buttons, numbers, selects; scores read-only; the item's own access and the section's
  `canEdit` decide; values set through phase 4's `setFieldValue` – one row per field, null clears –, a project's status
  among its template's; `#sections` → `MapItemSections`, phase 4's `ItemSections` as on the Overview);
  `#milestone-panel` → `MapMilestonePanel` (status with the DoD override confirmation, `DodChecklist`, a gate's
  `GateDecision` with the suggested status, *Open the project's milestones*; the project's access decides – the
  summary below then leaves its read-only DoD out); `#map-sharing` → the groups and *Share…*. **Library** (`LibraryPanel.vue`): every
  item on the device, search, type and status filters, the count on the map; rows drag as
  `application/x-pgpm-item`, the stage's drop calls `addItemCard(id, toWorld(x, y))`, *+* adds in the middle of the
  view. **Matrix settings** (`MatrixConfigDialog.vue`): axes (field, label, min, max, split), bubble size, the four
  labels, filter types, statuses, templates, groups – one patch of `matrix`. `?focus=item:<id>` goes to the canvas.
- **Item page *Maps* tab** (`views/maps/ItemMapsTab.vue`): canvas maps with the item's card count, matrix maps whose
  filter takes it with its quadrant or *Not yet assessed*, each linking `?focus=item:<id>`; *Add to map…* lists the
  canvas maps the user may edit (not templates) and calls `addCardToMap`.
- **Quadrant column and filter** (`views/maps/quadrants.ts`, `QuadrantFilter.vue`): phase 4's hook wasn't there, so
  the item lists got a minimal one: `columnsFor`/`visibleColumns` take extra columns, `ListContext.columnValue` and
  `quadrantOf`, `ItemFilters.quadrant = {mapId, quadrants}` (kept in views and `list:<type>`); one column
  `quadrant:<map id>` (*Quadrant · name*) per matrix map whose filter takes the type, showing and sorting by the
  label; the filter in the *Filters* popover.
- **Server assumptions**: all verified against the server in the phases 4–5 integration (§3.3); `preview` became last
  writer wins there. Bulk *Add to map* on the item lists and the template filter on the canvas' filter bar came with
  phase 7 (§13.8).
- **Integrated with the server (phases 4–5 integration)**: a text placed with the text tool is edited once its row is in
  the model – the device's source answers a write after a live-query round, so `MapCanvas` waits for the row instead of
  the next tick (with the memory source it was there at once); *Ctrl+Enter* or a click elsewhere keeps the text
  (*Escape* too since phase 7, §13.8). e2e (`maps.spec.ts`) against the real server: two users co-edit live (measured: a drawn
  rectangle ≈ 0.3 s, a move ≈ 0.2 s from pointerup to the other browser's DOM), offline editing with the conflict grouped
  under the map's name, the seeded *Prioritization matrix* with assessed ideas in their quadrants and a card moving when
  its ratings change, unsharing evicting the map live.

### 13.8 Phase 7 client (H2, as built)

- **Text editing**: *Escape* keeps what was typed (like *Ctrl/⌘+Enter* and a click elsewhere); one undo takes it back.
  A text just placed with the text tool and left empty is removed again. *Enter* on the canvas edits the text of the one
  selected shape, line or text (the keyboard's double-click), otherwise – and *F6* always – moves the focus to the open
  side panel's title.
- **Keyboard**: a focused control of the chrome (toolbar button, select, checkbox, … – `keyboard.ts` `isControl`) keeps
  Space, Enter, the arrows and Home/End (they used to pan, nudge the selection or be swallowed); the map's other
  shortcuts (Delete, Ctrl/⌘ Z …) still work from there. The toolbar moves between its buttons with the arrows and
  Home/End. The canvas is a named group (the map's name) described by a visually hidden list of its keys.
- **Side panel focus**: opening it leaves the focus on the map (its shortcuts keep working); Escape inside it closes it
  (not while a field takes Escape – the Drawer's document-wide Escape is off, it also fired during a drag on the map);
  closing it with the focus inside returns the focus to the canvas (`leave`). PrimeVue's FocusTrap stays on inside it:
  Tab cycles there, Escape leaves.
- **Bulk *Add to map…*** (`BulkActions`, `MapPickerDialog` – shared with the item's *Maps* tab): the canvas maps the
  user may edit; `addCardsToMap` places one card per picked item in a grid around the map's centre (`cardGrid`: about
  as many columns as rows, 24 units apart, in the list's order), each on top of the one before on the target layer –
  one batch; a read-only map is refused.
- **Template filter**: `MapFilterBar` offers the project templates of the projects shown (`templateIds`, the filter
  logic of §13.4); names from `project_templates` through the host's `templateNames` prop (MapView → MapCanvas,
  MatrixMap passes it on).

## 14. Configuration keys

ASP.NET configuration as in PNMTD (`appsettings.json` + environment overrides
`Section__Key`); secrets only via environment/`.env`.

| Key | Default | Source |
|---|---|---|
| `Database:Host/Port/Name/Username/Password` or `Database:ConnectionString` | `localhost` / 5432 / `pgpm` / `pgpm` / – | PNMTD |
| `PublicUrl` | | GGITSV (redirect URI base, WebSocket origin check) |
| `Proxy` | – (none trusted) | PNMTD (`docker`, addresses, networks; a bad entry stops the start) |
| `DataProtection:KeysPath` | `/var/lib/pgpm/keys` | PNMTD |
| `TZ` (environment only) | UTC | PNMTD (wall-clock rules such as `Auth:ResetHour`) |
| `Auth:InitialAdmin:Username` / `:Password` | `admin` / generated | PNMTD |
| `Auth:SessionDays` | 7 | PNMTD |
| `Auth:LoginDays` / `Auth:TotpDays` / `Auth:ResetHour` | 30 / 1 / 4 | PNMTD |
| `Auth:TotpPolicy` | `Optional` | PNMTD |
| `Auth:LocalLoginNetworks` | (all) – `all`, `none` (needs `Entra:Enabled`), or networks/addresses `10.0.0.0/8, 192.168.1.20` | GGITSV |
| `Auth:LoginRateLimit:PermitLimit` / `Auth:TotpIssuer` | 10 / `PGPM` | PNMTD |
| `Entra:Enabled` / `TenantId` / `ClientId` / `ClientSecret` | off | GGITSV |
| `Entra:AutoProvision` | true | GGITSV |
| `Entra:DefaultGroupId` | – | NEW |
| `Directory:Enabled` | false (also needs `Entra:TenantId/ClientId/ClientSecret`, not `Entra:Enabled`) | NEW |
| `Directory:IntervalHours` / `FullSyncDays` | 6 / 7 | NEW |
| `Files:Path` | `/var/lib/pgpm/files` | PNMTD |
| `Files:MaxAttachmentBytes` / `MaxImageBytes` | 25 MiB / 2 MiB (in bytes) | PNMTD |
| `Files:BlockedExtensions` / `UploadsPerHour` / `MinFreeMb` | see §10 / 200 / 1024 (an upload's chunk 0 that would leave less is 507 `file_storage_full`, §15 #86) | PNMTD |
| `Sync:PageLimit` / `MaxMutations` / `MaxBodyBytes` | 500 (1–1000) / 200 / 8 MiB (in bytes) | PLM / NEW |
| `Sync:TombstoneDays` | 90 (non-trash tombstones only) | PLM |
| `Sync:Live:Enabled` | true | NEW |
| `Sync:Live:MaxConnectionsPerUser` | 10 | NEW |
| `Sync:Live:AckTimeoutSeconds` / `HeartbeatSeconds` | 30 / 25 | NEW |
| `Sync:Live:RevalidateSeconds` / `HelloTimeoutSeconds` | 60 / 15 | NEW (phase 2) |
| `Packages:SeedOnFirstStart` | `core,innovation-ai` | NEW |
| `Mcp:Enabled` / `WriteHours` / `ReadDaysAfterOnline` | true / 12 / 7 | PNMTD |
| `Mcp:WriteRequiresTotp` / `StrikesToBlock` | true / 3 | PNMTD (phase 6) |
| `Mcp:CallsPerMinute` / `WritesPerMinute` | 120 / 20 | PNMTD (phase 6) |
| `Mcp:LogEntries` / `MaxResultChars` | 10000 / 40000 | PNMTD (phase 6) |
| `BackgroundTasks:Enabled` | true | PNMTD |

## 15. Deliberate deviations from the sources

| # | Deviation | Reason |
|---|---|---|
| 1 | snake_case columns via `EFCore.NamingConventions` (PNMTD: `[Table("snake")]` + quoted PascalCase columns) | readable SQL; GGITSV and PLM already do this |
| 2 | Microsoft link as columns on `users` (GGITSV: Identity `user_logins`) | PNMTD's auth has no Identity |
| 3 | Global sync counter (PLM: per user) | shared multi-user data |
| 4 | Typed server tables (PLM: opaque generic rows) | server-side access checks, search, MCP, Graph upsert |
| 5 | Per-field conflict detection, **user decides** (PLM: object `base_rev`, conflict copies) | decision 2026-11-26 #10 |
| 6 | Re-stamp + evictions + `access_epoch` reset (PLM: `key_epoch` for keys) | sharing can change while clients are offline |
| 7 | Dexie + Workbox precache (PLM: hand-written IndexedDB + hand-maintained SW list) | TypeScript/Vite code base; avoids precache drift |
| 8 | **Live WebSocket feed** + polling fallback (PLM: polling only, WS rejected) | collaborative maps, decision 2026-11-26 #10 |
| 9 | Local tables via `useLocalTable` (PNMTD: server-side `useServerTable`) | offline-first |
| 10 | Map edits saved immediately + undo (PNMTD: explicit save bar) | editor UX, offline outbox |
| 11 | Chunked uploads (PNMTD: single raw body) | queued offline uploads |
| 12 | MCP write token: 2FA satisfied by Microsoft link (PNMTD: TOTP only) | SSO users have Entra MFA |
| 13 | UI at `/` (PNMTD: `/pnmt-admin/`) | PWA scope |
| 14 | Rights are capabilities; visibility by **per-object group sharing** (PNMTD: area levels decide visibility, no object ACL) | decision 2026-11-26 #1 |
| 15 | Persons visible to all users (PNMTD/GGITSV: user lists behind rights) | assignments and sharing need the directory everywhere |
| 16 | Markdown uploads go through the offline file pipeline (PNMTD: direct `POST /api/v1/files` via XHR) | pasting images must work offline |
| 17 | Markdown conflicts via the sync conflict model + PNMTD's diff view (PNMTD: 409 `edit_conflict` on save) | one conflict model for all data |
| 18 | Not taken from PNMTD's editor: credentials warning, *sensitive* mark, AI tags | not relevant for PGPM |
| 19 | `TreatWarningsAsErrors` on, no `NoWarn` list; `.editorconfig` after GGITSV/PLM (PNMTD has none), style rules as suggestions | new code base without legacy warnings |
| 20 | JSON options via `AddJsonOptions`/`ConfigureHttpJsonOptions` for all controllers (PNMTD restricts its formatters to `/api` because of the frozen sensor API) | PGPM has no legacy API |
| 21 | Anonymous `GET /api/v1/info` (name, version) instead of PNMTD's `/api/v1/system/info` behind sign-in | the shell shows the version before any sign-in, offline-first |
| 22 | Phase 1 keeps the last `/auth/me` and the rights catalogue in `localStorage` (`pgpm.session`), not yet in IndexedDB `meta` | Dexie arrives with the sync core (phase 2), which moves it and wipes it with the local data |
| 23 | Persons code in `PGPM/Persons/` (namespace `PGPM.Persons`); the phase-6 directory sync must not use the namespace `PGPM.Directory` | `PGPM.Directory` hides `System.IO.Directory` in every `PGPM.*` file |
| 24 | TOTP QR code drawn on a `<canvas>` (PNMTD: `QRCode.toDataURL` into an `<img>`) | PGPM's CSP allows no `data:` images |
| 25 | `audit_log` with docs/02 §12's columns and dotted lower-case events; PNMTD's other columns (username, category, outcome, target name, subjects, request, browser, changes) in the `detail` jsonb | the data model's columns; one family prefix filters the page |
| 26 | `TestSignIn` is forwarded to by the cookie scheme for requests with its header (PNMTD: the default scheme, a second app with `testSignIn: false` for cookie tests) | one app per test class for both kinds of client |
| 27 | Seed groups by a custom migration with a `key_values` marker (doc: "via migration") | a deleted seed group stays deleted; phase 4's packages take the seeds over |
| 28 | `ssoError=no-account` for an unknown account while `Entra:AutoProvision` is off (GGITSV knows only failed, cancelled, deactivated, account-conflict) | GGITSV always provisions |
| 29 | Graph `/me` reads `userPrincipalName,mail` only (GGITSV also the on-premises names) | PGPM stores only `microsoft_user_name` |
| 30 | `items` is one entity class with every type's columns and the `type` discriminator (doc 02: EF Core TPH) | the sync reads and writes rows generically by column |
| 31 | No foreign keys from synced rows to `users` (`owner_user_id`, `created_by`, `updated_by`) nor yet to `statuses`, `files`, `project_templates`, other `items` | an item outlives its owner's account; the referenced tables come in phases 3–4 |
| 32 | An item's delete batch doesn't contain its `item_shares` | they decide who sees the tombstone and who may restore it |
| 33 | Deleted groups are tombstones with a query filter (phase 1: a hard delete) | clients must learn that the group is gone |
| 34 | `sync_clients.horizon_seq`, `sync_mutations.(batch_id, table_name, row_id, op, status)`, `delete_batches.root_table`, `sync_evictions.at` added to doc 02 §11 | horizon resets only for devices that may hold purged rows; initial shares checked across requests; restore authorises against the root row; eviction pruning |
| 35 | A wrong live `Origin` is HTTP 403 before the upgrade (close code `4403` is not used) | nothing of the session reaches a cross-site page |
| 36 | `user_settings` duplicates (two devices, same key) are merged as a patch with `old` = the existing value: the later wins | personal UI state needs no conflict dialog |
| 37 | Trigram index on `items.title` deferred to phase 3 (global search) | needs `pg_trgm`; nothing searches server-side yet |
| 38 | The client closes the live socket on the browser's `offline` event (docs/03 §11.7 "Integration") | an offline browser's socket (DevTools' and Playwright's offline mode, a closed lid) may stay open and keep the indicator *Live*; `online` catches up over HTTP |
| 39 | Wire timestamps always with six fractional digits (`UtcMicrosecondsJsonConverter`), not System.Text.Json's trimmed ones | the client sorts and compares them as text |
| 40 | Enum spellings from `[JsonStringEnumMemberName]` where a value has two words (`no_go`, `in_progress`, `map_image`), lower case otherwise, for the database, the wire and the REST API alike (`EnumText`) | the data model's values; one spelling everywhere |
| 41 | `assignment_roles` [S, ro], `saved_views.description`, `config_keys.hash`, `files.unreferenced_since`, `file_uploads.next_chunk` added to doc 02 | roles of the packages; views' descriptions in packages; local edits told apart; the 30-day rule; chunk order |
| 42 | `items.origin_item_id` is set by the client on a project's insert (conversion offline); the server sets the origin's `converted_to_item_id` | conversion works offline like any create |
| 43 | A `files` insert whose bytes (SHA-256, size) the user can already read is `available` at once and shares the stored bytes; stored bytes no row names are removed by the cleanup | conversion and copies need no upload; purges free the disk |
| 44 | Image metadata is stripped at the commit, so a stored image's `sha256`/`sizeBytes` may differ from the uploaded bytes (the row then carries the stored ones) | PNMTD strips without re-encoding; the client verifies downloads against the row |
| 45 | Chunks must come in order (`n` = 0 or the next; 409 `chunk_out_of_order`) | a retried chunk would otherwise be appended twice |
| 46 | Package requests wrap the package: `{package}` or `{builtIn}` (+ `overwrite`) | `overwrite` next to a package that allows no extra members |
| 47 | Visibility of a section: restricted only by `view` rows; `edit` rows alone restrict editing (docs/02 §7.3 read as the package schema says) | the review board decides, everybody sees the decision |
| 48 | Map templates: read by everybody, managed with *Configuration: Manage* (besides owner and admins) | the template catalogue is configuration |
| 49 | Configuration tables have no History (`change_log`); their changes are in the audit log (`config.*` events) | History is per item |
| 50 | `milestones.completed_at`, `decided_at`, `decided_by` and `dod_items.state_changed_*` are set by the server | "set automatically" (docs/01 §11) |
| 51 | Foreign keys from children to their parents (cascade) and to configuration (restrict); none to users, nor for references a row may outlive (status, template, cover image, connector ends) | integrity where a purge removes both anyway |
| 52 | `mcp_log` keeps PNMTD's further columns (kind, username, ip, user agent, client, method, tool kind, error code and text, summary, truncated, strike, targets); outcome `unauthenticated` for a refused token; `ended_why` and `outcome` as text with a CHECK (doc 02: enum-like values) | the admin log's filters and detail; the lower-case enum converter can't write `user_inactive` |
| 53 | MCP read token: no area levels at all (PNMTD: reading levels) | reading needs no right in PGPM; sharing decides (§6.2) |
| 54 | Object access capped by `Viewer.MaxAccess` (read/edit), the MCP viewer's visibility from the user's admin flag | every table hook and service is capped without knowing MCP |
| 55 | `Mcp:Use` needed to switch a token on or regenerate it, and checked per request (state `no_right`, the token stays on); switching off needs no right | the catalogue's "Use: own tokens" (§6.2) |
| 56 | A blocked token is reported by an audit entry `mcp.token_blocked` (PNMTD: Pushover and e-mail to every admin) | PGPM has no notifications in v1 |
| 57 | No delete window endpoints (`write/delete-window`); the columns stay reserved | no delete tools in v1 |
| 58 | MCP writes are audited (`mcp.item_created`, `mcp.item_updated`, actor `mcp`) besides the change log (the UI's sync pushes are not) | what an assistant did is in the audit log, joined to its MCP log entry by the correlation id |
| 59 | Refused MCP sign-ins are summed up per address **and token** per minute (PNMTD: per address) | a known token's refusals stay visible in its own log |
| 60 | `get_item` / `update_item` take an item's key (`N-12`) as well as its id; `share_with` gives `read` or `edit` only | assistants quote keys; the write token's access is capped at edit |
| 61 | `/mcp` gets the API's CSP and `no-store` | it is an API, not a page |
| 62 | Task health keys `task.directory_sync`, `task.mcp_token_expiry` (doc: `LAST_DIRECTORY_SYNC`) | phase 2's keys are `task.*` |
| 63 | `directory_sync_state` adds `last_mode`, `seen_count`, `linked_count`, `last_full_at`, `last_succeeded_at` | the weekly full run's due date and the admin page |
| 64 | The directory job writes one `SyncWriter` unit per Graph page and sets the persons' properties inside it (not as client mutations); no user in `created_by`/`updated_by` | the counter's lock is not held across Graph calls; the persons table refuses client patches of directory fields |
| 65 | *Run now* answers 202 and wakes the background task (no run inside the request); 409 `directory_off` while `Directory:Enabled` is off or the registration incomplete | a full run of a large tenant takes minutes |
| 66 | After a commit that stripped an image's metadata, the uploading device replaces its local bytes by the stored ones (downloaded at once, checked against the new sha256; PLM keeps what it uploaded) | every device holds the same bytes, verified against the row |
| 67 | Conversion copies the cover and the attachments as new `files` rows of the project that share the stored bytes; images embedded in the description keep pointing at the source's files (docs/01 §8 names attachments, not embedded images) | a row belongs to one item, nothing is uploaded twice; copying embedded images would mean rewriting the description |
| 68 | The commit's answer also carries `fileName` (an image's name gets its type's extension) | the client lays the answer over its row: the name must not lag behind the bytes |
| 69 | Playwright runs `identity.spec.ts` as a project of its own (`first-admin`) that every other spec depends on (phase 2: first by file name) | it needs the fresh stack's initial password; later specs' names must not change the order |
| 70 | `maps.preview` is last writer wins (no `old` check, never a conflict) and not recorded in the History (`SyncTable.LastWriterWins`, `NotInHistory`; §11.5) | a derived picture nobody decides between; 256 KB per visit would swamp the History |
| 71 | MCP `create_project` from a template and `convert_to_project` give the template's default shares and the source's shares at most *edit* (the browser copies them as they are) | a token grants no more than it may do itself (§7.2 capped access, #60) |
| 72 | MCP `update_item`/`create_*` `fields` add the field's section to the item when it doesn't show it yet | the browser edits only fields of sections on the item; a value in a section the item doesn't show would be hidden there |
| 73 | `get_map` gives a matrix card's values, axis positions (0–1) and the world point it aims at, not the nudged card frame | the spiral nudging depends on every card's size and the device's layout; the position and the quadrant are what an assistant reasons about |
| 74 | MCP field ranges and quadrant filters weigh at most 5,000 candidate items in memory (`invalid_argument` beyond: narrow the search) | scores are never stored (data model §7.4): they are computed per item as on the devices |
| 75 | `update_item`'s `expectedRev` guards the item row only; field values are patched with `old` = the value read in the same unit, milestones have `update_milestone`'s own `expectedRev` | each row has its own rev (§11.5) |
| 80 | The pull reads every table's highest seq first (one statement with the head and the horizon, an index lookup each) and skips the tables without a row above the cursor (§11.6) | a live feed's pull after a commit reads one or two tables instead of 26: 50 live clients' delivery p95 101 ms instead of 8 s at 20 pushes/s (docs/07 §1) |
| 81 | *Administration → System*'s stored file bytes are summed from `files` rows (each `storage_path` once), not by walking the folder | an instance with many files; the folder's own check is `FileCheckTask` |
| 82 | Deleting a device on the System page also tells its live channel (`reset`, close 4409), not only its next HTTP sync | the device starts over at once, as an access change does |
| 83 | At most 50 open editors (*… is editing*) per user; a new one ends the user's least recently seen (`EditingRegistry.MaxPerUser`) | the registry is in memory (docs/06 S-12) |
| 84 | The list's text comparisons use one `Intl.Collator` (numeric, base) and the search remembers normalised texts (`useLocalTable.ts`); each row's sort values are read once per sort (`matchRows`); a pull page is applied per table with one `bulkGet`, one outbox query and one `bulkPut` (`local/apply.ts`; PLM: row by row) | 10,000 items: the default sort 232 → 16 ms, a first sync of ~10,000 rows 12.2 → 2.8 s (docs/07 §2) |
| 85 | Cookies are `Secure` and HSTS is sent once `PublicUrl` is https, whatever the request's scheme (PNMTD: as the request came); the antiforgery cookie token stays as the request came | behind a TLS proxy without the `Proxy` setting the app sees plain http (docs/06 S-03, S-04) |
| 86 | An upload's first chunk is refused with 507 `file_storage_full` when it would leave less than `Files:MinFreeMb` free (PNMTD's `FileCheckTask` only warns) | a full disk takes the database with it (docs/06 S-06) |
| 87 | A file of an image purpose (cover, map image, embedded) shares stored bytes only with another image-purpose file, never with an attachment that says `image/*` (§15 #43) | an attachment's bytes are never checked or stripped (docs/06 S-07) |
| 88 | An upload refused with 507 `file_storage_full` stays queued as *waiting* (not *Failed*, not retried on every sync run like 503/429): tried again after the sync's longest error backoff (5 min) by the pipeline's own timer, or on *Retry*; the next files still go (§10.3) | not the person's fault, space may be freed; a full disk must not draw a 4 MiB chunk per sync run |
| 89 | While the sync pulls, `useLive` queries re-run together in bursts (≥ 500 ms apart, twice a burst's length if longer) and once after the pull (Dexie's `liveQuery`: each after every transaction that touches what it read; §12.10) | a list open during a large first sync re-read its rows (and the list's lookups theirs, each on its own) after every page, and the next page's write waited for those reads: 10,000 notes 11.3–12.8 → 3.3–4.6 s (docs/07 §2.3) |

## 16. Score evaluation [NEW]

One specification, implemented in `PGPM.WebUI/src/domain/score.ts` and
`PGPM/Config/ScoreEvaluator.cs` (+ `ScoreConfigValidator.cs`, field definitions
as plain `FieldSpec` records), both tested against the same vectors
(`spec/score-vectors.json`: field sets, values, expected results and
validation errors).

**Input values** (per input key):

| Input field type | Value | Max (for rescale/normalise) |
|---|---|---|
| number | value | `config.max` (required when used in a rescaled score, unless a `cap` gives it) |
| rating | value, or **NA** for `"na"` | `config.max`, default 5 |
| select | `points` of the chosen option (no points or unknown option → input invalid) | max points of all options (archived ones too) |
| multiselect | `count` (default) of the chosen options or `sum` of their points (`config.multiPoints`); an option chosen twice counts once; `[]` is an answer (0), not missing; an unknown option → invalid | option count / points sum of all options |
| score | its computed value, **rounded** (nesting allowed, no cycles) | 100 if it is normalised, else its `max` |
| any, no value (or JSON `null`) | **missing** | |

An invalid input (a value that doesn't fit its field type, e.g. `"12"` for a
number or `"na"` for anything but a rating; an input without definition or
without numeric meaning; a cycle) makes the result `null` like a missing one.

**Evaluation**:

1. Any input **missing** → result `null` (incomplete; e.g. matrix tray *Not yet
   assessed*).
2. Apply per-input `cap` to the value (NA stays NA) and to the input's max:
   max = min(cap, max), or the cap where the field has no max.
3. NA handling (`na`, default `ignore`): `ignore` → drop NA inputs; `zero` →
   NA = 0; `rescale` → drop NA inputs and multiply the result by `max / (sum of
   the maxima of answered inputs)` (sum only, and always – a `max` other than
   the inputs' total scales even without NA; for other ops same as `ignore`).
   Sum of maxima ≤ 0 → `null`.
4. `op`: `sum`, `avg` (over remaining inputs), `product`, `count` (non-zero
   inputs), `min`, `max`. No remaining inputs → `null`.
5. Multiply by `scale` (default 1); if `normalize`, result = result / `max` ×
   100 (`max` ≤ 0 → `null`). A normalised score's max (display "72 / 100", as an
   input) is 100.
6. Round half away from zero to `decimals` (integer 0–10, default 0). C# uses
   `decimal` (an overflow → `null`), TypeScript uses `number` and rounds only at
   this step: it first cuts the value to 15 significant digits (so 1.001 +
   0.004 rounds as 1.005) and shifts by powers of ten through the decimal text
   (2.675 → 2.68, never −0); vectors include rounding edge cases.

**Validation on save** (configuration, online): inputs exist, are not archived,
have numeric meaning, no cycles; `rescale`/`normalize` need maxima. Error codes
(the same in both implementations): `not_a_score`, `invalid_op`, `invalid_na`,
`invalid_decimals`, `no_inputs`, `duplicate_input`, `unknown_input`,
`archived_input`, `not_numeric` (only number, rating, select, multiselect and
score are numeric), `options_without_points` (a select, or a multiselect with
`multiPoints: sum`, with a non-archived option without points), `invalid_cap`
(cap on a key that isn't an input), `cycle` (through that input, also a cycle
further down), `max_required` (`max` > 0 for a rescaled sum or `normalize`) and
`input_max_required` (each input of a rescaled sum). The definition being saved
replaces the stored one with its key.

Example – feasibility index (doc 04 §4.2): `op: sum`, 12 ratings (max 5) +
`time_to_proof_of_value` (select, max 5 points), `na: rescale`, `max: 65` →
playbook p. 34 ratings give 53 + 5 = **58**; with two ratings *n/a* (answered max
55) a raw 48 becomes 48 × 65/55 = **56.7 → 57**.

## 17. Configuration packages [NEW]

Seed content (statuses, sections, fields, templates, groups, saved views, map
templates) is delivered as **configuration packages** instead of migrations, so
it can be inspected, re-applied and changed.

- **Format**: JSON, `PGPM/Packages/BuiltIn/<name>.pgpm.json`, specified by
  [`spec/config-package.schema.json`](../spec/config-package.schema.json)
  (draft 2020-12): `format: "pgpm-config-package"`, `formatVersion: 1`, `name`,
  `title`, `description`, `version`, `requires` and the collections `groups`,
  `statuses`, `sections`, `fields`, `templates`, `roles`, `savedViews`,
  `mapTemplates`, `matrixPresets` – objects keyed by a stable **key**
  `<entity>:<slug>` (`status:idea.assessed`, `field:feasibility_index`,
  `template:ai_use_case`, `group:review_board`, `view:review_board_agenda`,
  `maptemplate:impact_field`, `matrixpreset:prioritization`), unique across all
  packages.
- **References** use keys, never ids, and resolve within the package or a package
  it `requires` (transitively). Field and map configurations are copied as they
  are and therefore name fields by their bare key, as stored (score `inputs` and
  `cap`, matrix axes, `cardBadges`); view columns and sort use `field:<key>` as the
  list state does. Where stored JSON holds ids, the package holds keys:
  `statuses`/`templates`/`groups` → `statusIds`/`templateIds`/`groupIds` (matrix
  filter; views likewise, `sharedWith` → `groupIds`).
- **Key map**: server-only table `config_keys (entity text, key text, id uuid,
  package text)`, unique (`entity`, `key`) → re-import updates instead of
  duplicating; entities edited in the UI keep their key.
- **Import** (*Administration → Configuration packages*, *Configuration: Manage*;
  groups and rights need admin): upload or pick a built-in package → **dry run**
  (diff: create / update / unchanged / conflicts with local edits) → apply in
  one transaction through `SyncWriter` (all devices receive it live). Local edits
  win unless "overwrite" is chosen per entity.
- **Export**: current configuration (or a selection) as a package.
- **Built-in**: `core` (idea/lead/general project statuses, groups Members and
  Viewers, template *General project*, roles *Responsible*, *Contact*,
  *Contributor*, view *Learnings*) and `innovation-ai` (requires `core`; doc 04
  §4.2: sections, fields, scores, statuses, templates *AI use case* / *AI
  ambition*, gates with checklists, groups, roles, saved views, map templates,
  matrix preset *Prioritization matrix*). `ConfigPackageSeeder` imports both on
  the first start (`Packages:SeedOnFirstStart`).
- **Validation**: `tools/check-packages.py` (step *packages* of `./run-tests.sh
  --only shell`) evaluates the schema and the semantic rules – unique keys,
  resolvable references, field keys `^[a-z][a-z0-9_]{1,62}$`, score inputs numeric
  and without cycles, derivable rescale/normalise maxima, exactly one default
  status per item type across the built-in packages, template default status ∈
  allowed statuses, section `defaultFor` ⊆ `appliesTo`, gate suggestions are
  project statuses the template allows, map elements on existing layers, views,
  matrix presets and field defaults fitting their fields, icons in PrimeIcons –
  and rejects a set of broken variants (self-test). The importer applies the same
  rules before its dry run.

Format decisions (2026-11-27):

| Topic | Decision |
|---|---|
| Field key | the entry key without `field:` (`field:impact_score` → `impact_score`) |
| Status key | `status:<itemType>.<slug>`, matching `itemType` |
| Child entities | template milestones, map layers and elements have a local `key`; their config key is `<parent key>/<local key>` (`template:ai_use_case/g1`). Template statuses, sections and field defaults, section access and select options are identified by their pair or option id |
| Order | integers in the package (`sortOrder`, element `z`); the importer turns them into fractional index strings where the column is one (layers, milestones, elements) |
| Adoption | without a `config_keys` row the importer adopts an existing entity with the same natural key instead of creating a second one: group by normalized name (Members/Viewers of the migration), status by item type + name, field by key |
| Shared statuses | status names are unique per item type, so *AI ambition* uses *Draft* and *Active* of `core` plus *Closed* of `innovation-ai` |
| Compliance approval | a section of its own (rights are per section) on ideas and projects, part of the template *AI use case* |
| Sub-headings | optional `config.subheading` on every field type (the four feasibility areas) |
| Assignment roles | `roles` are suggestions for `assignments.role`; the data model has no table for them yet – phase 4 adds `assignment_roles` [S, ro] (`name`, `description`, `sort_order`, `is_archived`) |
| Matrix presets | imported as a matrix map template (`maps.kind = matrix`, `is_template = true`, one layer) that prefills *New matrix map* |
| Map templates | drawing elements only (`rect`, `ellipse`, `line`, `text`); at least one layer |
| Saved views | `definition` is the list state with keys; filters per `$defs/viewFilters` (`statuses`, `categories`, `templates`, `sharedWith`, `assignedToMe`, `overdue`, `decisions` for gate views, `hasLink {relation, direction, targetTypes}`, `fields [{field, op: eq\|in\|contains\|range\|set\|unset, value\|values\|min\|max}]`); built-in column ids in `COLUMNS` of `tools/check-packages.py`; imported as shared views owned by the importing user |
| Scores | a normalised score as input counts with max 100; `op: sum` with `na: rescale` needs `max` = the sum of the inputs' maxima |
| Rights | `{Area: Level}` of §6.2; a package can't make a group admin |
| Icons, colours | PrimeIcons 7 names without the `pi ` class (`pi-bolt`); lower-case `#rrggbb` from the Tailwind/Aura palette |

**As built (phase 4).** `PGPM/Packages/`: the built-in packages and `spec/config-package.schema.json` are embedded;
`PackageSchema` evaluates the schema as `tools/check-packages.py`'s evaluator does (the same keywords and messages),
`PackageChecker` its semantic rules (a test runs the script's self-test variants against it, another compares the
PrimeIcons list); `PackageImporter` checks, plans, applies and exports; `ConfigPackageSeeder` (a hosted service after the
migration) imports `Packages:SeedOnFirstStart` once per database (`key_values` `seed.packages`; the tests' default is
none).

- **Checking** an import: the package and the packages it requires – built-in ones as shipped, others as exported from
  what they brought – with the same rules, except that the default status is "at most one per item type in the package"
  (the importer makes it the type's default) instead of "exactly one across the built-in set". A required package must be
  imported already (`key_values` `package:<name>`, the import state).
- **Comparing**: each entity in its package form – keys instead of ids (`config_keys`), defaults filled in, unordered lists
  sorted, integer orders of fractional columns as ranks (milestones, layers, elements). Equal → `unchanged`; different →
  `update` when `config_keys.hash` (the form's SHA-256 as imported last) still matches the database, else `conflict`
  (edited here; also an adopted entity that differs, and one deleted here). Children are part of their parent's form and
  keyed `<parent key>/<local key>`.
- **Applying** (one `SyncWrite`, source `admin`; the seeder's `system`): in dependency order; integer `sortOrder`/`z` of
  fractional columns become `a0`, `a1` … (`FractionalKey.Sequence`); view filters and matrix filters get ids for keys;
  matrix presets are matrix map templates with one layer `Base` (`<key>/base`); saved views and map templates belong to
  the importing user (the seeder: the first admin); section access through the same code as the endpoint (resets);
  changed group rights forget the members' sessions. Conflicts are kept unless `overwrite` names their keys; the first
  start overwrites everything (the migration's seed groups become core's).
- The request wraps the package (`{package}` or `{builtIn}`) so that `overwrite` can sit next to it; a package that creates
  or changes groups (rights) needs an admin (403 `admin_required`).
- **Export**: every live, non-archived configuration entity (shared views, map templates) under its key, or a key made up
  from its name where it has none; `?package=` limits it to what a package brought. The export passes the checker.

## 18. Markdown editing [PNMTD]

Source: `PNMTD.WebUI/src/components/markdown/*` (about 6,900 lines incl. tests)
and its design in PNMTD `docs/locations-knowledge-plan.md` §5 (dialect §5.1,
Crepe §5.2, Monaco §5.3, switch and fallback §5.4, files §5.5, rendering §5.6,
drafts and conflicts §5.7, diagrams §5.8, editing page §5.9). Taken over as a
whole, renamed `pnmt` → `pgpm`, adapted as listed.

### 18.1 Components (copy)

| PNMTD file | Role | PGPM adaptation |
|---|---|---|
| `MarkdownEditor.vue` (1,108 lines) | The editor: *Visual / Markdown* switch, textarea fallback, uploads by paste, drop and buttons, drafts, conflict view, dialogs. `layout="page"` (EditingPage) or `"inline"`. Props `modelValue`, `mode` (`page` \| `field`), `baseRevision`, `draftKey`, `files`, `pickPage`, `conflict`, `saving`; events `save`, `cancel`, `dirty`; methods `saved()`, `focus()`, `insertMarkdown()` | `pickPage` → `pickItem` (local item search); credentials warning and `canMarkSensitive` removed; `conflict` fed by the sync (§18.5) |
| `VisualEditor.vue`, `crepeSetup.ts` (591), `crepeTheme.css` | Milkdown Crepe via `CrepeBuilder`: top bar, selection toolbar, `/` commands, block handle, tables, task lists, image block with upload, code blocks (CodeMirror inside Crepe), Mermaid preview under code blocks, placeholders; LaTeX off; **Crepe's AI feature off** (a unit test fails if it is configured); paste plugin for HTML and `data:` images; *frame* theme mapped to the app's tokens, scoped to `.pgpm-md-editor` | buttons *Attach file* and *Link to an item…* |
| `CodeEditor.vue`, `monacoSetup.ts` | Monaco: Markdown plus fenced-block languages only, no suggestions/formatters/language servers, word wrap, minimap toggle, JetBrains Mono, theme follows the app; paste/drop uploads; preview beside the code (300 ms after typing, scrolls with it); workers bundled by Vite (CSP `'self'`) | – |
| `MonacoDiff.vue`, `lineDiff.ts` | Diffs for conflicts (*Their changes* / *My changes*) and "the visual editor will rewrite 7 lines"; HTML-table fallback | also used for Markdown sync conflicts and History diffs |
| `MarkdownView.vue`, `MarkdownContent.ts`, `renderMarkdown.ts` | Rendering to a sanitised hast tree → Vue nodes (no HTML strings): remark-parse + remark-gfm → raw HTML as text (bare `<br>` excepted) → `rehype-sanitize` (GitHub schema, narrowed; http(s), mailto, tel) → `rehype-highlight` → own steps: links (external in new tab, internal via router), images only from `/api/v1/files/…`, attachment chips, heading ids with `user-content-` prefix, copy buttons, `mermaid` blocks, scrolling tables; print style | internal routes (`/projects/…`, `/ideas/…`, `/leads/…`, `/notes/…`, `/maps/…`); file URLs resolved through the local blob store (§18.4); `compact` variant for map cards |
| `MermaidDiagram.vue`, `mermaid.ts` | Lazy Mermaid 12: `securityLevel: 'strict'`, `htmlLabels: false`, extended `secure` keys, DOMPurify SVG pass, nothing fetched, `maxTextSize` 50,000, `maxEdges` 500, drawn when scrolled into view (≤ 50 per page), cached per source and theme, menu *Show source / Copy source / Download SVG* | – |
| `MarkdownImage.vue`, `AttachmentChip.vue`, `CodeBlock.vue`, `files.ts`, `types.ts` | Image (lazy, click → full size), attachment chip (icon, name, size, *File removed*), code block with copy | sources from the local blob store; *pending upload* state |
| `EditingPage.vue` | Full-window editing layout: header slots, drawer folded to the rail, Ctrl/⌘S, leave guards | – |
| `useUpload.ts` (416) | Browser side of uploads | transport replaced (§18.4) |
| Tests: `MarkdownEditor.test.ts`, `MarkdownEditor.fallback.test.ts`, `VisualEditor.test.ts`, `crepeSetup.test.ts`, `renderMarkdown.test.ts`, `mermaid.test.ts`, `useUpload.test.ts`, `lineDiff.test.ts`, `EditingPage.test.ts`, `roundTrip.fixture.ts` (Markdown that must survive Crepe unchanged) | | copied with the components |

Also from PNMTD: `stores/preferences.ts` `editorModes` (`{page, field}`, per
browser), `composables/useMedia.ts`, `composables/useNotify.ts`,
`composables/useCopy.ts`, `components/DiffView.vue`.

What the components take from the app around them is in
`components/markdown/services.ts`, given with Vue's provide/inject
(`provideMarkdownServices({ fileSink, resolveFileUrl, drafts })`), so the
editor knows nothing of the sync or IndexedDB:

| Service | Contract | Default (nothing provided) | Provided by |
|---|---|---|---|
| `FileSink` (`fileSinkKey`) | `store(file, kind: 'image' \| 'attachment', onProgress?) → Promise<{id, url, name, contentType, size, width?, height?} \| null>`; gets the file after `useUpload` steps 1–3; null = declined (it told the user), throw = the editor shows the error | files refused with a notice | phase 3: the offline file pipeline (§18.4) |
| `FileUrlResolver` (`fileUrlResolverKey`) | `(url, { localOnly? }) → Promise<string \| null>`: `/api/v1/files/…` → blob: URL / the URL / null (*Not available offline*); `localOnly` for compact texts; also Crepe's image block (`proxyDomURL`) | the URL itself | phase 3: local blob store |
| `DraftStore` (`draftStoreKey`) | `read(key)` (sync or async), `write(key, {revision, base, text, savedAt})`, `remove(key)`; keys `<table>:<id>:<field>` | `localStorage` `pgpm.draft.<key>` (PNMTD) | phase 2: IndexedDB `drafts` + mirror |

The editor's `save` event carries `{ markdown, base, revision, overwrite }`
(`base` = the patch's `old`); further events `take-theirs` (conflict) and
`restored` (a draft was restored, for §11.11's "changed by X since you
started"). *Link to an item…* is offered in the Markdown modes' bar too. A dev
route `/dev/markdown` (development builds only) shows editor and renderer side
by side.

### 18.2 Where it is used

| Text | Editor mode key | Place |
|---|---|---|
| Item description (notes: *Content*) | `page` | `/<type>/:id/edit` (EditingPage); the create dialog uses `layout="inline"` |
| Milestone / gate description | `field` | `/projects/:id/milestones/:mid/edit` |
| Field of type `markdown` | `field` | `/<type>/:id/fields/:key/edit` |
| Map side panel | – | rendered with `MarkdownView`; *Edit* → editing page with `?return=` back to the map (and `?focus=`) |
| Note cards on maps | – | `MarkdownView` compact (first lines, no diagrams) |

Short fields stay inline (`InlineField`); gate decision notes and DoD notes are
plain text.

### 18.3 Dialect (PNMTD §5.1)

- GFM (tables, task lists, strikethrough, autolinks); fenced code with
  highlighting for bash, PowerShell, YAML, JSON, XML, INI/TOML, SQL, Python,
  JavaScript, C#, Dockerfile, diff, nginx; ` ```mermaid ` is a diagram.
- No raw HTML (shown as text; a bare `<br>` is a line break – Crepe writes empty
  lines as `<br />`).
- Files: `![alt](/api/v1/files/{id}/{name})`; attachments as ordinary links.
- Links to PGPM pages are relative (`/projects/{id}`) and open in the app.
- Stored exactly as written; the server never reformats. The server reads
  Markdown with Markdig only for file references (cleanup) and plain text (MCP).

### 18.4 Files: from paste to offline upload

PNMTD `useUpload.ts` steps 1–3 unchanged: image vs attachment by type and intent;
JPEG redrawn through a canvas (orientation applied, metadata dropped); images
over 2 MB recompressed (WebP where available, else JPEG, 90 % then 80 %) and
scaled to max. 3,840 px, the user told (*Attach the original instead*); an
animated GIF over the limit is offered as an attachment.

Step 4 (XHR `POST /api/v1/files`) is replaced by PGPM's offline file pipeline
(§10.2):

1. Create a `files` row (`purpose: embedded` for images, `attachment` for other
   files, `item_id` = the edited item, `status: pending`) and store the bytes in
   IndexedDB `blobs` (plus thumbnail) – one outbox entry.
2. Return `/api/v1/files/{id}/{slug}` immediately; the editor inserts it.
3. `MarkdownImage` / `AttachmentChip` resolve file URLs through the local blob
   store first (blob URL), then the network; *pending upload* marker until
   `status = available`.
4. Chunked upload when online; server checks as in §10.1.

Embedded images are prefetched like cover images, so texts read completely
offline. Unreferenced `embedded` files are deleted after 30 days (data model §6).

### 18.5 Drafts and conflicts

- **Drafts**: PNMTD's per-`draftKey` drafts (`<table>:<id>:<field>`) with base
  text and revision, stored in IndexedDB `drafts` with PLM's synchronous
  `localStorage` mirror on `pagehide`; *Restore / Discard* on opening.
- **Saving** = one patch `{field: {old: <text when editing started>, new}}` through
  the local repo (works offline).
- **Live**: when the synced field changes while the editor is open, the editor
  sets `conflict = {by, at, theirs, base}` at once – before saving – and shows
  PNMTD's conflict view: *Their changes* / *My changes* (`MonacoDiff`, `lineDiff`
  fallback), *Save mine anyway* (patch with `old = theirs`), *Take theirs*.
- **Offline**: if a push returns a conflict for a Markdown field, the global
  conflict dialog's *Edit* opens the editor in this conflict view (§11.8).
- No three-way merge (as in PNMTD).

### 18.6 Who else is editing

- PNMTD: `POST …/editing` every 30 s, kept in memory, expires after 90 s,
  blocks nothing.
- PGPM: `POST /api/v1/editing {itemId, target}` (`target` = `description`,
  `field:<key>` or `milestone:<id>`) every 30 s while an editor is open,
  `DELETE` on close; kept in memory (`EditingRegistry`, singleton);
  `LiveSyncHub` sends `editing` / `editing_end` (§11.4) to live connections
  whose user can read the item. Shown in the editor header and on the detail
  page. Online only.

### 18.7 Bundle and offline

- Crepe, Monaco (+ workers), Mermaid and the highlight languages are separate
  lazy chunks, loaded on first use **and precached** by the service worker
  (§11.1), so editing, previews and diagrams work offline (spike S4, doc 05).
- Phones: Monaco is not used (textarea fallback, as in PNMTD).
- The CSP stays `'self'`: workers are bundled, Mermaid fetches no fonts or icons.
  PGPM's `img-src` has no `data:` (PNMTD's has), so the placeholder of a pasted
  `data:` image while it uploads is a blob: URL; the built CSS has no `data:`
  URLs (`assetsInlineLimit: 0`).
- Sizes (minified, as PNMTD's): Monaco 3,230 kB + its worker 304 kB + codicon
  font 153 kB, Crepe 679 kB, Mermaid core 85 kB plus lazily loaded diagram types
  (ELK layout 1,499 kB, KaTeX 259 kB, cytoscape 442 kB …). With every chunk,
  the precache grows to about 268 entries / 12 MB.
  `build.chunkSizeWarningLimit` is 3,300 kB so that Monaco's chunk doesn't warn.
