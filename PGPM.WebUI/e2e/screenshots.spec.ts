import { mkdirSync } from 'node:fs'
import { expect, test, type Browser, type BrowserContextOptions, type Page } from '@playwright/test'
import { generateNKeysBetween } from 'fractional-indexing'
import {
  Api,
  adminApi,
  localRows,
  pullAll,
  pushInserts,
  signInAs,
  uuidv7,
  type TestUser,
  type WireRow,
} from './helpers'

/**
 * The README's screenshots (docs/images): an innovation team's demo data – fifteen AI ideas in every stage of the funnel,
 * assessed with the playbook's ratings, a workshop board, the prioritization matrix and an *AI use case* project at its
 * first gate – pushed through the sync as the users' devices would, then photographed in the light and the dark theme
 * and on a phone. Only with `E2E_SCREENSHOTS=1`: `tools/screenshots.sh` runs it on a stack of its own and copies the
 * pictures from test-results/readme.
 */
test.skip(!process.env.E2E_SCREENSHOTS, 'The README screenshots: tools/screenshots.sh')
test.setTimeout(300_000)

const OUT = 'test-results/readme'
const DESKTOP: BrowserContextOptions = { viewport: { width: 1440, height: 900 }, deviceScaleFactor: 2 }
const PHONE: BrowserContextOptions = {
  viewport: { width: 390, height: 844 },
  deviceScaleFactor: 3,
  isMobile: true,
  hasTouch: true,
}

type Rows = { table: string; row: WireRow }[]

interface Idea {
  title: string
  status: string
  owner?: 'lena' | 'marco'
  unit: string
  impactField: string
  source: string
  problem: string
  /** The twelve feasibility ratings' level (0–5, varied a little around it) and the time to a proof of value. */
  feasibility?: { level: number; time: string }
  impact?: { reach: number; fit: number; band: number; advantages: number; value: number }
}

const ADVANTAGES = ['customer_experience', 'efficiency', 'quality', 'less_complexity', 'revenue_growth']
const BANDS = ['lt10k', 'k10_50', 'k50_200', 'k200_1m', 'gt1m']
const SPREAD = [0, -1, 0, 1, 0, -1, 1, 0, 0, -1, 1, 0]

const SHOWCASE = 'Customer e-mail triage'

const IDEAS: Idea[] = [
  {
    title: SHOWCASE,
    status: 'Ready for review',
    unit: 'Customer service',
    impactField: 'service_efficiency',
    source: 'process_analysis',
    problem: 'About 1,200 e-mails a day reach service@ and are sorted into 14 queues by hand.',
    feasibility: { level: 4.4, time: 'lt3' },
    impact: { reach: 4, fit: 5, band: 3, advantages: 4, value: 420_000 },
  },
  {
    title: 'Product description generator',
    status: 'Ready for review',
    owner: 'marco',
    unit: 'E-commerce',
    impactField: 'new_business',
    source: 'workshop',
    problem: '8,000 articles in the shop have no or only a supplier description.',
    feasibility: { level: 4, time: 'lt3' },
    impact: { reach: 3, fit: 4, band: 2, advantages: 3, value: 250_000 },
  },
  {
    title: 'Meeting minutes assistant',
    status: 'Selected',
    unit: 'All units',
    impactField: 'personal_productivity',
    source: 'genai_research',
    problem: 'Minutes are written late or not at all; decisions get lost.',
    feasibility: { level: 4.5, time: 'lt3' },
    impact: { reach: 4, fit: 2, band: 1, advantages: 1, value: 120_000 },
  },
  {
    title: 'Sales call summaries',
    status: 'Selected',
    unit: 'Sales',
    impactField: 'service_efficiency',
    source: 'crazy_8s',
    problem: 'Account managers spend an hour a day writing up calls for the CRM.',
    feasibility: { level: 3.8, time: 'lt3' },
    impact: { reach: 3, fit: 3, band: 2, advantages: 3, value: 200_000 },
  },
  {
    title: 'HR policy chatbot',
    status: 'Elaborating',
    unit: 'HR',
    impactField: 'personal_productivity',
    source: 'best_practice',
    problem: 'HR answers the same questions on holidays and expenses every week.',
    feasibility: { level: 3.5, time: 'm4_6' },
    impact: { reach: 3, fit: 2, band: 0, advantages: 2, value: 90_000 },
  },
  {
    title: 'Contract clause checker',
    status: 'Assessed',
    owner: 'marco',
    unit: 'Legal',
    impactField: 'process_innovation',
    source: 'process_analysis',
    problem: 'Supplier contracts wait two weeks for a first legal review.',
    feasibility: { level: 2, time: 'm7_9' },
    impact: { reach: 2, fit: 4, band: 3, advantages: 3, value: 300_000 },
  },
  {
    title: 'Spare-parts demand forecast',
    status: 'Assessed',
    unit: 'Logistics',
    impactField: 'process_innovation',
    source: 'customer_journey',
    problem: 'Stock-outs of spare parts stop customer machines for days.',
    feasibility: { level: 1.5, time: 'm10_12' },
    impact: { reach: 4, fit: 5, band: 4, advantages: 4, value: 600_000 },
  },
  {
    title: 'Churn early warning',
    status: 'Assessed',
    unit: 'Marketing',
    impactField: 'new_business',
    source: 'genai_research',
    problem: 'Customers leave without any sign we could act on.',
    feasibility: { level: 2, time: 'm10_12' },
    impact: { reach: 2, fit: 2, band: 1, advantages: 2, value: 110_000 },
  },
  {
    title: 'Code review helper',
    status: 'Watch',
    unit: 'IT',
    impactField: 'personal_productivity',
    source: 'genai_research',
    problem: 'Reviews of the in-house apps are slow and uneven.',
    feasibility: { level: 3, time: 'm4_6' },
    impact: { reach: 2, fit: 1, band: 0, advantages: 1, value: 80_000 },
  },
  {
    title: 'Automated credit decisions',
    status: 'Rejected',
    unit: 'Finance',
    impactField: 'process_innovation',
    source: 'other',
    problem: 'Credit checks for new business customers take three days.',
    feasibility: { level: 1, time: 'gt12' },
    impact: { reach: 3, fit: 1, band: 2, advantages: 2, value: 200_000 },
  },
  {
    title: 'Supplier risk radar',
    status: 'Elaborating',
    owner: 'marco',
    unit: 'Procurement',
    impactField: 'process_innovation',
    source: 'workshop',
    problem: 'News about suppliers in trouble reaches procurement too late.',
    feasibility: { level: 3, time: 'm4_6' },
  },
  {
    title: 'Onboarding buddy for new hires',
    status: 'Elaborating',
    unit: 'HR',
    impactField: 'personal_productivity',
    source: 'crazy_8s',
    problem: 'New colleagues need weeks to find their way around tools and people.',
  },
  {
    title: 'Field service photo inspection',
    status: 'New',
    unit: 'Field service',
    impactField: 'service_efficiency',
    source: 'customer_journey',
    problem: 'Technicians report damage in free text; parts are ordered twice.',
  },
  {
    title: 'Translation of technical manuals',
    status: 'New',
    owner: 'marco',
    unit: 'Engineering',
    impactField: 'service_efficiency',
    source: 'best_practice',
    problem: 'Manuals reach the French and Italian markets months after the German one.',
  },
  {
    title: 'Tender document summariser',
    status: 'New',
    unit: 'Sales',
    impactField: 'new_business',
    source: 'workshop',
    problem: 'Public tenders run to 200 pages; the bid decision takes too long.',
  },
]

const SHOWCASE_DESCRIPTION = `Around **1,200 customer e-mails** a day reach \`service@\` and are sorted by hand into 14 queues. A model reads
each e-mail as it arrives and

- classifies the request (order, complaint, change of address, …),
- extracts the customer and order number,
- routes it to the right queue, with its confidence – below 80 % a person decides.

Next step: a four-week test on last quarter's e-mails, together with the service desk.`

let admin: Api

test.beforeEach(async ({ baseURL }) => {
  admin = await adminApi(baseURL)
  mkdirSync(OUT, { recursive: true })
})

test.afterEach(async () => {
  await admin.dispose()
})

/** A user with a proper name, in the seeded groups; signed in through the API. */
async function member(
  baseURL: string | undefined,
  displayName: string,
  groupNames: string[],
): Promise<{ user: TestUser; api: Api }> {
  const groups = await admin.call<{ id: string; name: string }[]>('GET', '/api/v1/groups')
  const stamp = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 5)}`
  const username = `${displayName.split(' ')[0]!.toLowerCase()}-${stamp}`
  const created = await admin.call<{ user: { id: string }; temporaryPassword: { password: string } }>(
    'POST',
    '/api/v1/users',
    { username, displayName, groupIds: groupNames.map((n) => groups.find((g) => g.name === n)!.id) },
  )
  const password = `shots-${stamp}-pw`
  const api = await Api.create(baseURL)
  await api.signIn(username, created.temporaryPassword.password)
  await api.changePassword(created.temporaryPassword.password, password)
  return { user: { id: created.user.id, username, password, name: displayName }, api }
}

async function push(api: Api, rows: Rows) {
  for (let i = 0; i < rows.length; i += 200) {
    const results = await pushInserts(api, rows.slice(i, i + 200))
    expect(
      results.every((r) => r === 'accepted'),
      results.find((r) => r !== 'accepted'),
    ).toBe(true)
  }
}

async function device(browser: Browser, user: TestUser, path: string, options: BrowserContextOptions = DESKTOP) {
  const context = await browser.newContext(options)
  const page = await context.newPage()
  await signInAs(page, user, path)
  await expect(page.getByTestId('sync-indicator')).toContainText('Live')
  return page
}

/** The page as it settles: no spinner, transitions over, the pointer out of the way. */
async function shoot(page: Page, name: string, options: { fullPage?: boolean } = {}) {
  await expect(page.locator('.p-progressspinner')).toHaveCount(0)
  await page.mouse.move(0, 0)
  await page.waitForTimeout(700)
  await page.screenshot({ path: `${OUT}/${name}.png`, ...options })
}

/** The whole board in view, a step further out: the toolbars lie over the canvas's edges. */
async function fitWithMargin(page: Page) {
  await page.getByRole('button', { name: 'Zoom to fit' }).click()
  await page.getByRole('button', { name: 'Zoom out' }).click()
}

test('README screenshots', async ({ browser, baseURL }) => {
  const lena = await member(baseURL, 'Lena Fischer', ['Innovation team', 'Members'])
  const marco = await member(baseURL, 'Marco Rossi', ['Innovation team', 'Members'])
  const sara = await member(baseURL, 'Sara Meier', ['Review board', 'Members'])
  await sara.api.dispose()

  const config = await pullAll(lena.api)
  const field = (key: string) => {
    const found = config.field_definitions!.find((f) => f.key === key)
    if (!found) throw new Error(`The seeded field ${key} is missing`)
    return found
  }
  const status = (itemType: string, name: string) => {
    const found = config.statuses!.find((s) => s.itemType === itemType && s.name === name)
    if (!found) throw new Error(`The seeded status ${itemType} ${name} is missing`)
    return found
  }
  const groups = await admin.call<{ id: string; name: string }[]>('GET', '/api/v1/groups')
  const group = (name: string) => groups.find((g) => g.name === name)!
  const ideaSections = config
    .sections!.filter((s) => (s.defaultFor as string[]).includes('idea'))
    .sort((a, b) => (a.sortOrder as number) - (b.sortOrder as number))
  const feasibilityInputs = (field('feasibility_index').config as { inputs: string[] }).inputs.filter(
    (k) => k !== 'time_to_proof_of_value',
  )

  /** The playbook's assessment as field values of an item. */
  const assessment = (rows: Rows, itemId: string, idea: Pick<Idea, 'feasibility' | 'impact'>) => {
    const value = (key: string, v: unknown) =>
      rows.push({ table: 'field_values', row: { id: uuidv7(), itemId, fieldId: field(key).id, value: v } })
    if (idea.feasibility) {
      feasibilityInputs.forEach((key, i) =>
        value(key, Math.max(0, Math.min(5, Math.round(idea.feasibility!.level + SPREAD[i]! * 0.6)))),
      )
      value('time_to_proof_of_value', idea.feasibility.time)
    }
    if (idea.impact) {
      value('impact_reach', idea.impact.reach)
      value('impact_strategic', idea.impact.fit)
      value('impact_value_band', BANDS[idea.impact.band])
      value('strategic_advantages', ADVANTAGES.slice(0, idea.impact.advantages))
      value('business_value_chf', idea.impact.value)
    }
    return value
  }

  // --- The ideas, shared with the innovation team and every member ---
  const ideaIds = new Map<string, string>()
  for (const idea of IDEAS) {
    const id = uuidv7()
    ideaIds.set(idea.title, id)
    const showcase = idea.title === SHOWCASE
    const rows: Rows = [
      {
        table: 'items',
        row: {
          id,
          type: 'idea',
          title: idea.title,
          description: showcase ? SHOWCASE_DESCRIPTION : null,
          statusId: status('idea', idea.status).id,
          pinned: false,
          tags: [],
        },
      },
      { table: 'item_shares', row: { id: uuidv7(), itemId: id, groupId: group('Members').id, access: 'edit' } },
      {
        table: 'item_shares',
        row: { id: uuidv7(), itemId: id, groupId: group('Innovation team').id, access: 'edit' },
      },
      ...ideaSections.map((s, i) => ({
        table: 'item_sections',
        row: { id: uuidv7(), itemId: id, sectionId: s.id, sortOrder: (i + 1) * 10 },
      })),
    ]
    const value = assessment(rows, id, idea)
    value('problem', idea.problem)
    value('business_unit', idea.unit)
    value('impact_field', idea.impactField)
    value('idea_source', idea.source)
    if (showcase) {
      value(
        'data_basis',
        'The service mailbox of the last two years (about 600,000 e-mails) and the queue each went to.',
      )
      value(
        'expected_output',
        'A queue and a request type for every e-mail, the customer and order number, a confidence.',
      )
      value('transformation_level', 'redesign')
      value('ambition_contribution', 'Faster answers for our customers – the first goal of the AI ambition 2027.')
      value('value_assumptions', '3.5 FTE of sorting work, answers one day faster, fewer e-mails in the wrong queue.')
      value('risk_flags', ['human_in_the_loop'])
      value('eu_ai_act_class', 'limited')
      value('is_ai_system', true)
      value('data_classification', 'confidential')
      value('personal_data', true)
    }
    await push(idea.owner === 'marco' ? marco.api : lena.api, rows)
  }

  // Persons on the showcase idea: Lena and two from the business.
  const persons = [
    { name: 'Daniel Graf', job: 'Head of Customer Service', role: 'Sponsor' },
    { name: 'Nina Brunner', job: 'Data Steward', role: 'Data owner' },
  ].map((p) => ({ ...p, id: uuidv7() }))
  await push(
    admin,
    persons.map((p) => ({
      table: 'persons',
      row: {
        id: p.id,
        displayName: p.name,
        givenName: p.name.split(' ')[0],
        surname: p.name.split(' ')[1],
        jobTitle: p.job,
        department: 'Customer Service',
        isActive: true,
      },
    })),
  )
  const lenaPerson = (await pullAll(lena.api)).persons!.find((p) => p.userId === lena.user.id)!
  const showcaseId = ideaIds.get(SHOWCASE)!
  const keys = generateNKeysBetween(null, null, 3)
  await push(lena.api, [
    {
      table: 'assignments',
      row: { id: uuidv7(), itemId: showcaseId, personId: lenaPerson.id, role: 'Innovation team', sortOrder: keys[0] },
    },
    ...persons.map((p, i) => ({
      table: 'assignments',
      row: { id: uuidv7(), itemId: showcaseId, personId: p.id, role: p.role, sortOrder: keys[i + 1] },
    })),
  ])

  // --- The workshop board: the use-case funnel, a column per status, the idea cards in their columns ---
  const mapId = uuidv7()
  const [columnsLayer, ideasLayer] = [uuidv7(), uuidv7()]
  await push(lena.api, [
    {
      table: 'maps',
      row: {
        id: mapId,
        kind: 'canvas',
        name: 'AI use-case funnel – autumn workshop',
        settings: {
          background: '#ffffff',
          grid: false,
          snap: false,
          gridSize: 20,
          cardBadges: ['impact_score', 'feasibility_index'],
        },
      },
    },
    { table: 'map_shares', row: { id: uuidv7(), mapId, groupId: group('Members').id, access: 'edit' } },
    {
      table: 'map_layers',
      row: { id: columnsLayer, mapId, name: 'Columns', sortOrder: 'a0', isVisible: true, isLocked: true },
    },
    {
      table: 'map_layers',
      row: { id: ideasLayer, mapId, name: 'Ideas', sortOrder: 'a1', isVisible: true, isLocked: false },
    },
  ])
  const DECIDED = 1120
  const columns: [string, string, number][] = [
    ['New', '#64748b', 0],
    ['Elaborating', '#0ea5e9', 272],
    ['Assessed', '#6366f1', 544],
    ['Ready for review', '#8b5cf6', 816],
    ['Selected', '#16a34a', DECIDED],
    ['Watch', '#d97706', DECIDED + 272],
    ['Rejected', '#dc2626', DECIDED + 544],
  ]
  // Narrower than the package's template (300): fitted and a step further out, the cards stay above the zoom where they
  // show only their titles (0.45, maps/culling.ts).
  const CARD = { width: 232, height: 92, gap: 16 }
  const bodyHeight = 3 * (CARD.height + CARD.gap) + CARD.gap
  const zs = generateNKeysBetween(null, null, 60)
  let z = 0
  const element = (layerId: string, row: Record<string, unknown>): { table: string; row: WireRow } => ({
    table: 'map_elements',
    row: { id: uuidv7(), mapId, layerId, z: zs[z++]!, config: null, ...row },
  })
  const text = (x: number, y: number, width: number, height: number, t: string, style: Record<string, unknown>) =>
    element(columnsLayer, { kind: 'text', x, y, width, height, text: t, style: { align: 'left', ...style } })
  const board: Rows = [
    text(0, -170, 1920, 48, 'AI use-case funnel', { fontSize: 32, bold: true, textColor: '#0f172a' }),
    text(
      0,
      -118,
      1920,
      28,
      'From the long list to the two or three core ideas – the review board meets on 4 December.',
      {
        fontSize: 16,
        textColor: '#475569',
      },
    ),
    text(0, -56, 1072, 32, 'In the funnel', { fontSize: 16, bold: true, textColor: '#64748b' }),
    text(DECIDED, -56, 800, 32, 'Decided', { fontSize: 16, bold: true, textColor: '#64748b' }),
  ]
  for (const [name, colour, x] of columns) {
    board.push(
      element(columnsLayer, {
        kind: 'rect',
        x,
        y: 0,
        width: 256,
        height: 56,
        text: name,
        style: {
          fill: colour,
          stroke: colour,
          strokeWidth: 1,
          radius: 8,
          fontSize: 18,
          bold: true,
          textColor: '#ffffff',
          align: 'center',
        },
      }),
      element(columnsLayer, {
        kind: 'rect',
        x,
        y: 72,
        width: 256,
        height: bodyHeight,
        text: '',
        style: { fill: '#f8fafc', stroke: '#e2e8f0', strokeWidth: 1, radius: 8 },
      }),
    )
  }
  const cards = new Map<string, WireRow>()
  for (const [name, , x] of columns) {
    IDEAS.filter((i) => i.status === name).forEach((idea, n) => {
      const card = element(ideasLayer, {
        kind: 'item',
        itemId: ideaIds.get(idea.title),
        x: x + 12,
        y: 72 + CARD.gap + n * (CARD.height + CARD.gap),
        width: CARD.width,
        height: CARD.height,
        style: {},
        config: { size: 'compact' },
      })
      cards.set(idea.title, card.row)
      board.push(card)
    })
  }
  // A sticky note for the board, and an arrow from the strongest idea to it.
  const sticky = element(ideasLayer, {
    kind: 'rect',
    x: DECIDED,
    y: bodyHeight + 112,
    width: 256,
    height: 96,
    text: 'Review board, 4 Dec: decide on the two ideas ready for review',
    style: { fill: '#fef3c7', stroke: '#f59e0b', strokeWidth: 1, radius: 4, fontSize: 15, textColor: '#78350f' },
  })
  board.push(sticky)
  const from = cards.get(SHOWCASE)!
  const [ax, ay] = [(from.x as number) + CARD.width / 2, (from.y as number) + CARD.height]
  const [bx, by] = [DECIDED, (sticky.row.y as number) + 48]
  board.push(
    element(ideasLayer, {
      kind: 'line',
      x: Math.min(ax, bx),
      y: Math.min(ay, by),
      width: Math.abs(bx - ax),
      height: Math.abs(by - ay),
      points: [
        [ax - Math.min(ax, bx), ay - Math.min(ay, by)],
        [bx - Math.min(ax, bx), by - Math.min(ay, by)],
      ],
      startElementId: from.id,
      endElementId: sticky.row.id,
      style: { arrowEnd: 'arrow', stroke: '#f59e0b', strokeWidth: 2, strokeStyle: 'dashed' },
    }),
  )
  await push(lena.api, board)
  await marco.api.dispose()

  // --- Lena's browser ---
  const page = await device(browser, lena.user, '/ideas')

  // An AI use case project from its template, assessed, its first gate passed.
  await page.goto('/projects')
  await page.getByRole('button', { name: 'New project' }).click()
  const dialog = page.getByRole('dialog', { name: 'New project' })
  await dialog.getByLabel('Title').fill('Invoice recognition')
  await dialog.getByLabel('Template').click()
  await page.getByRole('option', { name: /AI use case/ }).click()
  await expect(dialog.getByLabel('Status')).toContainText('Preparing test')
  await dialog.getByRole('button', { name: 'Create project' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Invoice recognition' })).toBeVisible()
  const projectId = /\/projects\/([0-9a-f-]{36})/.exec(page.url())![1]!
  // The project on the server before its values: the browser's outbox sent.
  await expect.poll(async () => (await localRows(page, 'outbox')).length).toBe(0)
  const projectRows: Rows = []
  const projectValue = assessment(projectRows, projectId, {
    feasibility: { level: 4.2, time: 'lt3' },
    impact: { reach: 3, fit: 4, band: 3, advantages: 4, value: 380_000 },
  })
  projectValue('problem', 'Supplier invoices arrive as PDFs and are typed into the ERP by hand.')
  projectValue('business_unit', 'Finance')
  await push(lena.api, projectRows)
  await lena.api.dispose()

  await page.getByRole('link', { name: 'Milestones' }).click()
  const g0 = page.locator('[data-testid^="milestone-"]', { hasText: 'Ready for test' })
  // `all()` doesn't wait: the gate's checklist first.
  await expect(g0.getByRole('checkbox', { name: /^Done: / }).first()).toBeVisible()
  for (const box of await g0.getByRole('checkbox', { name: /^Done: / }).all()) await box.check()
  await expect(g0.getByTestId('dod-progress')).toHaveText('3 / 3')
  await g0.getByRole('radio', { name: 'Go', exact: true }).click()
  await g0.getByLabel('Decision note').fill('Sponsor, domain expert and delivery team named; four-week test agreed.')
  await g0.getByRole('button', { name: 'Record decision' }).click()
  await g0.getByRole('button', { name: 'Move project to Prototype / PoC' }).click()
  await expect(page.getByText('Prototype / PoC').first()).toBeVisible()
  await g0.getByRole('combobox', { name: 'Status of Ready for test' }).click()
  await page.getByRole('option', { name: 'Done', exact: true }).click()
  await expect(page.getByText('1 of 7 done')).toBeVisible()
  const g1 = page.locator('[data-testid^="milestone-"]', { hasText: 'PoC decision' })
  const g1Boxes = await g1.getByRole('checkbox', { name: /^Done: / }).all()
  for (const box of g1Boxes.slice(0, 2)) await box.check()
  await expect(g1.getByTestId('dod-progress')).toHaveText(`2 / ${g1Boxes.length}`)
  await page.reload()
  await expect(page.locator('[data-testid^="milestone-"]').first()).toBeVisible()
  await shoot(page, 'project-gates')

  // The matrix: business impact × ease of implementation.
  await page.goto('/maps')
  await page.getByRole('button', { name: 'New map' }).click()
  await page.getByRole('menuitem', { name: 'Matrix map' }).click()
  const matrixDialog = page.getByRole('dialog', { name: 'New matrix map' })
  await expect(matrixDialog).toContainText('Prioritization matrix')
  await matrixDialog.getByLabel('Name').fill('Prioritization – review board')
  await matrixDialog.getByRole('button', { name: 'Create map' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Prioritization – review board' })).toBeVisible()
  await expect(page.getByRole('button', { name: new RegExp(`${SHOWCASE}$`) })).toBeVisible()
  await shoot(page, 'matrix')

  // The workshop board.
  await page.goto(`/maps/${mapId}`)
  await expect(page.getByRole('button', { name: new RegExp(`${SHOWCASE}$`) })).toBeVisible()
  await page.getByRole('button', { name: 'Minimap' }).click()
  // Wide rather than tall: the board's own proportions, without empty canvas above and below.
  await page.setViewportSize({ width: 1440, height: 760 })
  await fitWithMargin(page)
  await shoot(page, 'map-funnel')
  await page.setViewportSize(DESKTOP.viewport!)

  // The ideas list and the showcase idea.
  await page.goto('/ideas')
  await expect(page.getByRole('row', { name: new RegExp(SHOWCASE) })).toBeVisible()
  await page.getByRole('button', { name: 'Columns' }).click()
  const chooser = page.getByRole('group', { name: 'Columns' })
  for (const column of ['Business unit', 'Impact score', 'Feasibility index'])
    await chooser.getByLabel(column, { exact: true }).check()
  await chooser.getByLabel('Modified', { exact: true }).uncheck()
  await page.keyboard.press('Escape')
  await expect(chooser).toBeHidden()
  await shoot(page, 'ideas-list')
  await page.getByRole('link', { name: SHOWCASE }).click()
  await expect(page.getByRole('heading', { level: 1, name: SHOWCASE })).toBeVisible()
  await shoot(page, 'idea')
  const ideaUrl = page.url()

  // Its Feasibility section on its own, in a window wide enough for each rating on one line and tall enough for the
  // section below the page's sticky header.
  const wide = await device(browser, lena.user, new URL(ideaUrl).pathname, {
    ...DESKTOP,
    viewport: { width: 1920, height: 1600 },
  })
  const feasibility = wide.locator('section', { has: wide.getByRole('button', { name: 'Feasibility', exact: true }) })
  await expect(feasibility.getByLabel('Feasibility index: 56 of 65').first()).toBeVisible()
  await feasibility.evaluate((section) => section.scrollIntoView({ block: 'center' }))
  await wide.mouse.move(0, 0)
  await wide.waitForTimeout(700)
  await feasibility.screenshot({ path: `${OUT}/idea-feasibility.png` })

  // The same idea in the dark theme, and on a phone.
  const dark = await device(browser, lena.user, new URL(ideaUrl).pathname, { ...DESKTOP, colorScheme: 'dark' })
  await expect(dark.getByRole('heading', { level: 1, name: SHOWCASE })).toBeVisible()
  await shoot(dark, 'idea-dark')

  const phone = await device(browser, lena.user, new URL(ideaUrl).pathname, PHONE)
  await expect(phone.getByRole('heading', { level: 1, name: SHOWCASE })).toBeVisible()
  await shoot(phone, 'phone-idea')
  await phone.goto('/ideas')
  await expect(phone.getByText(SHOWCASE).first()).toBeVisible()
  await shoot(phone, 'phone-ideas')
})
