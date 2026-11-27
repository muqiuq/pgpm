# PGPM – Administration Guide

> For PGPM 1.0. Users: see the [user guide](user-guide.md). Script reference and reverse-proxy examples:
> [deploy/README.md](../deploy/README.md). Design details: [03 Implementation reference](03-implementation-reference.md).

PGPM is one container (ASP.NET Core with the web app) and one PostgreSQL database. Browsers keep a local copy of
everything their user may see (IndexedDB) and stay in sync over a WebSocket; attachments live in a folder on the host.
This guide covers installing and running it, Microsoft Entra, users and rights, configuration, MCP, monitoring and
troubleshooting.

## Contents

1. [Installation](#1-installation)
2. [First start](#2-first-start)
3. [Microsoft Entra: single sign-on](#3-microsoft-entra-single-sign-on)
4. [Directory sync](#4-directory-sync)
5. [Configuration keys](#5-configuration-keys)
6. [Users, groups and rights](#6-users-groups-and-rights)
7. [Sharing model](#7-sharing-model)
8. [Configuration packages](#8-configuration-packages)
9. [Backup, restore, update](#9-backup-restore-update)
10. [MCP for AI assistants](#10-mcp-for-ai-assistants)
11. [Monitoring](#11-monitoring)
12. [Troubleshooting](#12-troubleshooting)

---

## 1. Installation

**Host.** Linux (amd64 or arm64) with Docker Engine and the compose plugin (v2), or Podman 4+ with `podman-docker`
(the `docker` command) and `docker-compose` against Podman's socket. 2 vCPU, 2–4 GB RAM and disk for the database, the attachments and a few
backups are plenty for a team. One PGPM container per database (see [release notes](release-notes-1.0.md)).

**Steps.**

```sh
git clone <repository> /srv/pgpm
cd /srv/pgpm/deploy
./init.sh --url https://pgpm.example.com   # .env with generated secrets; files/ and backups/
docker compose up -d
./info.sh
```

`init.sh` writes `deploy/.env` (`0600`) with a generated database password and the first admin's password (printed once),
sets `PublicUrl`, and creates the attachments' folder `files/` for the app's user (uid 1654, `0750`). The Microsoft
settings are in `.env` as comments. Every key of section 5 can be set there as `Section__Key=value`; after a change,
`docker compose up -d` recreates the container.

**The stack.** `db` (PostgreSQL 18, volume `pgpm-pgdata`) and `pgpm` (published on `127.0.0.1:7330`, volume
`pgpm-keys` for the Data Protection key ring, the bind-mounted `files/`). The app runs as a non-root user with a
read-only root filesystem, a `tmpfs` for `/tmp`, no capabilities and `no-new-privileges`; it applies its database
migrations at every start. Local changes go into `docker-compose.override.yml`.

**Reverse proxy.** PGPM must be served over HTTPS at the root of its own host name – the service worker, the PWA
installation and the secure cookies need it. The proxy must:

| Requirement | Why |
|---|---|
| Pass **WebSocket upgrades** (`Upgrade`, `Connection` headers) | the live sync channel `/api/v1/sync/live` |
| Idle timeout **≥ 60 s** | the server's heartbeat comes every 25 s; shorter timeouts make the channel reconnect constantly |
| Request bodies up to **32 MB** | sync pushes (≤ 8 MB), upload chunks (4 MB), package imports; nginx's default 1 MB answers 413 |
| Forward `Host`, `X-Forwarded-For`, `X-Forwarded-Proto`; keep `Origin` | redirect URIs, audit addresses, the live channel's and MCP's origin check |
| Forward every path unchanged | the app, `/api/`, `/mcp`, `/signin-oidc`, `/healthz` are all at the root |

Set `PublicUrl` to exactly the address users open. `.env` comes with `Proxy=docker` (a proxy on this host); for a proxy
elsewhere, set its address or network instead, so
the client addresses in the audit log and the login rate limit are the real ones. Caddy and nginx examples:
[deploy/README.md → Behind a reverse proxy](../deploy/README.md#behind-a-reverse-proxy).

## 2. First start

1. Open `PublicUrl`, sign in as `admin` with the password `init.sh` printed. PGPM asks for a new password at once
   (10–256 characters). The initial password in `.env` is read only while the database has no users.
2. Set up two-factor authentication (user menu → **Security**). `./info.sh` warns about admins without it;
   `Auth__TotpPolicy=RequiredForAdmins` enforces it.
3. The first start imported the built-in **configuration packages**:
   - `core`: statuses for ideas, leads and general projects, the groups **Members** (create notes, ideas, leads,
     projects and maps; use MCP) and **Viewers** (no rights), the template *General project*, assignment roles
     (*Responsible*, *Contact*, *Contributor*), the view *Learnings*.
   - `innovation-ai` (the digitalswitzerland AI playbook, [doc 04](04-innovation-methodology.md)): assessment sections,
     ratings and scores (feasibility index), statuses, the templates *AI use case* and *AI ambition* with gates and
     checklists, the groups **Innovation team**, **Review board** and **Compliance**, saved views, map templates and
     the matrix preset *Prioritization matrix*.
   `Packages__SeedOnFirstStart` chooses them (empty: none); they are imported once per database.
4. Add users (section 6) or connect Microsoft Entra (section 3), and put them into groups.
5. Schedule backups (section 9).

## 3. Microsoft Entra: single sign-on

One app registration serves the sign-in and the directory sync. Use the Microsoft Entra admin center
(<https://entra.microsoft.com>) with a role that may register apps and grant admin consent (Application Administrator or
Cloud Application Administrator; consent for application permissions needs Privileged Role Administrator or Global
Administrator).

1. **Register the app.** *Identity → Applications → App registrations → New registration*: name `PGPM`, *Supported
   account types*: **Accounts in this organizational directory only** (single tenant), *Redirect URI*: platform
   **Web**, `https://pgpm.example.com/signin-oidc` (`PublicUrl` + `/signin-oidc`; for a laptop test
   `http://localhost:7330/signin-oidc`). Register. *Administration → Microsoft integration* in PGPM shows the exact
   redirect URI it uses.
2. **Note the IDs.** On *Overview*: *Application (client) ID* → `Entra__ClientId`, *Directory (tenant) ID* →
   `Entra__TenantId`.
3. **Client secret.** *Certificates & secrets → Client secrets → New client secret*, a description and an expiry (put
   a reminder in the calendar before it). Copy the **Value** (not the Secret ID) at once → `Entra__ClientSecret`.
4. **Delegated permissions (sign-in).** *API permissions*: Microsoft Graph → *Delegated* → `openid`, `profile`,
   `email`, `User.Read`. `User.Read` is there by default; none of these needs admin consent. PGPM uses the
   authorization-code flow with PKCE; no implicit grant, no ID tokens from the authorization endpoint.
5. **Application permission (directory sync).** *API permissions → Add a permission → Microsoft Graph → Application
   permissions → `User.Read.All`*, then **Grant admin consent for <tenant>**. The status column must show
   *Granted*. Skip this step if you don't turn the directory sync on.
6. **App roles → PGPM groups.** *App roles → Create app role*: display name e.g. `PGPM Members`, *Allowed member
   types*: **Users/Groups**, *Value* e.g. `PGPM.MEMBERS`, a description, enabled. One role per PGPM group Entra should
   manage (e.g. `PGPM.REVIEW_BOARD`). In PGPM, open *Administration → Groups & Rights → <group>* and enter the role's
   value in **Entra app role**. From then on the group's members are whoever holds the role at their Microsoft
   sign-in (checked at every sign-in; the members can't be edited by hand). Groups without an app role are managed in
   PGPM. Entra never makes anyone an admin.
7. **Who may sign in.** *Enterprise applications → PGPM → Properties*: **Assignment required? = Yes** if only assigned
   users may sign in (recommended). *Users and groups → Add user/group*: pick users or Entra groups and the app role.
8. **Configure PGPM.** In `deploy/.env`:

   ```sh
   Entra__Enabled=true
   Entra__TenantId=<directory (tenant) id>
   Entra__ClientId=<application (client) id>
   Entra__ClientSecret=<secret value>
   # Entra__AutoProvision=true      the first sign-in creates the user (default)
   # Entra__DefaultGroupId=<id>     a PGPM group for users that got none from an app role (the id is in the group's URL)
   ```

   `docker compose up -d`. The start fails with a clear message when `TenantId`, `ClientId` or `ClientSecret` is
   missing or the tenant id is no GUID.
9. **Test.** The sign-in page shows **Sign in with Microsoft**. The first sign-in creates the user (username from
   `preferred_username`), links their person and applies the app roles. A local user with the same username is
   refused (`account-conflict`): link the Microsoft account to that user under *Administration → Users → <user>*.

Notes:

- Users are matched by tenant and object id only, never by e-mail.
- Signing out of PGPM does not sign out of Microsoft.
- A Microsoft user signs in again with Microsoft at least every `Auth__LoginDays` (30).
- `Auth__LocalLoginNetworks=none` turns password sign-in off entirely, so only Microsoft is left. A list of networks
  allows it only from there, e.g. for a break-glass admin.
- **Rotating the secret:** add a new secret, put it into `.env`, `docker compose up -d`, then delete the old one in
  Entra.

## 4. Directory sync

With `Directory__Enabled=true` (and `Entra__TenantId/ClientId/ClientSecret` – SSO itself may stay off) PGPM reads all
user accounts of the tenant, members and guests, into **Persons** with the application permission `User.Read.All`:
incrementally (Graph delta query) every `Directory__IntervalHours` (6), completely every `Directory__FullSyncDays` (7).
Disabled or removed accounts become inactive persons, never deleted; a person whose account signs in to PGPM is linked
to that user. Only changed persons are sent to the browsers.

*Administration → Microsoft integration* (right *Directory: Manage*) shows the SSO settings, the sync's state, the last
run with its counts and error, and has **Run now** and **Full run**. A failed run (e.g. missing admin consent:
`Authorization_RequestDenied`) is shown there and in the audit log; the next run continues where the last complete one
ended.

## 5. Configuration keys

ASP.NET configuration: `appsettings.json` in the image, overridden by environment variables `Section__Key`, which
`deploy/.env` provides. Secrets belong in `.env` only. Sizes are in bytes (MiB = 1,048,576).

**Hosting and database**

| Key | Default | Meaning |
|---|---|---|
| `PublicUrl` | – | the address users open: redirect URI base, the live channel's and MCP's origin, links in MCP results |
| `Proxy` | – (none trusted); `.env.example`: `docker` | trusted proxies, comma-separated: `docker` (172.16.0.0/12), addresses, networks. A bad entry stops the start |
| `TZ` (environment only) | UTC | IANA zone for wall-clock rules (`Auth:ResetHour`); an unknown zone is UTC |
| `Database:Host` / `Port` / `Name` / `Username` / `Password` | `localhost` / 5432 / `pgpm` / `pgpm` / – | the database (compose sets them) |
| `Database:ConnectionString` | – | a full Npgsql connection string instead of the parts |
| `DataProtection:KeysPath` | `/var/lib/pgpm/keys` | the key ring (sessions, 2FA secrets): a volume, in the backup |
| `BackgroundTasks:Enabled` | true | pruners, file tasks, MCP token expiry, directory sync |
| `Packages:SeedOnFirstStart` | `core,innovation-ai` | built-in packages imported on the first start (empty: none) |

**Sign-in**

| Key | Default | Meaning |
|---|---|---|
| `Auth:InitialAdmin:Username` / `Password` | `admin` / generated and logged once | the first admin, while there are no users; must change the password |
| `Auth:SessionDays` | 7 | sliding session lifetime |
| `Auth:LoginDays` | 30 (0: off) | a full sign-in again after so many days |
| `Auth:TotpDays` | 1 (0: only at sign-in) | the 2FA code again after so many days |
| `Auth:ResetHour` | 4 | the local hour (`TZ`) at which such a "day" ends |
| `Auth:TotpPolicy` | `Optional` | `Optional`, `RequiredForAdmins`, `Required` |
| `Auth:TotpIssuer` | `PGPM` | the name in authenticator apps |
| `Auth:LocalLoginNetworks` | all | `all`, `none` (needs `Entra:Enabled`), or addresses and networks (`10.0.0.0/8, 192.168.1.20`); a bad entry stops the start |
| `Auth:LoginRateLimit:PermitLimit` | 10 | sign-in attempts per minute and client address |
| `Entra:Enabled` | false | Microsoft sign-in |
| `Entra:TenantId` / `ClientId` / `ClientSecret` | – | the app registration (section 3); required when enabled |
| `Entra:AutoProvision` | true | the first Microsoft sign-in creates the user |
| `Entra:DefaultGroupId` | – | a group (id) for Microsoft users without one |

**Directory, files, sync**

| Key | Default | Meaning |
|---|---|---|
| `Directory:Enabled` | false | the directory sync (needs the Entra ids and secret, not `Entra:Enabled`) |
| `Directory:IntervalHours` / `FullSyncDays` | 6 / 7 | delta runs / full runs |
| `Files:Path` | `/var/lib/pgpm/files` | the attachments' folder (mounted from the host) |
| `Files:MaxAttachmentBytes` / `MaxImageBytes` | 26214400 (25 MiB) / 2097152 (2 MiB) | upload limits |
| `Files:BlockedExtensions` | `exe msi dll scr com bat cmd ps1 vbs js jar hta lnk` | refused extensions, also as double extensions |
| `Files:UploadsPerHour` / `MinFreeMb` | 200 / 1024 | uploads per user and hour / the disk's reserve: an upload that would leave less is refused, the hourly check warns below it |
| `Sync:PageLimit` / `MaxMutations` / `MaxBodyBytes` | 500 / 200 / 8388608 (8 MiB) | pull page size (1–1000) / changes per push / push body |
| `Sync:TombstoneDays` | 90 | how long tombstones outside the trash are kept |
| `Sync:Live:Enabled` | true | the live WebSocket channel (off: polling only) |
| `Sync:Live:MaxConnectionsPerUser` | 10 | open live connections per user |
| `Sync:Live:AckTimeoutSeconds` / `HeartbeatSeconds` | 30 / 25 | flow control / heartbeat |
| `Sync:Live:RevalidateSeconds` / `HelloTimeoutSeconds` | 60 / 15 | session re-check / time for the client's hello |

**MCP**

| Key | Default | Meaning |
|---|---|---|
| `Mcp:Enabled` | true | the MCP server at `/mcp` |
| `Mcp:WriteHours` / `ReadDaysAfterOnline` | 12 / 7 | write token lifetime / read token lifetime after the user was last online |
| `Mcp:WriteRequiresTotp` | true | a write token needs 2FA or a linked Microsoft account |
| `Mcp:StrikesToBlock` | 3 | refused calls in 24 h that block a token |
| `Mcp:CallsPerMinute` / `WritesPerMinute` | 120 / 20 | rate limits per token |
| `Mcp:LogEntries` / `MaxResultChars` | 10000 / 40000 | MCP log size / a tool result's cut-off |

The compose variables of `.env` (`PGPM_DB_PASSWORD`, `PGPM_IMAGE`, `PGPM_IMAGE_TAG`, `PGPM_BIND_ADDRESS`, `PGPM_PORT`,
`PGPM_FILES_DIR`, `PGPM_BACKUP_DIR`, `PGPM_BACKUP_KEEP`, `COMPOSE_PROJECT_NAME`) are described in
[deploy/.env.example](../deploy/.env.example).

## 6. Users, groups and rights

**Rights are capabilities** – what a user may *do*; what they may *see* is decided by sharing (section 7). A user is
either an **admin** (every right; alone manages users, groups and rights) or has the highest level of each area over
their groups:

| Area | Levels | Allows |
|---|---|---|
| Notes, Ideas, Leads, Projects | Create | creating items of the type (the creator owns them); converting needs Projects: Create |
| Maps | Create | creating maps |
| Persons | Edit | manual persons |
| Configuration | Manage | statuses, sections and fields, templates, map templates, shared saved views, configuration packages |
| Directory | Manage | the Microsoft integration page, running the directory sync |
| Mcp | Use < View < Manage | own tokens · the MCP log of everyone · switching off others' tokens |
| AuditLog | View | the audit log |
| System | View | the System page |

**Managing users.** *Administration → Users* (admins): add local users (a temporary password is shown once, valid for
7 days, changed at the first sign-in), deactivate, reset passwords and 2FA, set the admin flag, link a Microsoft
account. *Administration → Groups & Rights*: groups, their rights (a level per area) and an optional Entra app role
(section 3). Rights changes take effect at once, without a sync reset. On the server:

```sh
./add-user.sh ana --groups Members --display-name "Ana Example"
./list-users.sh
./reset-password.sh ana              # also lifts a lockout; --unlock-only only that
./reset-totp.sh ana                  # a lost authenticator (there are no recovery codes)
docker compose exec pgpm pgpm users set-admin ana on
docker compose exec pgpm pgpm groups list
```

The last active admin can't be removed or deactivated. Five failed sign-ins lock an account for 5 minutes (doubling up
to 60). Every change to users, groups and rights is in the audit log.

## 7. Sharing model

- Every **item** (note, idea, lead, project) and every **map** has an owner and is shared with **groups** at *read*,
  *edit* or *manage*. Not shared means private to its owner (and admins).
- **Owner** and **admins** always have *manage*. *Manage* may change sharing, transfer the owner, delete and restore.
  *Edit* changes content; *read* only reads.
- Milestones, attachments, assignments, links and field values follow their item; map layers and elements follow their
  map. Placing an item on a map needs *read* on the item; the card shows only what the viewer may see.
- **Sections** of ideas and projects may be limited to groups (*Sections & Fields → access*): visible and/or editable
  only for them (e.g. the review-board decision), enforced on the server and in MCP.
- Templates may give **default shares** to new projects; new items are otherwise shared as the creator chooses in the
  create dialog.
- Losing access removes the data from that user's devices live (evictions); gaining access delivers it.
- Admins see everything. Rights never widen visibility.

## 8. Configuration packages

Statuses, sections, fields, scores, templates with milestones and gates, groups, roles, saved views, map templates and
matrix presets travel as **configuration packages** (JSON, format in [doc 03 §17](03-implementation-reference.md),
schema `spec/config-package.schema.json`). *Administration → Configuration packages* (*Configuration: Manage*; a package
that creates or changes groups needs an admin):

1. **Upload package…** (a `.pgpm.json` file) or pick a built-in package, then **Dry run**: every entity as *create*,
   *update*, *unchanged* or *conflict* (changed here since its import), with the differences.
2. Choose which conflicts to **overwrite** (local edits win otherwise), then **Apply**: one transaction; every device
   receives the result live.
3. **Export** writes the current configuration – everything, or what one package brought – as a package, for another
   installation or for review.

Re-importing an unchanged package changes nothing. Entities keep their key (`status:idea.assessed`) when edited in the
UI, so a later import updates instead of duplicating. `tools/check-packages.py` validates a package file offline.

## 9. Backup, restore, update

All in `deploy/` ([deploy/README.md](../deploy/README.md) has the details):

- `./backup.sh [--keep 14]` writes one archive `backups/pgpm-<date>-<time>.tar`: a consistent `pg_dump` of the database,
  the Data Protection key ring, the attachments, a manifest and SHA-256 checksums. Run it from cron daily
  (`30 3 * * * /srv/pgpm/deploy/backup.sh --quiet --keep 14 >>/srv/pgpm/deploy/logs/backup.log 2>&1`) and copy the
  archives off the host. `.env` is not in it: keep a copy elsewhere.
- `./restore.sh <archive>` verifies the archive (checksums, dump, tar files), asks, stops PGPM, replaces database, key
  ring and files (`--force` when the files folder isn't empty), and starts PGPM. Into a new host: `./init.sh` first.
  Every browser then starts over: it sends its pending changes, drops its copy and loads everything again.
  `--verify-only` checks an archive without changing anything.
- `./update.sh [--tag v1.0.1]` pulls the release, takes a backup, recreates the containers, waits for `/healthz` and
  shows the old and new release. Browsers notice the new release over the live channel and offer the update. Going
  back after a migration: the old `PGPM_IMAGE_TAG` in `.env`, then `./restore.sh --force` with the update's archive.

## 10. MCP for AI assistants

PGPM is an MCP server (Streamable HTTP, stateless) at **`<PublicUrl>/mcp`**. It is on by default (`Mcp__Enabled`); each
user switches on their own tokens under user menu → **Security** → *MCP tokens* (right *Mcp: Use*, part of
*Members*):

- a **read token** (`pgpm_ro_…`): reading; lasts `Mcp:ReadDaysAfterOnline` (7) days after the user was last online;
- a **write token** (`pgpm_rw_…`): also creating and updating within the user's rights, capped at *edit* (no deletes,
  sharing, owner changes or administration); needs 2FA or a linked Microsoft account; lasts `Mcp:WriteHours` (12).

The value is shown once. A changed password or 2FA, or signing out everywhere, switches both off. The assistant sends
`Authorization: Bearer <token>`; browser cookies never authenticate `/mcp`, and a request with a foreign `Origin` is
refused. Every call is logged; three refused calls in 24 hours block the token.

**Client examples** (replace the address; keep tokens out of shared files and repositories):

Claude Code (command line, stored in the user's configuration):

```sh
claude mcp add --transport http pgpm https://pgpm.example.com/mcp --header "Authorization: Bearer pgpm_ro_…"
```

Claude Code project file `.mcp.json` (the token from an environment variable, so the file can be shared):

```json
{
  "mcpServers": {
    "pgpm": {
      "type": "http",
      "url": "https://pgpm.example.com/mcp",
      "headers": { "Authorization": "Bearer ${PGPM_MCP_TOKEN}" }
    }
  }
}
```

Clients that take remote servers with headers in their configuration (Cursor, VS Code under `servers`, and Claude
Desktop versions that support it) use the same form as above – the token card in PGPM shows it ready to copy. For a
client that only starts local (stdio) servers, such as older Claude Desktop versions (`claude_desktop_config.json`),
bridge with `mcp-remote` (needs Node.js on that computer):

```json
{
  "mcpServers": {
    "pgpm": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "https://pgpm.example.com/mcp", "--header", "Authorization:${PGPM_AUTH}"],
      "env": { "PGPM_AUTH": "Bearer pgpm_ro_…" }
    }
  }
}
```

**Administration → MCP** (*Mcp: View*): *Status* (enabled, limits, tokens on), *Tokens* (*Switch off…* with a reason,
*Revoke all…* with *Mcp: Manage*), *Log* (every call with its arguments redacted, the result, the audit entries and the
item history it caused), *Tools* (the catalogue). Changes through MCP go through the same validation and access checks
as the browser's, appear live, and carry the source *mcp* in History and the audit log.

## 11. Monitoring

- **`GET /healthz`**: `200 ok` while the database answers, `503` otherwise; anonymous, for load balancers and uptime
  checks. The container's health check uses it.
- **`./info.sh --quiet`**: exit code 0 when the container runs and `/healthz` answers – for cron or a monitoring agent.
  `./info.sh` shows the release, health, database and files size, sync head, Microsoft and MCP state, backups and
  warnings (no `Proxy`, plain-HTTP `PublicUrl`, no recent backup, admins without 2FA, a files folder the app can't
  write).
- **`GET /api/v1/info`**: the release (`{"name":"PGPM","version":"1.0.0"}`), anonymous.
- **Administration → System** (*System: View*): the instance as PGPM sees it – release and uptime; database size,
  stored file bytes and free disk; the sync head and the tombstone horizon; live connections; the background tasks
  (last run, result, *overdue* when a task missed its interval); the Microsoft directory and MCP state; and every
  device with its user, app, last seen, how far behind it is, pending changes, open conflicts and whether it is live.
  *Reset…* on a device makes it push what it has, wipe its local data and load everything again (at once when it is
  live, else at its next sync); the audit log records it as `system.client_reset`. Use it for a device that is stuck
  or far behind.
- **Logs**: `docker compose logs -f pgpm` (console, rotated by Docker: 3 × 10 MB). The **audit log**
  (*Administration → Audit log*) has sign-ins, refusals and every administrative change.

## 12. Troubleshooting

**The sync indicator never shows live / changes arrive only after seconds.** The live channel can't connect, and PGPM
falls back to polling. Check the proxy: the WebSocket upgrade on `/api/v1/sync/live`, the idle timeout of 60 s or more,
and `PublicUrl`. With `PublicUrl` set, the channel accepts only that origin. A user who opens PGPM under another name
(an IP address, an old host name) gets 403 on the upgrade. Corporate proxies that break WebSockets have the same effect.
Users keep working, only more slowly.

**A user's device "starts over" (loads everything again).** That is a **sync reset**. The server asks for one when the
user's access may have changed in ways the device can't follow:

- group membership or the admin flag changed;
- section visibility changed;
- the device was offline past the tombstone horizon (`Sync:TombstoneDays`);
- the database was restored;
- the device's record is gone.

The device sends its pending changes first, then wipes its synced data and pulls everything. With many items this takes
a moment, and it is expected.

**Sync conflicts.** Two people changed the same field while one was offline. The sync indicator shows *conflicts*; the
user resolves them in the conflict dialog: *mine*, *theirs* or *edit*, per object or for all. Admins can't resolve them
for users. Each device reports its pending changes and open conflicts with every sync (the sync clients on the System
page).

**Data gone on an iPhone or iPad.** Safari may delete a website's storage after some days without use. PGPM asks the
browser for persistent storage. Installing it to the home screen as an app protects the data best. Nothing synced is
lost: the next sign-in loads it again. Changes made offline and never synced are lost with the storage, though. Tell
iOS users to install the app and go online regularly. Attachments use at most 200 MB of the device's cache.

**413 Request Entity Too Large.** The proxy's body limit is too small. Raise it to 32 MB (`client_max_body_size 32m;`).
An upload larger than `Files:MaxAttachmentBytes` (25 MiB) is refused by PGPM itself with a clear message.

**Uploads fail with `file_storage_unavailable`.** The files folder is missing or not writable for uid 1654. `./info.sh`
shows the fix: `sudo chown 1654:1654 files && sudo chmod 0750 files`, or `podman unshare chown …` for rootless Podman.

**Users see *Server storage full* on their attachments.** An upload would leave less than `Files:MinFreeMb` free on the
files folder's disk (507 `file_storage_full`). *Administration → System* shows the free space. Free space or grow the
disk: the files stay queued on the users' devices and are tried again every five minutes (or with *Retry*).

**Microsoft sign-in errors.**

| Error | Fix |
|---|---|
| `AADSTS50011` (redirect URI mismatch) | the app registration's Web redirect URI must be exactly `PublicUrl` + `/signin-oidc` |
| `AADSTS50105` (not assigned) | assign the user or their group in *Enterprise applications → PGPM → Users and groups* |
| `AADSTS7000215` (invalid client secret) | the secret expired or the Secret ID was copied instead of the Value |
| `account-conflict` | a local user has that username: link the Microsoft account to them (*Users → <user>*) |
| `no-account` | `Entra:AutoProvision` is off and no user is linked |

**The directory sync fails with `Authorization_RequestDenied`.** The application permission `User.Read.All` lacks
admin consent (section 3, step 5).

**"The database was migrated by a newer PGPM."** An older image was started on a newer database. Use the newer tag, or
restore the backup the update took (section 9).

**Lost admin password or authenticator.** Run `./reset-password.sh <name>` or `./reset-totp.sh <name>` on the server.

**Too many sign-in attempts (429).** `Auth:LoginRateLimit:PermitLimit` counts per client address. Behind a proxy
without `Proxy`, all users share the proxy's address, so set `Proxy`.
