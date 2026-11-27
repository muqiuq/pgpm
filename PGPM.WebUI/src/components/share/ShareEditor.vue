<script setup lang="ts">
import Select from 'primevue/select'
import { computed } from 'vue'
import type { ShareSpec } from '@/api/sync'
import type { Access, GroupRow } from '@/local/rows'

/**
 * The groups an item is shared with, each with *read*, *edit* or *manage* (docs/01 §4.2): one line per group, *No
 * access* by default. Used by the create dialogs (local inserts, offline) and the Share dialog (online).
 */
const props = withDefaults(defineProps<{ groups: GroupRow[]; disabled?: boolean; idPrefix?: string }>(), {
  disabled: false,
  idPrefix: 'share',
})
const shares = defineModel<ShareSpec[]>({ required: true })

const levels: { label: string; value: Access | null }[] = [
  { label: 'No access', value: null },
  { label: 'Read', value: 'read' },
  { label: 'Edit', value: 'edit' },
  { label: 'Manage', value: 'manage' },
]

const accessOf = computed(() => new Map(shares.value.map((s) => [s.groupId, s.access])))

function set(groupId: string, access: Access | null) {
  const others = shares.value.filter((s) => s.groupId !== groupId)
  shares.value = access ? [...others, { groupId, access }] : others
}
</script>

<template>
  <div class="share-editor">
    <p v-if="!props.groups.length" class="muted">No groups yet: only you (and admins) will see it.</p>
    <ul v-else>
      <li v-for="group in props.groups" :key="group.id">
        <label :id="`${idPrefix}-${group.id}-label`" :for="`${idPrefix}-${group.id}`" class="name">
          <span>{{ group.name }}</span>
          <small v-if="group.description" class="muted ellipsis">{{ group.description }}</small>
        </label>
        <Select
          :input-id="`${idPrefix}-${group.id}`"
          :aria-labelledby="`${idPrefix}-${group.id}-label`"
          :model-value="accessOf.get(group.id) ?? null"
          :options="levels"
          option-label="label"
          option-value="value"
          size="small"
          :disabled="disabled"
          class="level"
          @update:model-value="(v: Access | null) => set(group.id, v)"
        />
      </li>
    </ul>
  </div>
</template>

<style scoped>
.share-editor ul {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-1);
  max-height: 18rem;
  overflow-y: auto;
}

.share-editor li {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--pgpm-space-3);
}

.name {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.level {
  width: 9rem;
  flex: none;
}
</style>
