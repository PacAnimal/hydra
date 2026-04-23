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

const GRID_INITIAL_COL = 2     // starting column for first host
const GRID_INITIAL_ROW = 2     // starting row for first host
const GRID_FALLBACK_ROW = 5    // row for disconnected (unreachable) hosts
const GRID_MAX_COL = 7         // wrap column at width 8

// derives HostConfig[] from a visual layout — creates neighbours for adjacent items from different hosts
export function deriveHostsFromLayout(items: LayoutItem[]): HostConfig[] {
  if (!items.length) return []

  const byPos = new Map<string, LayoutItem>()
  for (const item of items) {
    byPos.set(`${item.col},${item.row}`, item)
  }

  // group items by host name to collect deadCorners (all items for a host should agree; use first found)
  const hostDeadCorners = new Map<string, number | undefined>()
  for (const item of items) {
    if (!hostDeadCorners.has(item.hostName)) {
      hostDeadCorners.set(item.hostName, item.deadCorners)
    }
  }

  // build neighbour list per host name
  const hostNeighbours = new Map<string, NeighbourConfig[]>()
  const hostNames = new Set(items.map(i => i.hostName))
  for (const name of hostNames) hostNeighbours.set(name, [])

  for (const item of items) {
    for (const dir of DIRS) {
      const [dc, dr] = DIR_DELTA[dir]
      const adj = byPos.get(`${item.col + dc},${item.row + dr}`)
      if (!adj || adj.hostName === item.hostName) continue

      const n: NeighbourConfig = { direction: dir, name: adj.hostName }
      if (item.screenId) n.sourceScreen = item.screenId
      if (adj.screenId) n.destScreen = adj.screenId

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

// infers a visual layout from HostConfig[] — positions hosts on a grid based on neighbour directions
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
      items.set(k, { id: `inferred-${counter++}`, hostName, screenId, col: -1, row: -1 })
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

  // BFS to assign grid positions
  const firstKey = key(hosts[0].name)
  const first = items.get(firstKey)
  if (first) { first.col = GRID_INITIAL_COL; first.row = GRID_INITIAL_ROW }

  const queue: ItemKey[] = [firstKey]
  const visited = new Set<ItemKey>([firstKey])

  while (queue.length > 0) {
    const k = queue.shift()!
    const item = items.get(k)
    if (!item) continue

    const host = hosts.find(h => h.name === item.hostName)
    if (!host) continue

    for (const n of host.neighbours ?? []) {
      // match this layout item (by screenId) to the neighbour
      const nSourceScreen = n.sourceScreen ?? undefined
      if (item.screenId !== nSourceScreen && nSourceScreen !== undefined) continue

      const destKey = key(n.name, n.destScreen)
      if (visited.has(destKey)) continue

      const destItem = items.get(destKey)
      if (!destItem) continue

      const [dc, dr] = DIR_DELTA[n.direction]
      destItem.col = item.col + dc
      destItem.row = item.row + dr
      visited.add(destKey)
      queue.push(destKey)
    }
  }

  // handle unpositioned items (disconnected hosts)
  let freeCol = 0; let freeRow = GRID_FALLBACK_ROW
  const occupied = new Set([...items.values()].filter(i => i.col >= 0).map(i => `${i.col},${i.row}`))
  for (const item of items.values()) {
    if (item.col >= 0) continue
    while (occupied.has(`${freeCol},${freeRow}`)) {
      freeCol++
      if (freeCol > GRID_MAX_COL) { freeCol = 0; freeRow++ }
    }
    item.col = freeCol
    item.row = freeRow
    occupied.add(`${freeCol},${freeRow}`)
    freeCol++
  }

  // assign deadCorners from host config
  for (const host of hosts) {
    if (host.deadCorners === undefined) continue
    for (const item of items.values()) {
      if (item.hostName === host.name) item.deadCorners = host.deadCorners
    }
  }

  // deduplicate: prefer items with a screenId over items without (for same host/no-screen)
  // remove the bare `host:` key if a screen-specific one covers the same host
  const result: LayoutItem[] = []
  for (const [, item] of items) {
    // if this is a bare host key and there are other items for this host with screenIds, skip it
    if (!item.screenId) {
      const hasScreenItems = [...items.values()].some(i => i.hostName === item.hostName && i.screenId)
      if (hasScreenItems) continue
    }
    // also skip host keys that refer to the same (host, screen?) as a non-bare key
    result.push(item)
  }

  // remove duplicate positions by keeping the last item at each cell (may happen on dedup)
  const posMap = new Map<string, LayoutItem>()
  for (const item of result) {
    posMap.set(`${item.col},${item.row}`, item)
  }

  return [...posMap.values()]
}

// opposite direction helper exported for use elsewhere
export { OPPOSITE, DIR_DELTA, DIRS }
