<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import EmptyState from '@/components/EmptyState.vue'
import PageHeader from '@/components/PageHeader.vue'

/**
 * A menu entry's page until its phase of the build plan (docs/05 §3) replaces it: the page header and what is coming.
 * Title, icon, phase and summary come from the route (router/index.ts).
 */
const route = useRoute()
const title = computed(() => route.meta.title ?? '')
</script>

<template>
  <div class="page">
    <PageHeader :title="title" :crumbs="route.meta.crumbs" />
    <section class="panel">
      <EmptyState :icon="route.meta.icon" :title="`Coming in phase ${route.meta.phase}`" :text="route.meta.summary" />
    </section>
  </div>
</template>
