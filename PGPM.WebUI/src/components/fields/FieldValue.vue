<script setup lang="ts">
import { computed } from 'vue'
import StatusBadge from '@/components/items/StatusBadge.vue'
import type { FieldLike } from '@/domain/fields/fieldWidget'
import { renderValue, type RenderContext } from '@/domain/fields/renderValue'
import { scoreMax } from '@/domain/score'
import ScoreBadge from './ScoreBadge.vue'

/**
 * A field's value, read-only (docs/03 §12.7): select options as chips with their colour and points as a muted suffix
 * (an id the configuration lost as *(removed option)*), scores as a badge "58 / 65", ratings "4 / 5" or *n/a*, dates
 * with an *overdue* marker (`overdue`: the reason), Markdown as its first line (`compact`) – the full text is the
 * caller's (MarkdownView). Empty: a dash.
 */
const props = withDefaults(
  defineProps<{
    field: FieldLike
    /** The stored value; a score's computed result. */
    value: unknown
    context?: RenderContext
    overdue?: string | null
    /** A list cell: one line, small chips. */
    compact?: boolean
  }>(),
  { context: () => ({}), overdue: null, compact: false },
)

const rendered = computed(() => renderValue(props.field, props.value, props.context))
const markdownLine = computed(() => {
  if (props.field.type !== 'markdown' || typeof props.value !== 'string') return ''
  const line = props.value
    .split('\n')
    .map((l) => l.replace(/^[#>*\-\s]+/, '').trim())
    .find(Boolean)
  return line ?? ''
})
const decimals = computed(() =>
  Number.isInteger(props.field.config?.decimals) ? (props.field.config!.decimals as number) : 0,
)
</script>

<template>
  <span class="field-value" :class="{ compact }">
    <ScoreBadge
      v-if="field.type === 'score'"
      :value="typeof value === 'number' ? value : null"
      :max="scoreMax(field)"
      :decimals="decimals"
      :label="field.label"
      :compact="compact"
    />
    <span v-else-if="rendered.empty" class="muted">–</span>
    <template v-else-if="rendered.options">
      <span
        v-for="o in rendered.options"
        :key="o.id"
        class="chip"
        :class="{ removed: o.removed, archived: o.archived }"
        :style="o.color ? { '--chip': o.color } : undefined"
        :title="o.removed ? 'This option is no longer configured' : o.archived ? 'An archived option' : undefined"
      >
        <span v-if="o.color" class="dot" aria-hidden="true" />{{ o.label
        }}<small v-if="o.points !== null" class="points"> +{{ o.points }}</small>
      </span>
    </template>
    <span v-else-if="field.type === 'rating'" class="rating" :class="{ na: rendered.na }">{{ rendered.text }}</span>
    <a
      v-else-if="field.type === 'url' && typeof value === 'string'"
      :href="value"
      target="_blank"
      rel="noopener noreferrer"
      class="ellipsis"
      >{{ value }}</a
    >
    <a v-else-if="field.type === 'email' && typeof value === 'string'" :href="`mailto:${value}`" class="ellipsis">{{
      value
    }}</a>
    <span v-else-if="field.type === 'markdown'" class="ellipsis">{{ markdownLine }}</span>
    <span v-else :class="{ ellipsis: compact, multiline: field.type === 'textarea' && !compact }">{{
      rendered.text
    }}</span>
    <StatusBadge v-if="overdue" :overdue="overdue" />
  </span>
</template>

<style scoped>
.field-value {
  display: inline-flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pgpm-space-1);
  min-width: 0;
  max-width: 100%;
}

.field-value.compact {
  flex-wrap: nowrap;
  overflow: hidden;
}

.chip {
  --chip: var(--pgpm-muted);
  display: inline-flex;
  align-items: center;
  gap: 0.25rem;
  padding: 0 0.4375rem;
  border-radius: 999px;
  border: 1px solid color-mix(in srgb, var(--chip) 40%, var(--pgpm-border));
  background: color-mix(in srgb, var(--chip) 10%, transparent);
  font-size: 0.857rem;
  line-height: 1.5;
  white-space: nowrap;
}

.chip .dot {
  width: 0.5rem;
  height: 0.5rem;
  border-radius: 50%;
  background: var(--chip);
}

.chip.removed {
  font-style: italic;
  color: var(--pgpm-muted);
}

.chip.archived {
  opacity: 0.75;
}

.points {
  color: var(--pgpm-muted);
}

.rating {
  font-variant-numeric: tabular-nums;
}

.rating.na {
  color: var(--pgpm-muted);
}

.multiline {
  white-space: pre-wrap;
}
</style>
