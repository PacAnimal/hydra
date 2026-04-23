import type { LayoutItem, HostConfig, NeighbourConfig, Direction } from '../types'

const DIR_DELTA: Record<Direction, [number, number]> = {
  Left: [-1, 0],
  Right: [1, 0],
  Up: [0, -1],
  Down: [0, 1],
}

const OPPOSITE: Record<Direction, Direction> = {
  Left: 'Right', Right: 'Left', Up: 'Down', Down: 'Up',
}

const DIRS: Direction[] = ['Left', 'Right', 'Up', 'Down']

export const ADJACENCY_THRESHOLD = 80  // logical px — how close edges must be to count as neighbours
export const DEFAULT_W = 1920          // default screen width in logical pixels
export const DEFAULT_H = 1080          // default screen height in logical pixels

const INITIAL_X = 1920  // starting x for first host when inferring layout
const INITIAL_Y = 1080  // starting y for first host when inferring layout

// computes the source/dest percentage ranges for a neighbour edge pair
// itemStart/itemSize describe the item's dimension parallel to the shared edge (y for L/R, x for U/D)
// returns undefined fields for any value that equals the default (0 for start, 100 for end)
function computeEdgeRanges(
  itemStart: number, itemSize: number,
  adjStart: number, adjSize: number,
): { srcStart?: number; srcEnd?: number; dstStart?: number; dstEnd?: number } | null {
  const overlapLow = Math.max(itemStart, adjStart)
  const overlapHigh = Math.min(itemStart + itemSize, adjStart + adjSize)
  if (overlapHigh <= overlapLow) return null

  const srcStart = Math.round((overlapLow - itemStart) / itemSize * 100)
  const srcEnd = Math.round((overlapHigh - itemStart) / itemSize * 100)
  const dstStart = Math.round((overlapLow - adjStart) / adjSize * 100)
  const dstEnd = Math.round((overlapHigh - adjStart) / adjSize * 100)

  return {
    srcStart: srcStart !== 0 ? srcStart : undefined,
    srcEnd: srcEnd !== 100 ? srcEnd : undefined,
    dstStart: dstStart !== 0 ? dstStart : undefined,
    dstEnd: dstEnd !== 100 ? dstEnd : undefined,
  }
}

// derives HostConfig[] from a free-form visual layout
// two blocks are neighbours when their edges are within ADJACENCY_THRESHOLD px and their ranges overlap
export function deriveHostsFromLayout(items: LayoutItem[]): HostConfig[] {
  if (!items.length) return []

  // collect deadCorners per host (first occurrence wins)
  const hostDeadCorners = new Map<string, number | undefined>()
  for (const item of items) {
    if (!hostDeadCorners.has(item.hostName)) {
      hostDeadCorners.set(item.hostName, item.deadCorners)
    }
  }

  const hostNeighbours = new Map<string, NeighbourConfig[]>()
  const hostNames = new Set(items.map(i => i.hostName))
  for (const name of hostNames) hostNeighbours.set(name, [])

  for (const item of items) {
    for (const adj of items) {
      if (adj.id === item.id || adj.hostName === item.hostName) continue

      const vOverlap = Math.min(item.y + item.h, adj.y + adj.h) - Math.max(item.y, adj.y)
      const hOverlap = Math.min(item.x + item.w, adj.x + adj.w) - Math.max(item.x, adj.x)

      let dir: Direction | null = null
      if (Math.abs(adj.x - (item.x + item.w)) < ADJACENCY_THRESHOLD && vOverlap > 0) dir = 'Right'
      else if (Math.abs(item.x - (adj.x + adj.w)) < ADJACENCY_THRESHOLD && vOverlap > 0) dir = 'Left'
      else if (Math.abs(adj.y - (item.y + item.h)) < ADJACENCY_THRESHOLD && hOverlap > 0) dir = 'Down'
      else if (Math.abs(item.y - (adj.y + adj.h)) < ADJACENCY_THRESHOLD && hOverlap > 0) dir = 'Up'

      if (!dir) continue

      const n: NeighbourConfig = { direction: dir, name: adj.hostName }
      if (item.screenId) n.sourceScreen = item.screenId
      if (adj.screenId) n.destScreen = adj.screenId

      const ranges = (dir === 'Left' || dir === 'Right')
        ? computeEdgeRanges(item.y, item.h, adj.y, adj.h)
        : computeEdgeRanges(item.x, item.w, adj.x, adj.w)

      if (ranges) {
        if (ranges.srcStart !== undefined) n.sourceStart = ranges.srcStart
        if (ranges.srcEnd !== undefined) n.sourceEnd = ranges.srcEnd
        if (ranges.dstStart !== undefined) n.destStart = ranges.dstStart
        if (ranges.dstEnd !== undefined) n.destEnd = ranges.dstEnd
      }

      hostNeighbours.get(item.hostName)!.push(n)
    }
  }

  return Array.from(hostNames).map(name => {
    const host: HostConfig = { name }
    const neighbours = hostNeighbours.get(name) ?? []
    if (neighbours.length > 0) host.neighbours = neighbours
    const dc = hostDeadCorners.get(name)
    if (dc !== undefined) host.deadCorners = dc
    return host
  })
}

// infers a visual layout from HostConfig[] — places hosts on a pixel grid using BFS over neighbour directions
export function inferLayoutFromHosts(hosts: HostConfig[]): LayoutItem[] {
  if (!hosts.length) return []

  type ItemKey = string  // `hostName:screenId?`

  const items = new Map<ItemKey, LayoutItem>()
  let counter = 0

  function key(hostName: string, screenId?: string): ItemKey {
    return `${hostName}:${screenId ?? ''}`
  }

  function getOrCreate(hostName: string, screenId?: string): ItemKey {
    const k = key(hostName, screenId)
    if (!items.has(k)) {
      items.set(k, { id: `inferred-${counter++}`, hostName, screenId, x: -1, y: -1, w: DEFAULT_W, h: DEFAULT_H })
    }
    return k
  }

  // collect all (host, screen?) pairs
  for (const host of hosts) {
    getOrCreate(host.name)
    for (const n of host.neighbours ?? []) {
      getOrCreate(host.name, n.sourceScreen)
      getOrCreate(n.name, n.destScreen)
    }
  }

  // BFS to assign pixel positions
  const firstKey = key(hosts[0].name)
  const first = items.get(firstKey)
  if (first) { first.x = INITIAL_X; first.y = INITIAL_Y }

  const queue: ItemKey[] = [firstKey]
  const visited = new Set<ItemKey>([firstKey])

  while (queue.length > 0) {
    const k = queue.shift()!
    const item = items.get(k)
    if (!item) continue

    const host = hosts.find(h => h.name === item.hostName)
    if (!host) continue

    for (const n of host.neighbours ?? []) {
      const nSourceScreen = n.sourceScreen ?? undefined
      if (item.screenId !== nSourceScreen && nSourceScreen !== undefined) continue

      const destKey = key(n.name, n.destScreen)
      if (visited.has(destKey)) continue

      const destItem = items.get(destKey)
      if (!destItem) continue

      const [dx, dy] = DIR_DELTA[n.direction]

      if (n.direction === 'Right' || n.direction === 'Left') {
        destItem.x = item.x + dx * item.w
        // align using source/dest edge offsets so partial overlaps render correctly
        const srcFrac = (n.sourceStart ?? 0) / 100
        const dstFrac = (n.destStart ?? 0) / 100
        destItem.y = item.y + srcFrac * item.h - dstFrac * destItem.h
      } else {
        destItem.y = item.y + dy * item.h
        const srcFrac = (n.sourceStart ?? 0) / 100
        const dstFrac = (n.destStart ?? 0) / 100
        destItem.x = item.x + srcFrac * item.w - dstFrac * destItem.w
      }

      visited.add(destKey)
      queue.push(destKey)
    }
  }

  // place disconnected (unreachable) items in a fallback row
  let freeX = 0; let freeY = DEFAULT_H * 3
  for (const item of items.values()) {
    if (item.x >= 0) continue
    item.x = freeX
    item.y = freeY
    freeX += DEFAULT_W
    if (freeX > DEFAULT_W * 5) { freeX = 0; freeY += DEFAULT_H }
  }

  // propagate deadCorners from host config
  for (const host of hosts) {
    if (host.deadCorners === undefined) continue
    for (const item of items.values()) {
      if (item.hostName === host.name) item.deadCorners = host.deadCorners
    }
  }

  // deduplicate: if a host has screen-specific items, drop the bare host item
  const result: LayoutItem[] = []
  for (const [, item] of items) {
    if (!item.screenId) {
      const hasScreenItems = [...items.values()].some(i => i.hostName === item.hostName && i.screenId)
      if (hasScreenItems) continue
    }
    result.push(item)
  }

  return result
}

export { OPPOSITE, DIR_DELTA, DIRS }
