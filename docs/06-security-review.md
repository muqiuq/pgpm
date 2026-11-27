# PGPM – Security Review (release 1.0)

> Status: v1.0 (2026-11-27) · phase 7 hardening, track H1
> Spec: [03 Implementation](03-implementation-reference.md) §3–§7, §10, §11 · [05 Build plan](05-build-plan.md) §3 phase 7

## 1. Method

- **Code review** of every synced table descriptor (`SyncTables.All`, 26 tables), every v1 controller (24, the System
  page's included), the live channel, the files pipeline, MCP (token handler, caps, every tool), the host's headers and
  cookies, the audit and the logging calls (`grep Log*(`), the SSO handshake and the local login.
- **Threat model**: an authenticated user without a share (a *stranger*), a colleague with *read* where *edit* is needed,
  a colleague outside a section's groups, a cross-site page (CSRF, WebSocket hijacking, DNS rebinding), an MCP token
  (read and write) acting for its user, a TLS-terminating reverse proxy in front of the container. Out of scope: the host
  OS, PostgreSQL hardening, the reverse proxy's own configuration (P2), Microsoft Entra.
- **Negative tests** for what had none (§4), the fixes each with a test that failed before it (verified for S-01 and S-07 by
  reverting the fix).
- Severity: **High** – data a user must not see or change is reachable with normal use; **Medium** – reachable under a
  common deployment or with a token; **Low** – needs an unusual setup, an id nobody knows, or limits availability only;
  **Info** – by design or accepted, written down.

## 2. Findings

| ID | Area | Finding | Severity | Fix | Test |
|---|---|---|---|---|---|
| S-01 | REST History | `GET /api/v1/history` returned every change-log entry of the item, including the `field_values` (values) and `item_sections` of sections only other groups may view – a colleague reading the idea saw e.g. the compliance section's notes in the History tab | **High** | **fixed**: the History leaves out entries of field values and item sections whose field/section the user can't view (the pull's predicates, `AccessService.VisibleFields/VisibleSections`) | `SyncSecurityTests.AHiddenSectionNeverReachesOthersByPullOrHistory` |
| S-02 | MCP | `AccessService.MapAccessAsync` ignored `Viewer.MaxAccess`: an owner's or admin's read token had *manage* on maps | **Medium** | **fixed** (MCP track): capped like items | `McpMapToolTests` (read token gets read, write token edit; read-shared map refuses `add_item_to_map`) |
| S-03 | Cookies | All cookies were `Secure` only when the request was https. The deployment (container behind a TLS proxy, `Proxy` commented out in `deploy/.env.example`) sees plain http: the session cookie went out **without** `Secure` | **Medium** | **fixed**: with an https `PublicUrl` the session, `pgpm.2fa`, `XSRF-TOKEN` and the SSO correlation/nonce cookies are always `Secure` (`AuthSetup.SecureCookies`); the antiforgery cookie token stays as the request came (the framework refuses `Always` on http; it is useless without the session) – docs/03 §15 #85 | `SecurityTests.AnHttpsPublicUrlMakesEveryCookieSecureAndSendsHsts` |
| S-04 | Headers | No `Strict-Transport-Security` | Low | **fixed**: `max-age=31536000` on https requests and with an https `PublicUrl` | 〃 |
| S-05 | Trash | `POST /trash/{batch}/restore` finds the root without the pull's filter and asks the table's delete rule; `user_settings` allowed every delete (its rows were only ever reached through the own-rows filter): anybody knowing a batch id restored another user's deleted setting | Low | **fixed**: `UserSettingsTable` deletes (and so restores) own rows only | `SyncSecurityTests.TheTrashHistoryAndFilesOfForeignItemsAreRefused` |
| S-06 | Files | Uploads ignored `Files:MinFreeMb` (only `FileCheckTask` warned): an authenticated user could fill the disk the database shares | Low | **fixed**: chunk 0 is 507 `file_storage_full` when the upload would leave less (before it counts against `UploadsPerHour`) – §15 #86 | `SecurityTests.AnUploadThatWouldEatIntoTheDisksReserveIsRefused` |
| S-07 | Files | An image-purpose row (cover, map image, embedded) shared the stored bytes of any readable *attachment* whose client-declared type was `image/*` – bytes never checked by `ImageCheck` nor stripped of metadata, then served inline as an image | Low | **fixed**: image purposes share bytes only with image-purpose files (checked and stripped at their commit) – §15 #87 | `SyncSecurityTests.AnAttachmentNeverLendsItsBytesToAnImage` |
| S-08 | MCP | A write token could grant *manage* through a template's default shares or a conversion's copied shares | Low | **fixed** (MCP track): capped at edit (§15 #71) | `McpItemToolTests`, `McpConvertToolTests` |
| S-09 | MCP | `already_converted` named the key of a project the user can't read | Low | **fixed** (MCP track): a reference only | `McpConvertToolTests` |
| S-10 | Deployment | Login rate limit (10/min) and the audit's address are per client address; behind a proxy without `Proxy` every user is the proxy: one shared bucket (sign-in denial for all after 10 tries) and useless addresses in the audit | **Medium** | **open – deploy track (H3)**: make `Proxy=docker` the default in `deploy/.env.example` and say so in the admin guide; S-03 no longer depends on it | – |
| S-11 | Live | A live connection keeps the handshake's rights; a rights change sends `session/rights_changed` but the connection's computed `access` of map templates (*Configuration: Manage*) stays stale until it reconnects | Low | **fixed**: `LiveConnection.CheckSessionAsync` takes the new rights with the new groups | `LiveSyncTests.ARightsChangeReachesTheConnectionsComputedAccess` (fails without the fix) |
| S-12 | Presence | `POST /api/v1/editing` accepts any `field:<key>` matching the pattern; entries are per (item, target, user) without a cap: one user can grow the in-memory registry (90 s expiry) | Low | **fixed**: at most 50 open editors per user (`EditingRegistry.MaxPerUser`); a new one ends the user's least recently seen | `EditingRegistryTests` |
| S-13 | Limits | No global cap on live connections, no per-user rate limit on `/sync`, `/history`, `/trash` (only login, uploads per hour, MCP) | Low | accepted: authenticated users only; 10 sockets per user, 8 MB / 200 mutations per push, page caps; documented for the proxy (connection limits there) | – |
| S-14 | Sync | `sync_clients` is upserted by the device's id: another user sending that id takes the row over (the owner's device resets) | Info | accepted: UUID v7 client ids are never shown to other users | – |
| S-15 | REST | History, sharing and presence answer 403 for an existing item one can't read, 404 for a missing id: an existence oracle | Info | accepted: ids are unguessable v7 UUIDs; files and the sync answer 404 / `missing_access` without `current` | `SyncAccessTests` |
| S-16 | Sync | Ids of unreadable objects reach readers of the referring row: `item_links.toItemId`, a readable map's item cards (`{id, access: none}` in MCP), `template_sections` of a hidden section | Info | accepted: an id, never content | `McpMapToolTests` |
| S-17 | Login | After 5 failures the answer says *locked out*, which tells that the account exists | Info | accepted (PNMTD parity); rate-limited, the lockout grows to 60 min | `LoginTests` |
| S-18 | Logs | A generated first admin password is logged once at the first start | Info | accepted: must be changed at the first sign-in; set `Auth:InitialAdmin:Password` to avoid it | `AdministrationTests` |
| S-19 | MCP | *MCP: View* shows every user's MCP calls with redacted arguments, results and their History rows – content beyond the viewer's own shares | Info | by design (an audit right, docs/03 §6.2); grant it to auditors only (admin guide) | – |
| S-20 | Files | A collaborator with edit access restarts another's unfinished upload (chunk 0) | Info | accepted: they can delete the file anyway; the uploader's client starts over | – |
| S-21 | CSP | `style-src 'unsafe-inline'` | Info | accepted: PrimeVue's styled mode; scripts stay `'self'` only | `HostingTests` |
| S-22 | Host | `AllowedHosts=*`: without `PublicUrl`, MCP result links and the CSP's WebSocket origin follow the `Host` header | Info | accepted: production sets `PublicUrl` (admin guide); the origin checks compare like with like | `HostingTests.APublicUrlNamesTheWebSocketOrigin` |

**Counts:** High 1 (fixed) · Medium 3 (2 fixed, S-10 open for the deploy track) · Low 9 (8 fixed, S-13 accepted) ·
Info 9 (accepted). Nothing Critical.

## 3. Areas

### 3.1 Synced tables (`SyncTables.All`)

Every table was read for: who sees it (`Visible`), who inserts, patches, deletes (`Authorize*`), the whitelist, the
server-owned columns, cross-row checks (`ValidateAsync`), duplicate pairs and the delete's cascade/restore.

| Table | Visible | Insert | Patch | Delete | Notes |
|---|---|---|---|---|---|
| `groups` | all | – (ro) | – | – | `normalizedName`, `entraAppRole` hidden |
| `persons` | all (§15 #15) | *Persons: Edit* | *Persons: Edit*; Microsoft persons `notes` only; a user's person not name/e-mail | manual, no user | `source`, `entra*`, `userId` server-owned |
| `statuses` … `assignment_roles` | all; `sections` visible ones (+ `canEdit`), `field_definitions` and `template_field_defaults` of visible sections | – (ro, admin endpoints) | – | – | |
| `saved_views` | own + shared | shared needs *Configuration: Manage* | own; shared with the right | own; shared with the right | owner server-owned |
| `items` | owner, shares, admins | type's *Create* | edit | manage | `number`, owner, `statusChangedAt`, `convertedToItemId` server-owned; origin must be readable (marked converted only with edit); status of the type/template; cover an image of the item |
| `item_shares` / `map_shares` | with the item / map | only with a new item/map of the same batch by the same user (`sync_mutations`) | – | – | later changes: REST, manage |
| `maps` | owner, shares, templates, admins | *Maps: Create*; templates *Configuration: Manage* | edit; `defaultView` manage; `isTemplate` the right | manage | `templateSourceId` dropped if unreadable |
| `files` | the item's or map's readers | edit on the owner | edit (`fileName`, `thumbnail`) | edit | `status`, `storagePath` server-owned and hidden; byte sharing only for readable sources (S-07) |
| `item_sections` | readable item **and** visible section | edit + visible section that applies | edit | edit | duplicates merged |
| `field_values` | readable item **and** visible field | edit + visible and editable section; never scores | 〃 | 〃 | duplicate → patch from null, authorised as an insert first |
| `milestones`, `dod_items`, `assignments` | readable item | edit on the project | edit | edit | `completedAt`, `decided*`, `stateChanged*` server-owned |
| `item_links` | readable source | edit on source, read on target | – | edit on source | |
| `map_layers`, `map_elements` | readable map | edit on the map; a card's item readable (none on matrix maps), milestone/timeline's project readable, image a map image of this map, connector ends of this map | edit; layer of this map | edit; a map keeps a layer | coordinates bounded |
| `user_settings` | own | own (`userId` server-owned) | own | own (S-05) | duplicate → patch of the own row |

- A refused mutation of a row one can't see is `missing_access` **without** `current`; a patch's `current` is the
  visible row only. Mutation ids are per user (`duplicate_id` for another's).
- Restore authorises against the batch's root with the table's delete rule; purge is admin-only. Re-stamps and evictions
  follow sharing and owner changes; section view changes and memberships bump `access_epoch` (reset).

### 3.2 REST (24 controllers, `EndpointAuditTests`)

- Every controller derives from `V1ControllerBase` (`[CsrfProtection]`, ProblemDetails); only `AuthController.Login` and
  `LoginTotp` skip CSRF (before a session exists) – asserted by `SecurityTests`. Cookie-authenticated unsafe calls need
  `X-XSRF-TOKEN` (`LoginTests`). No CORS policy: same origin only.
- The endpoint audit matrix (rights per endpoint, anonymous allowlist, audit events and denied entries) covers every v1
  endpoint; object access is the service's: sharing/owner *manage*, History *read* (S-01), presence *edit*, Trash
  *manage* on the root (admins purge), files *edit* to upload, *read* to download (404 for no access).
- Sizes: push 8 MB / 200 mutations / 1000 resolutions, pull pages ≤ 1000 per table, REST pages ≤
  `PageQuery.MaxPageSize`, chunks ≤ 4 MiB, attachments 25 MB, images 2 MB, uploads 200 per user and hour.
- No OpenAPI or Swagger endpoint at run time (the document is generated at build).
- *Administration → System* (`GET /api/v1/system`, `DELETE /api/v1/system/clients/{id}`, *System: View*, in the matrix):
  shows devices' user agents, app versions and cursors, never a token, a password or a setting's secret (`PublicUrl`
  only); a device reset deletes its row (audited `system.client_reset`, subject its user) and tells its live channel –
  nothing on the server changes (`SystemTests`).

### 3.3 Live channel (`/api/v1/sync/live`)

Origin must equal `PublicUrl` (else the request's own scheme and host): HTTP 403 before the upgrade; the session cookie
(SameSite=Strict) authenticates; at most 10 sockets per user (4429), messages ≤ 64 KB, `hello` within 15 s, one
unacknowledged batch (ack ≤ 30 s), session revalidated every 60 s and at once on `SessionCache.Forget` (4401 / reset
4409), the rights taken over (S-11); a device reset from *Administration → System* is a `reset` at once. Clients send
no state-changing messages.

### 3.4 Files

Names cleaned of paths and control characters; double extensions checked against `Files:BlockedExtensions` at insert,
rename and commit; stored under `yyyy/MM/{id}-{slug}`, located only inside the folder and never through a symbolic link
(`FileStore.Locate`); SHA-256 and size verified at the commit; images decoded and stripped (PNG/JPEG/GIF/WebP, no SVG);
downloads: images inline as their checked type, everything else `application/octet-stream` as an attachment, `nosniff`,
API CSP, `Cross-Origin-Resource-Policy: same-origin`, ranges, ETag = SHA-256. Fixed: S-06, S-07.

### 3.5 MCP

Token scheme only on `/mcp` (no cookie), no `/api/v1` policy accepts a token (tested both ways); origin check (DNS
rebinding); read tokens read only, write tokens capped at edit and the write ceiling (`McpRights`), never the admin flag;
2FA for the write token; strikes, 120 calls / 20 writes per minute; tokens stored as SHA-256, shown once (`no-store`),
redacted from the MCP log's arguments. Every write goes through `SyncWriter` (the tables' rules above), so a tool can't do
what the sync refuses. Hidden sections are never returned (`get_item`, `search_items` filters, `list_config`, matrix axes)
nor accepted (`update_item`: `not_found`). Fixed: S-02, S-08, S-09.

### 3.6 Headers, cookies, sessions

CSP (UI: `script-src 'self'`, no `data:`, `frame-ancestors 'none'`; API and `/mcp`: `default-src 'none'`), `nosniff`,
`X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, COOP/CORP same-origin, a restrictive `Permissions-Policy`, HSTS
(S-04). Session cookie HttpOnly, SameSite=Strict, Secure (S-03), 7 days sliding, revalidated per request through
`SessionCache` (security stamp, active flag, 30-day sign-in); the 2FA ticket on `/api/v1/auth` only, 5 minutes.

### 3.7 Login and SSO

PBKDF2-SHA256 (210,000 iterations), a dummy hash for unknown names (same timing), lockout 5 → 5…60 min, rate limit
per address (S-10), TOTP replay protection, optional network restriction for the local login. SSO: authorisation code
with PKCE, state and nonce cookies (SameSite=Lax, Secure with S-03), return paths local only (`ReturnPaths`), no tokens
kept.

### 3.8 Audit and secrets

Every write endpoint names its audit event or why not (`EndpointAuditTests`); refusals are recorded (60 a minute per
user); sync changes are in the change log with source and correlation id; MCP writes are also audited. `[AuditSecret]`
on password hashes and TOTP secrets; MCP token values never stored or logged; the directory's client secret only as
"set" in the admin page; no `EnableSensitiveDataLogging`, SQL commands logged at warning only. S-18 is the one secret
written to the log, once and by design.

## 4. Tests added

- `PGPM.Tests/Sync/SyncSecurityTests.cs`:
  - `AStrangerIsRefusedOnEveryTableAndPullsNothingOfTheOwners` – an insert, a patch and a delete on every table of
    `SyncTables.All` (fails for a new table until it is added), each refusal's code, no `current`, nothing of the
    owner's changes, and the stranger's pull has none of the owner's rows.
  - `AnInsertNeverSetsWhatTheServerOwns` – owner, number, conversion, a person's user and source, a file's status and a
    traversal `storagePath` are the server's; patches of them are `not_patchable`.
  - `AHiddenSectionNeverReachesOthersByPullOrHistory` – item sections, values, definitions and template defaults of a
    view-restricted section are not pulled by a colleague nor in their History; a board member and an admin get them.
  - `ADuplicatePairNeverPatchesARowTheUserMayNotEdit` – field values, item sections, assignments, links of a read-only
    user.
  - `TheTrashHistoryAndFilesOfForeignItemsAreRefused` – Trash list/restore/purge of foreign items, maps and settings,
    History, download, chunk and commit of foreign files.
  - `AnAttachmentNeverLendsItsBytesToAnImage` – S-07 (fails without the fix).
- `PGPM.Tests/Hosting/SecurityTests.cs`: controllers and CSRF skips, Secure cookies and HSTS with an https `PublicUrl`
  (and neither without), the disk reserve.
- `PGPM.Tests/Sync/LiveSyncTests.ARightsChangeReachesTheConnectionsComputedAccess` (S-11),
  `PGPM.Tests/Sync/EditingRegistryTests` (S-12), `PGPM.Tests/Hosting/SystemTests` (the System endpoints).
- The MCP track's tool tests (`PGPM.Tests/Mcp/Mcp*ToolTests.cs`) for S-02, S-08, S-09 and hidden sections.
