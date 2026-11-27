import type { View } from '../panZoom'

/**
 * Spike S2 (docs/05 §7): pan and zoom frame times on a map, measured by driving the view from requestAnimationFrame –
 * every frame sets the next view, the time between frames is what it cost (rendering included). Each phase starts at
 * its view, waits until it has settled (detail, culling, first paint), then records.
 */

declare global {
  interface Window {
    /** The benchmark's results, for a browser driven from outside (tools/…, the spike's measurement). */
    __pgpmBench?: { status: 'running' | 'done'; results: PhaseResult[]; elements: number; userAgent: string }
  }
}

export interface PhaseResult {
  name: string
  frames: number
  fps: number
  /** Frame times in ms. */
  p50: number
  p95: number
  max: number
  /** Elements drawn at the end of the phase. */
  drawn: number
}

export interface Phase {
  name: string
  start: View
  /** The view of frame `i`. */
  step: (view: View, i: number) => View
  frames: number
}

const frame = () => new Promise<number>((resolve) => requestAnimationFrame(resolve))

function quantile(sorted: number[], q: number): number {
  if (!sorted.length) return 0
  return sorted[Math.min(sorted.length - 1, Math.floor(q * sorted.length))]!
}

export async function runPhase(phase: Phase, setView: (v: View) => void, drawn: () => number): Promise<PhaseResult> {
  setView(phase.start)
  // Long enough for the view to settle (full detail, a tight culling region) however fast frames come.
  await new Promise((resolve) => setTimeout(resolve, 500))
  for (let i = 0; i < 10; i++) await frame()
  let view = phase.start
  const times: number[] = []
  let last = await frame()
  for (let i = 0; i < phase.frames; i++) {
    view = phase.step(view, i)
    setView(view)
    const now = await frame()
    times.push(now - last)
    last = now
  }
  const sorted = [...times].sort((a, b) => a - b)
  const total = times.reduce((a, b) => a + b, 0)
  const round = (n: number) => Math.round(n * 10) / 10
  return {
    name: phase.name,
    frames: times.length,
    fps: round((1000 * times.length) / Math.max(1, total)),
    p50: round(quantile(sorted, 0.5)),
    p95: round(quantile(sorted, 0.95)),
    max: round(sorted.at(-1) ?? 0),
    drawn: drawn(),
  }
}

/** The standard phases for a map whose content spans `world` (a box) in a viewport of width × height. */
export function standardPhases(
  world: { minX: number; minY: number; maxX: number; maxY: number },
  width: number,
  height: number,
): Phase[] {
  const centre = { x: (world.minX + world.maxX) / 2, y: (world.minY + world.maxY) / 2 }
  const at = (k: number): View => ({ k, x: width / 2 - centre.x * k, y: height / 2 - centre.y * k })
  const pan =
    (dx: number, dy: number) =>
    (v: View, i: number): View => ({
      ...v,
      x: v.x + (Math.floor(i / 60) % 2 ? -dx : dx),
      y: v.y + (Math.floor(i / 60) % 2 ? -dy : dy),
    })
  const zoom =
    (factor: number) =>
    (v: View, i: number): View => {
      const f = Math.floor(i / 60) % 2 ? 1 / factor : factor
      const k = v.k * f
      return { k, x: width / 2 - ((width / 2 - v.x) * k) / v.k, y: height / 2 - ((height / 2 - v.y) * k) / v.k }
    }
  return [
    { name: 'pan at 100 %', start: at(1), step: pan(12, 5), frames: 240 },
    { name: 'pan at 60 %', start: at(0.6), step: pan(12, 5), frames: 240 },
    { name: 'pan at 45 % (just above LOD)', start: at(0.46), step: pan(12, 5), frames: 240 },
    { name: 'pan at 30 % (LOD)', start: at(0.3), step: pan(12, 5), frames: 240 },
    { name: 'pan at 12 % (whole map, LOD)', start: at(0.12), step: pan(6, 3), frames: 240 },
    { name: 'zoom 100 % ↔ 55 %', start: at(1), step: zoom(0.99), frames: 240 },
    { name: 'zoom 70 % ↔ 38 % (crosses LOD)', start: at(0.7), step: zoom(0.99), frames: 240 },
    { name: 'zoom 45 % ↔ 25 % (LOD)', start: at(0.45), step: zoom(0.99), frames: 240 },
  ]
}
