# PGPM – Functional Overview

> Status: v0.2 (2026-11-26) – includes the decisions of 2026-11-26 (§18) and the
> innovation-method recommendations R1–R8 of [04-innovation-methodology.md](04-innovation-methodology.md)
> Related: [02-data-model.md](02-data-model.md) · [03-implementation-reference.md](03-implementation-reference.md) · [05-build-plan.md](05-build-plan.md)

## 1. Purpose

PGPM (Philipp Global Project Manager) is a single place to capture everything that
might become or already is work: **Notes**, **Ideas**, **Leads** and **Projects**.
Its main use is tracking **innovation projects** – from the first idea through
assessment, prioritization, prototype and MVP to operation (doc 04). All items
can be laid out and connected on infinite-canvas **Maps**, which are the heart of
the application. The app is an offline-first PWA: everything except a few
explicitly online features works without a network connection; while online,
changes reach all other connected users live.

### Design principles

1. **Maps first.** Maps are the primary way to look at and arrange work. Tables
   and detail pages are the precise way to edit it.
2. **One object, many views.** A project placed three times on two maps is the
   same project. Changing its color, title or status anywhere changes it
   everywhere.
3. **Offline by default, live when online.** The UI reads from and writes to a
   local database. Online, a live connection spreads changes within about a
   second; offline, changes are queued and synced later.
4. **Shared by groups.** Every item and map is shared with groups (read, edit or
   manage). Admins see everything.
5. **Method built in, not hard-wired.** The innovation method (statuses, assessment
   fields, scores, gates, checklists, matrix) is delivered as a configuration
   package on top of generic features – it can be changed or replaced.
6. **Configurable, not programmable.** Statuses, fields, scores, sections and
   templates are configured in the admin UI. There is no scripting.
7. **Proven building blocks.** UI, auth, rights, MCP tokens come from PNMTD,
   Microsoft SSO from GGITSV, offline sync from PLM (doc 03).

## 2. Glossary

| Term | Meaning |
|---|---|
| **Item** | Collective term for Note, Idea, Lead and Project. |
| **Owner** | The user who created an item or map (transferable). Always has *manage* access. |
| **Share** | Grants a group *read*, *edit* or *manage* access to an item or map. |
| **Person** | An entry in the people directory (full name, e-mail). Synced from Microsoft Entra ID (whole tenant) or created manually. Every user has a person. |
| **User** | Someone who can log in. |
| **Group** | A set of users. Shares, rights and section visibility are granted to groups. |
| **Status** | Configurable lifecycle state of an idea, lead or project (e.g. *Assessed*, *Won*, *Prototype / PoC*). |
| **Section / Field** | A configurable attribute (field) of ideas and/or projects, grouped in a section. |
| **Rating** | A field type: score 0–5 (or *n/a*). |
| **Score** | A computed, read-only field (sum, average, product … of other fields), e.g. the feasibility index. |
| **Template** | A blueprint for new projects: allowed statuses, sections, default values, default shares, milestones and gates. |
| **Milestone** | A dated goal within a project with a *Definition of Done* (DoD) checklist. |
| **Gate** | A milestone that ends with a recorded decision: go, no-go, iterate, pivot or park. |
| **Link** | A typed relation between two items: *contributes to*, *is learning of*, *depends on*, *related*. |
| **Ambition** | A project (template *AI ambition*) that ideas and projects contribute to. |
| **Map** | An infinite canvas (kind *canvas*) or a value-driven portfolio chart (kind *matrix*). |
| **Map element** | Anything on a canvas: item card, milestone/gate, timeline, shape, line, text, image. |
| **Layer** | A named, orderable level of a map; elements live on a layer and are z-ordered within it. |
| **Saved view** | A stored search/filter/sort/column setup of a list; personal or shared. |
| **Sync conflict** | A local change that couldn't be applied because someone else changed the same field meanwhile; the user decides. |
| **Trash** | Deleted items and maps; they can be restored. |

## 3. Navigation

Left sidebar (layout and look copied from PNMTD: collapsible groups, folds to an
icon rail, overlay on phones). Entries are shown only if the user has the
respective right; empty groups disappear.

- **Work**
  1. **Maps** – maps the user can see (tabs: Maps · Templates) → map
  2. **Projects**
  3. **Leads**
  4. **Ideas**
  5. **Notes**
  6. **Trash**
- **Directory**
  7. **Persons**
- **Administration** (folded by default; admins / users with the respective rights)
  - Users · Groups & Rights · MCP
  - Statuses · Sections & Fields · Project Templates · Configuration packages
  - Microsoft integration (SSO + directory sync settings, last sync result, *Run now*)
  - Audit log · System (sync clients, live connections, storage, version)

Top bar: global search (`Ctrl/Cmd + K`, searches all item types offline),
**sync indicator** (live / syncing / offline / pending changes / conflicts,
§14.3), theme toggle (light/dark/system), user menu (account, security incl. own
MCP tokens, install app, logout).

## 4. Common behaviour of all items

### 4.1 Common attributes

| Attribute | Notes |
|---|---|
| Title | Required. The only required attribute for every type. |
| Description | Rich text (Markdown, WYSIWYG editor); labelled *Content* for notes. |
| Color | Palette (12 preset colors + custom hex). Card color on maps, accent in tables. |
| Image | Optional cover image (upload or paste). Shown on map cards and in the detail header. |
| Key | Running number per type assigned by the server (`N-12`, `I-42`, `L-7`, `P-0042`); "pending" for items created offline until the first sync. |
| Owner | User who created the item; can be transferred by someone with *manage* access. |
| Sharing | Groups with *read* / *edit* / *manage* access (§4.2). |
| Status | Ideas, leads and projects (§5). Notes have no status. |
| Assigned persons | Zero or more persons, each with an optional role (free text with suggestions: *Responsible*, *Contact*, *Contributor*, *Innovation team*, *Review board*, *Domain expert*, *Delivery team*, *Sponsor*, *Data owner*, *AI champion*). |
| Attachments | Zero or more documents. |
| Links | Typed relations to other items (§4.7). |
| Created / modified | Who and when; maintained automatically. |

### 4.2 Sharing and access

Access to an item is decided **per item**:

| Access | Allows |
|---|---|
| **Read** | see the item and everything below it (fields, milestones, attachments, links); place it on maps one can edit |
| **Edit** | + change fields and status, assign persons, attach files, edit milestones, gates and DoD, add links |
| **Manage** | + change sharing, transfer ownership, delete and restore |

- **Admins** have *manage* access to everything.
- The **owner** has *manage* access.
- Everybody else gets access through **shares**: an item is shared with one or
  more groups, each with *read*, *edit* or *manage*. A user's access is the highest
  level granted by any of their groups.
- An item without shares is **private** (owner and admins only).
- **New items**: the *Share with* field of the create dialog is preselected – for
  projects with the template's default shares, otherwise with all groups of the
  creator at *edit* – and can be changed before saving (works offline).
- **Changing sharing** later needs *manage* access and a connection.
- Losing access removes the item (and everything below it) from the user's device
  immediately when live, otherwise at the next sync.
- **Sections** can restrict visibility and editability of their fields further
  (§6.1). Hidden fields never reach the device of users who may not see them.
- Maps are shared the same way (§12.1). Map access and item access are
  independent: a card of an item the user may not read is shown as a grey
  *restricted* placeholder without title.

### 4.3 List page (table)

One list page per item type with:

- **Search**: full-text over key, title, description and text fields.
- **Filter**: per column; at least status, assigned person, owner, color,
  created/modified date ranges, "assigned to me", "shared with group …", overdue
  (status aging, §5). Ideas and projects additionally filter on dynamic fields
  (text contains, number/rating/score range, date range, select in, boolean).
- **Sort**: click on column header, multi-column with Shift.
- **Column chooser**: show/hide/reorder columns; every visible dynamic field and
  score can be a column; *days in status* column.
- **Saved views**: the current search/filter/sort/columns can be saved as a named
  view – personal, or shared with everybody (needs *Configuration: Manage*). A view
  selector sits next to the search box. Seeded examples: *Review board agenda*,
  *AI system inventory*, *Running experiments*, *Learnings* (doc 04 §4.2).
- The last used state per list is remembered per user.
- **Actions**: *New*, open detail (click row), bulk actions (assign person,
  change status, change color, add to map, delete).

All data is local, so search/filter/sort run client-side, instantly and offline.

### 4.4 Detail page

- Header line (PNMTD style): breadcrumbs, type icon, key, title, status badge
  (with *overdue* marker), owner, **Share** button; actions on the right,
  destructive actions in the ⋯ menu.
- Tabs (linkable, e.g. `/projects/:id/milestones`): **Overview** (fields by
  section, description, color, image, persons, attachments), **Milestones**
  (projects), **Links**, **Maps** (where the item is placed; "add to map…"),
  **History**.
- **Inline editing** (PNMTD pattern): a field shows its value; click → editor with
  Save/Discard, Enter saves, Esc discards. A saved field is written to the local
  database immediately (works offline) and synced.
- Ratings are edited with one click on 0–5 (or *n/a*); scores update live.
- The description is Markdown and shown rendered; *Edit* opens the full-window
  **Markdown editor** with a *Visual* (WYSIWYG) and a *Markdown* (source) mode
  (§4.10).
- *New …* opens a small dialog (title, *Share with*; template for projects) and
  then lands on the detail page.
- Users with *read* access see everything read-only.

### 4.5 Attachments

- Upload via file picker, drag & drop, or paste; multiple files at once.
- Name, size, type, uploader, date; image and PDF preview in the browser.
- Download, rename, delete.
- Offline: metadata syncs like any other data. File contents are uploaded when
  online (queued while offline, shown as "pending upload") and downloaded on
  demand; downloaded files are cached for offline use (up to 200 MB, oldest first
  evicted). Cover images and images placed on maps are always downloaded, so
  cards and maps look complete offline.
- Max file size 25 MB for attachments and 2 MB for images (configurable);
  executable file types are blocked. Images are prepared in the browser before
  upload (as in PNMTD): photos lose their metadata, images over 2 MB are
  recompressed and scaled to max. 3,840 px – the user is told and can attach the
  original instead.

### 4.6 Person assignment

- Pick persons from the directory with type-ahead search (name or e-mail).
- Each assignment can carry a role. A person can be assigned once per item.
- "Assigned to me" works via the person linked to the current user.

### 4.7 Links between items

| Relation | Typical use |
|---|---|
| **contributes to** | idea or project → ambition (doc 04 R7) |
| **is learning of** | note → project or idea ("lessons learned", shared systematically) |
| **depends on** | project → project |
| **related** | anything ↔ anything |

- Added on the *Links* tab (needs *edit* on the source item and *read* on the
  target). Shown on both sides; a link to an item the user can't read is shown
  as *restricted*.
- The *Links* tab of an ambition lists all contributing items grouped by status –
  a simple funnel.
- The seeded saved view *Learnings* lists notes with an *is learning of* link.
- On canvas maps, *Show links* draws links between placed cards as dashed
  connectors (§12.3).

### 4.8 History

Every change (field old → new, status, sharing, assignment, attachment,
milestone, gate decision, DoD, link) is recorded on the server with user, source
(UI, MCP, directory sync, restore) and timestamp, and shown in the History tab.
Sync conflicts and how they were resolved are recorded too. The History tab needs
a connection; offline it shows only the local changes not yet synced.

### 4.9 Delete, trash and restore

- Deleting an item (needs *manage*) moves it to the **Trash** together with
  everything that belongs to it (fields, milestones, attachments, assignments,
  links, its cards on all maps).
- *Work → Trash* lists deleted items and maps the user may manage (admins: all),
  with who deleted them and when.
- **Restore** brings the item back with everything that was deleted with it,
  including its cards on maps that still exist. Restoring needs a connection.
- Admins can **delete permanently** (single entries, or everything deleted before
  a chosen date). There is no automatic purge.

### 4.10 Rich text (Markdown)

Descriptions, note content, milestone descriptions and fields of type *Markdown*
use PNMTD's Markdown editor and renderer:

- **Dialect**: GitHub Flavored Markdown (tables, task lists, strikethrough,
  autolinks), fenced code blocks with highlighting, and **Mermaid diagrams**
  (` ```mermaid ` → flowcharts, Gantt charts, timelines, quadrant charts, pie,
  bar and line charts, mind maps …). Raw HTML is shown as text, never rendered
  (a bare `<br>` is a line break).
- **Two modes, one switch** above the editor:
  - **Visual** (WYSIWYG): top toolbar (headings, bold, italic, strikethrough,
    code, link, lists, task list, quote, code block, table, image, rule,
    *Attach file*, *Link to an item…*), toolbar on a selection, `/` commands,
    drag handle for blocks, table tools, diagrams drawn under their code while
    typing.
  - **Markdown** (source, as in VS Code): highlighting, folding by headings,
    find and replace, multiple cursors, command palette; an optional
    **preview** beside the code (diagrams included) that scrolls with it.
  - Switching never rewrites the text by itself. If the visual editor would
    reformat lines (e.g. `*` lists become `-`), it says so first: *Show*,
    *Switch anyway*, *Stay*. The last mode is remembered per browser, separately
    for descriptions and for fields.
  - On phones, or where an editor can't start, a plain text area with a
    *Preview* button takes over, keeping files, drafts and Ctrl/⌘S.
- **Editing page**: editing happens on a full-window page (the sidebar folds to
  its rail): header with back, key, title, status, the mode switch, *Preview*,
  who else is editing, *Cancel* and *Save* (Ctrl/⌘S). From a map's side panel,
  *Edit* opens this page and returns to the map afterwards.
- **Files**: pasting or dropping an image inserts it at the cursor; any other
  file becomes an attachment of the item and a chip in the text (icon, name,
  size). Works offline: the file shows at once and uploads when online.
- **Links**: *Link to an item…* searches items and inserts a link
  (`[P-0042 Website relaunch](/projects/…)`) that opens inside the app; external
  links open in a new tab.
- **Drafts**: unsaved text survives reloads and crashes (*You have unsaved
  changes from 10:42* – *Restore* / *Discard*); leaving with unsaved text asks.
- **Concurrent editing** (online): *Anna Muster is editing this description
  (since 10:42)* – it blocks nothing. If someone else saved the same text in the
  meantime, the editor shows *Their changes* and *My changes* as diffs and offers
  *Save mine anyway* or taking theirs – the same decision as any sync conflict
  (§14.4).
- **Reading**: rendered safely (sanitised, images only from PGPM, external images
  shown as links), code blocks with a copy button, wide tables scroll, diagrams
  with *Show source*, *Copy source*, *Download SVG*; print-friendly. Note cards
  on maps show the beginning of the content, rendered.

## 5. Statuses (ideas, leads, projects)

Users with *Configuration: Manage* maintain one status list per item type:

- Name, color, icon (optional), sort order.
- Category: **Open**, **Active**, **Closed** – drives default filters ("hide
  closed") and map styling (closed items are shown faded unless filtered
  otherwise).
- Exactly one default status per type (project templates can override).
- **Max. days** (optional): an item longer in this status gets an *overdue*
  marker in lists, on cards and in the side panel – e.g. *Prototype / PoC* 84 days
  (experiments ≤ 12 weeks, doc 04 §2.7). Lists can show *days in status*.
- A status in use can't be deleted; it can be **archived** (not selectable any
  more, still displayed).
- **Project templates** can restrict which statuses their projects may use
  (e.g. the *AI use case* template only uses *Preparing test … Retired*).
- Seeded lists:

  | Type | Statuses (category) |
  |---|---|
  | Idea | New (open) · Elaborating (open) · Assessed (open) · Ready for review (open) · Selected (closed) · Watch (closed) · Rejected (closed) |
  | Lead | New (open) · Qualified (active) · Proposal (active) · Won (closed) · Lost (closed) |
  | Project – general | Draft (open) · Active (active) · On hold (active) · Done (closed) · Cancelled (closed) |
  | Project – *AI use case* | Preparing test (open) · Prototype / PoC (active, 84 days) · MVP / pilot (active) · Rollout & scaling (active) · In operation (active) · Stopped (closed) · Retired (closed) |
  | Project – *AI ambition* | Draft (open) · Active (active) · Closed (closed) |

## 6. Sections and fields (ideas and projects)

### 6.1 Catalogue

Users with *Configuration: Manage* maintain a catalogue of **sections** and
**fields**. A section applies to **ideas**, **projects** or both (not to notes and
leads). Because the catalogue is shared, the same field means the same thing on
every item – this makes cross-item filtering, sorting, table columns, matrix maps
and idea → project conversion possible.

**Section**
- Name, description, sort order, collapsed by default (yes/no).
- Applies to: ideas and/or projects.
- Default for: ideas and/or projects without template – the section is added
  automatically to new items of that type.
- **Visible for groups** (empty = everyone with access to the item).
- **Editable for groups** (empty = everyone with *edit* access to the item).
  Edit implies visible.

**Field** (belongs to exactly one section)
- Label, key (stable technical name, used in scores, MCP, API, export), help
  text, sort order, required (yes/no), default value.
- Type:

  | Type | Options |
  |---|---|
  | Text (single line) | max length, placeholder |
  | Text (multi-line) | plain text |
  | **Markdown** | rich text; rendered on the detail page, edited in the Markdown editor (§4.10) |
  | Number | decimals, min, max, unit (e.g. "CHF", "h", "%") |
  | Date / Date-time | *timebox* flag: an item whose date has passed while it isn't in a closed status gets an *overdue* marker |
  | Boolean (checkbox) | |
  | Single select / Multi select | options with label, color and optional **points** (number used by scores) |
  | Person | single or multiple persons |
  | URL / E-mail | |
  | **Rating** | scale (default 0–5), *n/a* allowed (yes/no); edited with one click |
  | **Score** | read-only, computed: sum / average / product / count / min / max of other fields (numbers, ratings, select points, multi-select count or points, other scores); handling of *n/a* (rescale, ignore, count as 0); maximum (for "58 / 65"); optional normalisation to 0–100 |

- Scores are computed live on the device (also offline), are sortable and
  filterable, can be list columns and matrix axes. They are never typed in.
- Fields can be **archived** (hidden from forms, values kept); deleting a field
  with values requires a confirmation that lists the affected item count.
- Changing configuration needs a connection; the result reaches all devices
  through the sync.

### 6.2 How sections reach an item

- **Ideas** get the sections marked *default for ideas*.
- **Projects** get the sections of their template (or *default for projects*
  without template).
- Users with *edit* access can **add** further sections (that apply to the type)
  or **remove** a section from an item (values are kept but hidden until it is
  re-added).
- **Idea → project conversion** carries all sections that apply to projects and
  their values (same field = same value).

## 7. Notes

Quick capture of information.

| Field | Type | Notes |
|---|---|---|
| Title | text | required |
| Content | rich text | the main body |
| Pinned | boolean | pinned notes sort to the top |
| + common attributes (no status, no sections) | | |

Notes linked with *is learning of* to a project or idea are **learnings** (§4.7).

## 8. Ideas

Lightweight by default – only a title is required – but able to carry an
assessment.

| Field | Type | Notes |
|---|---|---|
| Title | text | required |
| Description | rich text | |
| Status | idea status | default *New* |
| Sections | default idea sections | e.g. *Use case profile*, *Value*, *Feasibility*, *Risk & responsibility*, *Review board decision* (doc 04 §4.2) |
| + common attributes | | |

Action **Convert to project** (needs *Projects: Create*): asks for a template,
creates a project with title, description, persons, color, image, attachments,
shares, links and all field values that apply to projects, and keeps the link
*"originated from idea I-42"*. The idea is kept (status e.g. *Selected*, marked as
converted) so it stays on maps and in the funnel.

## 9. Leads

A potential customer engagement / opportunity. Leads have configurable statuses
but no sections.

| Field | Type | Notes |
|---|---|---|
| Title | text | required |
| Description | rich text | |
| Status | lead status | seeded *New → Qualified → Proposal → Won / Lost* |
| Organisation | text | company / organisation name |
| Contact persons | assignment with role *Contact* | externals can be created as manual persons |
| Estimated value | number + currency | optional |
| Probability | percent | optional |
| Expected decision date | date | optional |
| Next action / due | text + date | shown in the list, overdue highlighted |
| Source | text | e.g. referral, website, event |
| + common attributes | | |

Action **Convert to project**, typically when the lead is won (same as for ideas).

## 10. Projects

### 10.1 Fixed fields

| Field | Type | Notes |
|---|---|---|
| Title | text | required |
| Key | | `P-0042` |
| Description | rich text | |
| Status | project status | allowed statuses depend on the template |
| Template | reference | the template the project was created from (informational) |
| Start / end date | date | optional, planned period; start date anchors template milestones |
| Origin | link | idea or lead it was converted from |
| + common attributes | | |
| + sections & fields | | §6 |
| + milestones and gates | | §11 |

### 10.2 Project templates

Users with *Configuration: Manage* maintain templates:

- Name, description, color, icon.
- **Allowed statuses** (empty = all project statuses) and default status.
- Sections included (ordered) and default values for fields.
- **Default shares** (groups and access levels preselected in the create dialog).
- **Milestones and gates**: title, description, kind (milestone/gate), DoD items,
  due date **relative to the project start** (e.g. "+8 weeks"), order; for gates
  optionally the status to suggest on *go* and on *no-go*.

*New project* asks for a template (or "blank"). The template is applied once,
also offline; later template changes do not alter existing projects.

Seeded templates: **AI use case** and **AI ambition** (doc 04 §4.2), **General
project** (general statuses, no sections).

## 11. Milestones and gates

Each project has an ordered list of milestones. A milestone is either a plain
**milestone** or a **gate**.

| Field | Type | Notes |
|---|---|---|
| Title | text | required |
| Kind | milestone / gate | |
| Description | rich text | |
| Due date | date | |
| Status | planned / in progress / done / cancelled | *overdue* is derived |
| Completed on | date | set automatically |
| Responsible persons | assignments | for gates: the deciders (role *Review board*) |
| Color | color | defaults to project color |
| **Definition of Done** | checklist | ordered criteria; each **open / done / n/a** with an optional **note** (evidence: link or short text; required for *n/a*), who and when |
| **Decision** (gates only) | pending / go / no-go / iterate / pivot / park | with decision note, decided by, decided on |

- Progress = done ÷ (all − n/a).
- A milestone can be marked *done* only when its DoD is complete (or the user
  confirms an override, recorded in History). A **gate** is done when its DoD is
  complete **and** a decision other than *pending* is recorded.
- Recording a gate decision offers the template's suggested status change (e.g.
  *go* at "PoC decision" → *MVP / pilot*; *no-go* → *Stopped*); the user confirms.
- A list *Gate decisions* across all projects shows the stop/go/scale record.
- Milestones appear on the project page as a list and a mini timeline (gates as
  diamonds) and can be placed on maps (§12.3).

## 12. Maps

### 12.1 Map list, kinds, sharing, templates

- *Maps* tab: all maps the user can access (name, kind, description, preview,
  owner, last modified). *Templates* tab: map templates.
- Create (*Maps: Create*), rename, duplicate, delete (to Trash).
- Two kinds:
  - **Canvas** – the infinite, freely arranged map (§12.2–12.7).
  - **Matrix** – items positioned automatically by two field values (§12.8).
- **Sharing**: like items (§4.2) – groups with *read*, *edit* or *manage*; owner
  and admins have *manage*. Sharing needs a connection.
- **New map from template** copies a template's layers and drawings (not item
  cards) – §12.9.

### 12.2 Canvas interaction

- **Pan**: drag on empty background, middle mouse, or space + drag; two fingers
  on touch devices.
- **Zoom**: mouse wheel / pinch, zoom buttons, *zoom to fit*, *zoom to selection*,
  zoom level indicator.
- **Grid**: optional background grid, snap-to-grid toggle.
- **Minimap**: small overview in a corner (toggle).
- **Select**: click; Shift/Cmd-click to add; rubber-band selection.
- **Move**: drag; arrow keys nudge (Shift = larger step).
- **Resize**: handles on the selected element; Shift keeps aspect ratio.
- **Edit**: double-click a text/shape to edit its text inline.
- **Clipboard**: copy, paste, duplicate (within and across maps). Pasting an item
  card creates another card of the same item.
- **Delete**: removes the element **from the map only**; the item itself is never
  deleted from a map.
- **No save button**: every completed operation is saved immediately (locally,
  then synced live).
- **Undo / redo** for all map operations of the session (`Ctrl/Cmd+Z`,
  `Ctrl/Cmd+Shift+Z`).
- **Deep link**: `…/maps/<id>?focus=item:<item id>` centres on the item's cards and
  selects them.
- Users with *read* access can pan, zoom, select and open the side panel but not
  change anything.

### 12.3 Element types

| Element | Description |
|---|---|
| **Item card** (Note, Idea, Lead, Project) | A reference to an item. Shows type icon, key, title, color, image, status (+ *overdue*), and type-specific badges: assigned persons, milestone progress, key scores (configurable, e.g. *Feasibility 58/65*). **An item can be placed any number of times on the same and on different maps.** Card size adjustable (compact ↔ expanded). |
| **Milestone / gate** | One milestone (dot) or gate (diamond) of a project with title and due date, colored by status / decision. |
| **Milestone timeline** | A line with dots and diamonds – all milestones and gates of a project (or a subset), horizontal or vertical, evenly spaced or by date. Updates automatically. |
| **Rectangle / rounded box** | Fill, border color/width/style, corner radius, optional text. For areas, quadrants, swimlanes. |
| **Ellipse / circle** | As rectangles. |
| **Line / arrow** | Straight or elbow, arrow heads, stroke color/width/style, optional label. Ends can be **connected** to elements and follow them. |
| **Text** | Font size, bold, italic, color, alignment, optional background. |
| **Image** | Uploaded or pasted; resizable, optional border. |

*Show links* (toggle) additionally draws the item links (§4.7) between placed
cards as dashed connectors; they are derived, not stored on the map.

Not in scope: freehand drawing, rotation, gradients, custom fonts, grouping of
elements, alignment guides.

### 12.4 Adding items to a canvas

- **Toolbar**: Select, Hand, Note, Idea, Lead, Project, Milestone, Timeline,
  Rectangle, Ellipse, Line/Arrow, Text, Image.
- An item tool + click on the canvas offers **Create new** (inline title; shares
  preselected as in §4.2) or **Add existing** (search dialog; items already on
  the map are marked but can be added again).
- A collapsible **library panel** lists items (searchable, filterable by type and
  status) that can be dragged onto the canvas.
- From any item detail page: *Add to map…*.

### 12.5 Side panel

Clicking an element opens a side panel sliding in from the right (PNMTD map
drawer: about 30rem wide, full width on phones). It is **non-modal** – the map
stays usable, clicking another element swaps the content, background click or
the close button closes it. Header (colored avatar with type icon, key, title,
status badge, close), a row of action buttons, then stacked sections (no tabs):

- **Item card** → quick edit of title, color, image, status, persons, key fields
  and scores, description (rendered; *Edit* opens the Markdown editor and
  returns to the map), attachments, milestones (projects), links; *Open
  details*; "this item appears 3× on this map".
- **Milestone / gate / timeline** → details, DoD checklist, gate decision.
- **Shape / line / text / image** → style properties.
- **Multi-selection** → common style properties, layer actions.
- **Nothing selected** → map properties (name, description, background, grid,
  sharing for managers).

### 12.6 Layers

- A canvas has one or more **named layers** (default *Base*), ordered; higher
  layers are drawn on top.
- Per layer: rename, reorder, **show/hide**, **lock**, delete (elements move to
  the layer below or are deleted, user chooses).
- Within a layer every element has a z-order: **Bring to front / forward**,
  **Send backward / to back** (context menu, `Ctrl/Cmd+]`, `Ctrl/Cmd+[`, +Shift).
- **Move to layer…** for selected elements.
- Layer visibility and lock state are shared; visibility can additionally be
  toggled locally for one's own view.

### 12.7 Visibility filter

A filter bar controls what is displayed – a per-user view setting:

- By element type: Notes, Ideas, Leads, Projects, Milestones, Drawings.
- By status, assigned person, color, template, quadrant, score range.
- Hidden elements are not deleted; lines connected to hidden elements are hidden.
- Users with *manage* access can save the current filter as the map's **default
  view**.

### 12.8 Matrix maps

A matrix map places item cards **automatically** by two field values – the
prioritization matrix of doc 04 §2.6, but usable for any pair of fields.

- Configuration: x field and y field (number, rating or score), axis labels,
  min/max, split lines, labels of the four quadrants (default *Star initiatives*,
  *Quick wins*, *Difficult but impactful*, *Limited interest*), optional bubble
  size field (e.g. business value), and which items appear (types, statuses,
  templates, shared-with filter).
- Cards move live when a value changes (also when changed in the side panel).
  Cards can't be dragged; overlapping cards are nudged apart.
- Items without values are listed in a side tray *Not yet assessed*.
- The quadrant of each item is available as a badge on cards and as a list
  column/filter.
- Free elements (text, shapes, lines, images) and layers can be added on top –
  e.g. notes taken during a review-board meeting.

### 12.9 Map templates

- Any canvas can be marked as a **template** (needs *Configuration: Manage*);
  templates appear in the *Templates* tab.
- *New map from template* copies layers and drawing elements, not item cards.
- Seeded: **Impact field canvas** (2×2 of doc 04 §2.3), **Crazy 8s**, **Customer
  journey**, **Use-case funnel** (columns per idea status).

### 12.10 Collaboration

- Several users can edit the same map at the same time. While online, changes of
  others appear **live, within about a second** (§14.3).
- Two users moving different cards never conflict. If two users change the same
  property of the same element before seeing each other's change (mostly after
  working offline), it becomes a **sync conflict** that the user resolves (§14.4).
- A user who loses access to a map no longer sees it (immediately when live).

### 12.11 Export (later)

Export visible area or whole map as PNG or SVG (later: PDF).

## 13. Innovation method support

PGPM supports the use-case method of the digitalswitzerland AI playbook (doc 04)
through a **configuration package** that is imported at installation and can be
changed afterwards:

| Playbook phase | In PGPM |
|---|---|
| AI ambition | project from template *AI ambition*; ideas/projects linked *contributes to* |
| Identify (focus, ≥ 30 ideas) | ideas (status *New*), workshops on map templates *Impact field canvas* / *Crazy 8s* / *Customer journey*, section *Use case profile* |
| Assess & prioritize | idea sections *Value* (incl. impact score), *Feasibility* (12 ratings + time to proof of value → feasibility index max. 65), *Risk & responsibility*; matrix map; saved view *Review board agenda*; section *Review board decision* editable only by group *Review board* |
| Review-board go | idea status *Selected* → convert to project with template *AI use case* |
| Test & introduce | project statuses *Preparing test → Prototype / PoC → MVP / pilot → Rollout & scaling → In operation*; gates G0–G3 with the playbook checklists as DoD; *Make or buy*, *Data & platform*, *Charter*, *Adoption & impact* sections; PoC timebox 84 days |
| Learnings | notes linked *is learning of* |
| AI-system inventory | saved view on projects with *AI system* = yes and EU AI Act class |

The package also seeds the groups *Innovation team*, *Review board* and
*Compliance*, the assignment roles and the saved views. Details: doc 04 §4.2.

## 14. Offline, live sync, PWA

### 14.1 PWA

- Installable on desktop and mobile (manifest, icons, standalone display).
- The app shell is cached; the app starts without network.
- New versions download in the background; "New version available – Reload".

### 14.2 What works offline

| Works offline | Online only |
|---|---|
| Browse, search, filter all items, persons, maps (incl. matrix maps) | Login (local and Microsoft SSO), password/2FA changes |
| Create / edit / delete items, milestones, gates, DoD, links | Downloading attachments not yet cached; uploading (queued) |
| Create projects from templates, convert ideas | Changing sharing of existing items and maps |
| Assign persons, edit fields and ratings (scores update) | Administration (users, groups, rights, statuses, sections, fields, templates, packages, Microsoft settings) |
| Edit maps (all element types, layers) | Trash: restore and permanent delete |
| Share new items (create dialog) | History tab, audit log, MCP token management |
| Markdown editing in both modes, pasting images and files, diagrams | *… is editing* notice |

### 14.3 Synchronization

- **Live while online**: the app keeps a live connection to the server. Own
  changes are sent about 0.2 s after an operation completes; changes of others
  arrive within about a second, in all open tabs.
- If the live connection drops, the app reconnects automatically and in the
  meantime checks for changes every 30 s.
- **Offline**: changes are queued (survive reloads and browser restarts) and sent
  when the connection is back; then the app catches up with everything it
  missed.
- The **sync indicator** shows: live / syncing / offline, number of pending
  changes, number of conflicts, last sync time; click → sync details with *Sync
  now* and the conflict list.
- A change the server refuses (e.g. access was revoked meanwhile) is rolled back
  locally and the user is told why.
- When a user loses access to data, it is removed from the device.

### 14.4 Sync conflicts

Live sync prevents most conflicts. A conflict happens when a field (or map
property) was changed by someone else **after** the user last saw it and
**before** the user's change reached the server – typically after working
offline.

- Changes to other fields of the same object are applied normally; only the
  colliding fields are held back as a **conflict**.
- The sync indicator turns orange ("3 conflicts") and a notice appears once.
- The **conflict dialog** lists the affected objects (type icon, key, title; map
  elements grouped per map). Per object it shows each colliding field: **your
  value** and **their value** (who, when). Actions:
  - per object: **Keep mine** · **Keep theirs** · **Edit** (text fields: editor
    prefilled with both versions);
  - for all: **Keep mine for all** · **Keep theirs for all**.
- Special cases:
  - the object was **deleted** by someone else → *Restore with my changes* (needs
    *manage*) or *Discard my changes*;
  - you **deleted** an object someone else changed → *Delete anyway* or *Keep it*.
- Until resolved, the object shows the server value with a *conflict* marker;
  unresolved conflicts survive reloads; logout warns about them.
- Duplicate additions (same person assigned twice, same section added twice,
  same link twice) are merged automatically – no conflict.
- Resolutions are recorded in the History.

### 14.5 Session, logout, local data

- **Session expired while offline**: local work continues; once online the user
  signs in again and pending changes are then synced.
- **Logout** wipes the local database and cached files; with pending changes or
  unresolved conflicts the user is warned first. If a different user signs in on
  the same browser, the previous user's local data is wiped.
- Local data is stored **unencrypted** in the browser (decision §18); logging out
  on shared devices is recommended.

## 15. Users, groups, rights, login

PNMTD model, Microsoft SSO from GGITSV (doc 03 §4–§7).

### 15.1 Login

- **Sign-in page** with *Sign in with Microsoft*; the local form is collapsed
  below it when SSO is enabled.
- **Microsoft SSO** (Entra ID, single tenant):
  - accounts are recognised by their Entra object id, never by e-mail or name;
  - first sign-in creates the user (can be switched off); a clash with an existing
    local username is refused and linked by an admin;
  - **Entra app roles → groups**: membership of bound groups is set from the
    user's roles at every sign-in;
  - users without any group after sign-in go into a configurable default group;
  - the user is linked to their directory person (same object id);
  - MFA is Entra's job; logging out of PGPM doesn't log out of Microsoft.
- **Local login** (username + password, optional TOTP): initial admin, externals,
  break-glass. Lockout after 5 failures, rate limited, optionally restricted to
  configured networks.
- **Sessions**: cookie, 7 days sliding; re-authentication at least every 30 days.

### 15.2 Users and groups

- Users: username, display name, e-mail, admin flag, active flag, groups,
  Microsoft account, linked person (created automatically for local users), last
  login, 2FA state.
- Admin actions: create (temporary password, 7 days), reset password, reset 2FA,
  unlock, deactivate (ends sessions and MCP tokens), link/unlink Microsoft
  account. The last active admin can't be removed.
- Groups: name, description, members, rights, optional Entra app role.
- Seeded groups: **Members**, **Viewers**, and with the innovation package
  **Innovation team**, **Review board**, **Compliance**.

### 15.3 Rights (capabilities)

Rights say what a user may **do**; what a user may **see** is decided by sharing
(§4.2). Rights are granted to groups; a higher level includes the lower ones;
admins have every right.

| Area | Levels | Meaning |
|---|---|---|
| Notes · Ideas · Leads · Projects (each) | Create | create items of this type (and become owner); converting needs *Projects: Create* |
| Maps | Create | create maps |
| Persons | Edit | create/edit manual persons (all users can see the directory) |
| Configuration | Manage | statuses, sections, fields, templates, map templates, shared saved views, configuration packages |
| Directory | Manage | Microsoft integration settings, run directory sync |
| MCP | Use < View < Manage | Use: own tokens · View: MCP log · Manage: switch off tokens of others |
| Audit log | View | |
| System | View | sync clients, live connections, storage, version |

Users, groups and rights: admins only. Seeded: **Members** = all *Create*, *Maps:
Create*, *MCP: Use*; **Viewers** = nothing (they see what is shared with them).

### 15.4 MCP access

PGPM exposes an MCP server (`/mcp`) so AI assistants can work with the data, with
the PNMTD token model:

- Each user can switch on a **read token** and a **write token** (value shown
  once, stored as a hash).
- A token acts with the user's access (sharing) and rights, **capped**: the read
  token only reads; the write token can create and change, but never delete,
  change sharing or administer.
- Write token: needs 2FA (TOTP, or a linked Microsoft account), expires after
  12 h. Read token: expires 7 days after the user was last active. Password/2FA
  changes and deactivation end both. 3 refused calls in 24 h block a token. Rate
  limit 120 calls / 20 writes per minute.
- Every call is logged; changes appear live for all users and in the History
  with source *MCP*.
- Tools: doc 03 §7.3.

## 16. Persons directory

- **Microsoft**: a server background job reads **all enabled user accounts of the
  tenant** (members and guests; guests are marked) via Microsoft Graph –
  incrementally every 6 h, in full weekly, plus *Run now*. It creates/updates
  persons with full name, e-mail, job title, department, company, phone.
- Disabled or removed accounts become **inactive** (never deleted); hidden in
  pickers, still shown where assigned.
- **Manual**: users with *Persons: Edit* create persons for externals; every
  local user automatically gets a manual person. Directory fields of Microsoft
  persons are read-only.
- All users can see the directory. Persons page: search, filter (source, active,
  member/guest, department), detail with all assignments of the person and the
  linked user.
- *Microsoft integration* admin page: last run (time, created / updated /
  deactivated counts, error).

## 17. Scope and phases

The build order is in [05-build-plan.md](05-build-plan.md).

**Version 1** – everything in this document except the items below.

**Later**
- KPI tracking (baseline, target, measurements), funnel and ambition dashboard,
  MCP tools for assessment drafts (doc 04 F9–F11)
- Tags, export (PNG/SVG/PDF), import (CSV)
- Element grouping, alignment guides, "arrange selection" on maps
- Share target (share text/links from other apps into a new note)
- Push notifications (milestone due, assignment, gate decision)
- Presence on maps (who is viewing), live preview of others' dragging

## 18. Decisions and open questions

### 18.1 Decisions (2026-11-26)

| # | Question | Decision |
|---|---|---|
| 1 | Item visibility | Admins see everything; everything else is **object sharing per group** (an item or map can be shared with one or more groups; users are members of groups). Access levels read / edit / manage; owner has manage. |
| 2 | Multiple placements | Allowed – any number of times on the same and on different maps. |
| 3 | Lead stages | Configurable – leads use the generic per-type status lists (§5). |
| 4 | Deleted items | Soft delete with Trash and restore; no automatic purge; admins can purge. |
| 5 | Persons sync scope | Whole tenant. |
| 6 | Single tenant | Yes. |
| 7 | Dynamic fields for leads | No. (Ideas and projects: yes, recommendation R3.) |
| 8 | Languages | English only. |
| 9 | Local encryption | Not needed – everything stored unencrypted. |
| 10 | Conflicts | Live sync over a WebSocket while online prevents most; remaining conflicts are shown to the user, who decides per object or for all. |
| 11 | Innovation method | Recommendations R1–R8 of doc 04 accepted; R9 later. Seed package in English. |

### 18.2 Open questions

1. **Default sharing of new items** – proposal: preselect all groups of the
   creator with *edit* (projects: template default shares). Alternative: private
   by default.
2. **Hosting** – domain name and reverse proxy for production (needed for the
   Entra redirect URI and WebSocket configuration) – see doc 05 §2.
