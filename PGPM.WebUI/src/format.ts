/**
 * Formatting shared by the views (PNMTD's format.ts, trimmed): v1 timestamps are UTC instants; they are shown in the
 * browser's zone, as relative times with the absolute one in a tooltip.
 */

const absolute = new Intl.DateTimeFormat(undefined, {
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
})

const dateOnly = new Intl.DateTimeFormat(undefined, { year: 'numeric', month: 'short', day: 'numeric' })

export function toDate(value: string | number | Date | null | undefined): Date | null {
  if (value === null || value === undefined || value === '') {
    return null
  }
  const date = value instanceof Date ? value : new Date(value)
  return Number.isNaN(date.getTime()) ? null : date
}

export function formatAbsolute(value: string | number | Date | null | undefined): string {
  const date = toDate(value)
  return date ? absolute.format(date) : '–'
}

/** "just now", "5 min ago", "in 3 h", "2 d ago"; beyond 30 days the date. */
export function formatRelative(value: string | number | Date | null | undefined, now: number): string {
  const date = toDate(value)
  if (!date) {
    return 'never'
  }
  const diff = now - date.getTime()
  const abs = Math.abs(diff)
  if (abs < 45_000) {
    return 'just now'
  }
  if (abs > 30 * 86_400_000) {
    return dateOnly.format(date)
  }
  const amount =
    abs < 3_600_000
      ? `${Math.round(abs / 60_000)} min`
      : abs < 86_400_000
        ? `${Math.round(abs / 3_600_000)} h`
        : `${Math.round(abs / 86_400_000)} d`
  return diff >= 0 ? `${amount} ago` : `in ${amount}`
}
