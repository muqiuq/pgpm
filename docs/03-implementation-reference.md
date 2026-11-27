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
│   ├── Directory/            Graph client + directory sync task
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
  `deploy/*.sh` wrap `docker compose exec pgpm pgpm users …`.

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
     (fallback policy = authenticated user, deny by default) → `UseSessionGates`
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
`default-src 'none'` + `no-store`). The
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

### 3.5 Tests [PNMTD]

- `TestApp : WebApplicationFactory<Program>` with a fake `TestSignIn` scheme
  driven by headers (`ClientWithRights("Projects:Create,Maps:Create")`).
- **Endpoint audit test** (`PNMTD.Tests/Hosting/EndpointAuditTests.cs`): an
  allowlist of anonymous endpoints and a matrix of every v1 endpoint's required
  right – a new endpoint fails the tests until it is added. Copy as-is.
- Sync tests: two simulated clients against one server (offline edits,
  conflicting patches, rights revoked mid-way, tombstone horizon).
- `FakeEntra` from GGITSV (§5.2).
- Playwright e2e incl. offline mode (`context.setOffline(true)`).

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

- Data-Protection key ring persisted to `/var/lib/pgpm/keys`, application name `PGPM`.
- Claims (`SessionClaims.Create`): user id, name, `pgpm:display_name`,
  `pgpm:stamp`, `pgpm:signed_in`, **new** `pgpm:auth_method` (`password` \| `microsoft`).
- Rights are **not** in the cookie: `SessionCache.ValidateAsync` loads stamp,
  active flag and rights per request through `IMemoryCache` (1 min TTL), rejects
  on stamp mismatch, adds `pgpm:admin` / `pgpm:right` claims.
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
  login only from these networks; Microsoft SSO from anywhere. Startup check:
  refuse a config where nobody can sign in.

### 4.4 TOTP

RFC 6238, SHA1, 6 digits, 30 s, ±1 step (`PNMTD.Lib/Security/TotpService.cs`),
replay protection via `totp_last_used_step`, secret encrypted with Data
Protection (`PGPM.Totp.v1`), policy `Auth:TotpPolicy = Optional |
RequiredForAdmins | Required` (applies to local accounts only). QR rendered
client-side (`qrcode`).

### 4.5 Endpoints

| Endpoint | Notes |
|---|---|
| `POST /api/v1/auth/login` | `{next:"done"\|"totp"}`; for `totp` a 5-min `pgpm.2fa` cookie (path `/api/v1/auth`) |
| `POST /api/v1/auth/login/totp`, `POST /auth/totp/daily`, `POST /auth/logout` | |
| `GET /api/v1/auth/me` | current user, `rights: {Area: Level}`, session flags, issues XSRF cookie; **cached in IndexedDB `meta`** for offline start |
| `GET /api/v1/auth/options` | `{microsoft: bool, local: bool}` **[GGITSV]** |
| `GET /api/v1/auth/sso/microsoft?returnTo=` | SSO start **[GGITSV]** |
| `/api/v1/account/{password, sessions/revoke, totp/*, access, mcp/*}` | own account |
| `/api/v1/users`, `/{id}/reset-password`, `/{id}/reset-totp`, `/{id}/unlock`, `/{id}/access`, `/{id}/microsoft-account` | admin only (`[RequireAdmin]`) |
| `/api/v1/groups`, `/{id}/members`, `/{id}/rights`, `GET /api/v1/rights` (catalogue) | admin only |

### 4.6 Bootstrap and recovery

- `UserAccounts.SeedAsync`: empty `users` table → create
  `Auth:InitialAdmin:Username` (default `admin`) with
  `Auth:InitialAdmin:Password` or log a generated one once,
  `must_change_password = true`.
- `GuardLastAdminAsync`: 409 `last_admin` when deleting/demoting/deactivating the
  last active admin.
- CLI: `pgpm users list|add|reset-password|reset-totp|unlock`, wrapped by
  `deploy/*.sh`.

### 4.7 Groups

`groups`, `group_rights (group_id, area, level)`, `user_groups` (data model §3).
`GroupService`: CRUD, `SetMembersAsync`, `AccessAsync` (effective rights + which
group grants them). Seed groups via migration with fixed GUIDs: **Members** and
**Viewers** (§6.2).

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
   `/sign-in?ssoError=deactivated|account-conflict`.

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
- Tool catalogue v1:

  | Tool | Kind | Requires |
  |---|---|---|
  | `search_items` (type, text, status, owner, person, field/score ranges, quadrant of a matrix map) | Read | item *read* |
  | `get_item` (fields incl. scores, status, milestones/gates, DoD, persons, links, attachment list) | Read | item *read* |
  | `list_config` (statuses, sections, fields incl. score definitions, templates) | Read | – |
  | `search_persons` | Read | – |
  | `list_maps`, `get_map` (layers, elements summary; matrix maps: positions and quadrants) | Read | map *read* |
  | `create_note`, `create_idea`, `create_lead` (`share_with`) | Write | type `Create` |
  | `create_project` (template, field values, `share_with`) | Write | `Projects:Create` |
  | `update_item` (title, description, color, status, type fields, field values, ratings) | Write | item *edit* |
  | `assign_person`, `unassign_person` | Write | item *edit* |
  | `link_items`, `unlink_items` | Write | *edit* on source, *read* on target |
  | `add_milestone`, `update_milestone`, `set_dod_item` (state, note) | Write | item *edit* |
  | `record_gate_decision` (decision, note, optional status change) | Write | item *edit* |
  | `add_item_to_map` (map, item, position or near element) | Write | map *edit*, item *read* |
  | `convert_to_project` (idea/lead, template) | Action | source *edit* + `Projects:Create` |

  Delete, sharing and owner tools are not offered in v1.

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

---

## 9. Background tasks

| Task | Interval | Source | Purpose |
|---|---|---|---|
| `DatabaseMigrationService` | startup | PNMTD | migrations + custom migrations |
| `ConfigPackageSeeder` | startup | NEW | first start: import built-in packages `core` and `innovation-ai` (§17) |
| `LiveSyncHub` | continuous | NEW | owns WebSocket connections, change feed, heartbeats (§11.6) |
| `McpTokenExpiryTask` | 5 min | PNMTD | record token ends |
| `DirectorySyncTask` | 6 h (+ run now) | NEW | §8 |
| `FileCleanupTask` | 1 h | PNMTD + PLM | stale uploads (24 h), bytes of purged files, `file_uploads` temp files |
| `FileCheckTask` | 1 h | PNMTD | free disk space (`Files:MinFreeMb`) |
| `TombstonePruner` | 1 h | PLM | delete tombstones of `other` delete batches older than `Sync:TombstoneDays` (90) and rows of purged batches; update `sync_state.tombstone_horizon_seq`. Trash batches are never pruned automatically. |
| `LogPruner` | daily | PNMTD | `mcp_log` (10,000), `sync_mutations` (30 d), `sync_evictions` (90 d), `change_log` map moves (90 d) |

All: `BackgroundService` + `PeriodicTimer` or signal, own DI scope per run,
catch-and-log, task health timestamp in `key_values`.

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
  the precache so Markdown editing works offline; `maximumFileSizeToCacheInBytes`
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
  (Maps, Ideas, Notes – "New note", "New idea" once creating exists, phase 3).
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
     (`partial`). Status changes: status must belong to the type (and template's
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
- **Playwright**: two browser contexts, change visible on the other < 1 s;
  offline edit → reconnect → conflict dialog → resolve.

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

### 12.6 Sync indicator, sharing, trash [NEW]

- `components/sync/SyncIndicator.vue` in the app bar: *live* (green dot),
  *syncing*, *offline* (grey), pending count, conflict count (orange); click →
  `SyncDialog.vue` (live status, cursor, last sync, pending list, *Sync now*,
  conflicts tab with `ConflictResolver.vue`, §11.8).
- `components/share/ShareDialog.vue`: groups with read/edit/manage (create
  dialog: local insert; later: `PUT …/shares`, online only), owner transfer.
- `views/trash/TrashView.vue`: `useServerTable` over `GET /api/v1/trash`, restore,
  purge (admin).

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
- **Level of detail**: below zoom 0.45 cards render as a colored rect + title only.
- **Viewport culling** above ~300 elements.
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
- Layout from `views/network/DevicePanel.vue`: header (2.75rem color avatar with
  type icon, h2 title, status badge, close), actions row, scrolling body with
  stacked `<section>`s – no tabs. Immediately saved color picker as in PNMTD
  `OverviewPanel.vue`.

### 13.4 Editing [NEW]

PNMTD maps only move nodes and save positions explicitly via a save bar. PGPM is
a real editor and offline-first:

- **No save bar**: every completed operation (drag end, resize end, style change,
  text edit) writes patches to the local repo immediately; during a drag only Vue
  state changes; one patch per element on `pointerup`; the live sync spreads it.
- **Remote changes during interaction**: incoming changes to an element the user
  is dragging are applied after the drag ends (then the local patch's `old` no
  longer matches → conflict only if the same property changed).
- **Undo/redo**: per-map command stack (session only); a command is a list of
  `{table, id, before, after}`; undo writes the inverse patches.
- **z-order / layers**: fractional keys (`generateKeyBetween`).
- **Connectors**: `line` elements with `start_element_id`/`end_element_id`;
  endpoints computed from the targets' borders; stored `points` are the fallback.
- **Links overlay**: *Show links* draws `item_links` between cards of linked items
  (all card pairs, dashed, not stored; nearest pair only when an item has several
  cards).
- **Timeline / milestones**: derived from the project's milestones; gates drawn
  as diamonds, colored by decision.
- **Clipboard**: JSON (`{"pgpm":"elements", …}`) via `navigator.clipboard`; paste
  creates new element ids; item cards reference the same items (another card).
- **Keyboard**: Delete, Ctrl/⌘ Z / Shift Z, C / V / D, ] / [ (+Shift), arrows,
  Space-pan, Esc, `F` fit.
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

## 14. Configuration keys

ASP.NET configuration as in PNMTD (`appsettings.json` + environment overrides
`Section__Key`); secrets only via environment/`.env`.

| Key | Default | Source |
|---|---|---|
| `Database:Host/Port/Name/Username/Password` or `Database:ConnectionString` | | PNMTD |
| `PublicUrl` | | GGITSV (redirect URI base, WebSocket origin check) |
| `Auth:InitialAdmin:Username` / `:Password` | `admin` / generated | PNMTD |
| `Auth:SessionDays` | 7 | PNMTD |
| `Auth:LoginDays` / `Auth:TotpDays` / `Auth:ResetHour` | 30 / 1 / 4 | PNMTD |
| `Auth:TotpPolicy` | `Optional` | PNMTD |
| `Auth:LocalLoginNetworks` | (all) | GGITSV |
| `Entra:Enabled` / `TenantId` / `ClientId` / `ClientSecret` | off | GGITSV |
| `Entra:AutoProvision` | true | GGITSV |
| `Entra:DefaultGroupId` | – | NEW |
| `Directory:Enabled` | false | NEW |
| `Directory:IntervalHours` / `FullSyncDays` | 6 / 7 | NEW |
| `Files:Path` | `/var/lib/pgpm/files` | PNMTD |
| `Files:MaxAttachmentBytes` / `MaxImageBytes` | 25 MB / 2 MB | PNMTD |
| `Files:BlockedExtensions` / `UploadsPerHour` / `MinFreeMb` | see §10 / 200 / 1024 | PNMTD |
| `Sync:PageLimit` / `MaxMutations` / `MaxBodyBytes` | 500 / 200 / 8 MB | PLM / NEW |
| `Sync:TombstoneDays` | 90 (non-trash tombstones only) | PLM |
| `Sync:Live:Enabled` | true | NEW |
| `Sync:Live:MaxConnectionsPerUser` | 10 | NEW |
| `Sync:Live:AckTimeoutSeconds` / `HeartbeatSeconds` | 30 / 25 | NEW |
| `Packages:SeedOnFirstStart` | `core,innovation-ai` | NEW |
| `Mcp:Enabled` / `WriteHours` / `ReadDaysAfterOnline` | true / 12 / 7 | PNMTD |
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

- **Format**: JSON, `PGPM/Packages/BuiltIn/<name>.pgpm.json`; every entity has a
  stable `key` (`status:idea.assessed`, `field:feasibility_index`,
  `template:ai_use_case`, `group:review_board`); references use keys, not ids.
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
  Viewers, roles) and `innovation-ai` (doc 04 §4.2: sections, fields, scores,
  statuses, templates *AI use case* / *AI ambition*, gates with checklists,
  groups, saved views, map templates). `ConfigPackageSeeder` imports both on the
  first start (`Packages:SeedOnFirstStart`).

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
browser), `composables/useMedia.ts`, `composables/useNotify.ts`.

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
