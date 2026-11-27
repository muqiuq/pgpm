import type { Element, ElementContent, Properties, Root, RootContent, Text } from 'hast'
import type { Nodes as MdNodes, Paragraph, Root as MdRoot } from 'mdast'
import bash from 'highlight.js/lib/languages/bash'
import csharp from 'highlight.js/lib/languages/csharp'
import diff from 'highlight.js/lib/languages/diff'
import dockerfile from 'highlight.js/lib/languages/dockerfile'
import ini from 'highlight.js/lib/languages/ini'
import javascript from 'highlight.js/lib/languages/javascript'
import json from 'highlight.js/lib/languages/json'
import nginx from 'highlight.js/lib/languages/nginx'
import powershell from 'highlight.js/lib/languages/powershell'
import python from 'highlight.js/lib/languages/python'
import routeros from 'highlight.js/lib/languages/routeros'
import sql from 'highlight.js/lib/languages/sql'
import xml from 'highlight.js/lib/languages/xml'
import yaml from 'highlight.js/lib/languages/yaml'
import rehypeHighlight from 'rehype-highlight'
import rehypeSanitize, { defaultSchema, type Options as SanitizeSchema } from 'rehype-sanitize'
import remarkGfm from 'remark-gfm'
import remarkParse from 'remark-parse'
import remarkRehype from 'remark-rehype'
import { unified } from 'unified'

/**
 * Markdown to a sanitised HTML tree, for reading (PNMTD docs/locations-knowledge-plan.md §5.6). The tree is
 * hast; MarkdownView turns it into Vue nodes, so no HTML string is ever put into the page.
 *
 * 1. remark-parse and remark-gfm: CommonMark with tables, task lists, strikethrough, autolinks and
 *    footnotes (§5.1).
 * 2. Raw HTML becomes text before remark-rehype: it is shown as written, never rendered.
 * 3. rehype-sanitize with GitHub's schema, narrowed to what Markdown itself produces: its elements,
 *    their attributes, and http(s), mailto: and tel: links.
 * 4. rehype-highlight for the languages of §5.1, `mermaid` left as plain text.
 * 5. PGPM's steps, on the sanitised tree (docs/03 §18.1):
 *    - links: no scheme left → text; external ones open in a new tab; PGPM's item pages (`/projects/…`,
 *      `/ideas/…`, `/leads/…`, `/notes/…`, `/maps/…`, `/persons/…`) become router links; links to
 *      /api/v1/files/… that aren't images become attachment chips;
 *    - images: only /api/v1/files/…, lazily, opening the full size; others become a link marked
 *      "external image, not loaded";
 *    - headings get ids with sanitize's prefix (`user-content-`), and #anchors point there;
 *    - code blocks get a copy button, `mermaid` blocks become diagrams (§5.8), tables scroll.
 * 6. `compact` (a map's note card, docs/03 §18.2): the first blocks only, a diagram as the word *Diagram*, no copy
 *    buttons, no heading ids (many cards share a page), images not opened by a click.
 *
 * The elements of step 5 that are components are `pgpm-*` elements, which only this code creates:
 * `pgpm-code` (properties: source, language), `pgpm-mermaid` (source, index),
 * `pgpm-attachment` (href, fileId, name) and `pgpm-link` (to, the router path).
 */

/** PGPM's pages a text links to inside the app (docs/03 §18.3): relative, opened through the router. */
export const internalRoute = /^\/(projects|ideas|leads|notes|maps|persons)(?:[/?#]|$)/

/** A file of PGPM's store (§5.1): /api/v1/files/{id}/{name}. */
export const fileUrl = /^\/api\/v1\/files\/([0-9A-Za-z-]+)\/[^/?#]+$/

/** The blocks a compact text shows when MarkdownView's `compact` is true. */
export const compactBlocks = 3

/** The prefix of every id from a page, so that a heading can't clobber the app's ids (§5.6). */
export const clobberPrefix = 'user-content-'

/** At most this many diagrams per page draw by themselves; the rest wait for *Show diagram* (§5.8). */
export const maxAutoDiagrams = 50

/** The highlighted languages of §5.1, with the names fences commonly use. */
const languages = {
  bash,
  csharp,
  diff,
  dockerfile,
  ini,
  javascript,
  json,
  nginx,
  powershell,
  python,
  routeros,
  sql,
  xml,
  yaml,
}
const aliases = { bash: ['shell', 'console'], ini: ['toml'], powershell: ['pwsh'], routeros: ['mikrotik'] }

// The elements and attributes mdast-util-to-hast writes for GFM, and nothing else: raw HTML never
// gets here, so GitHub's wider list (div, details, kbd, …) is narrowed to these.
const tagNames = [
  'a',
  'blockquote',
  'br',
  'code',
  'del',
  'em',
  'h1',
  'h2',
  'h3',
  'h4',
  'h5',
  'h6',
  'hr',
  'img',
  'input',
  'li',
  'ol',
  'p',
  'pre',
  'section',
  'strong',
  'sup',
  'table',
  'tbody',
  'td',
  'th',
  'thead',
  'tr',
  'ul',
]
const githubAttributes = defaultSchema.attributes ?? {}
export const sanitizeSchema: SanitizeSchema = {
  ...defaultSchema,
  tagNames,
  attributes: {
    a: githubAttributes.a,
    code: githubAttributes.code,
    h2: githubAttributes.h2,
    img: ['src'],
    input: githubAttributes.input,
    li: githubAttributes.li,
    ol: githubAttributes.ol,
    section: githubAttributes.section,
    ul: githubAttributes.ul,
    '*': ['align', 'alt', 'checked', 'id', 'start', 'title'],
  },
  protocols: {
    href: ['http', 'https', 'mailto', 'tel'],
    src: ['http', 'https'],
  },
  clobberPrefix,
}

/**
 * A bare `<br>` (no attributes), as Milkdown's own pattern reads it: the visual editor writes every empty line the
 * person typed as `<br />`, and GitHub and VS Code render one as a line break.
 */
const bareBreak = /^<br\s*\/?\s*>$/i

/**
 * Raw HTML as text (§5.1): an inline tag stays inline, a block of HTML becomes a paragraph. The one exception is a
 * bare `<br>`, which becomes Markdown's own line break (an empty line where it stands alone), never HTML.
 */
function htmlAsText() {
  return (tree: MdRoot) => {
    const walk = (node: MdNodes) => {
      if (!('children' in node)) {
        return
      }
      const block = node.type === 'root' || node.type === 'blockquote' || node.type === 'listItem'
      node.children = node.children.map((child) => {
        if (child.type !== 'html') {
          walk(child)
          return child
        }
        if (bareBreak.test(child.value.trim())) {
          const lineBreak = { type: 'break' as const, position: child.position }
          return block
            ? ({ type: 'paragraph', children: [lineBreak], position: child.position } as Paragraph)
            : lineBreak
        }
        const text = { type: 'text' as const, value: child.value, position: child.position }
        return block ? ({ type: 'paragraph', children: [text], position: child.position } as Paragraph) : text
      }) as typeof node.children
    }
    walk(tree)
  }
}

const isElement = (node: RootContent | ElementContent | Root): node is Element => node.type === 'element'

function textOf(node: Root | RootContent | ElementContent): string {
  if (node.type === 'text') {
    return node.value
  }
  if ('children' in node) {
    return node.children.map(textOf).join('')
  }
  return ''
}

const text = (value: string): Text => ({ type: 'text', value })

function element(tagName: string, properties: Properties, children: ElementContent[] = []): Element {
  return { type: 'element', tagName, properties, children }
}

/** GitHub's heading slugs: lower case, punctuation dropped, spaces as dashes; repeats get -1, -2, … */
export function slugger() {
  const seen = new Map<string, number>()
  return (value: string) => {
    const base =
      value
        .toLowerCase()
        .trim()
        .replace(/[^\p{L}\p{M}\p{N}\p{Pc}\- ]/gu, '')
        .replace(/ /g, '-') || 'section'
    const count = seen.get(base) ?? 0
    seen.set(base, count + 1)
    return count ? `${base}-${count}` : base
  }
}

/** Another site: http(s), or protocol-relative (//host/…), which sanitize lets through as relative. */
const external = (href: string) => /^(https?:)?\/\//i.test(href)
const externalRel = 'noopener noreferrer nofollow'

/** PGPM's steps (5. above), on the sanitised and highlighted tree. */
function pgpmSteps() {
  return (tree: Root) => {
    const slug = slugger()
    let diagrams = 0

    const rewrite = (node: Element, parents: Element[]): ElementContent[] => {
      const props = node.properties
      switch (node.tagName) {
        case 'a': {
          const href = typeof props.href === 'string' ? props.href : null
          if (href === null) {
            // A scheme sanitize doesn't allow (javascript:, data:, …): the text, not a link.
            return node.children
          }
          if (href.startsWith('#') && !href.startsWith(`#${clobberPrefix}`)) {
            props.href = `#${clobberPrefix}${href.slice(1)}`
          } else if (fileUrl.test(href) && !node.children.some((c) => isElement(c) && c.tagName === 'img')) {
            const [, fileId] = href.match(fileUrl)!
            return [element('pgpm-attachment', { href, fileId, name: textOf(node) })]
          } else if (internalRoute.test(href)) {
            return [element('pgpm-link', { to: href }, node.children)]
          } else if (external(href)) {
            props.target = '_blank'
            props.rel = externalRel
          }
          return [node]
        }
        case 'img': {
          const src = typeof props.src === 'string' ? props.src : ''
          const alt = typeof props.alt === 'string' ? props.alt : ''
          if (fileUrl.test(src)) {
            props.loading = 'lazy'
            props.decoding = 'async'
            if (parents.some((p) => p.tagName === 'a')) {
              return [node]
            }
            return [element('a', { href: src, target: '_blank', rel: 'noopener', className: ['md-image'] }, [node])]
          }
          const label = alt ? `external image, not loaded: ${alt}` : 'external image, not loaded'
          if (external(src) && !parents.some((p) => p.tagName === 'a')) {
            return [
              element('a', { href: src, target: '_blank', rel: externalRel, className: ['md-external-image'] }, [
                text(label),
              ]),
            ]
          }
          return [element('span', { className: ['md-external-image'] }, [text(src ? label : alt || 'image')])]
        }
        case 'pre': {
          const code = node.children.find((c): c is Element => isElement(c) && c.tagName === 'code')
          const classes = (code?.properties.className as string[] | undefined) ?? []
          const language = classes.find((c) => c.startsWith('language-'))?.slice('language-'.length) ?? ''
          const source = code ? textOf(code) : textOf(node)
          if (language.toLowerCase() === 'mermaid') {
            return [element('pgpm-mermaid', { source, index: diagrams++ })]
          }
          return [element('pgpm-code', { source, language }, [node])]
        }
        case 'table':
          return [element('div', { className: ['md-table'] }, [node])]
        case 'input':
          props.disabled = true
          return [node]
        case 'h1':
        case 'h2':
        case 'h3':
        case 'h4':
        case 'h5':
        case 'h6':
          if (!props.id) {
            props.id = clobberPrefix + slug(textOf(node))
          }
          return [node]
        default:
          return [node]
      }
    }

    const walk = (parent: Root | Element, parents: Element[]) => {
      const next: ElementContent[] = []
      for (const child of parent.children as ElementContent[]) {
        if (!isElement(child)) {
          next.push(child)
          continue
        }
        const inner = [...parents, child]
        walk(child, inner)
        next.push(...rewrite(child, parents))
      }
      parent.children = next as typeof parent.children
    }
    walk(tree, [])
  }
}

const processor = unified()
  .use(remarkParse)
  .use(remarkGfm)
  .use(htmlAsText)
  // Footnote ids without a prefix: sanitize adds it, and the #anchors are pointed there (5. above).
  .use(remarkRehype, { clobberPrefix: '' })
  .use(rehypeSanitize, sanitizeSchema)
  .use(rehypeHighlight, { languages, aliases, plainText: ['mermaid'], detect: false })
  .use(pgpmSteps)
  .freeze()

export interface RenderOptions {
  /** Only the first this many blocks, as a map's note card shows them (6. above); 0 or undefined: all. */
  compact?: number
}

const isHeading = (tagName: string) => /^h[1-6]$/.test(tagName)

/** A block as a compact text shows it (6. above). */
function compactBlock(node: Element): Element {
  if (node.tagName === 'pgpm-mermaid') {
    return element('p', { className: ['md-compact-diagram'] }, [text('Diagram')])
  }
  if (node.tagName === 'pgpm-code') {
    const pre = node.children.find((c): c is Element => isElement(c) && c.tagName === 'pre')
    return pre ? compactBlock(pre) : node
  }
  const classes = node.properties.className
  if (node.tagName === 'a' && Array.isArray(classes) && classes.includes('md-image')) {
    const image = node.children.find((c): c is Element => isElement(c) && c.tagName === 'img')
    if (image) {
      return image
    }
  }
  if (isHeading(node.tagName)) {
    delete node.properties.id
  }
  node.children = node.children.map((child) => (isElement(child) ? compactBlock(child) : child))
  return node
}

/** The first `blocks` blocks of a tree, and `…` when there were more. */
function compactTree(tree: Root, blocks: number): Root {
  const kept: RootContent[] = []
  let shown = 0
  for (const child of tree.children) {
    if (child.type !== 'element') {
      if (shown < blocks) {
        kept.push(child)
      }
      continue
    }
    if (shown === blocks) {
      kept.push(element('p', { className: ['md-more'] }, [text('…')]))
      break
    }
    shown++
    kept.push(compactBlock(child))
  }
  return { ...tree, children: kept }
}

/** The sanitised tree of a Markdown text (§5.6); `compact`, the first blocks of it (6. above). */
export function renderMarkdown(markdown: string, options: RenderOptions = {}): Root {
  const tree = processor.runSync(processor.parse(markdown)) as Root
  return options.compact && options.compact > 0 ? compactTree(tree, options.compact) : tree
}

/** Whether a Markdown text has a diagram, so a view can say so before Mermaid loads (§5.8). */
export function hasDiagram(markdown: string): boolean {
  return /^ {0,3}(`{3,}|~{3,})\s*mermaid\b/im.test(markdown)
}
