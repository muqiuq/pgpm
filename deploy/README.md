# Deploying PGPM

The production stack: the published image (`uisach/pgpm`, see the release notes) and PostgreSQL 18, in two containers
with named volumes, and a folder on this host for the attachments. TLS ends at a reverse proxy in front of it.

Nothing here builds from source. To debug on a laptop, use `../start.sh` instead. The full administration guide –
Microsoft Entra, users and rights, configuration packages, MCP, monitoring, troubleshooting – is
[docs/admin-guide.md](../docs/admin-guide.md).

## First run

```sh
git clone <repository> /srv/pgpm
cd /srv/pgpm/deploy

./init.sh --url https://pgpm.example.com   # writes .env with generated secrets, creates files/ and backups/
docker compose up -d
./info.sh
```

`init.sh` writes `.env` (mode `0600`) from [.env.example](.env.example) with a generated database password and the
password of the first admin, which it prints once. The first start migrates the database, creates that admin and imports
the built-in configuration packages `core` and `innovation-ai`. Sign in at the `PublicUrl` as `admin` with that password;
it must be changed at once.

`init.sh` refuses to overwrite an existing `.env` unless you pass `--force`, which keeps `PGPM_DB_PASSWORD` (the database
keeps the password it was created with). The secrets exist only in `.env`: keep a copy of it somewhere safe.

Podman works too: the scripts call `docker` and `docker compose` (or `docker-compose`), which `podman-docker` and
`docker-compose` against Podman's socket provide (the deploy test runs that way on macOS). Rootless Podman needs
`podman unshare chown 1654:1654 files` instead of `sudo chown`.

## The stack

| Container | What | Published |
|---|---|---|
| `db` | PostgreSQL 18 (`pg_isready` health check) | nothing |
| `pgpm` | the web app, the API, the live sync channel, MCP and the background tasks | `PGPM_PORT` (7330) → 8080, on `PGPM_BIND_ADDRESS` (127.0.0.1) |

| Data | Where | In the backup |
|---|---|---|
| Database | volume `<project>_pgpm-pgdata` | `database.pgdump` |
| Data Protection key ring (sessions, 2FA secrets) | volume `<project>_pgpm-keys` | `keys.tar.gz` |
| Attachments | folder `PGPM_FILES_DIR` (`./files`), owned by uid 1654, `0750` | `files.tar` |
| Settings and secrets | `.env` | **no** – keep a copy elsewhere |

`pgpm` runs hardened: as the non-root user `app` (uid 1654), with a read-only root filesystem, a `tmpfs` for `/tmp`, every
capability dropped and `no-new-privileges`. It starts once `db` is healthy, applies its migrations, and refuses a database
that a newer release has migrated. Its health check is `GET /healthz` (200 `ok` while the database answers).

Local changes go into `docker-compose.override.yml` (ignored by Git), so `git pull` can replace `docker-compose.yml`.

## Everyday operation

| Command | What it does |
|---|---|
| `./info.sh` | Release, health, database and files size, sync head, Microsoft and MCP state, backups, and what needs attention |
| `./info.sh --quiet` | Exits 0 when PGPM runs and `/healthz` answers, for monitoring |
| `./backup.sh` | Writes one archive `backups/pgpm-<yyyymmdd-hhmmss>.tar` |
| `./backup.sh --keep 14` | …and deletes all but the newest 14 (`PGPM_BACKUP_KEEP` in `.env` does the same) |
| `./backup.sh --no-files` | …without the attachments, for a host that backs up their folder itself |
| `./restore.sh <archive>` | Checks the archive, asks, and restores it – here, or into a fresh deployment |
| `./restore.sh <archive> --force` | …and replaces what the files folder holds |
| `./restore.sh <archive> --verify-only` | Only checks the archive (checksums, the dump, the key ring, the files) |
| `./update.sh` | Backs up, pulls the release `.env` names, recreates the containers, waits until healthy, shows the release |
| `./update.sh --tag v1.0.1` | Pins another release first |
| `./add-user.sh <name> [--groups Members] [--admin]` | Adds a local user and shows a temporary password once (7 days, changed at the first sign-in) |
| `./list-users.sh` | Lists the users: admin or groups, state, 2FA, lockout, last sign-in |
| `./reset-password.sh <name>` | A new temporary password; ends the user's sessions and lifts a lockout (`--unlock-only`: only the lockout) |
| `./reset-totp.sh <name>` | Turns a user's 2FA off (a lost authenticator) |
| `docker compose logs -f pgpm` | The app's log |
| `docker compose exec pgpm pgpm help` | The app's command line (`db migrate`, `users …`, `groups list`) |

## Backups

Cron, daily at 03:30, keeping two weeks:

```
30 3 * * * /srv/pgpm/deploy/backup.sh --quiet --keep 14 >>/srv/pgpm/deploy/logs/backup.log 2>&1
```

One archive (`0600`, in a `0700` folder: it holds password hashes) with:

- `manifest.txt` – the release, the image, the time, the sync head;
- `SHA256SUMS` – a checksum per part;
- `database.pgdump` – `pg_dump --format=custom`: one consistent snapshot, taken while PGPM keeps running;
- `keys.tar.gz` – the Data Protection key ring; without it all sessions end and the 2FA secrets can't be read;
- `files.tar` – the attachments, taken after the database (an upload in between is only an extra file, which the hourly
  cleanup removes; a file purged from the trash in between may be missing – the hourly file check reports it).

Copy the archives off the host (rsync, restic, borg, object storage). `./restore.sh <archive> --verify-only` checks a copy
without touching the stack.

## Restore

Into this stack (for example after a mistake): `./restore.sh backups/pgpm-….tar --force`. Into a new host: clone the
repository, `./init.sh --url …`, copy the old `.env` settings you need (`PublicUrl`, `Proxy`, `Entra__*`; the database
password may be the new one), then `./restore.sh <archive>` – it starts the database, restores, and starts PGPM.

The archive is checked before anything changes. Then PGPM stops; the database, the key ring and the files are replaced;
PGPM starts again. Everything since the backup is lost. The restore raises every user's access epoch, so **every browser
starts over** at its next sync: it sends what it still had to send (which may come back as a conflict), drops its local
copy and loads everything again. Sessions from before the backup stay valid (the key ring is the backup's); later ones end.

## Update

`./update.sh` pulls the image `.env` names (`PGPM_IMAGE`, `PGPM_IMAGE_TAG`), takes a backup, recreates the containers
(`up -d --wait`), waits for `/healthz` and shows the old and the new release. The new release migrates the database as it
starts. Connected browsers notice the new release over the live channel and show the app's update notice; their local
data stays.

Going back after the migrations ran: set `PGPM_IMAGE_TAG` in `.env` to the old release, then `./restore.sh --force` with
the archive the update took – it starts the old release on the old database (an old release refuses a database a newer
one migrated).

A **new PostgreSQL major version** means `./backup.sh`, the new image in `docker-compose.override.yml`, removing the
`pgpm-pgdata` volume, then `./restore.sh`. Never just change the tag on the old volume.

## Behind a reverse proxy

PGPM must be served over **HTTPS** at the root of its own host name (`https://pgpm.example.com/`): the service worker, the
PWA installation and the secure cookies need it. The proxy must:

- forward every path unchanged, with the `Host` header, `X-Forwarded-For` and `X-Forwarded-Proto`;
- pass **WebSocket upgrades** (`Upgrade` and `Connection` headers) on `/api/v1/sync/live`, the live sync channel;
- keep idle connections open for **60 s or more** (the server sends a heartbeat every 25 s; less than that and the live
  channel keeps reconnecting – the app then falls back to polling, which works, but changes arrive seconds later);
- accept request bodies up to **32 MB** (a sync push is at most 8 MB, an upload chunk 4 MB, a configuration package
  import a few MB; nginx's default of 1 MB answers 413);
- leave the `Origin` header alone (the live channel and `/mcp` compare it with `PublicUrl`).

In `.env`: `PublicUrl` is exactly the address users open (scheme and host, no path), and `Proxy` names the proxy whose
forwarded headers are trusted – `Proxy=docker` for a proxy on this host reaching the published port (the app sees
Docker's bridge, 172.16.0.0/12), or the proxy's address or network. With the proxy on this host, keep
`PGPM_BIND_ADDRESS=127.0.0.1`. `./info.sh` warns when `Proxy` is missing.

**Caddy** (WebSockets and the forwarded headers are passed as they are; certificates are automatic):

```
pgpm.example.com {
    request_body {
        max_size 32MB
    }
    reverse_proxy 127.0.0.1:7330 {
        # Keep live connections open across a configuration reload.
        stream_close_delay 5m
    }
}
```

**nginx:**

```nginx
# In the http block: the Connection header for WebSocket upgrades.
map $http_upgrade $connection_upgrade {
    default upgrade;
    ''      close;
}

server {
    listen 443 ssl;
    http2 on;
    server_name pgpm.example.com;
    # ssl_certificate …; ssl_certificate_key …;

    client_max_body_size 32m;

    location / {
        proxy_pass http://127.0.0.1:7330;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        # The live sync channel (/api/v1/sync/live).
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
        # Idle WebSocket connections: at least 60 s (heartbeats come every 25 s).
        proxy_read_timeout 120s;
        proxy_send_timeout 120s;
    }
}

server {
    listen 80;
    server_name pgpm.example.com;
    return 301 https://$host$request_uri;
}
```

Other proxies (Traefik, HAProxy, Apache, a load balancer of a cloud) need the same: WebSocket upgrades passed, an idle
timeout of 60 s or more, and a body limit of 32 MB. Run **one** PGPM container per database: the live channel's change feed
is in-process (see the release notes).

## Testing these scripts

`../run-tests.sh --only deploy` builds the image and runs `../tools/deploy-test.sh`: a scratch copy of this folder with a
compose project and a port of its own goes through `init.sh`, `up`, the first sign-in, `add-user.sh`, a note and an
attachment, `backup.sh`, a refused damaged archive, the stack wiped, `restore.sh` into a fresh stack, `update.sh`,
`info.sh` and `down`.
