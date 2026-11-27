<script setup lang="ts">
/**
 * PGPM's mark: four cards on a 2×2 matrix, the "star initiatives" quadrant solid (tools/make-icons.py draws the same on
 * the brand blue for the favicon and the app icons), in the current text colour.
 */
defineProps<{ size?: number }>()
</script>

<template>
  <svg
    class="logo-mark"
    viewBox="128 128 256 256"
    :width="size ?? 24"
    :height="size ?? 24"
    role="img"
    aria-label="PGPM"
  >
    <g fill="currentColor">
      <rect x="128" y="128" width="112" height="112" rx="20" fill-opacity="0.45" />
      <rect x="272" y="128" width="112" height="112" rx="20" />
      <rect x="128" y="272" width="112" height="112" rx="20" fill-opacity="0.45" />
      <rect x="272" y="272" width="112" height="112" rx="20" fill-opacity="0.45" />
    </g>
  </svg>
</template>
