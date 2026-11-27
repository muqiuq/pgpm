<script setup lang="ts">
import Tag from 'primevue/tag'
import type { MenuItem } from 'primevue/menuitem'
import { computed } from 'vue'
import FieldEditor from '@/components/fields/FieldEditor.vue'
import ScoreBadge from '@/components/fields/ScoreBadge.vue'
import type { MarkdownFile } from '@/components/markdown/types'
import MoreMenu from '@/components/MoreMenu.vue'
import { subheadingOf } from '@/domain/fields/fieldWidget'
import { formatDate, timeboxPassed, type RenderContext } from '@/domain/fields/renderValue'
import { scoreMax } from '@/domain/score'
import type { Catalogue } from '@/local/catalogue'
import { fieldReadOnly, fieldValue } from '@/local/fields'
import type { FieldDefinitionRow, ItemRow, SectionRow } from '@/local/rows'

/**
 * One section of an idea or project (docs/01 §6.1–§6.2): its fields in order (archived ones hidden, sub-headings where a
 * field starts one), each edited where it is shown; its scores also in the header, so a collapsed section still shows
 * them. `section.canEdit = false` (a group restriction) or *read* on the item make it read-only; *Remove section* keeps
 * the values (hidden until the section is back).
 */
const props = defineProps<{
  item: ItemRow
  section: SectionRow
  catalogue: Catalogue
  values: Record<string, unknown>
  scores: ReadonlyMap<string, number | null>
  canEdit: boolean
  collapsed: boolean
  closed: boolean
  today: string
  context: RenderContext
  conflicted: ReadonlySet<string>
  files?: readonly MarkdownFile[] | null
  save: (field: FieldDefinitionRow, value: unknown) => Promise<unknown>
}>()
const emit = defineEmits<{ toggle: []; remove: []; conflict: [] }>()

const fields = computed(() =>
  (props.catalogue.fieldsBySection.get(props.section.id) ?? []).filter((f) => !f.isArchived),
)
const scoreFields = computed(() => fields.value.filter((f) => f.type === 'score'))
const bodyId = computed(() => `section-${props.item.id}-${props.section.id}`)
const readOnlySection = computed(() => !props.section.canEdit)

const valueOf = (field: FieldDefinitionRow) =>
  field.type === 'score' ? (props.scores.get(field.id) ?? null) : fieldValue(props.catalogue, field, props.values)

function overdue(field: FieldDefinitionRow): string | null {
  const value = props.values[field.key]
  if (!timeboxPassed(field, value, props.today, props.closed)) return null
  return `${field.label} passed (${field.type === 'date' ? formatDate(String(value)) : String(value)})`
}

const readOnly = (field: FieldDefinitionRow) =>
  props.canEdit ? fieldReadOnly(props.item, props.section, field) : 'You can read this item, not change it.'

const decimals = (field: FieldDefinitionRow) =>
  Number.isInteger(field.config?.decimals) ? (field.config!.decimals as number) : 0

const menu = computed<MenuItem[]>(() =>
  props.canEdit
    ? [{ label: `Remove ${props.section.name}…`, icon: 'pi pi-minus-circle', command: () => emit('remove') }]
    : [],
)
</script>

<template>
  <section class="panel section-panel" :aria-labelledby="`${bodyId}-title`" :data-testid="`section-${section.id}`">
    <div class="panel-header header">
      <button type="button" class="toggle" :aria-expanded="!collapsed" :aria-controls="bodyId" @click="emit('toggle')">
        <i :class="['pi', collapsed ? 'pi-chevron-right' : 'pi-chevron-down']" aria-hidden="true" />
        <span :id="`${bodyId}-title`">{{ section.name }}</span>
      </button>
      <i
        v-if="readOnlySection"
        v-tooltip.top="'Only some groups may edit this section'"
        role="img"
        class="pi pi-lock muted lock"
        aria-label="Read-only for you"
        tabindex="0"
      />
      <Tag v-if="section.isArchived" value="Archived" severity="secondary" />
      <span class="scores">
        <ScoreBadge
          v-for="f in scoreFields"
          :key="f.id"
          v-tooltip.top="f.label"
          :value="scores.get(f.id) ?? null"
          :max="scoreMax(f)"
          :decimals="decimals(f)"
          :label="f.label"
        />
      </span>
      <MoreMenu v-if="menu.length" :items="menu" :label="`More actions for ${section.name}`" />
    </div>
    <div v-show="!collapsed" :id="bodyId" class="panel-body">
      <p v-if="section.description" class="muted description">{{ section.description }}</p>
      <p v-if="!fields.length" class="muted">This section has no fields yet.</p>
      <dl v-else class="facts">
        <template v-for="f in fields" :key="f.id">
          <dt v-if="subheadingOf(f)" class="subheading">{{ subheadingOf(f) }}</dt>
          <dt :class="{ required: f.required }">
            <span>{{ f.label }}</span>
            <i
              v-if="f.helpText"
              v-tooltip.top="f.helpText"
              role="img"
              class="pi pi-question-circle muted help"
              :aria-label="f.helpText"
              tabindex="0"
            />
            <i
              v-if="conflicted.has(f.id)"
              v-tooltip.top="'Sync conflict – open it'"
              class="pi pi-exclamation-triangle conflict"
              role="button"
              aria-label="Sync conflict"
              tabindex="0"
              @click="emit('conflict')"
              @keydown.enter="emit('conflict')"
            />
          </dt>
          <dd :data-field="f.key">
            <FieldEditor
              :field="f"
              :value="valueOf(f)"
              :save="(v: unknown) => save(f, v)"
              :readonly="readOnly(f)"
              :context="context"
              :overdue="overdue(f)"
              :item-type="item.type === 'idea' || item.type === 'project' ? item.type : undefined"
              :item-id="item.id"
              :files="files"
            />
          </dd>
        </template>
      </dl>
    </div>
  </section>
</template>

<style scoped>
.header {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
}

.toggle {
  all: unset;
  display: inline-flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  cursor: pointer;
  font-weight: 600;
  border-radius: var(--p-border-radius-sm);
}

.toggle:focus-visible {
  outline: 1px solid var(--p-primary-color);
}

.toggle i {
  font-size: 0.75rem;
  color: var(--pgpm-muted);
}

.scores {
  margin-left: auto;
  display: inline-flex;
  flex-wrap: wrap;
  gap: var(--pgpm-space-1);
  font-weight: 400;
}

.lock {
  font-size: 0.8rem;
}

.description {
  margin: 0 0 var(--pgpm-space-3);
}

.subheading {
  grid-column: 1 / -1;
  margin-top: var(--pgpm-space-2);
  font-size: 0.786rem;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--pgpm-muted);
}

.help {
  margin-left: var(--pgpm-space-1);
  font-size: 0.75rem;
}

.conflict {
  margin-left: var(--pgpm-space-1);
  color: var(--pgpm-warn);
  cursor: pointer;
}

dt.required span::after {
  content: ' *';
  color: var(--pgpm-muted);
}

/* Phones: the label above its value. */
@media (max-width: 599px) {
  .facts {
    grid-template-columns: minmax(0, 1fr);
    row-gap: var(--pgpm-space-1);
  }

  .facts > dd {
    margin-bottom: var(--pgpm-space-2);
  }
}
</style>
