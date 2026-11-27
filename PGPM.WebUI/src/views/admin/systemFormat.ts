/** "830 KB", "2.4 MB", "1.2 GB" (1 KB = 1024 bytes, as the file limits are written, docs/03 §10.1); "–" when unknown. */
export function bytes(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value) || value < 0) return '–'
  const units = ['bytes', 'KB', 'MB', 'GB', 'TB']
  let size = value
  let unit = 0
  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024
    unit++
  }
  if (unit === 0) return `${size} ${size === 1 ? 'byte' : 'bytes'}`
  return `${size >= 100 || Number.isInteger(size) ? Math.round(size) : size.toFixed(1)} ${units[unit]}`
}

/** "3 d 4 h", "2 h 5 min", "1 h", "45 s": how long the server runs, how often a task does. */
export function duration(seconds: number): string {
  const s = Math.max(0, Math.floor(seconds))
  const days = Math.floor(s / 86_400)
  const hours = Math.floor((s % 86_400) / 3600)
  const minutes = Math.floor((s % 3600) / 60)
  if (days > 0) return hours ? `${days} d ${hours} h` : `${days} d`
  if (hours > 0) return minutes ? `${hours} h ${minutes} min` : `${hours} h`
  if (minutes > 0) return `${minutes} min`
  return `${s} s`
}

/** The browser and system of a user agent, briefly ("Firefox on macOS"); the whole text stays in the title. */
export function agent(userAgent: string | null | undefined): string {
  if (!userAgent) return '–'
  const browser = /Edg\//.test(userAgent)
    ? 'Edge'
    : /Firefox\//.test(userAgent)
      ? 'Firefox'
      : /Chrome\//.test(userAgent)
        ? 'Chrome'
        : /Safari\//.test(userAgent)
          ? 'Safari'
          : null
  const system = /iPhone|iPad/.test(userAgent)
    ? 'iOS'
    : /Android/.test(userAgent)
      ? 'Android'
      : /Mac OS X|Macintosh/.test(userAgent)
        ? 'macOS'
        : /Windows/.test(userAgent)
          ? 'Windows'
          : /Linux/.test(userAgent)
            ? 'Linux'
            : null
  if (browser && system) return `${browser} on ${system}`
  return browser ?? system ?? (userAgent.length > 40 ? `${userAgent.slice(0, 40)}…` : userAgent)
}
