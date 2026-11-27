<script lang="ts">
/**
 * A line diff as an HTML table (PNMTD's, docs/03 §18.1): each hunk with its header, and every line with its number
 * in the old and the new version, added lines green, removed lines red. Where Monaco doesn't run (phones, or its
 * chunk didn't load), MonacoDiff shows the Markdown editor's diffs with it (components/markdown/lineDiff.ts): the
 * conflict's *Their changes* / *My changes*, the switch's rewrite, and the History tab's text diffs.
 */
export interface DiffLine {
  kind: 'Context' | 'Added' | 'Removed'
  text: string
  oldNumber: number | null
  newNumber: number | null
}

export interface DiffHunk {
  oldStart: number
  oldCount: number
  newStart: number
  newCount: number
  lines: DiffLine[]
}
</script>

<script setup lang="ts">
defineProps<{ diff: { hunks: DiffHunk[] } }>()

const mark = (kind: DiffLine['kind']) => (kind === 'Added' ? '+' : kind === 'Removed' ? '−' : ' ')
</script>

<template>
  <div class="diff" role="table" aria-label="Changes">
    <p v-if="!diff.hunks.length" class="muted same">The two versions are the same.</p>
    <template v-for="(hunk, h) in diff.hunks" :key="h">
      <div class="hunk" role="row">
        <span role="cell">@@ −{{ hunk.oldStart }},{{ hunk.oldCount }} +{{ hunk.newStart }},{{ hunk.newCount }} @@</span>
      </div>
      <div v-for="(line, i) in hunk.lines" :key="`${h}-${i}`" class="line" :class="line.kind.toLowerCase()" role="row">
        <span class="number" role="cell">{{ line.oldNumber ?? '' }}</span>
        <span class="number" role="cell">{{ line.newNumber ?? '' }}</span>
        <span class="mark" role="cell" aria-hidden="true">{{ mark(line.kind) }}</span>
        <span class="text" role="cell"
          ><span class="sr-only">{{ line.kind === 'Context' ? '' : line.kind + ': ' }}</span
          >{{ line.text }}</span
        >
      </div>
    </template>
  </div>
</template>

<style scoped>
.diff {
  font-family: var(--pgpm-mono);
  font-size: 0.857rem;
  border: 1px solid var(--pgpm-border);
  border-radius: var(--p-border-radius-md);
  background: var(--pgpm-bg);
  overflow: auto;
  max-height: 32rem;
}

.same {
  margin: 0;
  padding: var(--pgpm-space-2);
}

.hunk {
  padding: 2px var(--pgpm-space-2);
  color: var(--pgpm-muted);
  background: var(--pgpm-none-soft);
}

.line {
  display: grid;
  grid-template-columns: 3.5rem 3.5rem 1.5rem 1fr;
  white-space: pre;
}

.number {
  text-align: right;
  padding-right: var(--pgpm-space-2);
  color: var(--pgpm-muted);
  user-select: none;
}

.mark {
  text-align: center;
  user-select: none;
}

.added {
  background: var(--pgpm-ok-soft);
}

.removed {
  background: var(--pgpm-fail-soft);
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
}
</style>
