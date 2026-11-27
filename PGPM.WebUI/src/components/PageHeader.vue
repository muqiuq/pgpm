<script setup lang="ts">
/**
 * The one 40 px line every page starts with (docs/03 §12.1): breadcrumb and title on the left, key status
 * after it, the primary action on the right. No banner card.
 */
defineProps<{ title: string; crumbs?: { label: string; to?: string | object }[] }>()
</script>

<template>
  <header class="page-header">
    <div class="titles">
      <nav v-if="crumbs?.length" class="crumbs" aria-label="Breadcrumb">
        <template v-for="(crumb, i) in crumbs" :key="i">
          <RouterLink v-if="crumb.to" :to="crumb.to">{{ crumb.label }}</RouterLink>
          <span v-else>{{ crumb.label }}</span>
          <i class="pi pi-angle-right" aria-hidden="true" />
        </template>
      </nav>
      <h1 class="ellipsis">{{ title }}</h1>
      <div v-if="$slots.status" class="status"><slot name="status" /></div>
    </div>
    <div v-if="$slots.actions" class="actions"><slot name="actions" /></div>
  </header>
</template>

<style scoped>
.page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--pgpm-space-3);
  min-height: var(--pgpm-page-header-height);
  flex-wrap: wrap;
}

.titles {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  min-width: 0;
  flex: 1 1 auto;
}

.crumbs {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-1);
  color: var(--pgpm-muted);
  white-space: nowrap;
}

.crumbs a {
  color: var(--pgpm-muted);
}

.crumbs i {
  font-size: 0.75rem;
}

.status {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  flex-wrap: wrap;
}

.actions {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
}

/* On a phone the title wraps instead of being cut to a few letters; the key and status go below it. */
@media (max-width: 599px) {
  .titles {
    flex-wrap: wrap;
  }

  h1 {
    flex: 1 1 10rem;
    white-space: normal;
    overflow-wrap: anywhere;
  }
}

/* Printed, the title wraps instead of being cut; the actions stay on screen. */
@media print {
  .actions {
    display: none;
  }

  h1 {
    white-space: normal;
    overflow: visible;
  }
}
</style>
