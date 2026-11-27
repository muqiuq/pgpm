<script setup lang="ts">
import Select from 'primevue/select'
import { computed } from 'vue'
import type { TemplateParts } from '@/local/catalogue'

/**
 * The template of a new project (docs/01 §10.2): *Blank project* or one of the templates (not archived), each with its
 * icon, colour and description, and what it brings (sections, milestones and gates). v-model is the template's id (null:
 * blank).
 */
const props = defineProps<{ templates: readonly TemplateParts[]; inputId?: string }>()
const templateId = defineModel<string | null>({ default: null })

interface Option {
  id: string | null
  name: string
  description: string | null
  icon: string | null
  color: string | null
  summary: string
}

function summary(parts: TemplateParts): string {
  const gates = parts.milestones.filter((m) => m.kind === 'gate').length
  const milestones = parts.milestones.length - gates
  const bits = [
    parts.sections.length ? `${parts.sections.length} section${parts.sections.length === 1 ? '' : 's'}` : null,
    milestones ? `${milestones} milestone${milestones === 1 ? '' : 's'}` : null,
    gates ? `${gates} gate${gates === 1 ? '' : 's'}` : null,
  ].filter(Boolean)
  return bits.join(' · ')
}

const options = computed<Option[]>(() => [
  { id: null, name: 'Blank project', description: null, icon: 'pi-file', color: null, summary: '' },
  ...props.templates.map((p) => ({
    id: p.template.id,
    name: p.template.name,
    description: p.template.description,
    icon: p.template.icon,
    color: p.template.color,
    summary: summary(p),
  })),
])
const chosen = computed(() => options.value.find((o) => o.id === templateId.value) ?? options.value[0]!)
</script>

<template>
  <div class="template-picker">
    <Select
      v-model="templateId"
      :input-id="inputId"
      :options="options"
      option-label="name"
      option-value="id"
      aria-label="Template"
    >
      <template #value>
        <span class="option">
          <i
            :class="['pi', chosen.icon ?? 'pi-clone']"
            :style="{ color: chosen.color ?? undefined }"
            aria-hidden="true"
          />
          {{ chosen.name }}
        </span>
      </template>
      <template #option="{ option }">
        <span class="option">
          <i
            :class="['pi', option.icon ?? 'pi-clone']"
            :style="{ color: option.color ?? undefined }"
            aria-hidden="true"
          />
          <span>
            {{ option.name }}
            <small v-if="option.summary" class="muted"> · {{ option.summary }}</small>
          </span>
        </span>
      </template>
    </Select>
    <small v-if="chosen.description || chosen.summary" class="muted description">
      {{ chosen.description }}<template v-if="chosen.description && chosen.summary"> – </template>{{ chosen.summary }}
    </small>
  </div>
</template>

<style scoped>
.template-picker {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-1);
}

.option {
  display: inline-flex;
  align-items: center;
  gap: var(--pgpm-space-2);
}

.description {
  display: -webkit-box;
  -webkit-line-clamp: 3;
  line-clamp: 3;
  -webkit-box-orient: vertical;
  overflow: hidden;
}
</style>
