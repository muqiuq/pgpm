<script setup lang="ts">
import Button from 'primevue/button'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import Tag from 'primevue/tag'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { packagesApi, type PackageDiff, type PackageInfo, type PackageSource } from '@/api/config'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import PageHeader from '@/components/PageHeader.vue'
import { useNotify } from '@/composables/useNotify'
import { engine } from '@/local/boot'
import { useOnline } from '@/local/lookups'
import PackageDiffView from './PackageDiffView.vue'
import { applySummary, keepSelection, readPackage } from './packageDiff'

/**
 * *Administration → Configuration packages* (docs/01 §13, docs/03 §17, *Configuration: Manage*, online only): the
 * built-in packages with their import state; a built-in one or an uploaded file is first compared with the
 * configuration (dry run: new / update / unchanged / changed here), conflicts are kept unless chosen to be overwritten,
 * then applied in one transaction – every device gets the result through the sync. Packages that bring groups (rights)
 * need an admin. *Export* downloads the configuration (or what one package brought) as a package file.
 */
const notify = useNotify()
const online = useOnline()

const packages = ref<PackageInfo[] | null>(null)
const listError = ref<unknown>(null)
const loading = ref(false)

async function load() {
  if (!online.value) return
  loading.value = true
  listError.value = null
  try {
    packages.value = await packagesApi.list()
  } catch (e) {
    listError.value = e
  } finally {
    loading.value = false
  }
}
watch(online, (now) => now && void load(), { immediate: true })

// --- the dry run ---
const source = ref<PackageSource | null>(null)
const sourceTitle = ref('')
const diff = ref<PackageDiff | null>(null)
const overwrite = ref<string[]>([])
/** The selection the shown diff was made with. */
const ranWith = ref<string[]>([])
const problems = ref<string[]>([])
const running = ref(false)
const applying = ref(false)

const selectionChanged = computed(
  () => JSON.stringify([...overwrite.value].sort()) !== JSON.stringify([...ranWith.value].sort()),
)
const summary = computed(() => (diff.value ? applySummary(diff.value, overwrite.value) : ''))
const adminMissing = computed(() => !!diff.value && diff.value.needsAdmin && !diff.value.isAdmin)

async function dryRun(next: PackageSource, title: string, keep = false) {
  source.value = next
  sourceTitle.value = title
  problems.value = []
  if (!keep) {
    diff.value = null
    overwrite.value = []
  }
  running.value = true
  try {
    const result = await packagesApi.dryRun(next, overwrite.value)
    diff.value = result
    overwrite.value = keepSelection(overwrite.value, result)
    ranWith.value = [...overwrite.value]
  } catch (e) {
    diff.value = null
    showProblem(e, 'Dry run failed')
  } finally {
    running.value = false
  }
}

function showProblem(e: unknown, summary: string) {
  if (e instanceof ApiError && e.code === 'invalid_package') {
    const list = e.body.problems
    problems.value = Array.isArray(list) && list.length ? list.map(String) : [e.message]
  } else if (e instanceof ApiError && e.code === 'admin_required') {
    problems.value = ['This package changes groups and their rights: an administrator applies it.']
  } else {
    notify.error(e, summary)
  }
}

async function apply() {
  if (!source.value || !diff.value) return
  applying.value = true
  try {
    const result = await packagesApi.apply(source.value, overwrite.value)
    notify.success(
      `${sourceTitle.value} applied`,
      `${result.created} created, ${result.updated} updated${result.skipped ? `, ${result.skipped} kept (changed here)` : ''}.`,
    )
    void engine.syncNow()
    close()
    await load()
  } catch (e) {
    showProblem(e, 'Not applied')
  } finally {
    applying.value = false
  }
}

function close() {
  source.value = null
  diff.value = null
  overwrite.value = []
  ranWith.value = []
  problems.value = []
}

// --- upload ---
const fileInput = ref<HTMLInputElement | null>(null)
const uploadError = ref<string | null>(null)

async function uploaded(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file) return
  uploadError.value = null
  const read = readPackage(await file.text())
  if (!read.ok) {
    uploadError.value = `${file.name}: ${read.error}`
    return
  }
  await dryRun({ package: read.json }, `${read.title}${read.version ? ` ${read.version}` : ''}`)
}

// --- export ---
const exporting = ref<string | null>(null)
async function download(name?: string) {
  exporting.value = name ?? '*'
  try {
    const { text } = await packagesApi.export(name)
    const blob = new Blob([text], { type: 'application/json' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `${name ?? 'configuration'}.pgpm.json`
    document.body.appendChild(a)
    a.click()
    a.remove()
    setTimeout(() => URL.revokeObjectURL(url), 1000)
  } catch (e) {
    notify.error(e, 'Not exported')
  } finally {
    exporting.value = null
  }
}

function importedText(p: PackageInfo): string {
  const i = p.imported
  if (!i) return 'Not imported'
  const at = new Date(i.importedAt).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })
  return `Imported ${i.version} on ${at}${i.importedByName ? ` by ${i.importedByName}` : ''}`
}
</script>

<template>
  <div class="page">
    <PageHeader title="Configuration packages" :crumbs="[{ label: 'Administration' }]">
      <template #actions>
        <Button
          label="Upload package…"
          icon="pi pi-upload"
          size="small"
          severity="secondary"
          outlined
          :disabled="!online || running"
          @click="fileInput?.click()"
        />
        <Button
          label="Export configuration"
          icon="pi pi-download"
          size="small"
          severity="secondary"
          outlined
          :loading="exporting === '*'"
          :disabled="!online"
          @click="download()"
        />
        <input
          ref="fileInput"
          type="file"
          accept=".json,application/json"
          class="hidden-input"
          data-testid="package-file"
          aria-label="Package file"
          @change="uploaded"
        />
      </template>
    </PageHeader>
    <Message v-if="!online" severity="secondary" :closable="false" data-testid="offline-hint">
      Available online only: packages are compared, applied and exported with a connection.
    </Message>
    <Message v-if="uploadError" severity="error" :closable="false">{{ uploadError }}</Message>

    <section v-if="source" class="panel" aria-labelledby="dry-run-title" data-testid="dry-run">
      <div class="panel-header with-action">
        <span id="dry-run-title">Dry run · {{ sourceTitle }}</span>
        <Button
          icon="pi pi-times"
          text
          size="small"
          severity="secondary"
          aria-label="Close the dry run"
          @click="close"
        />
      </div>
      <div class="panel-body dry-run">
        <div v-if="running" class="center"><ProgressSpinner style="width: 28px; height: 28px" /></div>
        <Message v-if="problems.length" severity="error" :closable="false" data-testid="package-problems">
          <ul class="problems">
            <li v-for="p in problems" :key="p">{{ p }}</li>
          </ul>
        </Message>
        <template v-if="diff">
          <PackageDiffView v-model:overwrite="overwrite" :diff="diff" :disabled="applying" />
          <Message v-if="adminMissing" severity="warn" :closable="false" data-testid="admin-needed">
            This package creates or changes groups and their rights: only an administrator can apply it.
          </Message>
          <div class="form-actions">
            <Button
              label="Apply"
              icon="pi pi-check"
              size="small"
              :loading="applying"
              :disabled="!online || adminMissing || running"
              @click="apply"
            />
            <Button
              v-if="selectionChanged"
              label="Dry run again"
              icon="pi pi-refresh"
              size="small"
              severity="secondary"
              outlined
              :disabled="!online || running || applying"
              @click="dryRun(source!, sourceTitle, true)"
            />
            <span class="muted" data-testid="apply-summary">{{ summary }}</span>
          </div>
        </template>
      </div>
    </section>

    <section class="panel" aria-labelledby="packages-title">
      <div id="packages-title" class="panel-header">Packages</div>
      <EmptyState
        v-if="!online && !packages"
        icon="pi pi-wifi"
        title="Available online only."
        text="The packages are read from the server."
      />
      <ErrorState v-else-if="listError" :error="listError" @retry="load" />
      <div v-else-if="loading && !packages" class="center"><ProgressSpinner style="width: 28px; height: 28px" /></div>
      <EmptyState v-else-if="packages && !packages.length" icon="pi pi-box" title="No packages." />
      <ol v-else-if="packages" class="rows">
        <li v-for="p in packages" :key="p.name" :data-testid="`package-${p.name}`">
          <i class="pi pi-box muted" aria-hidden="true" />
          <span class="info">
            <span class="strong">
              {{ p.title }} <small class="mono muted">{{ p.name }} {{ p.version }}</small>
            </span>
            <small v-if="p.description" class="muted">{{ p.description }}</small>
            <small class="muted">{{ importedText(p) }}</small>
          </span>
          <Tag v-if="p.builtIn" value="Built-in" severity="secondary" />
          <Tag
            v-if="p.imported && p.imported.version !== p.version"
            :value="`${p.version} available`"
            severity="info"
          />
          <span class="actions">
            <Button
              v-if="p.builtIn"
              label="Dry run"
              icon="pi pi-search"
              size="small"
              text
              :disabled="!online || running"
              :aria-label="`Dry run of ${p.title}`"
              @click="dryRun({ builtIn: p.name }, `${p.title} ${p.version}`)"
            />
            <Button
              v-if="p.imported"
              label="Export"
              icon="pi pi-download"
              size="small"
              text
              :loading="exporting === p.name"
              :disabled="!online"
              :aria-label="`Export what ${p.title} brought`"
              @click="download(p.name)"
            />
          </span>
        </li>
      </ol>
    </section>
  </div>
</template>

<style scoped>
.hidden-input {
  display: none;
}

.with-action {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.dry-run {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-3);
}

.center {
  display: flex;
  justify-content: center;
  padding: var(--pgpm-space-4);
}

.problems {
  margin: 0;
  padding-left: 1rem;
}

.rows {
  list-style: none;
  margin: 0;
  padding: 0;
}

.rows li {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-3);
  padding: var(--pgpm-space-2) var(--pgpm-space-3);
  border-bottom: 1px solid var(--pgpm-border);
}

.rows li:last-child {
  border-bottom: none;
}

.info {
  display: flex;
  flex-direction: column;
  min-width: 0;
  flex: 1;
}

.actions {
  display: inline-flex;
  margin-left: auto;
}
</style>
