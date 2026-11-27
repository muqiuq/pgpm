# PGPM – User Guide

> For end users of PGPM 1.0. Administrators: see the [admin guide](admin-guide.md).
> Background: [summary](00-summary.md) · [innovation method](04-innovation-methodology.md)

PGPM is a web app for innovation work. You capture **notes**, **ideas**, **leads** and **projects**, lay them out on
**maps**, assign **persons** and share everything with **groups**. PGPM keeps a copy of everything you may see on
your device. That means it works offline, and while you are online, other people's changes appear within about a
second. This guide is a set of short "how do I…" steps. Section 12 walks through the innovation funnel of the
digitalswitzerland AI playbook, which PGPM ships as the configuration package *Innovation – AI use cases*. Your
installation may have changed or renamed parts of that configuration.

## Contents

1. [Signing in](#1-signing-in)
2. [Installing PGPM as an app](#2-installing-pgpm-as-an-app)
3. [Finding your way around](#3-finding-your-way-around)
4. [Items and lists](#4-items-and-lists)
5. [Saved views](#5-saved-views)
6. [Detail pages](#6-detail-pages)
7. [The Markdown editor](#7-the-markdown-editor)
8. [Attachments and cover images](#8-attachments-and-cover-images)
9. [Sharing](#9-sharing)
10. [Maps](#10-maps)
11. [Working offline](#11-working-offline)
12. [Walkthrough: the innovation funnel](#12-walkthrough-the-innovation-funnel)
13. [Using PGPM with an AI assistant (MCP)](#13-using-pgpm-with-an-ai-assistant-mcp)

---

## 1. Signing in

Signing in always needs a connection. After you sign in, PGPM opens **Maps**.

- **Microsoft account:** click **Sign in with Microsoft**; Microsoft's own multi-factor authentication applies. If
  PGPM has no account for you (*There is no PGPM account for your Microsoft account*), ask an administrator.
- **Username and password** (local account): unfold **Sign in with username and password** if the Microsoft button
  is shown, enter **Username** and **Password**, click **Sign in**. Repeated failures lock the account for a few
  minutes.

**First sign-in: choosing your own password.** A password an administrator set for you must be replaced before you
can continue. On **Choose a new password**, enter the **Password you signed in with**, then a **New password**
(10 to 256 characters; a passphrase works well) twice, and click **Save and continue**.

**Two-factor authentication (TOTP, local accounts only)**
- *Setting it up:* after signing in, PGPM may suggest it (*Protect your account…* → **Set up now** or **Later**).
  You can also set it up any time under user menu → **Security** → *Two-factor authentication* → **Set up**. Scan the
  QR code with an authenticator app (or type the setup key), enter the 6-digit code and click **Turn on**. If your
  installation requires 2FA, you can't skip this step.
- *Signing in:* after the password, PGPM asks for the 6-digit code (**Verify**).
- *The daily code:* PGPM asks for a code **once a day** in the dialog *Your 2FA code for today*. The page under the
  dialog stays as it is, so nothing you typed is lost.
- *Lost phone:* there are no recovery codes. An administrator turns 2FA off for you.
- *Turning it off:* **Security** → **Turn off…**, then confirm with your password and a current code.
- With a Microsoft account, the second factor is Microsoft's, not PGPM's.

**Sessions and signing out**
- A session lasts 7 days from its last use; after 30 days you sign in again. **Security** → **Sign out everywhere
  else** ends your other sessions.
- **Sign out** (user menu) **removes all PGPM data from this device**; with unsent changes or open conflicts PGPM
  asks first. It does not sign you out of Microsoft. A different user signing in on the same browser also wipes the
  previous user's data. Data on the device is unencrypted: sign out on shared devices.

## 2. Installing PGPM as an app

PGPM is a Progressive Web App: installed, it opens in its own window and starts without a network.

| Device | How |
|---|---|
| Desktop Chrome or Edge | User menu → **Install app**, or the browser's menu → *Install PGPM* |
| Safari on macOS | *File* → *Add to Dock* |
| iPhone / iPad (Safari) | **Share** → **Add to Home Screen** (the user menu's **Install app** shows these steps) |
| Android (Chrome) | User menu → **Install app**, or the browser's menu → *Install app* / *Add to Home screen* |

- **Install app** disappears from the user menu once you use PGPM as an installed app.
- **Persistent storage:** after each sign-in PGPM asks the browser to keep its data; otherwise the browser may delete
  it (with unsent changes) when space runs low.
- **Updates** download in the background: on *New version available* click **Reload**. On *PGPM is open in another
  tab with an older version*, close the other tab.

## 3. Finding your way around

- **Sidebar:** *Work* (Maps, Projects, Leads, Ideas, Notes, Trash), *Directory* (Persons) and, folded,
  *Administration* (visible only with the matching rights). The ☰ button folds the sidebar to an icon rail; on phones
  it opens the menu as an overlay. Each browser remembers which groups are folded.
- **Global search:** press **Ctrl K** (Windows, Linux) or **⌘K** (macOS), or click *Search* in the top bar. Every word
  you type must occur. Results are grouped into *Items* and *Persons*. Use ↑ / ↓ to move, **Enter** to open and
  **Esc** to close. Search works offline. On phones the top bar has no search field; use the search of each list.
- **Theme:** the sun/moon button switches between light and dark. Until you use it, PGPM follows your system setting.
- **User menu** (your initial, top right): your name, **Account**, **Security**, **Install app**, **Sign out**.
  **Account** shows your profile (username, name, e-mail, how you signed in, two-factor state) and changes a local
  password; **Security** has two-factor authentication, sessions, *Your access* (groups and rights) and the MCP
  tokens (section 13).
- **Persons** is the people directory (Microsoft accounts and externals). Everybody can search it; with
  *Persons: Edit* you can add externals with **New person**.

## 4. Items and lists

| Type | Key | What it has |
|---|---|---|
| Note | `N-12` | *Content*, **Pinned** (pinned notes sort first); no status |
| Idea | `I-42` | status, assessment sections and fields |
| Lead | `L-7` | status, Organisation, Estimated value and currency, Probability, Expected decision date, Next action and its due date, Source |
| Project | `P-0042` | status, template, Start and End date, sections, milestones and gates |

All four types also have a title, a description, a colour, a cover image, an owner, sharing, assigned persons,
attachments, links and a history. The server assigns the key. An item created offline shows `I-…` until it has synced.

**Create an item:** in the list (e.g. **Ideas**) click **New idea**, enter a **Title**, check **Status** and
**Share with** (section 9) – for projects also **Template** (or *Blank project*) and **Start date** – and click
**Create idea**. The item's page opens. This works offline too.

**Work with a list**
- **Search** (*Search ideas…*) looks in the key, title, description and field values.
- **Quick filters:** the status picker (*Any status*), **Assigned to me**, **Overdue**, **Pinned** (notes), **Mine**
  (items you own).
- **Filters** opens more: *Owner*, *Assigned person*, *Shared with*, *Colour*, *Created* and *Modified* date ranges,
  **Hide closed**, filters on fields (ideas and projects) and on a matrix map's quadrant. **Clear** resets them all.
- **Sort:** click a column header. **Ctrl-click** (⌘-click on macOS) another header to add a second sort column.
- **Columns:** show, hide and reorder columns. Key and title always show. Ideas and projects can show any field or
  score as a column, plus *Days in status* and a *Quadrant · map name* column per matrix map.
- **Bulk actions:** tick rows, then **Assign person…**, **Status…**, **Colour…**, **Delete…** or **Clear
  selection**. Rows you may not change are skipped and counted in a notice. Bulk *Add to map* is not available in
  1.0; use *Add to map…* on an item's page.
- Each list remembers its state for you. A cloud icon means *Not synced yet*, a triangle *Sync conflict*.

**Statuses and "Overdue".** Each status of an idea, lead or project is *open*, *active* or *closed* (**Hide
closed** uses this). The clock badge **Overdue** appears when an item stays in a status longer than allowed (e.g.
*Prototype / PoC*: 84 days), a lead's next action is past due, or a timebox date field has passed – never in a
closed status. Hover over the badge to see why.

**Delete and restore:** on the item's page **⋯** → **Delete idea…** (needs *manage*) moves it with everything
that belongs to it, including its map cards, to the **Trash**. **Work → Trash** (online only) lists deleted items
and maps you may manage; **Restore** brings one back complete. Only administrators can **Delete permanently**.

## 5. Saved views

A saved view stores a list's search, filters, sort and columns under a name.

- The **view menu** sits at the right of the filter bar, next to **Columns**. It lists *All ideas* (no view) and the
  saved views. Shared views are marked *(shared)*. When you change the current view, its name shows *(modified)*.
- **Save as new view…**: name it and, if you have *Configuration: Manage*, tick **Shared with everybody**.
- **Update "…"**, **Rename…** and **Delete…** work on your own personal views. Shared views can be changed only by
  users with *Configuration: Manage*.

Views that come with the built-in packages:

| List | View | Shows |
|---|---|---|
| Notes | *Learnings* | notes linked as *is learning of* a project or idea |
| Ideas | *Review board agenda* | ideas in *Ready for review*, highest impact score first |
| Projects | *AI system inventory* | projects with *AI system* = yes, with EU AI Act risk class, data classification, personal data |
| Projects | *Running experiments* | projects in *Prototype / PoC*, longest in that status first, with end of timebox |
| Gate decisions | *Gate decisions* | decided gates of all projects, newest first |

## 6. Detail pages

**Header:** type icon, key, title and status badge (with *Overdue*). Tags may follow: *Pinned*, *Converted to P-…*,
*Read only*, *Conflict* (click to resolve it), *Not synced yet*, and *"Name" is editing* when someone else is editing
the text. On the right are the actions: the pin (notes), **Convert to project** (ideas and leads),
**Share · groups** (or *Share · Private*) and **⋯** with **Delete…**. The owner is shown under *Details* and in the
Share dialog.

**Tabs** (each has its own link, e.g. `/projects/<id>/milestones`):

| Tab | Notes | Ideas | Leads | Projects |
|---|---|---|---|---|
| Overview | yes | yes | yes | yes |
| Milestones | – | – | – | yes |
| Links | yes | yes | yes | yes |
| Maps | yes | yes | yes | yes |
| History | yes | yes | yes | yes |

**Overview.** On the left: the *Description* (*Content* for notes) with **Edit**, the sections (ideas and projects)
and the *Attachments*. On the right: *Details* (Title, Status, Colour, the type's own fields, Template, Origin,
Converted to, Key, Owner, Shared with, Created, Modified), the *Cover image* and the *Persons*.

**Inline editing.** Click a value (or its pencil) to edit it, then click **Save** or **Discard**. **Enter** saves
(**Ctrl+Enter** in multi-line fields) and **Esc** discards. A saved value is stored on the device at once and synced.
- **Colour:** **Change** offers 12 preset colours or a custom hex value.
- **Ratings:** click a value from 0 to 5, or **n/a**. Clicking the chosen value again clears it.
- **Scores** are calculated and read-only, e.g. *58 / 65*. They show *incomplete* while an input is missing.
- **Sections:** **Add section** adds one; a section's **⋯** → **Remove *name*…** hides it (values are kept). A lock
  means only some groups may edit it. Fields you may not see never reach your device.
- With *read* access everything is read-only and the header shows *Read only*.

**Persons:** **Assign person** → type a name or e-mail in *Person…* → optionally a role (free text with
suggestions: *Responsible*, *Contact*, *Contributor*; with the innovation package also *Innovation team*, *Review
board*, *Domain expert*, *Delivery team*, *Sponsor*, *Data owner*, *AI champion*) → **Assign**. A person is assigned
once per item; assigning again changes the role. **Assigned to me** uses the person linked to your account.

**Links:** on the **Links** tab choose a relation (e.g. *This idea contributes to …*), click **Add link…** and pick
the item. Links show on both sides as pairs: *contributes to* / *contributed to by*, *is learning of* / *has
learning*, *depends on* / *is needed by*, *related to*. Items not shared with you show as *Restricted*. On an
ambition (section 12) the contributing items are sorted by status – a simple funnel.

**Maps tab:** the canvas maps the item is on (with the number of cards) and the matrix maps that show it, with its
quadrant or *Not yet assessed*. Each entry opens the map centred on the item. **Add to map…** puts a card in the middle
of a canvas map you can edit.

**History tab:** every change with when, who and what. Changes made through an assistant carry the tag *mcp*.
The history is on the server, so it needs a connection. Offline, the tab shows only your changes that haven't synced yet.

**Convert to project** (ideas and leads; needs *edit* and the right to create projects): enter **Project title**,
**Template**, **Start date** and **Status**. The dialog lists what the project gets – title, description, colour,
cover, attachments, sharing, persons, links, the field values that apply to projects, the template's sections,
milestones and gates, and a link back. **Convert** keeps the idea, moves it to *Selected* (a lead to *Won*) and shows
*Converted to P-…* in its header. Works offline.

**Milestones tab** (projects)
- **New milestone**: choose **Kind** (milestone or gate), **Title** and **Due date**. Templates bring milestones
  along, due relative to the project's start date.
- A mini timeline shows milestones as dots and gates as diamonds. Each card has a title, a status (*Planned*,
  *In progress*, *Done*, *Cancelled*), a due date (overdue in red), a colour, responsible persons, a description
  and the **Definition of Done**.
- *Definition of Done:* tick a criterion when it's done, or mark it **n/a** (PGPM then asks *Why doesn't it apply?*).
  Add evidence with the note icon (*a link or short text*). Progress counts done ÷ (all − n/a).
- *Gates* also have a **Decision**: *Pending*, *Go*, *No-go*, *Iterate*, *Pivot* or *Park*, a **Decision note** and
  **Record decision**. After *Go* or *No-go*, PGPM may offer **Move project to …** (the template's suggestion) or
  **Keep the status**.
- Setting *Done* while the DoD is incomplete (or a gate has no decision) asks first (**Mark done anyway**). The
  override is recorded in the History.
- **Gate decisions** (button on the Projects list, link on this tab) lists the gates of all projects you can read.

## 7. The Markdown editor

Descriptions, note content and milestone descriptions are Markdown. Click **Edit** to open them on a full-window
editing page.

- **Modes:** the switch **Visual | Markdown** at the top. *Visual* is a WYSIWYG editor with a toolbar (bold, italic,
  strikethrough, code, lists, task list, link, image, table, **Attach file**, **Link to an item…**, code block,
  quote, horizontal rule), a toolbar on selected text, `/` commands and a drag handle for blocks. *Markdown* is a
  source editor with **Preview** beside it. PGPM remembers the last mode per browser.
- **Switching** never changes your text by itself. If the visual editor would rewrite lines (e.g. `*` lists become
  `-`), PGPM asks: **Show** the difference, **Stay**, or **Switch anyway**.
- On phones, the Markdown mode is a plain text area with **Preview**.
- **Saving:** **Save** or **Ctrl+S** / **⌘S**. **Cancel** or back leaves; with unsaved changes PGPM asks first.
- **Images and files:** paste or drop an image and it appears at the cursor. Paste or drop any other file and it
  becomes an attachment of the item with a chip in the text. Large images are shrunk (see section 8);
  **Attach the original instead** keeps the original as an attachment. Offline, the file shows at once and uploads
  later.
- **Diagrams:** a code block that starts with ` ```mermaid ` is drawn as a diagram (flowcharts, Gantt charts,
  timelines, quadrant charts, pie charts, mind maps and more). In the rendered text, the diagram's menu has **Show
  source**, **Copy source** and **Download SVG**.
- **Link to an item…** searches your items and inserts a link such as `[P-0042 Website relaunch](/projects/…)`,
  which opens inside PGPM. External links open in a new tab.
- **Drafts** survive reloads and crashes: *You have unsaved changes from 10:42* – **Restore** or **Discard**.
- **Someone else editing** (online): *Name is editing this (since 10:42)* in the header; nothing is blocked.
- **Changed in between:** if someone saved the same text while you were editing, saving opens a comparison
  (**Their changes** / **My changes**) with **Keep editing**, **Take theirs** and **Save mine anyway**. Their
  version stays in the History.

## 8. Attachments and cover images

- **Attachments** (Overview tab): **Add files**, drop files on the panel or paste them, several at once. Click a
  name to rename it; the icons **Preview** (images, PDF), **Download** or **Delete** it.
- **Cover image:** **Add cover image** (or drop an image), **Replace**, **Remove**. It shows on the page and on map
  cards.

**Limits**
- Attachments: up to **25 MB** each. Images (covers, map images, pasted images) must be PNG, JPEG, GIF or WebP and are
  prepared to at most **2 MB**: photo metadata is removed, and larger images are recompressed and scaled down.
- Executable types are blocked, also when hidden in a double extension like `invoice.exe.pdf` (`exe`, `msi`, `dll`,
  `scr`, `com`, `bat`, `cmd`, `ps1`, `vbs`, `js`, `jar`, `hta`, `lnk`). Your installation may have other limits.

**Offline and upload states**
- Uploads work offline: the file stays on the device as **Pending upload** until sent; interrupted uploads resume.
- **Failed** with **Retry**: the server refused the file (e.g. type or size); the reason is shown.
- **Not uploaded yet**: added on another device that hasn't uploaded it yet.
- **Not available offline**: not cached on this device. Files you download are kept for offline use (up to 200 MB,
  oldest removed first); covers and map images are always downloaded.

## 9. Sharing

Access is decided per item and per map:

| Access | Allows |
|---|---|
| **Read** | see the item and everything below it; place it on maps you can edit |
| **Edit** | also change fields and status, assign persons, attach files, edit milestones, gates and DoD, add links |
| **Manage** | also change sharing, transfer ownership, delete and restore |

- The **owner** (who created it) and administrators always have *manage*.
- Others get access through **groups**. If you are in several groups, the highest level counts. An item without
  shares is **private**: only the owner and administrators see it.
- **Default shares:** the *Share with* list in the create dialog is preselected with your groups at *Edit*. A project
  from a template takes the template's shares instead. With the built-in packages these are:
  *General project* → Members (edit); *AI use case* → Innovation team, Review board, Compliance (edit);
  *AI ambition* → Innovation team, Review board (edit), Members (read). Adjust them in the dialog (works offline);
  *No access* leaves a group out.
- **Change sharing later:** **Share · …** in the header opens the Share dialog. There you set each group's level and
  can pick a new **Owner**. Changing the sharing needs *manage* access and a connection. With less access the dialog
  is read-only.
- **Sections** can be restricted further. In the innovation package, only the group *Review board* edits
  *Review board decision*, and only *Compliance* edits *Compliance approval*.
- **When access is removed**, the item (or map) and everything below it leaves your device: immediately while live,
  otherwise at the next sync. A notice *Access removed* tells you. On maps, a card of an item you can't read shows as a
  grey placeholder without a title.

## 10. Maps

**Maps** (home page) has two tabs: **Maps** and **Templates**. Each map shows a preview, its kind, owner and the
last change.

**Create a map** (needs *Maps: Create*): **New map** → **Canvas map** (a free, infinite board), **Matrix map**
(items placed by two values: **From a preset** such as *Prioritization matrix*, or **Own axes**) or **From
template…** (copies a template's layers and drawings, not its item cards). Give it a **Name**, check **Share with**,
click **Create map**. Each map's menu offers **Open**, **Rename…**, **Duplicate…**, **Share…**, **Delete…** (to the
Trash) and, on templates, **New map from this template**; with *Configuration: Manage* also **Mark as template**.
Built-in workshop templates: *Impact field canvas* (the playbook's 2×2), *Crazy 8s*, *Customer journey*,
*Use-case funnel*.

**The canvas.** There is no save button: PGPM saves every operation at once. Others see the change live.
- **Toolbar** (left): *Select*, *Hand (pan)*, *Note*, *Idea*, *Lead*, *Project*, *Milestone or gate*,
  *Milestone timeline*, *Rectangle*, *Ellipse*, *Line / arrow*, *Text*, *Image*, then **Undo**, **Redo**, **Layers**
  and **Map properties** (cog).
- **Add items:** pick an item tool and click on the canvas. **Create new** asks for a title, status and sharing.
  **Add existing** searches your items and marks those already *on the map · n×*. An item can be on a map any number
  of times: every card is the same item. Or open **Library** (top bar), search and filter by type and status, and
  drag an item onto the map (or click **+**).
- **Milestones and timelines:** *Milestone or gate* asks for a project, then one of its milestones or gates.
  *Milestone timeline* asks for a project and draws all its milestones (gates as diamonds). The timeline updates by
  itself; its panel sets orientation, spacing, titles and dates.
- **Shapes and text:** drag with *Rectangle* or *Ellipse* (or click for a default size). With *Text*, click and type;
  **Ctrl+Enter** or a click elsewhere keeps the text, **Esc** drops it. Double-click a text or shape to edit its text.
- **Lines and arrows:** drag with *Line / arrow*. Start and end on elements to connect them; connected lines follow
  their elements. Drag a line's end onto an element to connect it, or anywhere to free it. The style panel sets
  *Straight* or *Elbow*, arrow heads, colour, width and dash.
- **Images:** the *Image* tool opens a file chooser. You can also paste an image.
- **Select:** click; Shift-, Ctrl- or ⌘-click adds to the selection. Shift/Ctrl/⌘ + drag on the background draws a
  selection box. A drag on the background pans.
- **Pan and zoom:** drag the background, hold Space and drag, use the middle mouse button, or two fingers. The mouse
  wheel or a pinch zooms. Bottom right: **Grid**, **Snap to grid**, **Minimap**, zoom out / in, the zoom level (click
  for 100 %), **Zoom to fit** and **Zoom to selection**.
- **Remove from the map** (side panel, or Delete) removes only the card. The item itself stays.
- **Show links** (filter bar) draws the item links between cards as dashed lines.

**Keyboard shortcuts** (Ctrl on Windows and Linux, ⌘ on macOS)

| Keys | Action |
|---|---|
| Delete / Backspace | remove the selection from the map |
| Ctrl Z · Ctrl Shift Z (or Ctrl Y) | undo · redo (this session's operations) |
| Ctrl C · Ctrl X · Ctrl V · Ctrl D | copy · cut · paste · duplicate (also across maps; a pasted card is another card of the same item) |
| Ctrl A | select all |
| Ctrl ] · Ctrl Shift ] | bring forward · bring to front |
| Ctrl [ · Ctrl Shift [ | send backward · send to back |
| Arrow keys (Shift: larger step) | nudge the selection |
| Space + drag | pan |
| F · + · − | zoom to fit · zoom in · zoom out |
| Esc | cancel the drag or drawing, back to *Select*, or close the side panel |
| Shift while resizing | keep the aspect ratio |

**Layers.** **Layers** in the toolbar opens the panel, top layer first. With **+** you add a layer. A click on a
layer makes it the one new elements go to; a double-click renames it. The panel also moves layers up and down,
**shows or hides** them, **locks** them and deletes them. When you delete a layer, PGPM asks whether its elements
**Move them down** or **Delete them**. Visibility and lock apply to everybody. With read access, the eye hides a
layer only for you. In the side panel, **Move to layer…** moves selected elements, and the order buttons sort them
within their layer.

**Side panel.** Click an element and a panel opens on the right. The map stays usable, and clicking another element
swaps the content.
- *Item card:* **Open details**, **Expand card** / **Compact card**, order buttons, **Duplicate**,
  **Remove from the map**; a *Quick edit* of title, status, colour, image, persons and key values (ratings, numbers,
  selects; scores read-only); below it a summary with description, milestones and links, and *This item appears n× on
  this map* with **Select all**. To edit the description or attachments, use **Open details**.
- *Milestone or gate:* status, Definition of Done and gate decision.
- *Shapes, lines, text, images:* style (fill, stroke, text, alignment).
- *Nothing selected* (or the cog): map properties – name, description, background, grid, snap, grid size,
  *Badges on cards* – and the map's sharing with **Share…**.

**Filter bar** (top): element *Types* (Notes, Ideas, Leads, Projects, Milestones, Drawings), *Status*, *Persons*,
*Colour*, *Quadrant*, a *Score* range, **Show links**, **Reset**. This filter is your own view setting. Managers can
**Save as default** for everybody.

**Deep links.** A link of the form `…/maps/<map id>?focus=item:<item id>` centres the map on that item's cards and
selects them. The Maps tab of an item uses such links.

**Matrix maps**
- Cards are placed automatically by two values (number, rating or score). You can't drag them. Change a value in the
  side panel and the card moves, live for everybody.
- Axes, split lines and four quadrant labels form the background, by default *Star initiatives*, *Quick wins*,
  *Difficult but impactful*, *Limited interest*. Managers change axes, ranges, labels, bubble size and which items
  appear with **Matrix settings**.
- Items that lack a value wait in the tray **Not yet assessed**. Click one to fill in its values.
- You can add rectangles, ellipses, lines, text and images on top, e.g. notes from a review meeting.

**Read-only maps** show *Read only*: pan, zoom, select and the side panel work, changes don't. Not available in 1.0:
exporting a map as an image, and a template filter in the map's filter bar.

## 11. Working offline

PGPM keeps all data you may see in the browser, so most things work without a connection:

| Works offline | Needs a connection |
|---|---|
| Browsing, searching and filtering items, persons and maps (also matrix maps) | Signing in, changing password or 2FA |
| Creating, editing and deleting items, milestones, gates, DoD and links | Changing the sharing of existing items and maps |
| Projects from templates, converting ideas and leads | Trash (restore, permanent delete) |
| Assigning persons, editing fields and ratings (scores update) | History tab |
| Editing maps, all element types and layers | Downloading files not cached yet (uploads are queued) |
| Sharing new items in the create dialog; Markdown editing with files and diagrams | *… is editing* notice; MCP token settings |

**The sync indicator** (top bar) shows the connection: **Live** (changes arrive within about a second), **Syncing**,
**Online** (connected but without the live channel; PGPM checks for changes regularly), **Offline**, **Connecting**
or **Signed out**. Next to it you see the number of changes not sent yet and, in orange, the number of conflicts.
Click it to open **Synchronisation**:
- **Status** tab: the connection, *Last sync*, whether this tab runs the sync, and the *Pending* changes
  (*Everything is sent.* when there are none). **Sync now** syncs immediately.
- **Conflicts** tab: the conflicts to resolve.

**Conflicts.** Live sync prevents most conflicts. A conflict happens when someone else changed the same field before
your change reached the server, typically after you worked offline. Other fields are applied normally. PGPM shows
*1 sync conflict* once, and marks the item in lists, on its page and at the field. For each object the
**Conflicts** tab shows *Field*, *Yours* and *Theirs*:
- **Keep mine**, **Keep theirs**, or **Edit** (start from both versions, then **Save edit**);
- for everything at once: **Keep mine for all** or **Keep theirs for all**;
- if someone else deleted the object: **Discard my changes** or **Restore with my changes** (needs *manage* and a
  connection); if you deleted something another person changed: **Keep it** or **Delete anyway**.

Conflicts survive reloads until you decide. Duplicate additions (the same person, section or link added twice) are
merged without a conflict. If the server refuses a change (for example because your access was removed meanwhile),
PGPM undoes it on your device and tells you why (*Change undone*).

**Good to know**
- Offline changes survive reloads and browser restarts. If your session expires offline, keep working and sign in
  again once online; then PGPM sends them.
- Safari on iPhone and iPad may delete a website's data after a while without use. Install PGPM on the Home Screen
  (section 2) and go online regularly.
- **Sign out** wipes the device's data; PGPM warns about unsent changes and open conflicts first.

## 12. Walkthrough: the innovation funnel

This walkthrough follows the use-case method of the digitalswitzerland AI playbook as configured by the package
*Innovation – AI use cases* ([method in detail](04-innovation-methodology.md)). Names below are those of the package.
Your installation may have adapted them. The package adds the groups *Innovation team*, *Review board* and
*Compliance*.

**Step 1 – Set the ambition.** **Projects** → **New project** → Template **AI ambition**. Fill in the section
*Ambition*: *Period from / to*, *Qualitative goals*, *Guiding principles* (prefilled with the playbook's five),
*Constraints from the strategy*, *Quantitative targets*. Work through the milestones *Ambition defined*, *Ideation
round* and *Portfolio review*; adapt or delete the four example targets (e.g. *5 use cases tested*).

**Step 2 – Collect at least 30 ideas.** In a workshop, **Maps** → **New map** → **From template…** → *Impact field
canvas*, *Crazy 8s* or *Customer journey*; the *Idea* tool → **Create new** creates ideas where you click. Or
**Ideas** → **New idea**. Ideas start in *New*. Describe each in *Use case profile*: *Addressed problem*, *Required
data basis*, *Expected output*, *Impact field*, *Transformation level*, *Source of the idea*, *Business unit*. Link
each to the ambition (**Links** → *This idea contributes to …*); the ambition's Links tab then shows the funnel.
The *Ideation round* DoD includes *At least 30 ideas generated*.

**Step 3 – Assess** (statuses *Elaborating* → *Assessed*)
- *Value*: *Contribution to the AI ambition*, *Strategic advantages*, *Estimated business value*, *Assumptions
  behind the value*, the ratings *Reach* and *Strategic fit*, and *Value band* → **Impact score** (0–100).
- *Feasibility*: 12 ratings from 0 to 5, or *n/a*, in four groups:
  - *Data & infrastructure:* access to the data, infrastructure, data quality.
  - *Algorithms & solution:* we know how to solve it, a similar solution was built, suitable technologies.
  - *Processes & systems:* no new processes, processes easy to adapt, no substantial changes.
  - *Know-how:* technical, domain, general AI understanding.

  Add *Time to a verified proof of value* (< 3 months = 5 … > 12 months = 1). Together they give the
  **Feasibility index**, e.g. **58 / 65**. Answers marked *n/a* are left out and the result is scaled to 65. The
  score shows *incomplete* until every input is answered.
- *Risk & responsibility*: *Exceptional risks* (Ethical, Cyber security, Legal, Human in the loop), *Risk analysis*,
  **EU AI Act risk class** (*Minimal*, *Limited*, *High*, *Unacceptable*, *Not assessed*), *AI system*, *Data
  classification*, *Personal data*, *External models allowed*, *Human oversight*.
- *Compliance approval*: *Compliance approved*. Only the group Compliance can edit it.

**Step 4 – Prioritize.** **Maps** → **New map** → **Matrix map** → **From a preset** → **Prioritization matrix**.
Horizontal: *Ease of implementation* = feasibility index (0–65, split at 32.5); vertical: *Business impact* = impact
score (0–100, split at 50); bubble size = estimated business value. Quadrants: *Star initiatives* (top right),
*Quick wins* (bottom right), *Difficult but impactful* (top left), *Limited interest* (bottom left); ideas missing
values wait under *Not yet assessed*. Adjust ratings in the side panel and the cards move; the Ideas list can show
and filter by quadrant. The playbook starts with quick wins, then star initiatives, and keeps the difficult but
impactful ones in the pipeline.

**Step 5 – Review board.** Set mature ideas to *Ready for review*; the board works from the view **Review board
agenda**. Board members fill *Review board decision* – *Decision* (*Go*, *No-go*, *Park*, *Rework*), *Decided on*,
*Feedback to the submitter*, *Approved funding*, *Conditions* – which only the group Review board can edit. Then
set the idea to *Selected*, *Watch* or *Rejected*.

**Step 6 – Convert to an AI use case.** On a selected idea: **Convert to project** → Template **AI use case** →
start date → **Convert**. The project starts in *Preparing test*, keeps the assessment and adds *Data & platform*,
*Make or buy*, *Charter* and *Adoption & impact* (*AI system* = yes, *Make or buy* = *Undecided*). Assign the key
persons with roles such as *Sponsor*, *Domain expert*, *Delivery team*, *Data owner*, *AI champion*.

**Step 7 – Through the gates.** The template adds milestones and gates relative to the start date. PGPM shows the
gate titles; the playbook numbers them G0–G3:

| Gate | Due | On *Go* PGPM offers | On *No-go* |
|---|---|---|---|
| G0 *Ready for test* | start | *Prototype / PoC* | *Stopped* |
| G1 *PoC decision* (after *Prototype built*, +4 weeks) | +8 weeks | *MVP / pilot* | *Stopped* |
| G2 *Scale decision* (after *MVP live with pilot group*, +16 weeks) | +20 weeks | *Rollout & scaling* | *Stopped* |
| G3 *Impact review* (after *Rolled out*, +28 weeks) | +40 weeks | *In operation* | *Retired* |

For each gate:
1. Work through its **Definition of Done**, the playbook checklist (e.g. charter written, make or buy decided,
   EU AI Act risk class set, responsible-AI and security checks). Mark each criterion done or *n/a* with a reason,
   and add evidence.
2. Record the **Decision** (*Go*, *No-go*, *Iterate*, *Pivot* or *Park*) with a note → **Record decision**. After *Go* or
   *No-go*, confirm **Move project to …**.
3. Set the gate to *Done*. Keep experiments short: *Prototype / PoC* turns *Overdue* after 84 days, and so does a passed
   *End of timebox* (section *Charter*). The view **Running experiments** lists them, and **Gate decisions** keeps
   the stop / go / scale record.

**Step 8 – Operation.** After *Impact review* the project is *In operation*. In *Adoption & impact*, *Expected
productivity gain* × *Adoption* gives *Effective gain (%)* (30 % × 50 % = 15 %). Switched off: *Retired*.

**Learnings and inventory**
- Record a lesson as a note: **Links** → *This note is learning of …* → pick the project or idea. **Notes** → view
  **Learnings** collects them.
- **Projects** → view **AI system inventory** lists every project with *AI system* = yes, sorted by EU AI Act risk
  class. This list is step 1 of the EU AI Act.

## 13. Using PGPM with an AI assistant (MCP)

PGPM has an MCP server, so an AI assistant (e.g. in a desktop chat app or a code editor) can work with your data. The
assistant uses a personal token and sees and changes only what you may, with fewer rights.

**Switch a token on**
1. User menu → **Security** → card **MCP tokens** *for AI assistants*.
2. Switch on the **Read token** or **Write token**. *Your new token* shows the value **once**: store it in your
   assistant, then **I've stored it**. Lost it? **Regenerate…** (the old value stops working at once).
3. **How to connect an assistant** shows the address, `<your PGPM address>/mcp`, and a configuration example; the
   token goes in `Authorization: Bearer pgpm_…`. The token dialog also has a ready-made command for Claude Code.
   More client examples: [admin guide](admin-guide.md).

**The two tokens**

| | Read token | Write token |
|---|---|---|
| Can | read | read, create and edit; never delete, share or administer |
| Lasts (by default) | 7 days after you were last online in PGPM | 12 hours after switching it on (**Extend to 12 hours**) |
| Needs | the right *MCP: Use* | *MCP: Use* and two-factor authentication or a linked Microsoft account |

Changing your password or 2FA, or **Sign out everywhere else**, switches both tokens off. A token is blocked after
3 refused calls within 24 hours. The card shows each token's state, expiry and last use. Every call is logged.

**What an assistant can do.** Your assistant lists the tools your token may use, each with a description; admins see
the whole catalogue under *Administration → MCP → Tools*. With the read token an assistant searches and reads items and
persons and lists the configuration; with the write token it also creates and
updates items within your rights – the write token is capped: it never deletes, shares, changes owners or administers.
Fields you may not see are neither returned nor accepted. Ask the assistant for things like "find my notes about the customer
workshop" or "create a note with the learnings of this workshop, shared with Members".

Changes made by an assistant reach everybody live, like your own, and the item's **History** shows them with the source
*mcp*.
