interface Rect { x: number; y: number; w: number; h: number }

const MIN_OVERLAP_FRACTION = 0.05

// returns true if r is adjacent to at least one block in others (excluding excludeId)
// with ≥5% shared edge length
export function hasAdjacentNeighbour(r: Rect, others: (Rect & { id: string })[], excludeId?: string): boolean {
  for (const o of others) {
    if (o.id === excludeId) continue
    const vOverlap = Math.min(r.y + r.h, o.y + o.h) - Math.max(r.y, o.y)
    const hOverlap = Math.min(r.x + r.w, o.x + o.w) - Math.max(r.x, o.x)
    // horizontally adjacent — share a vertical edge
    if (Math.abs(r.x + r.w - o.x) < 1 || Math.abs(o.x + o.w - r.x) < 1) {
      const minEdge = Math.min(r.h, o.h)
      if (vOverlap >= minEdge * MIN_OVERLAP_FRACTION) return true
    }
    // vertically adjacent — share a horizontal edge
    if (Math.abs(r.y + r.h - o.y) < 1 || Math.abs(o.y + o.h - r.y) < 1) {
      const minEdge = Math.min(r.w, o.w)
      if (hOverlap >= minEdge * MIN_OVERLAP_FRACTION) return true
    }
  }
  return false
}

// snaps r to the nearest side of the nearest other block (by distance from r's center to each side midpoint)
// clamps the perpendicular axis to ensure ≥5% edge overlap
// returns the snapped rect
export function snapToNearestSide(r: Rect, others: (Rect & { id: string })[], excludeId?: string): Rect {
  const cx = r.x + r.w / 2
  const cy = r.y + r.h / 2

  let best: Rect | null = null
  let bestDist = Infinity

  for (const o of others) {
    if (o.id === excludeId) continue

    // try all 4 sides — compute snap position and distance from center of r to midpoint of that side
    const candidates: Array<{ nx: number; ny: number; dist: number }> = []

    // right side of o → r snaps to its left
    {
      const nx = o.x + o.w
      const midY = o.y + o.h / 2
      const dist = Math.hypot(cx - nx, cy - midY)
      const minOv = MIN_OVERLAP_FRACTION * Math.min(r.h, o.h)
      const ny = clamp(r.y, o.y - r.h + minOv, o.y + o.h - minOv)
      candidates.push({ nx, ny, dist })
    }
    // left side of o → r snaps to its right
    {
      const nx = o.x - r.w
      const midY = o.y + o.h / 2
      const dist = Math.hypot(cx - o.x, cy - midY)
      const minOv = MIN_OVERLAP_FRACTION * Math.min(r.h, o.h)
      const ny = clamp(r.y, o.y - r.h + minOv, o.y + o.h - minOv)
      candidates.push({ nx, ny, dist })
    }
    // bottom side of o → r snaps above it
    {
      const ny = o.y + o.h
      const midX = o.x + o.w / 2
      const dist = Math.hypot(cx - midX, cy - ny)
      const minOv = MIN_OVERLAP_FRACTION * Math.min(r.w, o.w)
      const nx = clamp(r.x, o.x - r.w + minOv, o.x + o.w - minOv)
      candidates.push({ nx, ny, dist })
    }
    // top side of o → r snaps below it
    {
      const ny = o.y - r.h
      const midX = o.x + o.w / 2
      const dist = Math.hypot(cx - midX, cy - o.y)
      const minOv = MIN_OVERLAP_FRACTION * Math.min(r.w, o.w)
      const nx = clamp(r.x, o.x - r.w + minOv, o.x + o.w - minOv)
      candidates.push({ nx, ny, dist })
    }

    for (const c of candidates) {
      if (c.dist < bestDist) {
        bestDist = c.dist
        best = { x: c.nx, y: c.ny, w: r.w, h: r.h }
      }
    }
  }

  return best ?? r
}

function clamp(val: number, min: number, max: number): number {
  return Math.max(min, Math.min(max, val))
}
