import { describe, expect, it } from 'vitest'
import type { MapRecord } from '@/local/maps'
import type { ItemRow } from '@/local/rows'
import type { FieldDefinition } from '@/maps/cards'
import type { ItemRow as MapItemRow, MatrixSettings } from '@/maps/types'
import { emptyContext, matchItem, readFilters, sortValue, visibleColumns } from '@/views/items/itemList'
import {
  axisFields,
  canvasTemplates,
  copyName,
  customMatrix,
  keyFields,
  mapsOfTab,
  matrixPresets,
  quadrantMapsFor,
  quadrantsOf,
} from './mapList'

const map = (id: string, extra: Partial<MapRecord>) => ({ id, name: id, kind: 'canvas', ...extra }) as MapRecord

describe('the map list’s rules (doc 01 §12.1)', () => {
  const maps = [
    map('Zeta', {}),
    map('Alpha', {}),
    map('Crazy 8s', { isTemplate: true }),
    map('Prioritization matrix', { isTemplate: true, kind: 'matrix', matrix: {} as MatrixSettings }),
    map('Another preset', { isTemplate: true, kind: 'matrix', matrix: {} as MatrixSettings }),
    map('Gone', { deleted: true }),
  ]

  it('lists maps and templates apart, by name; presets with the Prioritization matrix first', () => {
    expect(mapsOfTab(maps, 'maps').map((m) => m.id)).toEqual(['Alpha', 'Zeta'])
    expect(mapsOfTab(maps, 'templates').map((m) => m.id)).toEqual([
      'Another preset',
      'Crazy 8s',
      'Prioritization matrix',
    ])
    expect(matrixPresets(maps).map((m) => m.id)).toEqual(['Prioritization matrix', 'Another preset'])
    expect(canvasTemplates(maps).map((m) => m.id)).toEqual(['Crazy 8s'])
    expect(copyName('Board')).toBe('Copy of Board')
  })

  it('offers numbers, ratings and scores as axes with their ranges, and builds a matrix of two', () => {
    const axes = axisFields([
      { id: 'f1', key: 'impact', label: 'Impact', type: 'number', config: { min: 0, max: 100 } },
      { id: 'f2', key: 'data', label: 'Data', type: 'rating', config: { max: 5 } },
      { id: 'f3', key: 'feas', label: 'Feasibility', type: 'score', config: { op: 'sum', inputs: ['data'], max: 65 } },
      { id: 'f4', key: 'note', label: 'Note', type: 'text' },
      { id: 'f5', key: 'old', label: 'Old', type: 'number', isArchived: true },
    ])
    expect(axes.map((a) => [a.key, a.min, a.max])).toEqual([
      ['data', 0, 5],
      ['feas', 0, 65],
      ['impact', 0, 100],
    ])
    const matrix = customMatrix(axes[1]!, axes[2]!)
    expect(matrix.x).toEqual({ field: 'feas', label: 'Feasibility', min: 0, max: 65, split: 32.5 })
    expect(matrix.y.split).toBe(50)
    expect(matrix.quadrants?.tr).toBe('Star initiatives')
    expect(matrix.filter?.types).toEqual(['idea', 'project'])
  })

  it('takes a score’s inputs into the quick edit’s key fields, each once', () => {
    const defs: Record<string, FieldDefinition> = {
      feas: { id: '1', key: 'feas', label: 'F', type: 'score', config: { inputs: ['a', 'b'] } },
      a: { id: '2', key: 'a', label: 'A', type: 'rating' },
      b: { id: '3', key: 'b', label: 'B', type: 'rating' },
      impact: { id: '4', key: 'impact', label: 'I', type: 'number' },
    }
    expect(keyFields(['feas', 'impact', 'a', 'missing'], defs).map((d) => d.key)).toEqual(['feas', 'a', 'b', 'impact'])
  })
})

describe('an item’s quadrant on a matrix map (doc 01 §12.8)', () => {
  const matrix: MatrixSettings = {
    x: { field: 'x', min: 0, max: 10 },
    y: { field: 'y', min: 0, max: 10 },
    quadrants: { tr: 'Stars' },
    filter: { types: ['idea'], groupIds: ['g1'] },
  }
  const defs: Record<string, FieldDefinition> = {
    x: { id: 'fx', key: 'x', label: 'X', type: 'number' },
    y: { id: 'fy', key: 'y', label: 'Y', type: 'number' },
  }
  const items = [
    { id: 'i1', type: 'idea', title: 'High' },
    { id: 'i2', type: 'idea', title: 'Low' },
    { id: 'i3', type: 'idea', title: 'Unassessed' },
    { id: 'i4', type: 'idea', title: 'Not shared' },
    { id: 'p1', type: 'project', title: 'Wrong type' },
  ] as MapItemRow[]
  const values = new Map<string, Record<string, unknown>>([
    ['i1', { x: 9, y: 9 }],
    ['i2', { x: 1, y: 1 }],
    ['i3', { x: 1 }],
    ['i4', { x: 9, y: 9 }],
    ['p1', { x: 9, y: 9 }],
  ])
  const shared = (id: string, groups: readonly string[]) => id !== 'i4' && groups.includes('g1')

  it('places the candidates with both values, labelled by the map or the default', () => {
    const q = quadrantsOf(matrix, items, defs, values, shared)
    expect([...q.entries()]).toEqual([
      ['i1', { quadrant: 'tr', label: 'Stars' }],
      ['i2', { quadrant: 'bl', label: 'Limited interest' }],
    ])
  })

  it('offers a quadrant column per matrix map that takes the type, which sorts, shows and filters', () => {
    const maps = [
      map('m1', { kind: 'matrix', name: 'Review', matrix }),
      map('m2', { kind: 'matrix', name: 'Leads only', matrix: { ...matrix, filter: { types: ['lead'] } } }),
      map('m3', { kind: 'matrix', name: 'Template', isTemplate: true, matrix }),
    ]
    expect(quadrantMapsFor(maps, 'idea').map((m) => m.id)).toEqual(['m1'])
    const column = { id: 'quadrant:m1', label: 'Quadrant · Review' }
    expect(
      visibleColumns('idea', ['key', 'quadrant:m1', 'quadrant:gone'], undefined, [column]).map((c) => c.id),
    ).toEqual(['title', 'key', 'quadrant:m1'])
    const ctx = emptyContext({
      columnValue: (item, id) => (id === 'quadrant:m1' ? (item.id === 'i1' ? 'Stars' : null) : undefined),
      quadrantOf: (item) => (item.id === 'i1' ? 'tr' : null),
    })
    const item = (id: string) => ({ id, type: 'idea', title: id, createdAt: '', updatedAt: '' }) as unknown as ItemRow
    expect(sortValue(item('i1'), 'quadrant:m1', ctx)).toBe('Stars')
    const filters = readFilters({ quadrant: { mapId: 'm1', quadrants: ['tr', 'nope'] } })
    expect(filters.quadrant).toEqual({ mapId: 'm1', quadrants: ['tr'] })
    expect(matchItem(item('i1'), filters, ctx)).toBe(true)
    expect(matchItem(item('i2'), filters, ctx)).toBe(false)
    expect(readFilters({ quadrant: { mapId: 'm1', quadrants: [] } }).quadrant).toBeUndefined()
  })
})
