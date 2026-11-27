<script setup lang="ts">
import Button from 'primevue/button'
import { ref } from 'vue'
import MarkdownEditor from '@/components/markdown/MarkdownEditor.vue'
import MarkdownView from '@/components/markdown/MarkdownView.vue'
import { provideMarkdownServices, type FileSink, type FileUrlResolver } from '@/components/markdown/services'
import type { EditConflict, ItemLink, MarkdownFile, MarkdownSave } from '@/components/markdown/types'

/**
 * The Markdown editor and renderer side by side, for checking them by hand (docs/03 §18): `/dev/markdown`, registered
 * in development only (router/index.ts). Files live in this tab's memory as blob: URLs, a stand-in for phase 3's
 * offline pipeline: a pasted image shows at once, marked *Pending upload*.
 */

const sample = `# Website relaunch

The relaunch replaces the **old CMS** with a static site. See [P-0042 Website relaunch](/projects/0193f3c2-0000-7000-8000-000000000042)
and the [vendor's page](https://example.com).

## Plan

| Phase | Owner | Weeks |
| --- | --- | ---: |
| Discovery | Anna | 2 |
| Build | Ben | 6 |
| Rollout | Cleo | 1 |

## Checklist

- [x] Kick-off held
- [ ] Content inventory
- [ ] Redirects tested

\`\`\`bash
npm run build && rsync -av dist/ web01:/srv/www/
\`\`\`

\`\`\`mermaid
flowchart LR
  idea[Idea] --> assess{Assessment}
  assess -- go --> project[Project]
  assess -- park --> backlog[(Backlog)]
\`\`\`
`

const text = ref(sample)
const files = ref<MarkdownFile[]>([])
const conflict = ref<EditConflict | null>(null)
const saves = ref<MarkdownSave[]>([])
const compact = ref(false)
const blobs = new Map<string, string>()

const fileSink: FileSink = {
  async store(file, kind, onProgress) {
    const id = crypto.randomUUID()
    const name = file.name.replace(/[^\p{L}\p{N}._-]+/gu, '-')
    blobs.set(id, URL.createObjectURL(file))
    files.value = [
      ...files.value,
      { id, name: file.name, size: file.size, contentType: file.type, kind, status: 'pending' },
    ]
    onProgress?.(1)
    return { id, url: `/api/v1/files/${id}/${name}`, name: file.name, contentType: file.type, size: file.size }
  },
}

const resolveFileUrl: FileUrlResolver = async (url) => {
  const id = url.split('/')[4] ?? ''
  return blobs.get(id) ?? url
}

provideMarkdownServices({ fileSink, resolveFileUrl })

async function pickItem(): Promise<ItemLink | null> {
  return { label: 'I-0007 Chatbot for the service desk', href: '/ideas/0193f3c2-0000-7000-8000-000000000007' }
}

function simulateConflict() {
  conflict.value = {
    by: 'Anna Muster',
    at: new Date().toISOString(),
    theirs: `${sample}\nAnna added this line.\n`,
    base: sample,
  }
}
</script>

<template>
  <div class="playground">
    <header class="bar">
      <h1>Markdown playground</h1>
      <span class="muted">Development only. Files stay in this tab.</span>
      <span class="spacer" />
      <Button label="Simulate a conflict" size="small" severity="secondary" outlined @click="simulateConflict" />
      <Button
        :label="compact ? 'Full view' : 'Compact view'"
        size="small"
        severity="secondary"
        outlined
        @click="compact = !compact"
      />
    </header>
    <div class="columns">
      <MarkdownEditor
        v-model="text"
        v-model:conflict="conflict"
        mode="field"
        layout="inline"
        height="70vh"
        draft-key="dev:playground:text"
        :files="files"
        :pick-item="pickItem"
        @save="(s: MarkdownSave) => (saves = [s, ...saves].slice(0, 3))"
        @take-theirs="(theirs: string) => (text = theirs)"
      />
      <section class="view panel" aria-label="Rendered">
        <div class="panel-body" :class="{ card: compact }">
          <MarkdownView :source="text" :files="files" :compact="compact" />
        </div>
      </section>
    </div>
    <p v-if="saves.length" class="muted">
      Last save: {{ saves[0]!.markdown.length }} characters, base {{ saves[0]!.base.length }} characters, overwrite
      {{ saves[0]!.overwrite }}.
    </p>
  </div>
</template>

<style scoped>
.playground {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-3);
  padding: var(--pgpm-space-3) var(--pgpm-space-4);
}

.bar {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pgpm-space-2);
}

.bar h1 {
  margin: 0;
  font-size: 1.15rem;
}

.spacer {
  flex: 1;
}

.columns {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(28rem, 1fr));
  gap: var(--pgpm-space-3);
  align-items: start;
}

.view {
  max-height: calc(70vh + 3rem);
  overflow: auto;
}

.card {
  max-width: 18rem;
  border: 1px solid var(--pgpm-border);
  border-radius: var(--p-border-radius-md);
  background: var(--pgpm-note-yellow);
}
</style>
