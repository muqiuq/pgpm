import type { Row } from '@/local/outbox'
import { fetchApi, fetchJson } from './client'

/**
 * The file transfer of docs/03 §10.2 (PLM's chunked protocol, plaintext): the metadata (`files` rows) syncs like any
 * row; the bytes go through these calls. Raw bodies, so they go through `fetchApi` rather than the generated client.
 *
 * | Call | Body | Answer |
 * |---|---|---|
 * | `PUT /api/v1/files/{id}/chunks/{n}` | raw bytes ≤ 4 MiB; chunk 0 starts over, others in order | `{received}` |
 * | `POST /api/v1/files/{id}/commit` | `{sha256, size}` | part of the row: `{id, status: "available", sha256, sizeBytes, contentType, width, height, fileName, rev, seq}` – an image's stored bytes (metadata stripped) |
 * | `GET /api/v1/files/{id}/{name}` | – | the bytes (Range, ETag = sha256); 404 `file_pending` before the commit |
 *
 * Refusals (ProblemDetails `code`, docs/03 §10.3): 409 `chunk_out_of_order` (+ `expected`), `file_available`,
 * `size_mismatch`, `sha256_mismatch`, `upload_missing`; 413 `too_large`; 415 `not_an_image`; 422 `blocked_type`; 429
 * `too_many_uploads`; 503 `file_storage_unavailable`; 507 `file_storage_full` (chunk 0 would eat into the disk's reserve,
 * `Files:MinFreeMb`); 404 for a row the user can't see, 403 `missing_access` for a reader.
 */
export interface FileTransport {
  putChunk(id: string, index: number, bytes: ArrayBuffer, signal?: AbortSignal): Promise<{ received: number }>
  commit(id: string, body: { sha256: string; size: number }, signal?: AbortSignal): Promise<Row>
  download(id: string, name: string, signal?: AbortSignal): Promise<{ bytes: ArrayBuffer; type: string }>
}

/** A file's URL in Markdown and for the browser: `/api/v1/files/{id}/{name}` (docs/03 §18.3). */
export function fileUrl(id: string, name: string): string {
  return `/api/v1/files/${id}/${encodeURIComponent(name || 'file')}`
}

/** The file id of a URL of PGPM's file store (relative or on this origin); null for any other URL. */
export function fileIdOf(url: string): string | null {
  let path = url
  try {
    const base = typeof location !== 'undefined' ? location.origin : 'http://localhost'
    const parsed = new URL(url, base)
    if (parsed.origin !== base) return null
    path = parsed.pathname
  } catch {
    return null
  }
  const match = /^\/api\/v1\/files\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})(?:\/|$)/i.exec(path)
  return match ? match[1]!.toLowerCase() : null
}

export const filesApi: FileTransport = {
  putChunk: (id, index, bytes, signal) =>
    fetchJson(`/api/v1/files/${id}/chunks/${index}`, { method: 'PUT', body: bytes, signal }),
  commit: (id, body, signal) => fetchJson(`/api/v1/files/${id}/commit`, { method: 'POST', body, signal }),
  async download(id, name, signal) {
    const response = await fetchApi(fileUrl(id, name), { signal })
    return {
      bytes: await response.arrayBuffer(),
      type: response.headers.get('Content-Type') ?? 'application/octet-stream',
    }
  },
}
