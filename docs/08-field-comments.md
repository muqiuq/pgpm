# PGPM – Field comments (plan)

> Status: proposed (2026-11-27) · for release 1.1, not built yet
> Changes: [01](01-functional-overview.md) §6 (sections and fields) · [02](02-data-model.md) §7.4–§7.5 ·
> [03](03-implementation-reference.md) §7.3 (MCP), §12.7 (field widgets) · decisions to confirm: §10

## 1. Goal

Beside a field's value, a user can write **one line of text**: why a statement got a 2, where a number comes from,
what holds a date up. Examples: *Only via a CSV export, API planned for Q2*, *Source: finance report 2026*, *n/a – no
personal data involved*.

An administrator switches this on **per field**. It is **off by default** for every field, including all fields of the
built-in packages.

```
Feasibility                                                                       [ 38 / 65 ]  ⋯
DATA & INFRASTRUCTURE
We have access to the data ⓘ       [0][1][2][3][4][5][n/a]   Only via a CSV export, API planned for Q2
We have the infrastructure ⓘ       [0][1][2][3][4][5][n/a]   [+]                ← no comment yet: Add comment
We have the data quality ⓘ         [0][1][2][3][4][5][n/a]                      ← comments off for this field
Time to a verified proof of value  [ 3–6 months       ⌄ ]   Depends on the vendor's pilot
```

## 2. Behaviour

### 2.1 Switching it on (administrators)

- *Administration → Sections* → a field → *Edit* gets a **Allow a comment** switch (off by default). It can be
  switched on for every field type except **score**, because a score is computed and stores no value.
- With the switch on, an optional **Comment placeholder** appears (up to 100 characters, e.g. *Why this rating?*). It
  is shown in the empty comment input.
- In the section's field list, a field with comments on shows a small *Comment* tag.
- Configuration packages carry both settings (§3.1). The built-in packages `core` and `innovation-ai` stay unchanged,
  so comments stay off there.
- **Switching it off later keeps the comments.** They are no longer shown, no longer returned through MCP, and new
  ones are not accepted. Switching it on again shows them again. History keeps every change either way. Deleting on
  switch-off is the alternative (§10 #2); it is not recommended, because a switch in a dialog should not destroy what
  users wrote.

### 2.2 Writing and reading (users)

- **Position.** The comment sits on the field's row, after compact widgets: rating, yes/no, select, number, date and
  time, URL, e-mail, single person, single-line text. It sits below wide ones: text area, Markdown, multi-select,
  several persons. On a phone (< 600 px) it is always below the value.
- **No comment yet, field editable:** a small muted comment icon button (*Add comment to \<field\>*, tooltip *Add
  comment*). It is always visible, not only on hover, so it works on touch screens and can be found (§10 #5). A click
  opens a single-line input with the placeholder.
- **With a comment:** muted text on one line, cut with "…" when too long; the full text is in a tooltip. A click (or
  Enter) edits it in place, like every inline field. Enter saves, Esc cancels, and saving an empty text removes the
  comment.
- **Read-only** (no edit right on the item or the section): the text only, with no button and nothing at all when
  empty.
- A comment can exist **without a value**, e.g. *Waiting for the data team* before the statement is rated. Clearing the
  value keeps the comment, and the other way round.
- While someone edits a comment, others see *… is editing* on it, as on a value.

### 2.3 Rules

| Rule | |
|---|---|
| One line | No line breaks or other control characters. A single-line input can't hold any (browsers remove pasted line breaks); the server refuses them. |
| Length | 1–500 characters after trimming; an empty text means no comment. |
| Never required | A field's *Required* concerns its value only. |
| No effect on numbers | Comments never change scores, matrix positions, quadrants, timeboxes or aging. |
| Access | As the value's: whoever may see the section sees the comment, and whoever may edit the section edits it. A hidden section hides it everywhere: pull, History, MCP. |
| Offline and live | As values: written to the local database and the outbox, sent live to everyone who can see it. |
| Conflicts | Value and comment are separate keys. One user changing the rating while another changes the comment is **no conflict**. Two users changing the same comment are a conflict on the comment only, shown in the conflict dialog as *\<field\> – comment*. |
| History | Lines of its own: *\<field\> comment: "old" → "new"*, with who, when and the source (browser or MCP). |
| Conversion | *Convert to project* copies comments along with the values. |
| Templates | No default comments: template defaults set values only. |

### 2.4 Where comments appear

| Place | Version 1.1 |
|---|---|
| Item page, sections | read and edit |
| Map: an item card's quick edit in the side panel (the fields it shows, e.g. a matrix's ratings) | read and edit |
| Item lists | an optional column *\<field\> – comment* per field with comments on (hidden by default), sorted as text; the list's search finds comments |
| History tab | yes |
| Conflict dialog | yes |
| MCP | `list_config`, `get_item`, `create_*`, `update_item` (§6) |
| Global search | no (it doesn't search field values either) |
| Map card badges, matrix cards | no |

### 2.5 Not part of this change

- Comments on an item's fixed fields: title, status, a lead's value and probability, and so on. These aren't
  configurable fields (§10 #3).
- Comments on score fields.
- Multi-line or formatted comments, comment threads, mentions, an author per comment (History shows who wrote it).
- A comment required in some cases, e.g. when a statement is *n/a*. This could come later as `commentRequiredWhen`.

## 3. Data model

### 3.1 `field_definitions.config` – two keys, no new column

They are valid for every type except `score`, and validated like `subheading`, the existing key that applies to every
type:

```jsonc
{ "allowComment": true, "commentPlaceholder": "Why this rating?" }
```

| Key | Type | Rule |
|---|---|---|
| `allowComment` | bool | absent or false: off |
| `commentPlaceholder` | string | 1–100 characters, only together with `allowComment: true` |

**Why `config` and not columns:** `subheading` set the precedent of a key that applies to every type. `config` is
already synced, travels with the field in packages, appears in the importer's diff and is checked by
`check-packages.py`. There is no migration of `field_definitions` and no new column on the client's definition rows.

### 3.2 `field_values.comment` – new column

| Column | Type | Notes |
|---|---|---|
| `comment` | text null | one line, 1–500 characters; null: no comment |

- Migration: add the nullable column, with no backfill.
- A row may have `value` null and a comment. Rows with a null value exist already: clearing a value patches it to null.
- On the wire it is `comment`, insertable and **patchable** (`Patchable` becomes `{ value, comment }`), so the per-key
  conflict check treats it apart from `value`.
- Pair merge: two devices insert the same (item, field) offline and the server turns the younger insert into a patch
  (03 §11.5). That patch must carry `comment` as well as `value`. Check that the merge copies every patchable key.
- IndexedDB: no new index, because rows are stored whole. Confirm while building that no Dexie version bump is needed.

## 4. Server

- `FieldValueRules.CheckConfig`: accept `allowComment` (bool) and `commentPlaceholder` (1–100 characters, only with
  `allowComment`); refuse both on `score`.
- `FieldValuesTable`:
  - `Patchable` gets `comment`.
  - `ValidateAsync`: `comment` is null, or 1–500 characters after trimming with no control characters (including
    U+2028/U+2029).
  - A non-null comment needs the field's `allowComment`; otherwise it is refused with `comment_not_allowed`, and the
    device shows it like any other refusal. Clearing to null is always accepted.
- Visibility is row-level and unchanged. A test proves that a hidden section's comment stays out of the pull and out of
  History (the High finding fixed in [06](06-security-review.md) filters rows; prove it for the new key too).
- Packages: `PackageSchema`, `PackageChecker` and `tools/check-packages.py` learn both keys; the importer's diff shows
  them like other `config` changes.
- *Convert to project*, on the server (MCP `convert_to_project`) and on the client: copy `comment` along with `value`.
- *… is editing*: `ItemsController`'s target pattern gets `comment:<key>`, next to `field:<key>`.
- Regenerate OpenAPI if the field value row is part of a typed contract.

## 5. Client

| Where | Change |
|---|---|
| `local/rows.ts`, `domain/score.ts` (`FieldConfig`) | `FieldValueRow.comment`; `allowComment`, `commentPlaceholder` |
| `local/fields.ts` | `setFieldComment(tx, itemId, field, comment, { section, old })`: patch, or insert with `value: null`. Same checks as `setFieldValue` (edit right, `fieldReadOnly`) plus `allowComment`; trims, empty → null. |
| `components/fields/FieldComment.vue` (new) | the shown text, the *Add comment* button and the editor (built on `InlineField`, type text, 500 characters, the placeholder); presence target `comment:<key>` |
| `components/fields/FieldEditor.vue`, `components/items/SectionPanel.vue` | render `FieldComment` when `allowComment`; beside compact widgets, below wide ones and on phones |
| `views/maps/ItemQuickEdit.vue` | the comment under each field it shows (it draws its own rating buttons, not `FieldEditor`) |
| `views/items/itemList.ts` | a comment column per field with comments on (column provider, text sort); `searchText` adds comments |
| `views/items/ItemHistory.vue`, `components/sync/ConflictResolver.vue` | the label *\<field\> – comment* for the key |
| `views/admin/sections/FieldDialog.vue`, `fieldConfig.ts`, `SectionsView.vue` | the switch and the placeholder (not for score); the *Comment* tag |
| conversion (client) | copy `comment` |

Accessibility (03 §12.10): the input is labelled *\<field\> comment*, and the button is *Add comment to \<field\>*. A
shown comment's button carries its text in its name (WCAG 2.5.3). The accessibility spec covers an item with comments.

## 6. MCP

- `list_config`: each field gets `allowComment` (and `commentPlaceholder` when set).
- `get_item`: each field with comments on and a comment gets `comment`.
- `create_note/idea/lead/project`, `update_item`: a new argument **`field_comments`**, an object of field key or label →
  text or null. It is refused (`invalid_argument`) for a field without comments, and has the same rights and token caps
  as `fields`. `fields` stays as it is, so existing clients keep working (§10 #6).
- `search_items`: unchanged in 1.1. Matching comments with `query` could come later.
- The tool descriptions mention comments.

## 7. Tests

**Server (MSTest)**
- `CheckConfig`: both keys valid and invalid; refused on score.
- Sync:
  - inserting and patching a comment, and a comment without a value;
  - too long, or containing a line break: refused;
  - a field without `allowComment`: a comment is refused, null is accepted;
  - value and comment patched at once by two devices: both applied, no conflict;
  - two devices patching the comment: a conflict on `comment` only;
  - pair merge keeps the comment;
  - hidden section: the comment is not pulled and not in History;
  - `SyncSecurityTests` covers the new key for strangers.
- Conversion copies the comment. MCP: `get_item`, `update_item` and `create_idea` with `field_comments`, refusals,
  token caps. Packages: import and diff of both keys.

**Client (Vitest)**
- `setFieldComment`: insert with a null value, patch, clear, trimming, refused when off or read-only.
- `FieldComment` / `FieldEditor`: shown, add, edit, cancel, read-only; placement per type.
- `FieldDialog`: the switch and the placeholder saved in `config`; hidden for score.
- `itemList`: the comment column, its sort and the search.
- History and conflict labels.

**End-to-end (Playwright)**
- An admin turns comments on for a rating. User A comments while offline, and user B sees it live after A reconnects.
- A changes the rating while B changes the comment: no conflict.
- A read-only user sees the text without a button.
- The accessibility spec (axe, and every value shown whole) runs on an item with comments.

## 8. Documentation

- [01](01-functional-overview.md) §6: comments on fields.
- [02](02-data-model.md): §7.4 the two `config` keys, §7.5 `comment`.
- [03](03-implementation-reference.md): §7.3 MCP, §12.7 field widgets, §15 for any deviation.
- [User guide](user-guide.md) (sections and fields) and [admin guide](admin-guide.md) (field options, packages).
- Release notes 1.1.

## 9. Steps

| Step | Content | Size |
|---|---|---|
| 1 | Server: migration, validation, sync, packages, conversion, presence target; server tests | M |
| 2 | MCP: `list_config`, `get_item`, `field_comments`; tests | S |
| 3 | Client core: rows, `setFieldComment`, `FieldComment`, `FieldEditor`/`SectionPanel`, admin dialog; unit tests | M |
| 4 | Client surfaces: lists, History, conflict dialog, map quick edit, conversion | M |
| 5 | End-to-end and accessibility, documentation | S |

Order: step 1 first, because it fixes the wire shape. Steps 2 and 3 can then run in parallel; steps 4 and 5 follow.
Done when every `./run-tests.sh` group is green and the docs in §8 are updated.

## 10. Decisions to confirm

| # | Question | Recommendation |
|---|---|---|
| 1 | Maximum length | 500 characters |
| 2 | Switching comments off for a field | keep them hidden (shown again when switched back on), not delete them |
| 3 | Scope | configurable fields of any type except score; not an item's fixed fields (title, status, lead value, …) |
| 4 | Item lists | an optional column per field; no tooltip on the value cell in 1.1 |
| 5 | *Add comment* when empty | a muted icon button, always visible (touch, discoverability), not only on hover |
| 6 | MCP | a separate `field_comments` argument; `fields` unchanged |
