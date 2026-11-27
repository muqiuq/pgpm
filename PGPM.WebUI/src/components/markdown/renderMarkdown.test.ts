import type { Element, ElementContent, Root } from 'hast'
import { createPinia, setActivePinia } from 'pinia'
import { flushPromises } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { mountWithUi } from '@/test/mount'
import MarkdownView from './MarkdownView.vue'
import { compactBlocks, renderMarkdown } from './renderMarkdown'
import { fileUrlResolverKey, type FileUrlResolver } from './services'

// PNMTD docs/locations-knowledge-plan.md §12: nothing executable comes out, external images become links,
// PGPM's images stay, file links become chips.

function elements(node: Root | Element, out: Element[] = []): Element[] {
  for (const child of node.children as ElementContent[]) {
    if (child.type === 'element') {
      out.push(child)
      elements(child, out)
    }
  }
  return out
}

const dangerousUrl = /^\s*(javascript|vbscript|data|file):/i

/** Everything an attack could leave behind, in the tree and in the DOM. */
function assertInert(markdown: string) {
  const tree = renderMarkdown(markdown)
  for (const el of elements(tree)) {
    expect([
      'script',
      'style',
      'iframe',
      'object',
      'embed',
      'svg',
      'math',
      'form',
      'base',
      'meta',
      'link',
    ]).not.toContain(el.tagName)
    for (const [key, value] of Object.entries(el.properties)) {
      expect(key.toLowerCase().startsWith('on'), `${el.tagName}.${key}`).toBe(false)
      if (['href', 'src', 'action', 'formAction', 'xlinkHref'].includes(key)) {
        expect(String(value)).not.toMatch(dangerousUrl)
      }
    }
  }

  const view = mountWithUi(MarkdownView, { props: { source: markdown } })
  const root = view.element as HTMLElement
  expect(root.querySelectorAll('script, style, iframe, object, embed, svg, math, form, base, meta, link')).toHaveLength(
    0,
  )
  for (const el of Array.from(root.querySelectorAll('*'))) {
    for (const attr of Array.from(el.attributes)) {
      expect(attr.name.startsWith('on'), `${el.tagName} ${attr.name}`).toBe(false)
      if (['href', 'src', 'action', 'formaction', 'xlink:href'].includes(attr.name)) {
        expect(attr.value).not.toMatch(dangerousUrl)
      }
    }
  }
  view.unmount()
  return root
}

const corpus = [
  '[click](javascript:alert(1))',
  '[click](JaVaScRiPt:alert(1))',
  '[click](  javascript:alert(1))',
  '[click](java&#09;script:alert(1))',
  '[click](&#106;avascript:alert(1))',
  '[click](javascript&colon;alert(1))',
  '[click](vbscript:msgbox(1))',
  '[click](data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==)',
  '[click](file:///etc/passwd)',
  '<javascript:alert(1)>',
  '[ref]: javascript:alert(1)\n\n[click][ref]',
  '![x](javascript:alert(1))',
  '![x](data:image/svg+xml;base64,PHN2ZyBvbmxvYWQ9YWxlcnQoMSk+)',
  '<script>alert(1)</script>',
  '<img src=x onerror=alert(1)>',
  'Inline <img src=x onerror=alert(1)> image',
  '<svg onload=alert(1)><circle r=5 /></svg>',
  '<a href="javascript:alert(1)">x</a>',
  '<iframe src="https://evil.example"></iframe>',
  '<style>body { display: none }</style>',
  '<form action="https://evil.example"><input name=x></form>',
  '<details open ontoggle=alert(1)>x</details>',
  '<math><mi xlink:href="javascript:alert(1)">x</mi></math>',
  '&lt;script&gt;alert(1)&lt;/script&gt;',
  '<!-- <script>alert(1)</script> -->',
  '[x](https://example.com "t\\" onmouseover=\\"alert(1)")',
  '```html\n<script>alert(1)</script>\n```',
  '| a |\n|---|\n| <img src=x onerror=alert(1)> |',
  '- [ ] <b onclick=alert(1)>task</b>',
  '[^1]\n\n[^1]: <script>alert(1)</script>',
]

describe('renderMarkdown', () => {
  beforeEach(() => setActivePinia(createPinia()))

  it.each(corpus)('leaves nothing executable: %s', (markdown) => {
    assertInert(markdown)
  })

  it('shows raw HTML as text', () => {
    const root = assertInert('<script>alert(1)</script>\n\nand <b>bold</b>')
    expect(root.textContent).toContain('<script>alert(1)</script>')
    expect(root.textContent).toContain('<b>bold</b>')
    expect(root.querySelector('b')).toBeNull()
  })

  it('renders the visual editor’s empty lines (a bare <br />) as line breaks, not text', () => {
    // What Crepe saves for "This is nice", two empty lines, then an image.
    const root = assertInert('This is nice\n\n<br />\n\n<BR>\n\nline one<br/>line two')
    expect(root.textContent).not.toContain('<br')
    expect(root.textContent).not.toContain('<BR')
    const paragraphs = Array.from(root.querySelectorAll('p'))
    expect(paragraphs.map((p) => p.innerHTML.replace(/\n/g, ''))).toEqual([
      'This is nice',
      '<br>',
      '<br>',
      'line one<br>line two',
    ])
  })

  it('keeps a <br> with anything more as text', () => {
    const root = assertInert('<br onclick="alert(1)">\n\na <br class="x"> b')
    expect(root.querySelectorAll('br')).toHaveLength(0)
    expect(root.textContent).toContain('<br onclick="alert(1)">')
    expect(root.textContent).toContain('<br class="x">')
  })

  it('breaks the line at a bare <br> next to other HTML, which stays text', () => {
    const root = assertInert('<br><script>alert(1)</script>')
    expect(root.querySelectorAll('br')).toHaveLength(1)
    expect(root.textContent).toContain('<script>alert(1)</script>')
  })

  it('makes a link with a forbidden scheme its text', () => {
    const root = assertInert('[click me](javascript:alert(1))')
    expect(root.querySelector('a')).toBeNull()
    expect(root.textContent).toBe('click me')
  })

  it('keeps http(s), mailto: and tel: links; external ones open in a new tab', () => {
    const view = mountWithUi(MarkdownView, {
      props: { source: '[web](https://example.com) [mail](mailto:a@example.com) [phone](tel:+41441234567)' },
    })
    const [web, mail, phone] = view.findAll('a')
    expect(web!.attributes('href')).toBe('https://example.com')
    expect(web!.attributes('target')).toBe('_blank')
    expect(web!.attributes('rel')).toBe('noopener noreferrer nofollow')
    expect(mail!.attributes('href')).toBe('mailto:a@example.com')
    expect(phone!.attributes('href')).toBe('tel:+41441234567')
    view.unmount()
  })

  it('treats a protocol-relative link as external', () => {
    const view = mountWithUi(MarkdownView, { props: { source: '[x](//evil.example/path)' } })
    expect(view.find('a').attributes('target')).toBe('_blank')
    view.unmount()
  })

  it("shows PGPM's images, lazily, and other images as links that aren't loaded", () => {
    const view = mountWithUi(MarkdownView, {
      props: {
        source: '![Cage B](/api/v1/files/0193f3c2-aa/cage-b.png)\n\n![logo](https://tracker.example/pixel.png)',
      },
    })
    const images = view.findAll('img')
    expect(images).toHaveLength(1)
    expect(images[0]!.attributes('src')).toBe('/api/v1/files/0193f3c2-aa/cage-b.png')
    expect(images[0]!.attributes('loading')).toBe('lazy')
    const external = view.find('a.md-external-image')
    expect(external.attributes('href')).toBe('https://tracker.example/pixel.png')
    expect(external.text()).toBe('external image, not loaded: logo')
    view.unmount()
  })

  it('makes links to files that are not images attachment chips, with their size when known', () => {
    const files = [{ id: '0194ab', name: 'ups-manual.pdf', size: 2_516_582, contentType: 'application/pdf' }]
    const view = mountWithUi(MarkdownView, {
      props: {
        source: '[UPS manual.pdf](/api/v1/files/0194ab/ups-manual.pdf) and [old](/api/v1/files/0195cd/old.zip)',
        files,
      },
    })
    const chips = view.findAll('.attachment-chip')
    expect(chips).toHaveLength(2)
    expect(chips[0]!.attributes('href')).toBe('/api/v1/files/0194ab/ups-manual.pdf')
    expect(chips[0]!.attributes('download')).toBeDefined()
    expect(chips[0]!.text()).toContain('UPS manual.pdf')
    expect(chips[0]!.text()).toContain('2.4 MB')
    // Not among the page's files: removed.
    expect(chips[1]!.text()).toContain('File removed')
    view.unmount()
  })

  it.each([
    '/projects/0193f3c2-aa',
    '/ideas/0193f3c2-aa',
    '/leads/0193f3c2-aa',
    '/notes/0193f3c2-aa',
    '/maps/0193f3c2-aa?focus=0194',
    '/persons/0193f3c2-aa',
    '/projects/0193f3c2-aa/milestones',
    '/ideas',
  ])("routes links to PGPM's items through the router: %s", (href) => {
    const tree = renderMarkdown(`[P-0042 Website relaunch](${href})`)
    const link = elements(tree).find((e) => e.tagName === 'pgpm-link')
    expect(link?.properties.to).toBe(href)
    expect(elements(tree).some((e) => e.tagName === 'a')).toBe(false)
  })

  it('leaves other relative links as ordinary links, in the same tab', () => {
    for (const href of ['/projectsx/1', '/admin/users', '/trash', 'projects/1']) {
      const tree = renderMarkdown(`[x](${href})`)
      expect(
        elements(tree).some((e) => e.tagName === 'pgpm-link'),
        href,
      ).toBe(false)
      const link = elements(tree).find((e) => e.tagName === 'a')
      expect(link?.properties.href, href).toBe(href)
      expect(link?.properties.target, href).toBeUndefined()
    }
  })

  it('renders an item link as a RouterLink', () => {
    const view = mountWithUi(MarkdownView, {
      props: { source: 'See [P-0042 Website relaunch](/projects/0193).' },
      global: { stubs: { RouterLink: { props: ['to'], template: '<a class="router-link" :href="to"><slot /></a>' } } },
    })
    const link = view.find('a.router-link')
    expect(link.attributes('href')).toBe('/projects/0193')
    expect(link.text()).toBe('P-0042 Website relaunch')
    view.unmount()
  })

  it("prefixes heading ids and #anchors, so a page can't clobber the app's ids", () => {
    const view = mountWithUi(MarkdownView, {
      props: { source: '# app\n\n## Drivers & queues\n\n## app\n\n[up](#app)' },
    })
    const ids = view.findAll('h1, h2').map((h) => h.attributes('id'))
    expect(ids).toEqual(['user-content-app', 'user-content-drivers--queues', 'user-content-app-1'])
    expect(view.find('a').attributes('href')).toBe('#user-content-app')
    view.unmount()
  })

  it('prefixes footnote ids and their links alike', () => {
    const view = mountWithUi(MarkdownView, { props: { source: 'Text[^n].\n\n[^n]: The note.' } })
    const ref = view.find('a[data-footnote-ref]')
    const target = ref.attributes('href')!.slice(1)
    expect(target.startsWith('user-content-')).toBe(true)
    expect(view.find(`[id="${target}"]`).exists()).toBe(true)
    view.unmount()
  })

  it('shows task boxes disabled', () => {
    const view = mountWithUi(MarkdownView, { props: { source: '- [x] done\n- [ ] open' } })
    const boxes = view.findAll('input[type="checkbox"]')
    expect(boxes).toHaveLength(2)
    for (const box of boxes) {
      expect((box.element as HTMLInputElement).disabled).toBe(true)
    }
    expect((boxes[0]!.element as HTMLInputElement).checked).toBe(true)
    view.unmount()
  })

  it('highlights code, with a copy button, and wraps tables to scroll', () => {
    const view = mountWithUi(MarkdownView, {
      props: { source: '```bash\necho "hi"\n```\n\n| a | b |\n|---|---|\n| 1 | 2 |' },
    })
    expect(view.find('.md-code button[aria-label="Copy the bash code"]').exists()).toBe(true)
    expect(view.find('pre code.language-bash .hljs-built_in, pre code.language-bash .hljs-string').exists()).toBe(true)
    expect(view.find('.md-table > table').exists()).toBe(true)
    view.unmount()
  })

  it('leaves mermaid blocks for the diagram component, with their source', () => {
    const tree = renderMarkdown('```mermaid\nflowchart LR\n  a --> b\n```')
    const diagram = elements(tree).find((e) => e.tagName === 'pgpm-mermaid')
    expect(diagram?.properties.source).toBe('flowchart LR\n  a --> b\n')
    expect(elements(tree).some((e) => e.tagName === 'pre')).toBe(false)
  })

  it('numbers diagrams, so that only the first 50 draw by themselves', () => {
    const tree = renderMarkdown(
      Array.from({ length: 52 }, (_, i) => `\`\`\`mermaid\npie\n"x": ${i}\n\`\`\``).join('\n\n'),
    )
    const indexes = elements(tree)
      .filter((e) => e.tagName === 'pgpm-mermaid')
      .map((e) => e.properties.index)
    expect(indexes).toHaveLength(52)
    expect(indexes[51]).toBe(51)
  })
})

describe('compact (map cards)', () => {
  beforeEach(() => setActivePinia(createPinia()))

  const long = [
    '# Workshop',
    'First paragraph.',
    '```mermaid\nflowchart LR\n  a --> b\n```',
    '- one\n- two',
    'Never shown.',
  ].join('\n\n')

  it('keeps the first blocks, marks the cut, and draws no diagram', () => {
    const tree = renderMarkdown(long, { compact: 3 })
    const top = tree.children.filter((c): c is Element => c.type === 'element')
    expect(top.map((e) => e.tagName)).toEqual(['h1', 'p', 'p', 'p'])
    expect(elements(tree).some((e) => e.tagName === 'pgpm-mermaid')).toBe(false)
    expect(top[2]!.properties.className).toEqual(['md-compact-diagram'])
    expect(top[3]!.properties.className).toEqual(['md-more'])
    expect(JSON.stringify(tree)).not.toContain('Never shown')
  })

  it('shows all of a short text without the cut', () => {
    const tree = renderMarkdown('One.\n\nTwo.', { compact: 3 })
    expect(elements(tree).some((e) => (e.properties.className as string[] | undefined)?.includes('md-more'))).toBe(
      false,
    )
  })

  it('gives headings no ids and code no copy button, and doesn’t load Mermaid', () => {
    const view = mountWithUi(MarkdownView, {
      props: { source: '## Plan\n\n```bash\necho hi\n```\n\n```mermaid\npie\n"a": 1\n```', compact: true },
    })
    expect(view.classes()).toContain('compact')
    expect(view.find('h2').attributes('id')).toBeUndefined()
    expect(view.find('pre code.language-bash').exists()).toBe(true)
    expect(view.find('.md-code button').exists()).toBe(false)
    expect(view.find('.md-diagram').exists()).toBe(false)
    expect(view.text()).toContain('Diagram')
    view.unmount()
  })

  it(`takes ${compactBlocks} blocks when compact is true`, () => {
    const view = mountWithUi(MarkdownView, {
      props: { source: ['a', 'b', 'c', 'd', 'e'].join('\n\n'), compact: true },
    })
    expect(view.findAll('p').map((p) => p.text())).toEqual(['a', 'b', 'c', '…'])
    view.unmount()
  })

  it('shows images only where this device has them, never opening them with a click', async () => {
    const resolve = vi.fn<FileUrlResolver>(async (url, options) =>
      options?.localOnly && url.includes('/remote/') ? null : `blob:local/${url.split('/')[4]}`,
    )
    const view = mountWithUi(MarkdownView, {
      props: {
        source: '![Here](/api/v1/files/local/here.png)\n\n![There](/api/v1/files/remote/there.png)',
        compact: true,
      },
      global: { provide: { [fileUrlResolverKey as symbol]: resolve } },
    })
    await flushPromises()
    expect(resolve).toHaveBeenCalledWith('/api/v1/files/local/here.png', { localOnly: true })
    const images = view.findAll('img')
    expect(images.map((i) => i.attributes('src'))).toEqual(['blob:local/local'])
    expect(view.find('a.md-image').exists()).toBe(false)
    expect(view.text()).toContain('There')
    expect(view.text()).not.toContain('Not available offline')
    view.unmount()
  })
})

describe('file URLs through the resolver (docs/03 §18.4)', () => {
  beforeEach(() => setActivePinia(createPinia()))

  const mountWith = (source: string, resolve: FileUrlResolver | null, files: unknown = null) =>
    mountWithUi(MarkdownView, {
      props: { source, files: files as never },
      global: resolve ? { provide: { [fileUrlResolverKey as symbol]: resolve } } : {},
    })

  it('uses the URL itself without a resolver', () => {
    const view = mountWith('![Plan](/api/v1/files/0193/plan.png)', null)
    expect(view.find('img').attributes('src')).toBe('/api/v1/files/0193/plan.png')
    expect(view.find('a.md-image').attributes('href')).toBe('/api/v1/files/0193/plan.png')
    view.unmount()
  })

  it('shows an image and opens its full size from where the resolver has it', async () => {
    const resolve = vi.fn<FileUrlResolver>(async () => 'blob:http://localhost/1234')
    const view = mountWith('![Plan](/api/v1/files/0193/plan.png)', resolve)
    await flushPromises()
    expect(resolve).toHaveBeenCalledWith('/api/v1/files/0193/plan.png', { localOnly: false })
    expect(view.find('img').attributes('src')).toBe('blob:http://localhost/1234')
    expect(view.find('img').attributes('alt')).toBe('Plan')
    expect(view.find('a.md-image').attributes('href')).toBe('blob:http://localhost/1234')
    view.unmount()
  })

  it('says an image is not available offline where the resolver has none', async () => {
    const view = mountWith('![Plan](/api/v1/files/0193/plan.png)', async () => null)
    await flushPromises()
    expect(view.find('img').exists()).toBe(false)
    expect(view.text()).toContain('Not available offline: Plan')
    view.unmount()
  })

  it('downloads an attachment from where the resolver has it, and marks a pending upload', async () => {
    const files = [
      { id: '0194', name: 'manual.pdf', size: 2_516_582, contentType: 'application/pdf', status: 'pending' },
      { id: '0195', name: 'plan.png', size: 1000, contentType: 'image/png', status: 'pending' },
    ]
    const view = mountWith(
      '[Manual](/api/v1/files/0194/manual.pdf)\n\n![Plan](/api/v1/files/0195/plan.png)',
      async (url) => `blob:local${url}`,
      files,
    )
    await flushPromises()
    const chip = view.find('a.attachment-chip')
    expect(chip.attributes('href')).toBe('blob:local/api/v1/files/0194/manual.pdf')
    expect(chip.attributes('download')).toBe('manual.pdf')
    expect(chip.text()).toContain('2.4 MB')
    expect(chip.text()).toContain('Pending upload')
    expect(view.find('.md-image-frame .md-pending').text()).toContain('Pending upload')
    view.unmount()
  })

  it('shows an attachment it can’t have as not available offline', async () => {
    const files = [{ id: '0194', name: 'manual.pdf', size: 100, contentType: 'application/pdf' }]
    const view = mountWith('[Manual](/api/v1/files/0194/manual.pdf)', async () => null, files)
    await flushPromises()
    expect(view.find('a.attachment-chip').exists()).toBe(false)
    expect(view.find('.attachment-chip.unavailable').text()).toContain('Not available offline')
    view.unmount()
  })
})
