# PGPM

An offline-first web app (PWA) to capture and drive innovation work – from a raw idea through assessment,
prioritization, prototype and MVP to operation. Notes, ideas, leads and projects are laid out on **maps**, shared with
**groups**, and synced **live** between everyone connected; everything a user may see also works offline.
One-page overview: [docs/00-summary.md](docs/00-summary.md).

## Features

- **Items**: notes, ideas, leads, projects – keys (`I-42`), configurable statuses with overdue markers, owner, sharing,
  assigned persons with roles, attachments and cover images, typed links, history, trash with full restore.
- **Lists** with search, filters, sort, columns, bulk actions and saved views (personal and shared); detail pages with
  inline editing; a Markdown editor (visual or source) with pasted images, Mermaid diagrams and item links.
- **Configuration**: sections and fields per item type (text, number, date, select, person, URL, rating 0–5, computed
  scores), section access per group, project templates with milestones and **gates** (Definition of Done, go / no-go /
  iterate / pivot / park), all delivered as importable **configuration packages**.
- **Maps**: an infinite canvas (item cards, shapes, connected lines, text, images, milestones and timelines; layers,
  snap, minimap, undo, live co-editing) and **matrix maps** that place items by two scores; map templates for workshops.
- **Innovation method**: the digitalswitzerland AI playbook as a built-in package – ambition, idea funnel, assessment
  with a feasibility index, prioritization matrix, review board, AI use-case projects with gates G0–G3
  ([doc 04](docs/04-innovation-methodology.md)).
- **Access**: local accounts with TOTP, Microsoft Entra SSO (app roles → groups), a persons directory synced from
  Entra, rights as capabilities, sharing per item and map at *read*, *edit* or *manage*.
- **Offline and sync**: installable PWA; local IndexedDB copy, live WebSocket feed (< 1 s) with polling fallback,
  offline changes queued, field conflicts resolved by the user.
- **MCP server** for AI assistants: personal read and write tokens, capped rights, every call logged.

## Architecture

```
 Browser (PWA)                                     Server (one container)                   Host
┌─────────────────────────────────┐   HTTPS   ┌───────────────────────────────────┐
│ Vue 3 + PrimeVue UI             │  ───────▶ │ ASP.NET Core (.NET 10)            │
│ Service worker (Workbox):       │  /api/v1  │  REST API · auth (cookie, TOTP,   │   ┌──────────────┐
│   app shell offline             │           │  Entra OIDC) · rights · sharing   │──▶│ PostgreSQL 18│
│ Dexie (IndexedDB): all data the │  /api/v1/ │  Sync: SyncWriter · pull pages ·  │   └──────────────┘
│   user may see, outbox, blobs   │ sync/live │  evictions · live hub (WebSocket) │   ┌──────────────┐
│ Sync engine: push/pull, live    │ ◀═══════▶ │  Files (chunked uploads)          │──▶│ files/ folder│
│   channel, conflicts            │   WS      │  Config packages · Maps · Persons │   └──────────────┘
└─────────────────────────────────┘           │  MCP server (/mcp, bearer tokens) │   ┌──────────────┐
 AI assistant ── Streamable HTTP ───────────▶ │  Background tasks (directory sync,│──▶│ key ring     │
                                              │  pruners, file checks)            │   └──────────────┘
                                              └──────────────┬────────────────────┘
                                                             │ Microsoft Graph (directory), Entra ID (SSO)
```

Every write – from the browser, MCP, the directory sync or a package import – goes through one `SyncWriter`: the same
validation and access checks, a global change sequence, the change log, and a live push to every device that may see
the row. Details: [docs/03-implementation-reference.md](docs/03-implementation-reference.md).

## Quick start (development)

Needs Podman (or Docker with `ENGINE=docker`), the .NET 10 SDK and Python 3 for the tests. Node never runs on the host.

```sh
./start.sh            # builds the image, starts PostgreSQL and PGPM: http://localhost:7330/
WEB=dev ./start.sh    # …plus Vite with hot reload: http://localhost:5173/
./stop.sh             # stops everything (./stop.sh --data also offers to delete the data)
```

The first run writes `dev.env` (gitignored) with a generated database password and first-admin password, and prints
the sign-in.

## Development workflow

- **npm** only in a container: `tools/npm.sh install --save-exact <pkg>@<version>`, `tools/npm.sh run lint`,
  `tools/npm.sh test`, `tools/npm.sh run gen:api` (TypeScript types from `PGPM.WebUI/src/api/openapi.json`).
- **.NET**: `dotnet build PGPM.slnx` (warnings are errors; the Debug build rewrites `openapi.json` – commit it with
  API changes); migrations with `dotnet ef migrations add <Name> --project PGPM/PGPM.csproj`. Lock files are committed;
  the image restores in locked mode.
- **Tests**: `./run-tests.sh` runs every group, `--only` some of them, `--list` shows them:

  | Group | What |
  |---|---|
  | `dotnet` | locked restore, build, no pending model change, PGPM.Tests on a throwaway PostgreSQL (`tools/test-db.sh`) in two time zones |
  | `web` | npm package check and signatures, API types, vue-tsc, ESLint, Prettier, Vitest, the build – in a node container |
  | `contract` | PGPM.Contract.Tests against the image (`tools/contract.sh`, port 7332) |
  | `e2e` | Playwright against the image, incl. offline and two-browser sync (`tools/e2e.sh`, port 7331) |
  | `deploy` | the `deploy/` scripts for real: init, up, users, a note and a file, backup, restore, update (`tools/deploy-test.sh`, port 7390) |
  | `shell` | `bash -n`, shellcheck, the configuration packages (`tools/check-packages.py`) |

- **CI** (`.github/workflows/ci.yml`) runs the same groups on GitHub's runners (deploy nightly and before a release);
  `release.yml` publishes the image for a version tag `v<Version>` (secrets documented in the file).

```
PGPM/                 ASP.NET Core host (feature folders: Auth, Sync, Items, Maps, Files, Mcp, Packages, …)
PGPM.Lib/             password hashing, TOTP, helpers
PGPM.WebUI/           Vue app incl. service worker
PGPM.Tests/           MSTest + WebApplicationFactory     PGPM.Contract.Tests/  against a running container
deploy/               production compose stack and scripts    tools/  dev and test scripts
spec/                 package schema, shared test vectors      docs/   specification and guides
```

## Configuration, deployment, MCP

- **Deployment**: [deploy/README.md](deploy/README.md) – `./init.sh`, `docker compose up -d`, backups, restore, updates,
  reverse proxy (Caddy, nginx).
- **Administration** – configuration keys, Microsoft Entra registration, users and rights, packages, monitoring,
  troubleshooting: [docs/admin-guide.md](docs/admin-guide.md).
- **MCP**: `<PublicUrl>/mcp`, personal tokens under *Security → MCP tokens*; client examples in
  [docs/admin-guide.md §10](docs/admin-guide.md#10-mcp-for-ai-assistants).
- **Using it**: [docs/user-guide.md](docs/user-guide.md). What's in 1.0: [docs/release-notes-1.0.md](docs/release-notes-1.0.md).

## Documentation

[00 Summary](docs/00-summary.md) · [01 Functional overview](docs/01-functional-overview.md) ·
[02 Data model](docs/02-data-model.md) · [03 Implementation reference](docs/03-implementation-reference.md) ·
[04 Innovation method](docs/04-innovation-methodology.md) · [05 Build plan](docs/05-build-plan.md) ·
[Admin guide](docs/admin-guide.md) · [User guide](docs/user-guide.md) · [Release notes 1.0](docs/release-notes-1.0.md)

## Licence

Not decided yet: no licence is granted until one is added here.
