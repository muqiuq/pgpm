/**
 * UUID v7 (RFC 9562), as the server expects for client-created rows (data model §1): 48 bits of milliseconds, version
 * 7, a counter in the next 12 bits so that ids made in the same millisecond still sort in creation order, then random
 * bits. The repository has its own generator with the sync core; the in-memory data source and the playground use this.
 */
let lastMs = -1
let counter = 0

function hex(bytes: Uint8Array): string {
  return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('')
}

export function uuidv7(now = Date.now()): string {
  const bytes = new Uint8Array(16)
  crypto.getRandomValues(bytes)
  if (now <= lastMs) {
    counter = (counter + 1) & 0xfff
    if (counter === 0) lastMs++
    now = lastMs
  } else {
    lastMs = now
    counter = (bytes[6]! & 0x07) << 8
  }
  for (let i = 5; i >= 0; i--) {
    bytes[i] = now % 256
    now = Math.floor(now / 256)
  }
  bytes[6] = 0x70 | (counter >> 8)
  bytes[7] = counter & 0xff
  bytes[8] = 0x80 | (bytes[8]! & 0x3f)
  const h = hex(bytes)
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`
}
