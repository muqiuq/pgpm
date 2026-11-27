import type { Ref } from 'vue'
import type { Changes, Row } from '@/local/outbox'
import type {
  Access,
  AssignmentRow,
  DodItemRow,
  ElementRow,
  FieldDefinitionRow,
  FieldValueRow,
  ItemRow,
  LayerRow,
  LinkRow,
  MapRow,
  MapViewSettings,
  MilestoneRow,
  PersonRow,
  StatusRow,
} from './types'

/**
 * Everything a map's UI reads and writes (docs/03 §13), so that the canvas and the matrix map know nothing of the local
 * database or the sync: phase 5 implements it over `src/local/repo/*` (reads through `liveQuery` → refs, writes through
 * the repo's `create/patch/remove`, which append to the outbox); `createMemorySource` (memorySource.ts) holds the rows
 * in memory for the tests and the playground.
 *
 * Reads are reactive and hold live rows only (no tombstones). Rows are never changed in place: a change replaces the
 * row object, and a row that didn't change keeps its identity (the canvas memoises by identity – a real source
 * reconciles query results by id and `rev`). `items` are those the map's elements refer to (cards, a timeline's project,
 * a milestone's project) and, on a matrix map, the candidates of `maps.matrix.filter` the user can read (its `groupIds`
 * applied); an element whose item isn't among them is drawn *restricted*.
 *
 * Writes go to the three map tables only; the item quick edit in the side panel is the host's (`#item-panel` slot).
 * A patch's keys are columns or dotted members of jsonb columns (`style.fill`), each with the value the user saw and
 * the new one, as in the outbox (data model §14.1). `batch` groups the writes made in `fn` into one outbox batch.
 */
export type MapTable = 'maps' | 'map_layers' | 'map_elements'

export interface MapDataSource {
  readonly map: Readonly<Ref<MapRow | null>>
  /** The map's layers, any order. */
  readonly layers: Readonly<Ref<readonly LayerRow[]>>
  /** The map's elements, any order. */
  readonly elements: Readonly<Ref<readonly ElementRow[]>>
  readonly items: Readonly<Ref<readonly ItemRow[]>>
  /** All statuses (archived ones too: still displayed). */
  readonly statuses: Readonly<Ref<readonly StatusRow[]>>
  /** The milestones and gates of the projects among `items`, and those placed on the map. */
  readonly milestones: Readonly<Ref<readonly MilestoneRow[]>>
  /** The Definition of Done of those milestones. */
  readonly dodItems: Readonly<Ref<readonly DodItemRow[]>>
  /** Item links between `items` (both ends readable; others are not drawn). */
  readonly links: Readonly<Ref<readonly LinkRow[]>>
  /** Person assignments of `items` (and their milestones). */
  readonly assignments: Readonly<Ref<readonly AssignmentRow[]>>
  /** The persons those assignments name (for the filter's labels). */
  readonly persons: Readonly<Ref<readonly PersonRow[]>>
  /** All field definitions the user may see (scores need their inputs'). */
  readonly fieldDefinitions: Readonly<Ref<readonly FieldDefinitionRow[]>>
  /** The field values of `items`. */
  readonly fieldValues: Readonly<Ref<readonly FieldValueRow[]>>
  /** The current user's access to the map (`maps.access`): read can look, edit and manage can change. */
  readonly access: Readonly<Ref<Access>>
  /** `user_settings` `map:<id>`: the personal view (filter, hidden layers, links, viewport); null: none saved. */
  readonly viewSettings: Readonly<Ref<MapViewSettings | null>>
  setViewSettings(settings: MapViewSettings): Promise<void>
  /** A file's URL for `<image>` (a `blob:` URL from the local store), the thumbnail when asked; null: not on the device. */
  fileUrl(fileId: string, options?: { thumbnail?: boolean }): Promise<string | null>
  /** A new row id (UUID v7). */
  newId(): string
  create(table: MapTable, row: Row): Promise<void>
  patch(table: MapTable, id: string, changes: Changes): Promise<void>
  remove(table: MapTable, id: string): Promise<void>
  batch<T>(fn: () => T | Promise<T>): Promise<T>
}
