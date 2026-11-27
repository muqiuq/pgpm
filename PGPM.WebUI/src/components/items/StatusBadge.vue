<script setup lang="ts">
import type { StatusRow } from '@/local/rows'

/**
 * An item's status (docs/01 §5, docs/03 §12.7): its colour, icon and name; with an *overdue* marker when the item is
 * longer in it than it allows (the reason as the tooltip). An unknown status (not synced yet) shows as a dash.
 */
defineProps<{ status?: StatusRow | null; overdue?: string | null }>()
</script>

<template>
  <span class="status-badge-wrap">
    <span
      v-if="status"
      class="status-badge"
      :class="{ archived: status.isArchived }"
      :style="{ '--status': status.color }"
    >
      <i v-if="status.icon" :class="['pi', status.icon]" aria-hidden="true" />
      <span v-else class="dot" aria-hidden="true" />
      <span class="name">{{ status.name }}</span>
    </span>
    <span v-else-if="!overdue" class="muted">–</span>
    <span
      v-if="overdue"
      v-tooltip.top="overdue"
      role="img"
      class="overdue"
      :aria-label="`Overdue: ${overdue}`"
      tabindex="0"
    >
      <i class="pi pi-clock" aria-hidden="true" /> Overdue
    </span>
  </span>
</template>

<style scoped>
.status-badge-wrap {
  display: inline-flex;
  align-items: center;
  gap: var(--pgpm-space-1);
  min-width: 0;
}

.status-badge {
  display: inline-flex;
  align-items: center;
  gap: 0.375rem;
  padding: 0.0625rem 0.5rem;
  border-radius: 999px;
  border: 1px solid color-mix(in srgb, var(--status) 45%, transparent);
  background: color-mix(in srgb, var(--status) 14%, transparent);
  color: var(--pgpm-text);
  font-size: 0.857rem;
  line-height: 1.5;
  white-space: nowrap;
  max-width: 100%;
}

.status-badge .pi {
  color: var(--status);
  font-size: 0.786rem;
}

.status-badge .name {
  overflow: hidden;
  text-overflow: ellipsis;
}

.status-badge.archived {
  opacity: 0.7;
  font-style: italic;
}

.dot {
  width: 0.5rem;
  height: 0.5rem;
  border-radius: 50%;
  background: var(--status);
  flex: none;
}

.overdue {
  display: inline-flex;
  align-items: center;
  gap: 0.25rem;
  padding: 0.0625rem 0.375rem;
  border-radius: 999px;
  background: var(--pgpm-warn-soft, color-mix(in srgb, var(--pgpm-warn) 18%, transparent));
  color: var(--pgpm-warn);
  font-size: 0.786rem;
  font-weight: 600;
  white-space: nowrap;
}
</style>
