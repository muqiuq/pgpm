<script setup lang="ts">
import QRCode from 'qrcode'
import { onMounted, ref, watch } from 'vue'

/**
 * A QR code drawn in the browser (PNMTD, docs/03 §4.4): the otpauth:// URI never leaves the page. On a canvas, not as a
 * data: image as PNMTD does – PGPM's CSP allows no data: (docs/03 §3.1).
 */
const props = withDefaults(defineProps<{ text: string; size?: number; label?: string }>(), {
  size: 184,
  label: 'QR code',
})
const canvas = ref<HTMLCanvasElement | null>(null)

async function draw() {
  if (!canvas.value) return
  await QRCode.toCanvas(canvas.value, props.text, { width: props.size, margin: 1, errorCorrectionLevel: 'M' })
}

onMounted(draw)
watch(() => [props.text, props.size], draw)
</script>

<template>
  <canvas ref="canvas" class="qr-code" role="img" :aria-label="label" :width="size" :height="size" />
</template>

<style scoped>
.qr-code {
  display: block;
  /* White behind the code in the dark theme too: phone cameras read dark on light. */
  background: #fff;
  padding: 4px;
  border-radius: var(--p-border-radius-md);
}
</style>
