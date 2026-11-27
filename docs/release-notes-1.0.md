# PGPM 1.0 – Release Notes

> Release 1.0.0 · image `uisach/pgpm:v1.0.0` · [admin guide](admin-guide.md) · [user guide](user-guide.md)

The first release of PGPM: an offline-first PWA for innovation work. Ideas, leads, projects and notes live on maps,
are shared with groups, and sync live between everyone connected.

## Features

**Items and lists**
- Notes, ideas, leads and projects, each with a key (`N-…`, `I-…`, `L-…`, `P-…`), title, Markdown description, colour,
  cover image, owner, sharing, assigned persons with roles, attachments, typed links (*contributes to*, *learning of*,
  *depends on*, *related*), history and trash with full restore.
- Configurable statuses per item type (open / active / closed, maximum days → overdue).
- Lists with search, filters, sort, a column chooser, bulk actions, saved views (personal and shared) and the last list
  state; global search (Ctrl/⌘K).
- Detail pages with inline editing and read-only mode by access; ideas and leads convert into projects, sections and
  values included.
- A Markdown editor with a *Visual* mode and a *Markdown* mode. It handles pasted images and files, Mermaid diagrams,
  *Link to an item…*, crash-safe drafts and a "… is editing" notice.

**Configuration and method**
- Sections and fields for ideas and projects: text, number, date, select with points, person, URL, rating 0–5 / n/a,
  and computed scores. Sections can be visible or editable for given groups only.
- Project templates set the allowed statuses, sections, defaults and default shares. They also add milestones and gates
  relative to the start date.
- Milestones and gates have a Definition of Done (done / n/a with a note). Gate decisions (go, no-go, iterate, pivot,
  park) suggest a status change.
- Configuration packages (import with a dry run, conflicts, export). Built in:
  - `core`;
  - `innovation-ai`, the digitalswitzerland AI playbook: assessment with a feasibility index, review board, *AI use
    case* projects with gates G0–G3, saved views, map templates, a prioritization matrix.

**Maps**
- An infinite SVG canvas holds item cards (an item can appear several times), milestones and gates, timelines,
  rectangles, ellipses, connected lines and arrows, text and images.
- Canvas tools: layers (order, show/hide, lock), grid and snap, a minimap, undo/redo, a clipboard, a visibility filter,
  deep links and a side panel.
- Matrix maps place items by two scores, with quadrants and a *Not yet assessed* tray. Map templates cover workshops.
- Live co-editing.

**Access**
- Local accounts with TOTP, lockout and forced password changes. Microsoft Entra SSO, with app roles mapped to groups
  and a default group.
- A persons directory synced from Entra (the whole tenant, delta queries), plus manual persons.
- Rights are capabilities per area. Sharing per item and map: *read*, *edit* or *manage*.
- An audit log of every administrative change, every sign-in and every refusal.

**Offline and sync**
- Installable PWA: the app shell and all data the user may see live on the device (IndexedDB). Maps and scores work
  offline too.
- A live WebSocket feed delivers others' changes in under a second and falls back to polling. Offline changes are
  queued, and colliding field changes become conflicts the user resolves (mine / theirs / edit).
- Losing access removes data from the device live; signing out wipes it.

**MCP server for AI assistants**
- MCP at `/mcp` (Streamable HTTP) with personal read and write tokens: capped rights, rate limits, strikes and a full
  call log.
- Writes go through the same validation and access checks as the browser's, and appear live.
- 21 tools: search and read items, persons, maps (matrix quadrants included) and the configuration; create notes,
  ideas, leads and projects from their templates, update them, place them on maps, manage milestones, gates and their
  Definition of Done, link items, assign persons and convert to a project. They are listed under
  *Administration → MCP → Tools*.

**Operations**
- One hardened container (non-root, read-only) and PostgreSQL 18.
- `deploy/` scripts:
  - `init.sh`, `info.sh`;
  - `backup.sh`: one verified archive with database, key ring and files, with retention;
  - `restore.sh`: verified, into an existing or a fresh stack;
  - `update.sh`: backup, pull, health wait;
  - the user scripts.
- `/healthz`; the image is published for amd64 and arm64.

## Known limitations

- **One server instance per database.** The live change feed runs inside the process, so two PGPM containers on one
  database would not see each other's live changes. Scale up, not out. A PostgreSQL `LISTEN/NOTIFY` feed for scale-out
  is a later step.
- **A sync reset reloads everything.** The device first sends its pending changes, then reloads everything it may see.
  With large data sets this takes a moment. Resets happen after:
  - group membership or admin-flag changes;
  - section visibility changes;
  - long offline periods (90 days of tombstones);
  - a restore.
- **No presence on maps.** You don't see who else is looking at a map or where their cursor is. Texts show
  "… is editing".
- **No export or import of data yet.** There is no map export (PNG/SVG/PDF) and no CSV/Excel export or import of items.
  The configuration can be exported and imported as packages.
- **MCP has no delete, sharing or owner-transfer tools** (by design). Assessment drafts by an assistant come later.
- **Client gaps:**
  - no bulk *Add to map* on the item lists;
  - the map side panel edits the main fields only (description and attachments via *Open details*);
  - no template filter on the canvas filter bar;
  - the Trash purges one entry at a time;
  - on phones, the global search field is hidden (search from the lists).
- **Local data is not encrypted** on the device. Signing out wipes it. Shared computers should sign out.
- **iOS/iPadOS** may evict the storage of websites that are not installed. Install PGPM to the home screen, and sync
  before going offline for long.
- **Signing out of PGPM does not sign out of Microsoft.** The Entra client is authenticated with a client secret only
  (certificates later).
- **Proxies must pass WebSockets** with an idle timeout of 60 s or more. Without that the app still works, but changes
  arrive over polling with a delay.
- One time zone per installation (`TZ`) for wall-clock rules.

Planned for 1.x: KPI tracking, a funnel and ambition dashboard, MCP assessment drafts, tags, export and import,
presence on maps, push notifications.

## Upgrade notes

- **This is the first release.** Install it fresh: `deploy/init.sh`, `docker compose up -d` ([admin guide](admin-guide.md)).
  Test installations of pre-release builds (0.1.0) should be set up afresh; their databases are not a supported upgrade
  path.
- **Later releases:** `deploy/update.sh` (or `--tag vX.Y.Z`) backs up, pulls and migrates on start. Going back needs the
  update's archive: the old `PGPM_IMAGE_TAG` in `.env`, then `deploy/restore.sh --force` – PGPM refuses a database that
  a newer release has migrated.
- **Take backups from day one:** add `deploy/backup.sh --quiet --keep 14` to cron, copy the archives off the host,
  and keep a copy of `deploy/.env`, which is not in the backup.
- **Reverse proxy:**
  - WebSocket upgrade on `/api/v1/sync/live`;
  - idle timeout ≥ 60 s;
  - body limit 32 MB;
  - `PublicUrl` equal to the address users open;
  - `Proxy` set ([deploy/README.md](../deploy/README.md)).
