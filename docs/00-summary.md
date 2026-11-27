# PGPM – Summary

> Status: v0.2 (2026-11-27) · one-page overview of structure and features.
> Details: [01 Functional overview](01-functional-overview.md) · [02 Data model](02-data-model.md) · [03 Implementation](03-implementation-reference.md) · [04 Innovation method](04-innovation-methodology.md) · [05 Build plan](05-build-plan.md) · [06 Security review](06-security-review.md) · [07 Performance](07-performance.md)

## What it is

An offline-first web app (PWA) to capture and drive innovation work – from a
raw idea through assessment, prioritization, prototype and MVP to operation.
Everything is laid out on **maps**, shared with **groups**, and synced **live**
between all connected users.

## Building blocks

| Object | Purpose | Key features |
|---|---|---|
| **Note** | quick capture, learnings | content, pinned; linkable as *learning of* a project/idea |
| **Idea** | candidate use case | configurable status, assessment sections (ratings, scores), review-board decision, convert → project |
| **Lead** | external opportunity | configurable status (New → Won/Lost), organisation, value, probability, next action, convert → project |
| **Project** | the actual work | template, configurable status (per template), sections & fields, milestones and **gates** with Definition of Done |
| **Map** | visual workspace | *canvas* (free infinite board) or *matrix* (items placed by two scores) |
| **Person** | people directory | synced from Microsoft Entra (whole tenant) + manual externals; assigned to anything with a role |

Common to all items: title, description, color, cover image, key (`I-42`),
owner, sharing, assigned persons, attachments, typed links, history, trash.

## Screens

```
Sidebar                       Each item type                 Map
├─ Work                       ├─ List: search · filter ·     ├─ Canvas: toolbar · layers ·
│  ├─ Maps (+ Templates)      │   sort · columns · saved     │   filter bar · minimap ·
│  ├─ Projects                │   views · bulk actions       │   library panel
│  ├─ Leads                   └─ Detail: header (key,        ├─ Matrix: axes · quadrants ·
│  ├─ Ideas                       status, owner, Share) +    │   "not yet assessed" tray
│  ├─ Notes                       tabs Overview · Milestones └─ Side panel (right, non-modal)
│  └─ Trash                       · Links · Maps · History      opens on click
├─ Directory: Persons
└─ Administration: Users · Groups & Rights · MCP · Statuses · Sections & Fields ·
   Templates · Configuration packages · Microsoft integration · Audit log · System
Top bar: global search (Ctrl/⌘K) · sync indicator (live / offline / pending / conflicts) · theme · user menu
```

## Features

**Items and configuration**
- Inline editing everywhere; Markdown texts with PNMTD's editor – *Visual*
  (WYSIWYG) or *Markdown* (source with preview) – pasted images and files,
  Mermaid diagrams, item links, crash-safe drafts.
- Configurable **statuses** per type (open / active / closed, max. days → *overdue*).
- Configurable **sections and fields** for ideas and projects: text, number,
  date (timebox), select (with points), person, URL, **rating 0–5 / n/a**,
  computed **score** (sum/avg/… e.g. feasibility index 58/65). Sections can be
  visible/editable for specific groups only.
- **Project templates**: allowed statuses, sections, defaults, default shares,
  milestones and gates relative to the start date.
- **Milestones and gates**: DoD checklist (done / n/a + evidence), gate decision
  go / no-go / iterate / pivot / park → suggested status change.
- **Links** between items: contributes to · learning of · depends on · related.
- Attachments with offline upload queue and cache; cover images.
- History of every change; **Trash** with full restore (incl. map cards).

**Maps**
- Infinite canvas: item cards (an item may appear any number of times),
  milestones/gates, timelines, rectangles, ellipses, connected lines/arrows,
  text, images; named layers with z-order, show/hide, lock; grid, snap,
  minimap; undo/redo; clipboard; visibility filter; deep links.
- Click → side panel with quick edit; changes saved instantly, visible live to
  co-editors.
- **Matrix maps** (e.g. business impact × ease of implementation with *Star
  initiatives / Quick wins / Difficult but impactful / Limited interest*).
- **Map templates** for workshops (impact field 2×2, Crazy 8s, customer journey,
  funnel).

**Innovation method** (digitalswitzerland AI playbook, shipped as an editable
configuration package)
- Ambition → ideas (≥ 30) → assessment (value, 12 feasibility ratings, risk
  flags, EU AI Act class) → matrix → review board → *AI use case* project →
  gates G0–G3 (PoC, MVP, rollout, impact review) with playbook checklists →
  operation; learnings as linked notes; AI-system inventory view.

**Access**
- **Sharing per item and map**: groups get *read*, *edit* or *manage*; owner
  and admins have *manage*; private if not shared.
- **Rights** = capabilities only (create per type, maps, configuration,
  directory, MCP, audit, system); admins manage users/groups.
- Login: Microsoft SSO (Entra, app roles → groups) and local login with TOTP.
- **MCP server** for AI assistants: personal read/write tokens, capped rights,
  logged; search, create, update, rate, decide gates, place on maps.

**Offline and sync**
- Installable PWA; all data the user may see lives in the browser (IndexedDB),
  so everything incl. maps and scores works offline.
- Online: **live WebSocket feed** – others' changes appear in < 1 s; own changes
  sent ~0.2 s after an operation; polling fallback.
- Offline changes queue and sync on reconnect; colliding field changes become
  **sync conflicts** the user resolves (mine / theirs / edit, per object or all).
- Losing access removes data from the device; logout wipes it (stored
  unencrypted).

## Technology (one line each)

- **Backend**: .NET 10, ASP.NET Core, EF Core + PostgreSQL 18, single container
  (PNMTD stack); Microsoft SSO from GGITSV; Graph directory sync job.
- **Frontend**: Vue 3 + PrimeVue 4 (PNMTD design), Dexie (IndexedDB), Workbox
  service worker, SVG map renderer (PNMTD pan/zoom/drawer).
- **Sync**: PLM's cursor protocol without encryption + global sequence, sharing
  visibility, evictions, live WebSocket channel.

## Build order

0 Bootstrap → 1 Identity & rights → **2 Sync core** → 3 Items & lists →
4 Configuration & method → 5 Maps → 6 Directory & MCP → 7 Hardening & release.
Later: KPI tracking, dashboard, MCP assessment drafts, export, tags, presence.
