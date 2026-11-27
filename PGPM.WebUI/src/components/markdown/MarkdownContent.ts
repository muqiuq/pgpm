import type { Element, ElementContent, Properties, Root } from 'hast'
import { defineAsyncComponent, defineComponent, h, type PropType, type VNodeChild } from 'vue'
import { RouterLink } from 'vue-router'
import AttachmentChip from './AttachmentChip.vue'
import CodeBlock from './CodeBlock.vue'
import MarkdownImage from './MarkdownImage.vue'
import { fileUrl, maxAutoDiagrams } from './renderMarkdown'
import type { MarkdownFile } from './types'

// Mermaid's component (and with it Mermaid) only for texts that have a diagram (§5.8).
const MermaidDiagram = defineAsyncComponent(() => import('./MermaidDiagram.vue'))

/** A hast property as a DOM attribute: className → class, ariaLabel → aria-label, colSpan → colspan. */
function attributeName(key: string): string {
  if (key === 'className') {
    return 'class'
  }
  if (key === 'htmlFor') {
    return 'for'
  }
  if (/^(aria|data)[A-Z]/.test(key)) {
    return key.replace(/[A-Z]/g, (c) => `-${c.toLowerCase()}`)
  }
  return key.toLowerCase()
}

function attributes(properties: Properties): Record<string, unknown> {
  const out: Record<string, unknown> = {}
  for (const [key, value] of Object.entries(properties)) {
    if (value === null || value === undefined || value === false) {
      continue
    }
    out[attributeName(key)] = Array.isArray(value) ? value.join(' ') : value
  }
  return out
}

/** The source line a top-level block starts on, for the code editor's preview to scroll with (§5.3). */
function lineOf(node: Element): number | undefined {
  if (node.position) {
    return node.position.start.line
  }
  for (const child of node.children) {
    if (child.type === 'element') {
      const line = lineOf(child)
      if (line) {
        return line
      }
    }
  }
  return undefined
}

interface Context {
  files: Map<string, MarkdownFile> | null
  /** A compact text (a map card): images only from this device, never fetched (docs/03 §18.2). */
  compact: boolean
}

/** A file's image as MarkdownImage shows it: its file's metadata (a pending upload) and how it opens. */
function image(node: Element, context: Context, zoom: boolean): VNodeChild {
  const props = node.properties
  const src = String(props.src)
  const id = src.match(fileUrl)?.[1] ?? ''
  return h(MarkdownImage, {
    src,
    alt: typeof props.alt === 'string' ? props.alt : undefined,
    title: typeof props.title === 'string' ? props.title : undefined,
    file: context.files?.get(id) ?? null,
    zoom,
    localOnly: context.compact,
  })
}

const isFileImage = (node: ElementContent | undefined): node is Element =>
  node?.type === 'element' &&
  node.tagName === 'img' &&
  typeof node.properties.src === 'string' &&
  fileUrl.test(node.properties.src)

function render(nodes: ElementContent[], context: Context, top = false): VNodeChild[] {
  return nodes.map((node) => {
    if (node.type === 'text') {
      return node.value
    }
    if (node.type !== 'element') {
      return null
    }
    const props = node.properties
    const children = () => render(node.children, context)
    const line = top ? lineOf(node) : undefined
    switch (node.tagName) {
      case 'pgpm-mermaid':
        return h(MermaidDiagram, {
          source: String(props.source ?? ''),
          auto: Number(props.index ?? 0) < maxAutoDiagrams,
          'data-line': line,
        })
      case 'pgpm-code':
        return h(
          CodeBlock,
          { source: String(props.source ?? ''), language: String(props.language ?? ''), 'data-line': line },
          { default: children },
        )
      case 'pgpm-attachment': {
        const id = String(props.fileId ?? '')
        const file = context.files?.get(id) ?? null
        return h(AttachmentChip, {
          href: String(props.href ?? ''),
          name: String(props.name ?? ''),
          file,
          removed: context.files !== null && !file,
        })
      }
      case 'pgpm-link':
        return h(RouterLink, { to: String(props.to ?? '/') }, { default: children })
      case 'a': {
        // An image of the store, opening its full size: MarkdownImage links to where it shows it from (a blob: URL).
        const classes = props.className
        const only = node.children.find((c) => c.type === 'element')
        if (Array.isArray(classes) && classes.includes('md-image') && isFileImage(only)) {
          return image(only, context, true)
        }
        return h('a', { ...attributes(props), 'data-line': line }, render(node.children, context))
      }
      case 'img':
        if (isFileImage(node)) {
          return image(node, context, false)
        }
        return h('img', attributes(props))
      default:
        return h(node.tagName, { ...attributes(props), 'data-line': line }, render(node.children, context))
    }
  })
}

/**
 * The sanitised tree of renderMarkdown as Vue nodes (PNMTD docs/locations-knowledge-plan.md §5.6): no HTML
 * string reaches the page. PGPM's own elements become their components.
 */
export default defineComponent({
  name: 'MarkdownContent',
  props: {
    tree: { type: Object as PropType<Root>, required: true },
    files: { type: Array as PropType<readonly MarkdownFile[] | null>, default: null },
    compact: { type: Boolean, default: false },
  },
  setup(props) {
    return () => {
      const files = props.files ? new Map(props.files.map((f) => [f.id, f])) : null
      return render(props.tree.children as ElementContent[], { files, compact: props.compact }, true)
    }
  },
})
