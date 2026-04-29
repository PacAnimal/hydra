import { describe, it, expect } from 'vitest'
import { hasAdjacentNeighbour, snapToNearestSide, compactLayout } from '../utils/canvasLayout'

// helper to make a rect with an id
function r(id: string, x: number, y: number, w: number, h: number) {
  return { id, x, y, w, h }
}

const W = 1920
const H = 1080

describe('hasAdjacentNeighbour', () => {
  it('returns false for empty others', () => {
    expect(hasAdjacentNeighbour(r('a', 0, 0, W, H), [])).toBe(false)
  })

  it('returns false when only other is excluded', () => {
    const others = [r('b', W, 0, W, H)]
    expect(hasAdjacentNeighbour(r('a', 0, 0, W, H), others, 'b')).toBe(false)
  })

  it('returns true for perfectly touching right neighbour', () => {
    const others = [r('b', W, 0, W, H)]
    expect(hasAdjacentNeighbour(r('a', 0, 0, W, H), others)).toBe(true)
  })

  it('returns true for perfectly touching left neighbour', () => {
    const others = [r('b', -W, 0, W, H)]
    expect(hasAdjacentNeighbour(r('a', 0, 0, W, H), others)).toBe(true)
  })

  it('returns true for perfectly touching bottom neighbour', () => {
    const others = [r('b', 0, H, W, H)]
    expect(hasAdjacentNeighbour(r('a', 0, 0, W, H), others)).toBe(true)
  })

  it('returns true for perfectly touching top neighbour', () => {
    const others = [r('b', 0, -H, W, H)]
    expect(hasAdjacentNeighbour(r('a', 0, 0, W, H), others)).toBe(true)
  })

  it('returns true when overlap is exactly 5% of shared edge', () => {
    // r.h = 1080, o.h = 1080 — 5% = 54px overlap
    const others = [r('b', W, H - 54, W, H)]
    expect(hasAdjacentNeighbour(r('a', 0, 0, W, H), others)).toBe(true)
  })

  it('returns false when overlap is less than 5% of shared edge', () => {
    // only 53px overlap out of 1080 min → below 5%
    const others = [r('b', W, H - 53, W, H)]
    expect(hasAdjacentNeighbour(r('a', 0, 0, W, H), others)).toBe(false)
  })

  it('returns false when blocks share a corner but no edge', () => {
    // diagonal — no shared horizontal or vertical edge
    const others = [r('b', W, H, W, H)]
    expect(hasAdjacentNeighbour(r('a', 0, 0, W, H), others)).toBe(false)
  })

  it('returns true when touching with a very short block (5% of smaller)', () => {
    // small block 100px tall touching a 1080px block — 5% of 100 = 5px overlap
    const others = [r('b', W, H - 5, W, 100)]
    expect(hasAdjacentNeighbour(r('a', 0, 0, W, H), others)).toBe(true)
  })
})

describe('snapToNearestSide', () => {
  it('returns original rect when no others', () => {
    const rect = r('a', 500, 500, W, H)
    expect(snapToNearestSide(rect, [])).toEqual(rect)
  })

  it('returns original rect when only other is excluded', () => {
    const rect = r('a', 500, 500, W, H)
    const others = [r('b', 0, 0, W, H)]
    expect(snapToNearestSide(rect, others, 'b')).toEqual(rect)
  })

  it('snaps to right side of neighbour when dragged to its right', () => {
    // drag center is well to the right of anchor
    const anchor = r('b', 0, 0, W, H)
    const dragged = r('a', W + 200, 0, W, H)
    const snapped = snapToNearestSide(dragged, [anchor])
    expect(snapped.x).toBe(W) // touching right edge of anchor
    expect(snapped.y).toBe(0)
  })

  it('snaps to left side of neighbour when dragged to its left', () => {
    const anchor = r('b', W * 2, 0, W, H)
    const dragged = r('a', W - 200, 0, W, H)
    const snapped = snapToNearestSide(dragged, [anchor])
    expect(snapped.x).toBe(W) // touching left edge of anchor
    expect(snapped.y).toBe(0)
  })

  it('snaps to bottom side when dragged below', () => {
    const anchor = r('b', 0, 0, W, H)
    const dragged = r('a', 0, H + 200, W, H)
    const snapped = snapToNearestSide(dragged, [anchor])
    expect(snapped.y).toBe(H) // touching bottom edge of anchor
    expect(snapped.x).toBe(0)
  })

  it('snaps to top side when dragged above', () => {
    const anchor = r('b', 0, H * 2, W, H)
    const dragged = r('a', 0, H - 200, W, H)
    const snapped = snapToNearestSide(dragged, [anchor])
    expect(snapped.y).toBe(H) // touching top edge of anchor
    expect(snapped.x).toBe(0)
  })

  it('snaps to bottom side and clamps x when drag is far below-right of anchor', () => {
    // far below and to the right → bottom snap wins by distance; x must be clamped for ≥5% overlap
    const anchor = r('b', 0, 0, W, H)
    const dragged = r('a', W + 50, H * 10, W, H)
    const snapped = snapToNearestSide(dragged, [anchor])
    expect(snapped.y).toBe(H) // bottom snap
    // minOv = 5% of min(W, W) = 96; max_x = W - 96 = 1824
    expect(snapped.x).toBe(W - Math.round(0.05 * W))
  })

  it('snaps to right side and clamps y when drag is far right and slightly below anchor', () => {
    // far right and slightly below → right snap wins by distance; y must be clamped for ≥5% overlap
    const anchor = r('b', 0, 0, W, H)
    const dragged = r('a', W * 10, H + 50, W, H)
    const snapped = snapToNearestSide(dragged, [anchor])
    expect(snapped.x).toBe(W) // right snap
    // minOv = 5% of min(H, H) = 54; max_y = H - 54 = 1026
    expect(snapped.y).toBe(H - Math.round(0.05 * H))
  })

  it('picks closest block when multiple are available', () => {
    // anchor1 is closer to drag center than anchor2
    const anchor1 = r('b', 0, 0, W, H)
    const anchor2 = r('c', W * 5, 0, W, H)
    // drag is just right of anchor1
    const dragged = r('a', W + 100, 0, W, H)
    const snapped = snapToNearestSide(dragged, [anchor1, anchor2])
    expect(snapped.x).toBe(W) // snapped to right side of anchor1, not anchor2
  })
})

describe('compactLayout', () => {
  it('returns items unchanged when all are adjacent', () => {
    // A–B–C in a row, all touching
    const items = [r('A', 0, 0, W, H), r('B', W, 0, W, H), r('C', W * 2, 0, W, H)]
    expect(compactLayout(items, 'X')).toEqual(items)
  })

  it('does not move the dropped item', () => {
    // A floating far away from B; dropped item is A — should not be moved
    const items = [r('A', 9999, 0, W, H), r('B', 0, 0, W, H)]
    const result = compactLayout(items, 'A')
    expect(result.find(i => i.id === 'A')!.x).toBe(9999)
  })

  it('closes the gap when middle screen is removed from a 3-screen row', () => {
    // realistic layout: B was between A and C; user dragged B to the top of C
    // B is now directly above C (adjacent vertically), leaving a gap between A and C
    const A = r('A', 1920, 1080, W, H)
    const B = r('B', 5760, 0, W, H)    // dropped above C
    const C = r('C', 5760, 1080, W, H)
    const result = compactLayout([A, B, C], 'B')
    const rA = result.find(i => i.id === 'A')!
    const rC = result.find(i => i.id === 'C')!
    // A should snap rightward to be adjacent to C's left edge
    expect(rA.x + rA.w).toBe(rC.x)
  })

  it('cascading: multiple floating items collapse toward each other', () => {
    // A, B, C all floating with gaps; D (dropped) is far away
    const A = r('A', 0, 0, W, H)
    const B = r('B', W * 3, 0, W, H)   // gap W*2 between A and B
    const C = r('C', W * 6, 0, W, H)   // gap W*2 between B and C
    const D = r('D', W * 9, 0, W, H)   // dropped item
    const result = compactLayout([A, B, C, D], 'D')
    const rA = result.find(i => i.id === 'A')!
    const rB = result.find(i => i.id === 'B')!
    const rC = result.find(i => i.id === 'C')!
    // A, B, C should collapse into a touching chain
    expect(rA.x + rA.w).toBe(rB.x)
    expect(rB.x + rB.w).toBe(rC.x)
  })
})
