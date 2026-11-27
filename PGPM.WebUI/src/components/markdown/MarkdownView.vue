<script setup lang="ts">
import { computed } from 'vue'
import MarkdownContent from './MarkdownContent'
import { clobberPrefix, compactBlocks, renderMarkdown } from './renderMarkdown'
import type { MarkdownFile } from './types'

/**
 * Markdown, rendered for reading (docs/03 §18.1; PNMTD docs/locations-knowledge-plan.md §5.6): descriptions,
 * fields, a revision, the editors' previews, map cards. The rules are renderMarkdown's; this is the look, and
 * in-page #anchors scrolled to without leaving the route.
 *
 * `files`: the metadata of the files the text uses (the synced `files` rows). With it, attachment chips show their
 * size and a pending upload, and a file that isn't among them shows as removed; without it (null), chips show their
 * link's text. Files are shown through the provided resolver (services.ts).
 *
 * `compact` (a map's note card, docs/03 §18.2): the first blocks only (true: three, or the number given), no
 * diagrams, images only where they are on this device.
 */
const props = withDefaults(
  defineProps<{ source: string; files?: readonly MarkdownFile[] | null; compact?: boolean | number }>(),
  { files: null, compact: false },
)

const blocks = computed(() => (props.compact === true ? compactBlocks : props.compact || 0))
const tree = computed(() => renderMarkdown(props.source, { compact: blocks.value }))

// A plain #anchor would go through the router, whose scrollBehavior starts the page at its top.
function onClick(event: MouseEvent) {
  const link = (event.target as HTMLElement | null)?.closest?.('a[href^="#"]')
  if (!link || event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey) {
    return
  }
  const id = decodeURIComponent(link.getAttribute('href')!.slice(1))
  const target = document.getElementById(id) ?? document.getElementById(clobberPrefix + id)
  if (target) {
    event.preventDefault()
    target.scrollIntoView({ block: 'start' })
    history.replaceState(history.state, '', `#${target.id}`)
  }
}
</script>

<template>
  <div class="pgpm-markdown" :class="{ compact: blocks > 0 }" @click="onClick">
    <MarkdownContent :tree="tree" :files="files" :compact="blocks > 0" />
  </div>
</template>

<style>
/* Markdown as read (§5.6). Not scoped: the content is built from the tree, and the editors' previews share it. */
.pgpm-markdown {
  overflow-wrap: anywhere;
  line-height: 1.55;
}

.pgpm-markdown > :first-child {
  margin-top: 0;
}

.pgpm-markdown > :last-child {
  margin-bottom: 0;
}

.pgpm-markdown :is(p, ul, ol, blockquote, pre, .md-table, .md-code, hr) {
  margin: 0 0 0.85em;
}

.pgpm-markdown :is(h1, h2, h3, h4, h5, h6) {
  margin: 1.2em 0 0.5em;
  line-height: 1.3;
  font-weight: 600;
  scroll-margin-top: calc(var(--pgpm-header-height) + var(--pgpm-space-2));
}

.pgpm-markdown h1 {
  font-size: 1.5rem;
}

.pgpm-markdown h2 {
  font-size: 1.3rem;
  padding-bottom: 0.2em;
  border-bottom: 1px solid var(--pgpm-border);
}

.pgpm-markdown h3 {
  font-size: 1.15rem;
}

.pgpm-markdown :is(h4, h5, h6) {
  font-size: 1rem;
}

.pgpm-markdown :is(ul, ol) {
  padding-left: 1.6em;
}

.pgpm-markdown li + li,
.pgpm-markdown li > :is(ul, ol) {
  margin-top: 0.2em;
}

.pgpm-markdown li > :is(ul, ol) {
  margin-bottom: 0;
}

.pgpm-markdown .contains-task-list {
  list-style: none;
  padding-left: 0.4em;
}

.pgpm-markdown .contains-task-list .contains-task-list {
  padding-left: 1.4em;
}

.pgpm-markdown .task-list-item input {
  margin: 0 0.45em 0 0;
  vertical-align: -0.1em;
}

.pgpm-markdown blockquote {
  padding: 0 0 0 1em;
  border-left: 3px solid var(--pgpm-border);
  color: var(--pgpm-muted);
}

.pgpm-markdown hr {
  border: none;
  border-top: 1px solid var(--pgpm-border);
}

.pgpm-markdown img {
  max-width: 100%;
  height: auto;
  border-radius: var(--p-border-radius-sm);
}

.pgpm-markdown a.md-image {
  display: inline-block;
  cursor: zoom-in;
}

.pgpm-markdown .md-external-image {
  color: var(--pgpm-muted);
  font-style: italic;
}

.pgpm-markdown :not(pre) > code {
  padding: 0.1em 0.35em;
  border-radius: var(--p-border-radius-sm);
  background: var(--pgpm-bg);
  border: 1px solid var(--pgpm-border);
}

.pgpm-markdown pre {
  padding: var(--pgpm-space-3);
  border: 1px solid var(--pgpm-border);
  border-radius: var(--p-border-radius-md);
  background: var(--pgpm-bg);
  overflow-x: auto;
  line-height: 1.45;
}

.pgpm-markdown pre code {
  font-family: var(--pgpm-mono);
  font-size: 0.9286rem;
  white-space: pre;
}

.pgpm-markdown .md-code > pre {
  margin: 0;
}

.pgpm-markdown .md-table {
  overflow-x: auto;
}

.pgpm-markdown table {
  border-collapse: collapse;
  min-width: 50%;
}

.pgpm-markdown :is(th, td) {
  padding: var(--pgpm-space-1) var(--pgpm-space-3);
  border: 1px solid var(--pgpm-border);
  vertical-align: top;
}

.pgpm-markdown th {
  background: var(--pgpm-bg);
  font-weight: 600;
  text-align: left;
}

.pgpm-markdown th[align='center'],
.pgpm-markdown td[align='center'] {
  text-align: center;
}

.pgpm-markdown th[align='right'],
.pgpm-markdown td[align='right'] {
  text-align: right;
}

.pgpm-markdown section.footnotes {
  margin-top: 2em;
  font-size: 0.9286rem;
  color: var(--pgpm-muted);
}

.pgpm-markdown .sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
}

/* A compact text (a map's note card): tighter, headings no larger than a title, the cut marked. */
.pgpm-markdown.compact :is(p, ul, ol, blockquote, pre, .md-table, hr) {
  margin-bottom: 0.4em;
}

.pgpm-markdown.compact :is(h1, h2, h3, h4, h5, h6) {
  margin: 0 0 0.3em;
  padding: 0;
  border: none;
  font-size: 1em;
}

.pgpm-markdown.compact pre {
  padding: var(--pgpm-space-1) var(--pgpm-space-2);
  overflow: hidden;
}

.pgpm-markdown.compact img {
  max-height: 8rem;
}

.pgpm-markdown .md-compact-diagram,
.pgpm-markdown .md-more {
  color: var(--pgpm-muted);
  font-style: italic;
}

/* highlight.js's classes in PGPM's colours (light; dark below). */
.pgpm-markdown .hljs-comment,
.pgpm-markdown .hljs-quote,
.pgpm-markdown .hljs-meta {
  color: var(--p-surface-500);
  font-style: italic;
}

.pgpm-markdown :is(.hljs-keyword, .hljs-selector-tag, .hljs-built_in, .hljs-name, .hljs-section) {
  color: var(--p-purple-700);
}

.pgpm-markdown :is(.hljs-string, .hljs-attr-value, .hljs-template-variable, .hljs-addition) {
  color: var(--p-green-700);
}

.pgpm-markdown :is(.hljs-number, .hljs-literal, .hljs-symbol, .hljs-bullet) {
  color: var(--p-orange-700);
}

.pgpm-markdown :is(.hljs-attr, .hljs-attribute, .hljs-variable, .hljs-property, .hljs-params) {
  color: var(--p-sky-700);
}

.pgpm-markdown :is(.hljs-title, .hljs-type, .hljs-function, .hljs-class) {
  color: var(--p-blue-700);
}

.pgpm-markdown .hljs-deletion {
  color: var(--p-red-700);
}

:root.pgpm-dark .pgpm-markdown :is(.hljs-keyword, .hljs-selector-tag, .hljs-built_in, .hljs-name, .hljs-section) {
  color: var(--p-purple-300);
}

:root.pgpm-dark .pgpm-markdown :is(.hljs-string, .hljs-attr-value, .hljs-template-variable, .hljs-addition) {
  color: var(--p-green-300);
}

:root.pgpm-dark .pgpm-markdown :is(.hljs-number, .hljs-literal, .hljs-symbol, .hljs-bullet) {
  color: var(--p-orange-300);
}

:root.pgpm-dark .pgpm-markdown :is(.hljs-attr, .hljs-attribute, .hljs-variable, .hljs-property, .hljs-params) {
  color: var(--p-sky-300);
}

:root.pgpm-dark .pgpm-markdown :is(.hljs-title, .hljs-type, .hljs-function, .hljs-class) {
  color: var(--p-blue-300);
}

:root.pgpm-dark .pgpm-markdown .hljs-deletion {
  color: var(--p-red-300);
}

/* Printing (§5.6): a description or a note prints cleanly, links show where they go. */
@media print {
  .pgpm-markdown {
    color: #000;
  }

  .pgpm-markdown a[href^='http']::after {
    content: ' (' attr(href) ')';
    font-size: 0.85em;
    color: #444;
  }

  .pgpm-markdown :is(pre, .md-table, .md-code, img, blockquote) {
    break-inside: avoid;
  }

  .pgpm-markdown pre,
  .pgpm-markdown pre code {
    white-space: pre-wrap;
  }

  .pgpm-markdown .md-table {
    overflow: visible;
  }
}
</style>
