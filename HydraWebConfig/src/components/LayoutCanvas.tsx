import { useState, useRef, useCallback, useId } from 'react'
import type { LayoutItem, HostConfig } from '../types'
import { newLayoutItem } from '../defaults'
import { deriveHostsFromLayout, ADJACENCY_THRESHOLD, DEFAULT_W, DEFAULT_H } from '../utils/layout'
import { hasAdjacentNeighbour, snapToNearestSide } from '../utils/canvasLayout'

const SCALE = 1 / 12        // 1920 logical px → 160 canvas px
const PAD = 24              // canvas padding (canvas px)
const SNAP = 10             // increment snap (logical px)
const SNAP_EDGE = 80        // snap-to-edge threshold (logical px)
const MIN_SIZE = 400        // minimum block dimension (logical px)

const HOST_COLORS = [
  '#2563eb', '#16a34a', '#dc2626', '#9333ea', '#f59e0b',
  '#0891b2', '#be185d', '#7c3aed', '#059669', '#d97706',
]

const ASPECT_PRESETS = [
  { label: '16:9', w: 1920, h: 1080 },
  { label: '16:10', w: 1920, h: 1200 },
  { label: '21:9', w: 2560, h: 1080 },
  { label: '4:3', w: 1600, h: 1200 },
  { label: '4K', w: 3840, h: 2160 },
]

type ResizeCorner = 'nw' | 'ne' | 'sw' | 'se'
type DragType = 'move' | `resize-${ResizeCorner}`

interface DragState {
  id: string
  type: DragType
  startPointerX: number
  startPointerY: number
  startX: number
  startY: number
  startW: number
  startH: number
}

interface Ghost {
  x: number; y: number; w: number; h: number
  overlapping: boolean
  disconnected: boolean  // would float — auto-snap applied on drop
}

interface SelectedItem {
  id: string
  hostName: string
  screenId: string
  w: string
  h: string
  isMaster: boolean
}

interface AddForm {
  hostName: string
  screenId: string
}

interface Props {
  items: LayoutItem[]
  onChange: (items: LayoutItem[]) => void
}

function toPx(logical: number): number { return logical * SCALE }
function fromPx(px: number): number { return px / SCALE }
function blockLeft(x: number): number { return toPx(x) + PAD }
function blockTop(y: number): number { return toPx(y) + PAD }
function snapTo(val: number): number { return Math.round(val / SNAP) * SNAP }

function rectsOverlap(
  a: { x: number; y: number; w: number; h: number },
  b: { x: number; y: number; w: number; h: number },
): boolean {
  return a.x < b.x + b.w && a.x + a.w > b.x && a.y < b.y + b.h && a.y + a.h > b.y
}

function applyEdgeSnap(
  x: number, y: number, w: number, h: number,
  others: LayoutItem[], dragId: string,
): { x: number; y: number } {
  let snapX = x, snapY = y

  for (const o of others) {
    if (o.id === dragId) continue
    if (Math.abs(x - o.x) < SNAP_EDGE) snapX = o.x
    else if (Math.abs((x + w) - (o.x + o.w)) < SNAP_EDGE) snapX = o.x + o.w - w
    else if (Math.abs((x + w) - o.x) < SNAP_EDGE) snapX = o.x - w
    else if (Math.abs(x - (o.x + o.w)) < SNAP_EDGE) snapX = o.x + o.w

    if (Math.abs(y - o.y) < SNAP_EDGE) snapY = o.y
    else if (Math.abs((y + h) - (o.y + o.h)) < SNAP_EDGE) snapY = o.y + o.h - h
    else if (Math.abs((y + h) - o.y) < SNAP_EDGE) snapY = o.y - h
    else if (Math.abs(y - (o.y + o.h)) < SNAP_EDGE) snapY = o.y + o.h
  }

  return { x: snapX, y: snapY }
}

function uniqueHosts(items: LayoutItem[]): string[] {
  return [...new Set(items.map(i => i.hostName))]
}

function makeColorMap(items: LayoutItem[]): Map<string, string> {
  const hosts = uniqueHosts(items)
  return new Map(hosts.map((h, i) => [h, HOST_COLORS[i % HOST_COLORS.length]]))
}

function canvasSize(items: LayoutItem[], ghost?: Ghost | null): { w: number; h: number } {
  const rects = [...items, ...(ghost ? [ghost] : [])]
  const maxX = rects.length ? Math.max(...rects.map(r => r.x + r.w)) : DEFAULT_W * 3
  const maxY = rects.length ? Math.max(...rects.map(r => r.y + r.h)) : DEFAULT_H * 2
  return {
    w: Math.max(toPx(maxX) + PAD * 3, 500),
    h: Math.max(toPx(maxY) + PAD * 3, 220),
  }
}

interface Connection {
  fromId: string; toId: string
  x1: number; y1: number; x2: number; y2: number
  color: string
}

// builds directed connections using BFS-derived host edges so arrows radiate outward from master
function buildConnections(items: LayoutItem[], hosts: HostConfig[], colorMap: Map<string, string>): Connection[] {
  const conns: Connection[] = []

  for (const host of hosts) {
    for (const n of host.neighbours ?? []) {
      const srcItem = items.find(i => i.hostName === host.name && i.screenId === n.sourceScreen)
        ?? items.find(i => i.hostName === host.name)
      const dstItem = items.find(i => i.hostName === n.name && i.screenId === n.destScreen)
        ?? items.find(i => i.hostName === n.name)
      if (!srcItem || !dstItem) continue

      const color = colorMap.get(host.name) ?? '#888'

      if (n.direction === 'Right') {
        const midY = (Math.max(srcItem.y, dstItem.y) + Math.min(srcItem.y + srcItem.h, dstItem.y + dstItem.h)) / 2
        conns.push({ fromId: srcItem.id, toId: dstItem.id, x1: blockLeft(srcItem.x + srcItem.w), y1: blockTop(midY), x2: blockLeft(dstItem.x), y2: blockTop(midY), color })
      } else if (n.direction === 'Left') {
        const midY = (Math.max(srcItem.y, dstItem.y) + Math.min(srcItem.y + srcItem.h, dstItem.y + dstItem.h)) / 2
        conns.push({ fromId: srcItem.id, toId: dstItem.id, x1: blockLeft(srcItem.x), y1: blockTop(midY), x2: blockLeft(dstItem.x + dstItem.w), y2: blockTop(midY), color })
      } else if (n.direction === 'Down') {
        const midX = (Math.max(srcItem.x, dstItem.x) + Math.min(srcItem.x + srcItem.w, dstItem.x + dstItem.w)) / 2
        conns.push({ fromId: srcItem.id, toId: dstItem.id, x1: blockLeft(midX), y1: blockTop(srcItem.y + srcItem.h), x2: blockLeft(midX), y2: blockTop(dstItem.y), color })
      } else if (n.direction === 'Up') {
        const midX = (Math.max(srcItem.x, dstItem.x) + Math.min(srcItem.x + srcItem.w, dstItem.x + dstItem.w)) / 2
        conns.push({ fromId: srcItem.id, toId: dstItem.id, x1: blockLeft(midX), y1: blockTop(srcItem.y), x2: blockLeft(midX), y2: blockTop(dstItem.y + dstItem.h), color })
      }
    }
  }

  return conns
}

interface EdgeIndicator {
  side: 'left' | 'right' | 'top' | 'bottom'
  startPct: number
  endPct: number
  color: string
}

function getEdgeIndicators(item: LayoutItem, items: LayoutItem[], colorMap: Map<string, string>): EdgeIndicator[] {
  const indicators: EdgeIndicator[] = []

  for (const adj of items) {
    if (adj.id === item.id || adj.hostName === item.hostName) continue

    const vOverlap = Math.min(item.y + item.h, adj.y + adj.h) - Math.max(item.y, adj.y)
    const hOverlap = Math.min(item.x + item.w, adj.x + adj.w) - Math.max(item.x, adj.x)
    const adjColor = colorMap.get(adj.hostName) ?? '#888'

    if (Math.abs(adj.x - (item.x + item.w)) < ADJACENCY_THRESHOLD && vOverlap > 0) {
      const lo = Math.max(item.y, adj.y), hi = Math.min(item.y + item.h, adj.y + adj.h)
      indicators.push({ side: 'right', startPct: (lo - item.y) / item.h * 100, endPct: (hi - item.y) / item.h * 100, color: adjColor })
    } else if (Math.abs(item.x - (adj.x + adj.w)) < ADJACENCY_THRESHOLD && vOverlap > 0) {
      const lo = Math.max(item.y, adj.y), hi = Math.min(item.y + item.h, adj.y + adj.h)
      indicators.push({ side: 'left', startPct: (lo - item.y) / item.h * 100, endPct: (hi - item.y) / item.h * 100, color: adjColor })
    } else if (Math.abs(adj.y - (item.y + item.h)) < ADJACENCY_THRESHOLD && hOverlap > 0) {
      const lo = Math.max(item.x, adj.x), hi = Math.min(item.x + item.w, adj.x + adj.w)
      indicators.push({ side: 'bottom', startPct: (lo - item.x) / item.w * 100, endPct: (hi - item.x) / item.w * 100, color: adjColor })
    } else if (Math.abs(item.y - (adj.y + adj.h)) < ADJACENCY_THRESHOLD && hOverlap > 0) {
      const lo = Math.max(item.x, adj.x), hi = Math.min(item.x + item.w, adj.x + adj.w)
      indicators.push({ side: 'top', startPct: (lo - item.x) / item.w * 100, endPct: (hi - item.x) / item.w * 100, color: adjColor })
    }
  }

  return indicators
}

export function LayoutCanvas({ items, onChange }: Props) {
  const canvasRef = useRef<HTMLDivElement>(null)
  const formIdPrefix = useId()

  const [drag, setDrag] = useState<DragState | null>(null)
  const [ghost, setGhost] = useState<Ghost | null>(null)

  const [showAdd, setShowAdd] = useState(false)
  const [addForm, setAddForm] = useState<AddForm>({ hostName: '', screenId: '' })
  const [selected, setSelected] = useState<SelectedItem | null>(null)

  const colorMap = makeColorMap(items)
  const { w: cw, h: ch } = canvasSize(items, ghost)
  const allHostNames = uniqueHosts(items)
  const hostListId = `${formIdPrefix}-hosts`

  const derivedHosts = deriveHostsFromLayout(items)
  const connectionCount = derivedHosts.reduce((s, h) => s + (h.neighbours?.length ?? 0), 0)
  const masterHostName = items.find(i => i.isMaster)?.hostName
  const connections = buildConnections(items, derivedHosts, colorMap)

  function pointerCanvasXY(e: React.PointerEvent): { px: number; py: number } {
    const rect = canvasRef.current!.getBoundingClientRect()
    return {
      px: e.clientX - rect.left + (canvasRef.current!.scrollLeft ?? 0),
      py: e.clientY - rect.top + (canvasRef.current!.scrollTop ?? 0),
    }
  }

  const startDrag = useCallback((e: React.PointerEvent, id: string, type: DragType) => {
    e.stopPropagation()
    const item = items.find(i => i.id === id)
    if (!item) return
    const { px, py } = pointerCanvasXY(e)
    ;(e.currentTarget as HTMLElement).setPointerCapture(e.pointerId)
    setDrag({ id, type, startPointerX: px, startPointerY: py, startX: item.x, startY: item.y, startW: item.w, startH: item.h })
    setSelected({ id, hostName: item.hostName, screenId: item.screenId ?? '', w: String(item.w), h: String(item.h), isMaster: item.isMaster ?? false })
    setGhost(null)
  }, [items])

  const onPointerMove = useCallback((e: React.PointerEvent<HTMLDivElement>) => {
    if (!drag) return
    const { px, py } = pointerCanvasXY(e)
    const dxLog = fromPx(px - drag.startPointerX)
    const dyLog = fromPx(py - drag.startPointerY)

    let nx = drag.startX, ny = drag.startY, nw = drag.startW, nh = drag.startH

    switch (drag.type) {
      case 'move': {
        nx = snapTo(drag.startX + dxLog)
        ny = snapTo(drag.startY + dyLog)
        const snapped = applyEdgeSnap(nx, ny, nw, nh, items, drag.id)
        nx = Math.max(0, snapped.x)
        ny = Math.max(0, snapped.y)
        break
      }
      case 'resize-se':
        nw = Math.max(MIN_SIZE, snapTo(drag.startW + dxLog))
        nh = Math.max(MIN_SIZE, snapTo(drag.startH + dyLog))
        break
      case 'resize-sw': {
        const delta = Math.min(drag.startW - MIN_SIZE, snapTo(-dxLog))
        nw = drag.startW - delta; nx = drag.startX + delta
        nh = Math.max(MIN_SIZE, snapTo(drag.startH + dyLog))
        break
      }
      case 'resize-ne': {
        nw = Math.max(MIN_SIZE, snapTo(drag.startW + dxLog))
        const delta = Math.min(drag.startH - MIN_SIZE, snapTo(-dyLog))
        nh = drag.startH - delta; ny = drag.startY + delta
        break
      }
      case 'resize-nw': {
        const deltaW = Math.min(drag.startW - MIN_SIZE, snapTo(-dxLog))
        nw = drag.startW - deltaW; nx = drag.startX + deltaW
        const deltaH = Math.min(drag.startH - MIN_SIZE, snapTo(-dyLog))
        nh = drag.startH - deltaH; ny = drag.startY + deltaH
        break
      }
    }

    nx = Math.max(0, nx); ny = Math.max(0, ny)
    const proposed = { x: nx, y: ny, w: nw, h: nh }
    const overlapping = items.some(i => i.id !== drag.id && rectsOverlap(proposed, i))
    const disconnected = !overlapping && items.length > 1 && !hasAdjacentNeighbour(proposed, items, drag.id)
    setGhost({ ...proposed, overlapping, disconnected })
  }, [drag, items])

  const onPointerUp = useCallback(() => {
    if (!drag || !ghost) { setDrag(null); setGhost(null); return }
    if (!ghost.overlapping) {
      let { x, y, w, h } = ghost
      // auto-snap to nearest side when the dropped position would be floating
      if (ghost.disconnected) {
        const snapped = snapToNearestSide({ x, y, w, h }, items, drag.id)
        x = Math.max(0, snapped.x); y = Math.max(0, snapped.y)
      }
      onChange(items.map(i => i.id === drag.id ? { ...i, x, y, w, h } : i))
      setSelected(s => s?.id === drag.id ? { ...s, w: String(w), h: String(h) } : s)
    }
    setDrag(null); setGhost(null)
  }, [drag, ghost, items, onChange])

  const addItem = useCallback(() => {
    const hostName = addForm.hostName.trim()
    if (!hostName) return
    // place touching the rightmost existing block (same y); first block goes to origin
    const anchor = items.length > 0 ? items.reduce((a, b) => (a.x + a.w) >= (b.x + b.w) ? a : b) : null
    const x = anchor ? anchor.x + anchor.w : DEFAULT_W
    const y = anchor ? anchor.y : DEFAULT_H
    const isMaster = items.length === 0  // first item is master by default
    const newItem: LayoutItem = { ...newLayoutItem(hostName, addForm.screenId.trim() || undefined, x, y), isMaster }
    onChange([...items, newItem])
    setAddForm({ hostName: '', screenId: '' })
    setShowAdd(false)
  }, [addForm, items, onChange])

  const removeItem = useCallback((id: string) => {
    onChange(items.filter(i => i.id !== id))
    if (selected?.id === id) setSelected(null)
  }, [items, onChange, selected])

  const applySelected = useCallback(() => {
    if (!selected) return
    const hostName = selected.hostName.trim()
    if (!hostName) return
    const w = Math.max(MIN_SIZE, Number(selected.w) || DEFAULT_W)
    const h = Math.max(MIN_SIZE, Number(selected.h) || DEFAULT_H)
    onChange(items.map(i => i.id === selected.id
      ? { ...i, hostName, screenId: selected.screenId.trim() || undefined, w, h }
      : i
    ))
  }, [selected, items, onChange])

  const applyPreset = useCallback((w: number, h: number) => {
    if (!selected) return
    // preserve current orientation when applying a preset
    const item = items.find(i => i.id === selected.id)
    const vertical = item && item.h > item.w
    const fw = vertical ? Math.min(w, h) : Math.max(w, h)
    const fh = vertical ? Math.max(w, h) : Math.min(w, h)
    setSelected(s => s ? { ...s, w: String(fw), h: String(fh) } : null)
    onChange(items.map(i => i.id === selected.id ? { ...i, w: fw, h: fh } : i))
  }, [selected, items, onChange])

  const setOrientation = useCallback((orientation: 'horizontal' | 'vertical') => {
    if (!selected) return
    const item = items.find(i => i.id === selected.id)
    if (!item) return
    const isVertical = item.h > item.w
    const wantVertical = orientation === 'vertical'
    if (isVertical === wantVertical) return  // already correct
    const [nw, nh] = [item.h, item.w]  // swap
    setSelected(s => s ? { ...s, w: String(nw), h: String(nh) } : null)
    onChange(items.map(i => i.id === selected.id ? { ...i, w: nw, h: nh } : i))
  }, [selected, items, onChange])

  const setMaster = useCallback((id: string, master: boolean) => {
    const targetHost = items.find(i => i.id === id)?.hostName
    // move isMaster to every item of the new master host; strip it from all others
    const updated = items.map(i => ({
      ...i,
      isMaster: master && i.hostName === targetHost ? true : (master ? false : i.isMaster),
    }))
    onChange(updated)
    setSelected(s => s?.id === id ? { ...s, isMaster: master } : s)
  }, [items, onChange])

  return (
    <div className="layout-section">
      <div className="layout-header">
        <span className="layout-legend">
          {allHostNames.map(h => (
            <span key={h} className="legend-chip" style={{ background: `${colorMap.get(h)}22`, borderColor: colorMap.get(h), color: colorMap.get(h) }}>
              {h}
            </span>
          ))}
          {!allHostNames.length && <span className="layout-empty-hint">Add screens below to build your layout</span>}
        </span>
        {connectionCount > 0 && (
          <span className="layout-stat">{connectionCount} connection{connectionCount !== 1 ? 's' : ''}</span>
        )}
      </div>

      <div className="layout-canvas-scroll" style={{ minHeight: Math.max(ch, 240) }}>
        <div
          ref={canvasRef}
          className="layout-canvas"
          style={{ width: cw, height: ch }}
          onPointerMove={onPointerMove}
          onPointerUp={onPointerUp}
          onClick={() => setSelected(null)}
        >
          <svg className="layout-svg" width={cw} height={ch}>
            <defs>
              {[...colorMap.values()].filter((v, i, a) => a.indexOf(v) === i).map(color => (
                <marker key={color} id={`arrow-${color.replace('#', '')}`} markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
                  <path d="M0,0 L0,6 L8,3 z" fill={color} />
                </marker>
              ))}
            </defs>
            {connections.map(c => (
              <line
                key={`${c.fromId}-${c.toId}`}
                x1={c.x1} y1={c.y1} x2={c.x2} y2={c.y2}
                stroke={c.color} strokeWidth={2} strokeOpacity={0.6}
                markerEnd={`url(#arrow-${c.color.replace('#', '')})`}
              />
            ))}
          </svg>

          {drag && ghost && (
            <div
              className={`layout-block-ghost${ghost.overlapping ? ' overlap' : ghost.disconnected ? ' floating' : ''}`}
              style={{ left: blockLeft(ghost.x), top: blockTop(ghost.y), width: toPx(ghost.w), height: toPx(ghost.h) }}
            />
          )}

          {items.map(item => {
            const color = colorMap.get(item.hostName) ?? '#888'
            const isDragging = drag?.id === item.id
            const isSelected = selected?.id === item.id
            const isMasterHost = masterHostName !== undefined && item.hostName === masterHostName
            const edgeIndicators = getEdgeIndicators(item, items, colorMap)

            return (
              <div
                key={item.id}
                className={`layout-block${isDragging ? ' dragging' : ''}${isSelected ? ' selected' : ''}`}
                style={{
                  left: blockLeft(item.x),
                  top: blockTop(item.y),
                  width: toPx(item.w),
                  height: toPx(item.h),
                  borderColor: color,
                  background: `${color}18`,
                  boxShadow: isSelected ? `0 0 0 2px ${color}` : undefined,
                }}
                onPointerDown={e => startDrag(e, item.id, 'move')}
                onClick={e => {
                  e.stopPropagation()
                  setSelected({ id: item.id, hostName: item.hostName, screenId: item.screenId ?? '', w: String(item.w), h: String(item.h), isMaster: item.isMaster ?? false })
                }}
              >
                {isMasterHost && <div className="layout-block-master-badge">M</div>}
                <div className="layout-block-host" style={{ color }}>{item.hostName}</div>
                {item.screenId && <div className="layout-block-screen">{item.screenId}</div>}
                <div className="layout-block-size">{item.w} × {item.h}</div>

                <button
                  className="layout-block-remove"
                  onClick={e => { e.stopPropagation(); removeItem(item.id) }}
                  aria-label="remove"
                >✕</button>

                {edgeIndicators.map((ind, idx) => {
                  const style: React.CSSProperties = { background: ind.color, opacity: 0.8, position: 'absolute', pointerEvents: 'none', borderRadius: 2 }
                  if (ind.side === 'right') Object.assign(style, { right: -3, width: 6, top: `${ind.startPct}%`, height: `${ind.endPct - ind.startPct}%` })
                  else if (ind.side === 'left') Object.assign(style, { left: -3, width: 6, top: `${ind.startPct}%`, height: `${ind.endPct - ind.startPct}%` })
                  else if (ind.side === 'bottom') Object.assign(style, { bottom: -3, height: 6, left: `${ind.startPct}%`, width: `${ind.endPct - ind.startPct}%` })
                  else Object.assign(style, { top: -3, height: 6, left: `${ind.startPct}%`, width: `${ind.endPct - ind.startPct}%` })
                  return <div key={idx} style={style} />
                })}

                {isSelected && (['nw', 'ne', 'sw', 'se'] as ResizeCorner[]).map(corner => (
                  <div
                    key={corner}
                    className={`layout-resize-handle layout-resize-${corner}`}
                    onPointerDown={e => { e.stopPropagation(); startDrag(e, item.id, `resize-${corner}`) }}
                  />
                ))}
              </div>
            )
          })}
        </div>
      </div>

      {selected && (
        <div className="layout-selected-panel">
          <div className="layout-selected-title">Block Settings</div>
          <div className="field-row">
            <div className="field flex-grow">
              <label htmlFor={`${formIdPrefix}-sel-host`}>Host Name</label>
              <input
                id={`${formIdPrefix}-sel-host`}
                type="text"
                list={hostListId}
                value={selected.hostName}
                onChange={e => setSelected(s => s ? { ...s, hostName: e.target.value } : null)}
                onBlur={applySelected}
              />
            </div>
            <div className="field flex-grow">
              <label htmlFor={`${formIdPrefix}-sel-screen`}>Screen Identifier</label>
              <input
                id={`${formIdPrefix}-sel-screen`}
                type="text"
                value={selected.screenId}
                placeholder="e.g. HDMI-1 (optional)"
                onChange={e => setSelected(s => s ? { ...s, screenId: e.target.value } : null)}
                onBlur={applySelected}
              />
            </div>
            <div className="field" style={{ justifyContent: 'flex-end' }}>
              <label
                className="checkbox-label"
                style={{ marginBottom: 4 }}
                title={selected.isMaster ? 'There must be one master in the configuration' : undefined}
              >
                <input
                  type="checkbox"
                  checked={selected.isMaster}
                  disabled={selected.isMaster}
                  onChange={e => setMaster(selected.id, e.target.checked)}
                />
                Master
              </label>
            </div>
            <button
              className="btn-remove-block"
              style={{ alignSelf: 'flex-end' }}
              onClick={() => removeItem(selected.id)}
            >
              Remove
            </button>
          </div>
          <div className="field-row" style={{ marginBottom: 8 }}>
            <div className="field">
              <label htmlFor={`${formIdPrefix}-sel-w`}>Width (px)</label>
              <input
                id={`${formIdPrefix}-sel-w`}
                type="number"
                min={MIN_SIZE}
                step={SNAP}
                value={selected.w}
                onChange={e => setSelected(s => s ? { ...s, w: e.target.value } : null)}
                onBlur={applySelected}
              />
            </div>
            <div className="field">
              <label htmlFor={`${formIdPrefix}-sel-h`}>Height (px)</label>
              <input
                id={`${formIdPrefix}-sel-h`}
                type="number"
                min={MIN_SIZE}
                step={SNAP}
                value={selected.h}
                onChange={e => setSelected(s => s ? { ...s, h: e.target.value } : null)}
                onBlur={applySelected}
              />
            </div>
            <div className="field">
              <label htmlFor={`${formIdPrefix}-sel-orient`}>Orientation</label>
              <select
                id={`${formIdPrefix}-sel-orient`}
                value={Number(selected.h) > Number(selected.w) ? 'vertical' : 'horizontal'}
                onChange={e => setOrientation(e.target.value as 'horizontal' | 'vertical')}
              >
                <option value="horizontal">Horizontal</option>
                <option value="vertical">Vertical</option>
              </select>
            </div>
          </div>
          <div className="layout-aspect-row">
            <span className="layout-aspect-label">Preset:</span>
            {ASPECT_PRESETS.map(p => {
              const item = items.find(i => i.id === selected.id)
              const vertical = item && item.h > item.w
              const fw = vertical ? Math.min(p.w, p.h) : Math.max(p.w, p.h)
              const fh = vertical ? Math.max(p.w, p.h) : Math.min(p.w, p.h)
              const active = selected.w === String(fw) && selected.h === String(fh)
              return (
                <button
                  key={p.label}
                  className={`layout-aspect-btn${active ? ' active' : ''}`}
                  onClick={() => applyPreset(p.w, p.h)}
                >
                  {p.label}
                </button>
              )
            })}
          </div>
        </div>
      )}

      <datalist id={hostListId}>
        {allHostNames.map(h => <option key={h} value={h} />)}
      </datalist>

      {showAdd ? (
        <div className="layout-add-form">
          <div className="field-row" style={{ marginBottom: 0 }}>
            <div className="field flex-grow">
              <label htmlFor={`${formIdPrefix}-add-host`} className="required">Host Name</label>
              <input
                id={`${formIdPrefix}-add-host`}
                type="text"
                list={hostListId}
                value={addForm.hostName}
                placeholder="e.g. desktop"
                autoFocus
                onChange={e => setAddForm(f => ({ ...f, hostName: e.target.value }))}
                onKeyDown={e => { if (e.key === 'Enter') addItem() }}
              />
            </div>
            <div className="field flex-grow">
              <label htmlFor={`${formIdPrefix}-add-screen`}>Screen ID</label>
              <input
                id={`${formIdPrefix}-add-screen`}
                type="text"
                value={addForm.screenId}
                placeholder="e.g. HDMI-1 (optional)"
                onChange={e => setAddForm(f => ({ ...f, screenId: e.target.value }))}
                onKeyDown={e => { if (e.key === 'Enter') addItem() }}
              />
            </div>
            <button className="btn-primary" style={{ alignSelf: 'flex-end' }} onClick={addItem}>Add</button>
            <button className="btn-ghost" style={{ alignSelf: 'flex-end' }} onClick={() => setShowAdd(false)}>Cancel</button>
          </div>
        </div>
      ) : (
        <button className="btn-add" onClick={() => setShowAdd(true)}>+ Add Screen</button>
      )}

      {items.length > 0 && (
        <p className="hint" style={{ marginTop: 8 }}>
          Drag screens to arrange. Select a screen to resize or set aspect ratio. Adjacent screens from different hosts automatically become neighbours.
        </p>
      )}
    </div>
  )
}
