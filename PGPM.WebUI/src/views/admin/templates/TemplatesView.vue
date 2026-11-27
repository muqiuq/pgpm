<script setup lang="ts">
import Button from 'primevue/button'
import Message from 'primevue/message'
import Tag from 'primevue/tag'
import ToggleSwitch from 'primevue/toggleswitch'
import type { MenuItem } from 'primevue/menuitem'
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import { templatesApi } from '@/api/config'
import EmptyState from '@/components/EmptyState.vue'
import MoreMenu from '@/components/MoreMenu.vue'
import PageHeader from '@/components/PageHeader.vue'
import { useNotify } from '@/composables/useNotify'
import { engine } from '@/local/boot'
import { useTemplates } from '@/local/catalogue'
import { useOnline } from '@/local/lookups'
import type { ProjectTemplateRow } from '@/local/rows'
import { offsetText } from './templateForm'

/**
 * *Administration → Project Templates* (docs/01 §10.2, *Configuration: Manage*): the templates in their order with
 * colour, icon, description, the number of sections and milestones/gates, archived ones on request. Move up/down,
 * archive/unarchive through the templates endpoints (online only); *New* and a click open the editor page.
 */
const router = useRouter()
const notify = useNotify()
const online = useOnline()
const { templates, loaded } = useTemplates()

const showArchived = ref(false)
const all = computed(() => templates.value.templates)
const shown = computed(() => all.value.filter((t) => showArchived.value || !t.isArchived))
const archivedCount = computed(() => all.value.filter((t) => t.isArchived).length)

function summary(t: ProjectTemplateRow): string {
  const parts = templates.value.byId.get(t.id)
  if (!parts) return ''
  const gates = parts.milestones.filter((m) => m.kind === 'gate').length
  const milestones = parts.milestones.length - gates
  const last = parts.milestones.reduce((max, m) => Math.max(max, m.offsetDays ?? 0), 0)
  return [
    `${parts.sections.length} ${parts.sections.length === 1 ? 'section' : 'sections'}`,
    `${milestones} ${milestones === 1 ? 'milestone' : 'milestones'}`,
    `${gates} ${gates === 1 ? 'gate' : 'gates'}`,
    parts.milestones.length ? `until ${offsetText(last)}` : '',
    parts.statusIds.length ? `${parts.statusIds.length} statuses` : 'all statuses',
  ]
    .filter(Boolean)
    .join(' · ')
}

const busy = ref(false)
async function call(action: () => Promise<unknown>, success: string, failure: string) {
  busy.value = true
  try {
    await action()
    notify.success(success)
    void engine.syncNow()
  } catch (e) {
    notify.error(e, failure)
  } finally {
    busy.value = false
  }
}

function move(t: ProjectTemplateRow, step: -1 | 1) {
  const at = shown.value.findIndex((x) => x.id === t.id)
  const other = shown.value[at + step]
  if (!other) return
  const ids = all.value.map((x) => x.id)
  const a = ids.indexOf(t.id)
  const b = ids.indexOf(other.id)
  ids[a] = other.id
  ids[b] = t.id
  void call(() => templatesApi.order(ids), 'Order saved', 'Order not saved')
}

function menu(t: ProjectTemplateRow): MenuItem[] {
  const disabled = !online.value || busy.value
  return [
    t.isArchived
      ? {
          label: 'Unarchive',
          icon: 'pi pi-replay',
          disabled,
          command: () => void call(() => templatesApi.unarchive(t.id), `${t.name} is offered again`, 'Not changed'),
        }
      : {
          label: 'Archive',
          icon: 'pi pi-folder',
          disabled,
          command: () => void call(() => templatesApi.archive(t.id), `${t.name} is archived`, 'Not archived'),
        },
  ]
}

const open = (t: ProjectTemplateRow) => router.push({ name: 'template', params: { id: t.id } })
</script>

<template>
  <div class="page">
    <PageHeader title="Project Templates" :crumbs="[{ label: 'Administration' }]">
      <template #actions>
        <Button
          label="New template"
          icon="pi pi-plus"
          size="small"
          :disabled="!online"
          @click="router.push({ name: 'template-new' })"
        />
      </template>
    </PageHeader>
    <Message v-if="!online" severity="secondary" :closable="false" data-testid="offline-hint">
      Available online only: templates can be changed with a connection.
    </Message>
    <div v-if="archivedCount" class="toolbar">
      <span class="switch">
        <ToggleSwitch v-model="showArchived" input-id="tp-archived" />
        <label for="tp-archived">Show archived ({{ archivedCount }})</label>
      </span>
    </div>
    <section class="panel" aria-label="Project templates">
      <EmptyState
        v-if="loaded && !shown.length"
        icon="pi pi-clone"
        title="No project templates yet."
        text="A template brings statuses, sections, defaults, milestones and gates to new projects."
      />
      <ol v-else class="rows">
        <li v-for="(t, i) in shown" :key="t.id" :class="{ archived: t.isArchived }" :data-testid="`template-${t.id}`">
          <span class="order">
            <Button
              icon="pi pi-angle-up"
              text
              size="small"
              severity="secondary"
              :disabled="!online || busy || i === 0"
              :aria-label="`Move ${t.name} up`"
              @click="move(t, -1)"
            />
            <Button
              icon="pi pi-angle-down"
              text
              size="small"
              severity="secondary"
              :disabled="!online || busy || i === shown.length - 1"
              :aria-label="`Move ${t.name} down`"
              @click="move(t, 1)"
            />
          </span>
          <span class="badge" :style="{ background: t.color ?? 'var(--pgpm-none)' }" aria-hidden="true">
            <i :class="t.icon ? `pi ${t.icon}` : 'pi pi-briefcase'" />
          </span>
          <button type="button" class="pick" :aria-label="`Open ${t.name}`" @click="open(t)">
            <span class="name strong ellipsis">{{ t.name }}</span>
            <small class="muted ellipsis">{{ summary(t) }}</small>
            <small v-if="t.description" class="muted ellipsis">{{ t.description }}</small>
          </button>
          <Tag v-if="t.isArchived" value="Archived" severity="secondary" />
          <span class="actions">
            <MoreMenu :items="menu(t)" :label="`More actions for ${t.name}`" />
          </span>
        </li>
      </ol>
    </section>
  </div>
</template>

<style scoped>
.toolbar {
  display: flex;
  align-items: center;
}

.switch {
  display: inline-flex;
  align-items: center;
  gap: var(--pgpm-space-2);
}

.rows {
  list-style: none;
  margin: 0;
  padding: 0;
}

.rows li {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  min-height: 52px;
  padding: var(--pgpm-space-1) var(--pgpm-space-3) var(--pgpm-space-1) var(--pgpm-space-1);
  border-bottom: 1px solid var(--pgpm-border);
}

.rows li:last-child {
  border-bottom: none;
}

.rows li.archived {
  opacity: 0.6;
}

.order {
  display: inline-flex;
  flex: none;
}

.badge {
  flex: none;
  width: 28px;
  height: 28px;
  border-radius: 6px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  color: #fff;
}

.pick {
  all: unset;
  display: flex;
  flex-direction: column;
  min-width: 0;
  flex: 1;
  cursor: pointer;
}

.pick:focus-visible {
  outline: 1px solid var(--p-primary-color);
}

.actions {
  margin-left: auto;
  display: inline-flex;
}
</style>
