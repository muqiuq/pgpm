<script setup lang="ts">
import EmptyState from '@/components/EmptyState.vue'
import PageHeader from '@/components/PageHeader.vue'
</script>

<template>
  <div class="page">
    <PageHeader title="Not found" />
    <section class="panel">
      <EmptyState
        icon="pi pi-question-circle"
        title="There is no page here."
        text="It may have moved, or the link is wrong."
      >
        <RouterLink to="/">Back to the start</RouterLink>
      </EmptyState>
    </section>
  </div>
</template>
