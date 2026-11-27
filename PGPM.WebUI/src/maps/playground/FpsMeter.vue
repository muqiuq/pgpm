<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'

/** A dev overlay (spike S2): frames per second over the last second, the slowest frame, and what is drawn. */
defineProps<{ drawn: number; total: number; zoom: number }>()

const fps = ref(0)
const worst = ref(0)
let handle = 0
let last = 0
let times: number[] = []

function tick(now: number) {
  if (last) times.push(now - last)
  last = now
  const total = times.reduce((a, b) => a + b, 0)
  if (total >= 1000) {
    fps.value = Math.round((1000 * times.length) / total)
    worst.value = Math.round(Math.max(...times))
    times = []
  }
  handle = requestAnimationFrame(tick)
}

onMounted(() => (handle = requestAnimationFrame(tick)))
onBeforeUnmount(() => cancelAnimationFrame(handle))
</script>

<template>
  <div class="fps panel" role="status" aria-label="Frame rate">
    <strong :class="{ low: fps < 45 }">{{ fps }} fps</strong>
    <span class="muted">worst {{ worst }} ms</span>
    <span class="muted">{{ drawn }} / {{ total }} drawn · {{ Math.round(zoom * 100) }} %</span>
  </div>
</template>

<style scoped>
.fps {
  display: flex;
  flex-direction: column;
  padding: var(--pgpm-space-1) var(--pgpm-space-2);
  border-radius: var(--p-border-radius-md);
  font-size: 0.857rem;
  font-feature-settings: 'tnum';
  box-shadow: 0 2px 10px rgb(0 0 0 / 10%);
  white-space: nowrap;
}

.low {
  color: var(--pgpm-fail);
}
</style>
