# PGPM – Performance (release 1.0)

> Status: v1.0 (2026-11-27) · phase 7 hardening, track H1; §2.3's list during a first sync: follow-up after H2
> Targets: [05 Build plan](05-build-plan.md) §3 phase 7 (10,000 items in lists, 1,000-element maps, 50 concurrent live
> clients on one instance), §7 spikes S1/S2 · implementation: [03](03-implementation-reference.md) §11.6, §12.3, §13.1

## 0. Summary

| What | Before | After | How measured |
|---|---|---|---|
| 50 live clients, 5 others pushing 20 notes/s: delivery latency p50 / p95 | 1,992 / 8,073 ms | **77 / 101 ms** | §1, `LiveLoadTests` |
| 〃 at 5 notes/s: p50 / p95, server+clients CPU | 129 / 156 ms, 164 % of a core | **64 / 74 ms, 67 %** | §1 |
| Table reads per pull (live feed after a commit) | 26.1 | **1.9** | §1, `SyncReader.Counters` |
| Item list, 10,000 ideas: default sort (newest change) | 232 ms | **16 ms** | §2.1, `useLocalTable.perf.test.ts` |
| 〃 sort by title / by status then date / search one word | 168 / 202 / 70 ms | **12 / 15 / 11 ms** | §2.1 |
| First sync, ~10,000 rows (the admin of the used e2e stack) | 12.2 s | **2.8 s** | §2.3 |
| First sync of 10,000 notes with the notes list open, until it counts them | 11.3–12.8 s | **3.3–4.6 s** | §2.3, `e2e/perf.spec.ts` |
| 1,000-element map, real app: pan and zoom | spike S2 (playground): 60 fps, throttled zooms 43–54 fps | **60 fps in every phase**; 4× throttled 55–60 fps | §2.2, `e2e/perf.spec.ts` |

## 1. Server: the live feed under load

**Setup.** `PGPM.Tests/Sync/LiveLoadTests.cs` (MSTest category `Load`, skipped unless `PGPM_LOAD=1`): one `TestApp`
(in-process TestServer, the real live hub and sync), PostgreSQL 18 from `tools/test-db.sh` (podman, `fsync=off`), 50
reader users in one group, each with one live WebSocket after a first HTTP sync; 5 writer users push notes, each insert
with its share to the group in one batch, at a fixed rate. Latency = from sending the push to a reader receiving the row
in a `changes` message (every reader acks every message). CPU = the test process (server **and** the 55 clients), as a
share of one core. Apple-silicon Mac, 14 cores.

```
PGPM_LOAD=1 [PGPM_LOAD_RATE=20] [PGPM_LOAD_PUSHES=200] [PGPM_LOAD_READERS=50] [PGPM_LOAD_BASELINE=1] \
  dotnet test --project PGPM.Tests/PGPM.Tests.csproj --filter TestCategory=Load --output detailed
```

**Finding.** Every commit wakes every live connection (`ChangeFeed.Publish`), and each connection's pull queried all 26
synced tables plus the evictions, in its own snapshot – 50 × 27 queries per commit, nearly all of them answering
nothing. At 20 commits/s the database was busy with those empty reads, so the pushes themselves queued (p50 1.9 s) and
deliveries fell seconds behind.

**Fix** (docs/03 §11.6, §15 #80): the pull first reads the head, the tombstone horizon and every table's highest seq in
one statement (`SELECT max(seq)` is an index lookup on the unique seq index of each table) and leaves out the tables –
and the evictions – without anything above the cursor. In the pull's own REPEATABLE READ snapshot, so the answer is
exactly the same; `SyncReaderTests` compares both ways from every cursor of a history with inserts, patches, a
re-stamp, an eviction, a delete and full pages. Coalescing was already there (a 25 ms debounce per connection, and at
most one unacknowledged batch): 10,000 deliveries took 8,163 pulls.

| 50 readers, 5 writers | pushes | delivery p50 / p95 / p99 / max (ms) | push p50 / p95 (ms) | CPU | table reads / pull |
|---|---|---|---|---|---|
| baseline, 20/s | 200 | 1,992 / 8,073 / 10,684 / 12,751 | 1,894 / 7,948 | 308 % | 26.1 |
| **skipping, 20/s** | 200 | **77 / 101 / 125 / 190** | **30 / 50** | 208 % | **1.9** |
| baseline, 5/s | 100 | 129 / 156 / 238 / 348 | 12 / 16 | 164 % | 26.1 |
| **skipping, 5/s** | 100 | **64 / 74 / 86 / 178** | 14 / 18 | **67 %** | **2.0** |
| skipping, 50/s | 500 | 819 / 4,638 / 7,013 / 8,370 | 831 / 4,653 | 274 % | 1.8 |

Of the ~65 ms at low load, 25 ms are the debounce, ~15 ms the push, the rest the pull and the WebSocket. **Limit**: all
writes take the global counter's lock (docs/03 §11.5), one at a time; with 50 live readers on this machine about 35–40
commits/s are sustained, beyond that pushes queue (the 50/s row). A team of 50 people editing by hand stays far below
(the e2e co-editing measures 200–300 ms end to end between two browsers). Scale-out remains `LISTEN/NOTIFY` (§11.6).

Not changed, for later if the numbers ever require it: the per-pull `ItemAccessAsync` (two queries for the computed
`access` of delivered items), one pull per connection even for users of the same groups (a shared page would need
per-user visibility anyway), the debounce of 25 ms.

## 2. Client

### 2.1 Lists with 10,000 items

**Setup.** `PGPM.WebUI/src/composables/useLocalTable.perf.test.ts` (`tools/npm.sh run bench`; skipped by `npm test`):
10,000 ideas with titles, descriptions, statuses, owners, timestamps; the list's real text, filter and sort functions
(`views/items/itemList.ts`) through `matchRows`, the work `useLocalTable` does on every change of data, search, filter or
sort. Each case is warmed up three times, then the median of 20 runs (Node 24 in the node container, jsdom).

| Case (10,000 ideas) | before (ms) | after (ms) |
|---|---|---|
| no search, newest change first (the default) | 232 | 16 |
| search one word (2,667 hits) | 70 | 11 |
| search two words, one with a diacritic | 40 | 10 |
| sort by title | 168 | 12 |
| filter two statuses, closed hidden | 51 | 5 |
| sort by status, then newest change | 202 | 15 |

**Hot spots and fixes** (shared code, `composables/useLocalTable.ts`, docs/03 §15 #84):

1. `compareValues` called `localeCompare(…, undefined, { numeric, sensitivity })` – a new collator per comparison,
   ~140,000 comparisons per sort. Now one `Intl.Collator` with the same options (identical order).
2. The sort read each row's sort value twice per comparison (a person's name, a score, a status lookup); now once per row
   and sort (`matchRows`).
3. The search normalised a row's text once **per word**, and again on every keystroke; now once per row, and
   `normalise()` remembers the texts it saw last (bounded to 8 M characters).

Spike S1's question – Dexie `liveQuery` plus Vue with 10,000 items – is answered by the same numbers: the list's
computation is now well below a frame's budget; the remaining cost is IndexedDB's read on a change (outside these
numbers) and rendering one page (50–200 rows) of the DataTable.

### 2.2 A 1,000-element map in the real app

**Setup.** `PGPM.WebUI/e2e/perf.spec.ts` (`E2E_PERF=1 tools/e2e.sh perf.spec.ts`; skipped otherwise): spike S2's stress
map (600 item cards of 300 notes, 300 rectangles, ellipses and texts, 100 connected lines on a 40-column grid) pushed
through the sync by its user, opened at `/maps/{id}` after the normal sign-in and first sync, then panned with the
middle button and zoomed with the wheel – real input events – while a `requestAnimationFrame` loop records every frame.
Production build in the e2e stack, headless Chromium of the Playwright image in a Linux container on an Apple-silicon
Mac (software raster, 1440 × 900) – the spike's environment, but the real app instead of the playground.

The map opens fitted (about 12 %, every card in LOD); the wheel zooms 1.2× per notch, a notch every 50 ms (so a zoom
phase's frames include the idle ones between notches – its *max* is the one to read); pans are 2 × 120 pointer moves.

| Phase | fps | p50 / p95 / max (ms) | 4× CPU throttling: fps, max (ms) | zoom, drawn |
|---|---|---|---|---|
| pan the whole map (opening view, LOD) | 60 | 16.7 / 16.7 / 16.8 | 59.5, 50 | 0.12, 1,000 |
| zoom in to about 50 % (crosses LOD), 8 notches | 60 | 16.7 / 16.7 / 16.8 | 59.3, 33 | 0.51 |
| pan at about 50 % (full detail, culled) | 60 | 16.7 / 16.8 / 16.8 | 58.6, 50 | 0.51, 326 |
| zoom in to about 100 %, 4 notches | 60 | 16.7 / 16.8 / 16.8 | 60, 17 | 1.06 |
| pan at about 100 % | 60 | 16.7 / 16.7 / 16.8 | 60, 17 | 1.06, 99 |
| zoom out to the whole map, 12 notches | 60 | 16.7 / 16.8 / 16.8 | 55, 67 | 0.12, 1,000 |

**Compared with spike S2** (the playground, the view set from `requestAnimationFrame`): the real app – with the sync,
the side panel's stores, the live channel and the production build – holds the same 60 fps cap in every phase; with
4× throttling only single frames miss it (the culling region renewed, the LOD threshold crossed: up to 67 ms, the spike
saw up to 150 ms), where the spike's throttled zooms ran at 43–54 fps. No change was needed; the guard in the spec fails
when a phase's p95 exceeds 100 ms.

### 2.3 The first sync of 10,000 items

**Setup.** `e2e/perf.spec.ts` (second test): one user pushes 10,000 notes, each shared with the team (20,000 rows, 49 s
through the sync at 200 mutations per request); a teammate signs in for the first time at `/notes`; then a search is
typed into the list and cleared. Same environment as §2.2.

**Hot spot found and fixed** (shared code, `local/apply.ts`, docs/03 §15 #84): a pull page was applied row by row – a
read of the stored row, a query of the outbox for that row, a write – about 3 IndexedDB requests per row, one after the
other. Now per table of the page: one `bulkGet`, one outbox query for the table, one `bulkPut` (the same rebase of
pending entries; `apply.test.ts`, `sync.test.ts` unchanged and green). The admin's first sync of the used e2e stack
(1,546 items, 5,087 map elements, their shares and children – about 10,000 rows) went from **12.2 s to 2.8 s**.

| 10,000 notes, a teammate's first sign-in (H1, before the follow-up below) | time |
|---|---|
| landing on `/maps`: until the indicator says *Live* | 2.2 s |
| then `/notes` until it counts 10,000 | 1.0 s |
| landing on `/notes` (the list open while the pages arrive): until it counts 10,000 | 11.8 s |
| search "chatbot" (2,000 hits, the 250 ms debounce included) / cleared | 0.33 s / 0.34 s |

**Fixed** (phase 7 follow-up, shared code `local/useLive.ts`, docs/03 §12.10, §15 #89). A list open during a large
first sync re-read all its rows after every page's transaction (Dexie `liveQuery` via `useLive`), and those reads
queued the next page's write. Instrumenting the notes list showed three heavy queries, not one: its rows, the type of
every item (`types`) and every share (`item_shares`) – 10,000 rows each, each re-running on its own. Now, while the
engine pulls (`status.syncing`, in every tab: boot.ts hands it to `setLivePulling`), a change only holds a live query
back; the held ones re-run **together, in bursts**, when the last burst has been over for **500 ms** (or for twice its
length, if longer – a burst's end taken after the task its answers render in), and **once right after the pull**.
Between bursts the pull's pages are written without waiting. Outside a pull every change re-runs a query at once, as
before. Dexie still calls the query on a change: `useLive` then reads nothing (it hands over the last run's result if
Dexie dropped it for a change during that run, so the count still grows) and subscribes anew with the burst – one real
run that observes again. Every list benefits (all of them read through `useLive`), so do the detail pages and the
sync indicator's counts.

| 10,000 notes, a teammate's first sign-in (`e2e/perf.spec.ts`, runs of the same session) | before | after |
|---|---|---|
| landing on `/notes` (the list open while the pages arrive): until it counts 10,000 | 12.8 / 12.1 / 11.3 s (H1: 11.8 s) | **4.6 / 3.3 s** |
| another teammate, the list closed: landing on `/maps` until *Live*, then `/notes` until it counts 10,000 | 1.8 + 0.9 / 1.7 + 0.8 s | 2.2 + 0.9 / 1.6 + 0.8 s |

Before and after alternated on the same machine (other test runs active on it, so single runs vary by a second or
two). The spec measures both cases now: the list-closed teammate signs in after the list-open one (warm server caches –
the comparison favours the closed case), so an open list now costs about a second over landing elsewhere, instead of
nine. A first version that spaced each query's re-runs on its own (500 ms after its last one) gained little – 11.3 and
7.1 s: the three heavy queries took turns and kept IndexedDB busy nearly all the pull (an instrumented run: 30 runs
adding up to 6.3 s of query time in a 6.3 s pull); bursts leave the pauses free for the pages.

In the bench (`tools/npm.sh run bench`: the 10,000 ideas and a share each applied in pages of 500 rows per table, one
transaction each as the engine does, into fake-indexeddb, with the list's three heavy queries; median of three):

| First sync of 10,000 ideas | list closed | list open, before | list open, after |
|---|---|---|---|
| no time between the pages: until the list counts all; the queries' runs | 229 ms; 3 | 1,874 ms; 66 | **365 ms; 6** |
| 50 ms per request: until the list counts all; the queries' runs | 1,224 ms; 3 | 1,992 ms; 66 | **1,496 ms; 12** |

(fake-indexeddb reads 10,000 rows faster than a browser's IndexedDB, which is why the real app gains more.)
`src/local/useLive.test.ts` counts the re-runs of a 16-page pull (bursts at least 500 ms apart and one after it; a run
per page without the coalescing) and checks that the held queries run together.

## 3. What was checked and left

- **Re-stamp on sharing a large map** (docs/05 §6): one `UPDATE … FROM unnest()` per table of the subtree, inside the
  write unit; a 1,000-element map re-stamps in one statement per table (unchanged).
- **Pull pages** stay at 500 rows per table (`Sync:PageLimit`); a first sync of 10,000 items is ~20 pages per table that
  has them, now without the empty tables' queries.
- For H2 (views, not changed here): the item list renders the page's rows with PrimeVue's DataTable; with 200 rows per
  page and many dynamic columns this, not the computation, is the larger cost of a keystroke.
