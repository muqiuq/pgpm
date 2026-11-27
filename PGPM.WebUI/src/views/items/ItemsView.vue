<script setup lang="ts">
import type { ItemType } from '@/local/rows'
import ItemList from './ItemList.vue'

/**
 * *Work → Projects / Leads / Ideas / Notes* (docs/01 §4.3): one list page for every type. The routes share this
 * component, so the list underneath is keyed on the type – its state (filters, view, columns, `list:<type>`) starts
 * anew for each.
 */
defineProps<{ type: ItemType }>()
</script>

<template>
  <ItemList :key="type" :type="type" />
</template>
