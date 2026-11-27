import { expect, test, type Page } from '@playwright/test'
import { generateNKeysBetween } from 'fractional-indexing'
import {
  Api,
  createTeam,
  pushInserts,
  signInAs,
  signOut,
  uuidv7,
  type Team,
  type TestUser,
  type WireRow,
} from './helpers'

/**
 * The real app's frame times on a 1,000-element map (docs/05 §3 phase 7, docs/07 §2; spike S2 measured the playground):
 * a canvas like the spike's stress map – 600 item cards of 300 notes, 300 rectangles, ellipses and texts, 100
 * connected lines – pushed through the sync, opened as its user would, then panned (middle button) and zoomed (wheel)
 * with real input events while a requestAnimationFrame loop records every frame. Prints p50 / p95 / max and fps per
 * phase. Only with `E2E_PERF=1` (tools/e2e.sh passes it on): `E2E_PERF=1 tools/e2e.sh perf.spec.ts`.
 */
test.skip(!process.env.E2E_PERF, 'A measurement: E2E_PERF=1 tools/e2e.sh perf.spec.ts')
test.setTimeout(180_000)

let team: Team

test.beforeEach(async ({ baseURL }) => {
  team = await createTeam(baseURL, ['Pia', 'Quinn', 'Rosa'], { Notes: 'Create', Maps: 'Create' })
})

test.afterEach(async () => {
  await team.admin.dispose()
})

/** Rows in batches of at most 200 (Sync:MaxMutations), each accepted. */
async function push(api: Api, rows: { table: string; row: WireRow }[]) {
  for (let i = 0; i < rows.length; i += 200) {
    const results = await pushInserts(api, rows.slice(i, i + 200))
    expect(
      results.every((r) => r === 'accepted'),
      results.find((r) => r !== 'accepted'),
    ).toBe(true)
  }
}

/** The stress map of spike S2 (src/maps/playground/sampleData.ts `stressMap`), through the sync; answers its id. */
async function stressMap(api: Api, groupId: string, count = 1000): Promise<string> {
  const notes = Array.from({ length: 300 }, (_, i) => ({ id: uuidv7(), title: `Card ${i}` }))
  for (const note of notes) {
    await push(api, [
      {
        table: 'items',
        row: { id: note.id, type: 'note', title: note.title, description: null, pinned: false, tags: [] },
      },
      { table: 'item_shares', row: { id: uuidv7(), itemId: note.id, groupId, access: 'edit' } },
    ])
  }
  const mapId = uuidv7()
  const layerId = uuidv7()
  await push(api, [
    { table: 'maps', row: { id: mapId, kind: 'canvas', name: 'Stress map', settings: { grid: false } } },
    { table: 'map_shares', row: { id: uuidv7(), mapId, groupId, access: 'edit' } },
    {
      table: 'map_layers',
      row: { id: layerId, mapId, name: 'Base', sortOrder: 'a0', isVisible: true, isLocked: false },
    },
  ])
  const zs = generateNKeysBetween(null, null, count)
  let seed = 42
  const random = () => ((seed = (seed * 16807) % 2147483647) - 1) / 2147483646
  const elements: WireRow[] = []
  const cards: WireRow[] = []
  for (let i = 0; elements.length < count; i++) {
    const x = (i % 40) * 270 + Math.round(random() * 20)
    const y = Math.floor(i / 40) * 170 + Math.round(random() * 20)
    const kind = random()
    const common = { id: uuidv7(), mapId, layerId, z: zs[elements.length]!, x, y, config: null }
    if (kind < 0.6) {
      const card = {
        ...common,
        kind: 'item',
        itemId: notes[i % notes.length]!.id,
        width: 220,
        height: 92,
        style: {},
        config: { size: 'compact' },
      }
      cards.push(card)
      elements.push(card)
    } else if (kind < 0.72) {
      elements.push({
        ...common,
        kind: 'rect',
        width: 200,
        height: 110,
        text: `Box ${i}`,
        style: { fill: 'blue', radius: 8 },
      })
    } else if (kind < 0.8) {
      elements.push({ ...common, kind: 'ellipse', width: 180, height: 110, text: 'Topic', style: {} })
    } else if (kind < 0.9) {
      elements.push({
        ...common,
        kind: 'text',
        y: y + 30,
        width: 220,
        height: 40,
        text: `Sticky thought ${i}`,
        style: { fontSize: 16 },
      })
    } else if (cards.length > 2) {
      const a = cards[cards.length - 1]!
      const b = cards[Math.max(0, cards.length - 1 - Math.ceil(random() * 8))]!
      if (a.id === b.id) continue
      const [ax, ay, bx, by] = [a.x as number, a.y as number, b.x as number, b.y as number]
      elements.push({
        ...common,
        kind: 'line',
        x: Math.min(ax, bx),
        y: Math.min(ay, by),
        width: Math.max(1, Math.abs(bx - ax)),
        height: Math.max(1, Math.abs(by - ay)),
        points: [
          [ax - Math.min(ax, bx), ay - Math.min(ay, by)],
          [bx - Math.min(ax, bx), by - Math.min(ay, by)],
        ],
        startElementId: a.id,
        endElementId: b.id,
        style: { arrowEnd: 'arrow' },
      })
    }
  }
  await push(
    api,
    elements.map((row) => ({ table: 'map_elements', row })),
  )
  return mapId
}

interface Phase {
  name: string
  /** The zoom and the elements drawn after the phase. */
  zoom: number
  drawn: number
  frames: number
  fps: number
  p50: number
  p95: number
  max: number
}

/** Frame times while `gesture` runs: a requestAnimationFrame loop in the page, started before and stopped after it. */
async function measure(page: Page, name: string, gesture: () => Promise<void>): Promise<Phase> {
  await page.waitForTimeout(600)
  await page.evaluate(() => {
    const w = window as unknown as { pgpmFrames: number[]; pgpmRecording: boolean }
    w.pgpmFrames = []
    w.pgpmRecording = true
    const tick = (t: number) => {
      if (!w.pgpmRecording) return
      w.pgpmFrames.push(t)
      requestAnimationFrame(tick)
    }
    requestAnimationFrame(tick)
  })
  await gesture()
  const stamps = await page.evaluate(() => {
    const w = window as unknown as { pgpmFrames: number[]; pgpmRecording: boolean }
    w.pgpmRecording = false
    return w.pgpmFrames
  })
  const times = stamps.slice(1).map((t, i) => t - stamps[i]!)
  const sorted = [...times].sort((a, b) => a - b)
  const at = (q: number) => sorted[Math.min(sorted.length - 1, Math.floor(q * sorted.length))] ?? 0
  const span = (stamps[stamps.length - 1]! - stamps[0]!) / 1000
  const round = (n: number) => Math.round(n * 10) / 10
  const canvas = page.getByTestId('map-canvas')
  const transform = (await canvas.locator('svg.surface > g').first().getAttribute('transform')) ?? ''
  const zoom = Number(/scale\(([^)]+)\)/.exec(transform)?.[1] ?? Number.NaN)
  return {
    name,
    zoom: Math.round(zoom * 100) / 100,
    drawn: await canvas.locator('[data-element-id]').count(),
    frames: times.length,
    fps: round(times.length / span),
    p50: round(at(0.5)),
    p95: round(at(0.95)),
    max: round(sorted.at(-1) ?? 0),
  }
}

test('a 1,000-element map pans and zooms fluidly in the real app', async ({ page, baseURL }) => {
  const user = team.users[0]!
  const api = await Api.create(baseURL)
  await api.signIn(user.username, user.password)
  const mapId = await stressMap(api, team.group.id)
  await api.dispose()

  await signInAs(page, user, `/maps/${mapId}`)
  await expect(page.getByRole('heading', { level: 1, name: 'Stress map' })).toBeVisible()
  const svg = page.getByTestId('map-canvas').locator('svg.surface')
  await expect(svg).toBeVisible()
  const drawn = () => page.getByTestId('map-canvas').locator('[data-element-id]').count()
  await expect.poll(drawn, { timeout: 30_000 }).toBeGreaterThan(100)
  // E2E_PERF_THROTTLE=4: a slow laptop, as spike S2 measured it (Chromium's CPU throttling).
  const throttle = Number(process.env.E2E_PERF_THROTTLE ?? 1)
  if (throttle > 1) {
    const cdp = await page.context().newCDPSession(page)
    await cdp.send('Emulation.setCPUThrottlingRate', { rate: throttle })
  }
  const box = (await svg.boundingBox())!
  const cx = box.x + box.width / 2
  const cy = box.y + box.height / 2

  const pan = async (dx: number, dy: number, steps: number) => {
    await page.mouse.move(cx, cy)
    await page.mouse.down({ button: 'middle' })
    await page.mouse.move(cx + dx, cy + dy, { steps })
    await page.mouse.move(cx, cy, { steps })
    await page.mouse.up({ button: 'middle' })
  }
  // A notch every 50 ms, as a hand turns a wheel: each one's frame is measured among the idle ones around it.
  const wheel = async (deltaY: number, notches: number) => {
    await page.mouse.move(cx, cy)
    for (let i = 0; i < notches; i++) {
      await page.mouse.wheel(0, deltaY)
      await page.waitForTimeout(50)
    }
  }

  const phases: Phase[] = []
  // The map opens fitted (about 12 %, cards in LOD); 1.2× per notch: 8 notches ≈ 50 % (full detail, culled), 4 more ≈ 100 %.
  phases.push(await measure(page, 'pan the whole map (opening view, LOD)', () => pan(-300, -150, 120)))
  phases.push(await measure(page, 'zoom in to about 50 % (crosses LOD), 8 notches', () => wheel(-100, 8)))
  phases.push(await measure(page, 'pan at about 50 % (full detail)', () => pan(-700, -350, 120)))
  phases.push(await measure(page, 'zoom in to about 100 %, 4 notches', () => wheel(-100, 4)))
  phases.push(await measure(page, 'pan at about 100 %', () => pan(-700, -350, 120)))
  phases.push(await measure(page, 'zoom out to the whole map, 12 notches', () => wheel(100, 12)))

  const report = [
    `1,000 elements, CPU throttling ${throttle}×; ${await page.evaluate(() => navigator.userAgent)}`,
    ...phases.map(
      (p) =>
        `${p.name}: ${p.fps} fps, p50 ${p.p50} ms, p95 ${p.p95} ms, max ${p.max} ms (${p.frames} frames; zoom ${p.zoom}, ${p.drawn} drawn)`,
    ),
  ].join('\n')
  console.log(report)
  test.info().annotations.push({ type: 'frames', description: report })
  // A guard, not the target (60 fps): a regression to a frame in 5 that misses 100 ms fails.
  for (const p of phases) expect(p.p95, `${p.name}\n${report}`).toBeLessThan(100)
})

const words = [
  'website',
  'relaunch',
  'chatbot',
  'invoice',
  'forecast',
  'portal',
  'onboarding',
  'pricing',
  'archive',
  'survey',
]

/**
 * 10,000 notes (docs/05 §3 phase 7: "10,000 items in lists"), shared with the team through the sync by one user; a
 * teammate's first sign-in: the time until the notes list counts all of them (sign-in, first sync into IndexedDB, the
 * list), then a search typed into the list (its 250 ms debounce included) and cleared again. Then a third teammate's
 * first sign-in with the list closed (docs/07 §2.3): landing on the maps until the sync is live, then the notes list.
 */
test('10,000 notes: the first sync, the list and its search in the real app', async ({ page, baseURL }) => {
  test.setTimeout(300_000)
  const [pia, quinn, rosa] = team.users as [TestUser, TestUser, TestUser]
  const api = await Api.create(baseURL)
  await api.signIn(pia.username, pia.password)
  const count = 10_000
  let started = Date.now()
  const rows: { table: string; row: WireRow }[] = []
  for (let i = 0; i < count; i++) {
    const id = uuidv7()
    const title = `${words[i % words.length]} ${words[(i * 7) % words.length]} note ${i}`
    rows.push({ table: 'items', row: { id, type: 'note', title, description: null, pinned: false, tags: [] } })
    rows.push({ table: 'item_shares', row: { id: uuidv7(), itemId: id, groupId: team.group.id, access: 'edit' } })
  }
  await push(api, rows)
  await api.dispose()
  const seeded = Date.now() - started
  const hits = Array.from(
    { length: count },
    (_, i) => `${words[i % words.length]} ${words[(i * 7) % words.length]}`,
  ).filter((t) => t.includes('chatbot')).length

  started = Date.now()
  await signInAs(page, quinn, '/notes')
  const report = page.getByText(/^\d+–\d+ of \d+$/)
  await expect(report).toHaveText(new RegExp(` of ${count}$`), { timeout: 120_000 })
  const listed = Date.now() - started
  await expect(page.getByTestId('sync-indicator')).toContainText('Live', { timeout: 60_000 })

  const search = page.getByRole('textbox', { name: 'Search notes…' })
  started = Date.now()
  await search.fill('chatbot')
  await expect(report).toHaveText(new RegExp(` of ${hits}$`))
  const searched = Date.now() - started
  started = Date.now()
  await search.fill('')
  await expect(report).toHaveText(new RegExp(` of ${count}$`))
  const cleared = Date.now() - started

  // The list closed meanwhile: signing out wipes this browser's data, the next teammate's first sync starts from 0.
  await signOut(page)
  started = Date.now()
  await signInAs(page, rosa, '/maps')
  await expect(page.getByTestId('sync-indicator')).toContainText('Live', { timeout: 120_000 })
  const synced = Date.now() - started
  started = Date.now()
  await page.getByRole('link', { name: 'Notes', exact: true }).click()
  await expect(report).toHaveText(new RegExp(` of ${count}$`), { timeout: 60_000 })
  const opened = Date.now() - started

  const text = [
    `${count} notes pushed in ${seeded} ms (${count * 2} rows)`,
    `teammate: sign-in to the list counting ${count}: ${listed} ms`,
    `search "chatbot" (${hits} hits, 250 ms debounce included): ${searched} ms; cleared: ${cleared} ms`,
    `another teammate, the list closed: sign-in on /maps until Live ${synced} ms, then /notes counting ${count}: ${opened} ms`,
  ].join('\n')
  console.log(text)
  test.info().annotations.push({ type: 'list', description: text })
  expect(searched).toBeLessThan(2_000)
})
