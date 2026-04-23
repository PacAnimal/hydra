import { useState, useRef, useCallback, useId } from 'react'
import type { LayoutItem } from '../types'
import { newLayoutItem } from '../defaults'
import { deriveHostsFromLayout, DIR_DELTA, DIRS } from '../utils/layout'

const CELL_W = 160
const CELL_H = 88
const GAP = 16
const PAD = 20
const STRIDE_X = CELL_W + GAP
const STRIDE_Y = CELL_H + GAP

const HOST_COLORS = [
  '#2563eb', '#16a34a', '#dc2626', '#9333ea', '#f59e0b',
  '#0891b2', '#be185d', '#7c3aed', '#059669', '#d97706',
]

function uniqueHosts(items: LayoutItem[]): string[] {
  return [...new Set(items.map(i => i.hostName))]
}

function makeColorMap(items: LayoutItem[]): Map<string, string> {
  const hosts = uniqueHosts(items)
  return new Map(hosts.map((h, i) => [h, HOST_COLORS[i % HOST_COLORS.length]]))
}

function itemLeft(col: number): number { return col * STRIDE_X + PAD }
function itemTop(row: number): number { return row * STRIDE_Y + PAD }
function itemCX(col: number): number { return itemLeft(col) + CELL_W / 2 }
function itemCY(row: number): number { return itemTop(row) + CELL_H / 2 }

interface Connection {
  fromId: string; toId: string
  x1: number; y1: number; x2: number; y2: number
  color: string
}

function buildConnections(items: LayoutItem[], colorMap: Map<string, string>): Connection[] {
  const byPos = new Map(items.map(i => [`${i.col},${i.row}`, i]))
  const seen = new Set<string>()
  const conns: Connection[] = []

  for (const item of items) {
    for (const dir of DIRS) {
      const [dc, dr] = DIR_DELTA[dir]
      const adj = byPos.get(`${item.col + dc},${item.row + dr}`)
      if (!adj || adj.hostName === item.hostName) continue

      const pairKey = [item.id, adj.id].sort().join('|')
      if (seen.has(pairKey)) continue
      seen.add(pairKey)

      // line from near-edge of item to near-edge of adj
      let x1 = itemCX(item.col), y1 = itemCY(item.row)
      let x2 = itemCX(adj.col), y2 = itemCY(adj.row)
      if (dir === 'Right') { x1 = itemLeft(item.col) + CELL_W; x2 = itemLeft(adj.col) }
      if (dir === 'Left') { x1 = itemLeft(item.col); x2 = itemLeft(adj.col) + CELL_W }
      if (dir === 'Down') { y1 = itemTop(item.row) + CELL_H; y2 = itemTop(adj.row) }
      if (dir === 'Up') { y1 = itemTop(item.row); y2 = itemTop(adj.row) + CELL_H }

      conns.push({ fromId: item.id, toId: adj.id, x1, y1, x2, y2, color: colorMap.get(item.hostName) ?? '#888' })
    }
  }
  return conns
}

function canvasSize(items: LayoutItem[]): { w: number; h: number } {
  const maxCol = items.length ? Math.max(...items.map(i => i.col)) : 2
  const maxRow = items.length ? Math.max(...items.map(i => i.row)) : 2
  return {
    w: (maxCol + 1) * STRIDE_X + PAD * 2,
    h: (maxRow + 1) * STRIDE_Y + PAD * 2,
  }
}

interface AddForm {
  hostName: string
  screenId: string
}

interface SelectedItem {
  id: string
  hostName: string
  screenId: string
  deadCorners: string
}

interface Props {
  items: LayoutItem[]
  onChange: (items: LayoutItem[]) => void
}

export function LayoutCanvas({ items, onChange }: Props) {
  const canvasRef = useRef<HTMLDivElement>(null)
  const formIdPrefix = useId()

  const [drag, setDrag] = useState<{
    id: string; offsetX: number; offsetY: number; x: number; y: number
  } | null>(null)
  const [ghostPos, setGhostPos] = useState<{ col: number; row: number } | null>(null)

  const [showAdd, setShowAdd] = useState(false)
  const [addForm, setAddForm] = useState<AddForm>({ hostName: '', screenId: '' })
  const [selected, setSelected] = useState<SelectedItem | null>(null)

  const colorMap = makeColorMap(items)
  const { w: cw, h: ch } = canvasSize(items)
  const connections = buildConnections(items, colorMap)

  const snapToGrid = useCallback((canvasX: number, canvasY: number): { col: number; row: number } => {
    const col = Math.max(0, Math.round((canvasX - PAD) / STRIDE_X))
    const row = Math.max(0, Math.round((canvasY - PAD) / STRIDE_Y))
    return { col, row }
  }, [])

  const onPointerDownBlock = useCallback((e: React.PointerEvent<HTMLDivElement>, id: string) => {
    e.stopPropagation()
    const item = items.find(i => i.id === id)
    if (!item) return
    const rect = canvasRef.current!.getBoundingClientRect()
    const blockLeft = itemLeft(item.col) + rect.left - (canvasRef.current!.scrollLeft ?? 0)
    const blockTop = itemTop(item.row) + rect.top - (canvasRef.current!.scrollTop ?? 0)
    const offsetX = e.clientX - blockLeft
    const offsetY = e.clientY - blockTop
    ;(e.currentTarget as HTMLElement).setPointerCapture(e.pointerId)
    setDrag({ id, offsetX, offsetY, x: e.clientX, y: e.clientY })
    setGhostPos({ col: item.col, row: item.row })
    setSelected({ id, hostName: item.hostName, screenId: item.screenId ?? '', deadCorners: item.deadCorners?.toString() ?? '' })
  }, [items])

  const onPointerMove = useCallback((e: React.PointerEvent<HTMLDivElement>) => {
    if (!drag) return
    const rect = canvasRef.current!.getBoundingClientRect()
    const canvasX = e.clientX - rect.left + (canvasRef.current!.scrollLeft ?? 0) - drag.offsetX
    const canvasY = e.clientY - rect.top + (canvasRef.current!.scrollTop ?? 0) - drag.offsetY
    const pos = snapToGrid(canvasX + CELL_W / 2, canvasY + CELL_H / 2)
    setGhostPos(pos)
    setDrag(d => d ? { ...d, x: e.clientX, y: e.clientY } : null)
  }, [drag, snapToGrid])

  const onPointerUp = useCallback((_e: React.PointerEvent<HTMLDivElement>) => {
    if (!drag || !ghostPos) return
    const occupied = items.some(i => i.id !== drag.id && i.col === ghostPos.col && i.row === ghostPos.row)
    if (!occupied) {
      onChange(items.map(i => i.id === drag.id ? { ...i, col: ghostPos.col, row: ghostPos.row } : i))
    }
    setDrag(null)
    setGhostPos(null)
  }, [drag, ghostPos, items, onChange])

  const addItem = useCallback(() => {
    const hostName = addForm.hostName.trim()
    if (!hostName) return

    const occupied = new Set(items.map(i => `${i.col},${i.row}`))
    let col = 0; let row = 0
    outer: for (row = 0; row < 10; row++) {
      for (col = 0; col < 10; col++) {
        if (!occupied.has(`${col},${row}`)) break outer
      }
    }

    const newItem = newLayoutItem(hostName, addForm.screenId.trim() || undefined, col, row)
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
    const dc = selected.deadCorners.trim() ? Number(selected.deadCorners) : undefined
    onChange(items.map(i => i.id === selected.id
      ? { ...i, hostName, screenId: selected.screenId.trim() || undefined, deadCorners: dc }
      : i
    ))
  }, [selected, items, onChange])

  const allHostNames = uniqueHosts(items)
  const hostAddListId = `${formIdPrefix}-hosts`

  // summary of what the layout produces (for confirmation)
  const derivedHosts = deriveHostsFromLayout(items)
  const connectionCount = derivedHosts.reduce((s, h) => s + (h.neighbours?.length ?? 0), 0)

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

      <div
        className="layout-canvas-scroll"
        style={{ minHeight: Math.max(ch, 240) }}
      >
        <div
          ref={canvasRef}
          className="layout-canvas"
          style={{ width: Math.max(cw, 500), height: Math.max(ch, 220) }}
          onPointerMove={onPointerMove}
          onPointerUp={onPointerUp}
          onClick={() => setSelected(null)}
        >
          {/* connection lines */}
          <svg
            className="layout-svg"
            width={Math.max(cw, 500)}
            height={Math.max(ch, 220)}
          >
            <defs>
              {[...colorMap.values()].filter((v, i, a) => a.indexOf(v) === i).map(color => (
                <marker key={color} id={`arrow-${color.replace('#', '')}`} markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
                  <path d="M0,0 L0,6 L8,3 z" fill={color} />
                </marker>
              ))}
            </defs>
            {connections.map(c => {
              const arrowId = `arrow-${c.color.replace('#', '')}`
              return (
                <line
                  key={`${c.fromId}-${c.toId}`}
                  x1={c.x1} y1={c.y1} x2={c.x2} y2={c.y2}
                  stroke={c.color}
                  strokeWidth={2}
                  strokeOpacity={0.6}
                  markerEnd={`url(#${arrowId})`}
                />
              )
            })}
          </svg>

          {/* ghost drop target while dragging */}
          {drag && ghostPos && (
            <div
              className="layout-block-ghost"
              style={{ left: itemLeft(ghostPos.col), top: itemTop(ghostPos.row) }}
            />
          )}

          {/* screen blocks */}
          {items.map(item => {
            const color = colorMap.get(item.hostName) ?? '#888'
            const isDragging = drag?.id === item.id
            const isSelected = selected?.id === item.id
            const pos = isDragging && ghostPos ? ghostPos : item
            return (
              <div
                key={item.id}
                className={`layout-block${isDragging ? ' dragging' : ''}${isSelected ? ' selected' : ''}`}
                style={{
                  left: itemLeft(pos.col),
                  top: itemTop(pos.row),
                  borderColor: color,
                  background: `${color}18`,
                  boxShadow: isSelected ? `0 0 0 2px ${color}` : undefined,
                }}
                onPointerDown={e => onPointerDownBlock(e, item.id)}
                onClick={e => {
                  e.stopPropagation()
                  setSelected({ id: item.id, hostName: item.hostName, screenId: item.screenId ?? '', deadCorners: item.deadCorners?.toString() ?? '' })
                }}
              >
                <div className="layout-block-host" style={{ color }}>{item.hostName}</div>
                {item.screenId && <div className="layout-block-screen">{item.screenId}</div>}
                <button
                  className="layout-block-remove"
                  onClick={e => { e.stopPropagation(); removeItem(item.id) }}
                  aria-label="remove"
                >✕</button>

                {/* edge indicators for connections */}
                {DIRS.map(dir => {
                  const [dc, dr] = DIR_DELTA[dir]
                  const pos2 = { col: item.col + dc, row: item.row + dr }
                  const adj = items.find(i => i.col === pos2.col && i.row === pos2.row && i.hostName !== item.hostName)
                  if (!adj) return null
                  const adjColor = colorMap.get(adj.hostName) ?? '#888'
                  return (
                    <div
                      key={dir}
                      className={`layout-edge layout-edge-${dir.toLowerCase()}`}
                      style={{ background: adjColor, opacity: 0.7 }}
                      title={`→ ${adj.hostName}${adj.screenId ? ` (${adj.screenId})` : ''}`}
                    />
                  )
                })}
              </div>
            )
          })}
        </div>
      </div>

      {/* selected block details */}
      {selected && (
        <div className="layout-selected-panel">
          <div className="layout-selected-title">Block Settings</div>
          <div className="field-row">
            <div className="field flex-grow">
              <label htmlFor={`${formIdPrefix}-sel-host`}>Host Name</label>
              <input
                id={`${formIdPrefix}-sel-host`}
                type="text"
                list={hostAddListId}
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
            <div className="field">
              <label htmlFor={`${formIdPrefix}-sel-dc`}>Dead Corners</label>
              <input
                id={`${formIdPrefix}-sel-dc`}
                type="number"
                min="0"
                value={selected.deadCorners}
                placeholder="inherit"
                onChange={e => setSelected(s => s ? { ...s, deadCorners: e.target.value } : null)}
                onBlur={applySelected}
              />
            </div>
            <div className="field" style={{ justifyContent: 'flex-end' }}>
              <button
                className="btn-remove-block"
                onClick={() => removeItem(selected.id)}
                style={{ marginTop: 'auto' }}
              >
                Remove
              </button>
            </div>
          </div>
        </div>
      )}

      {/* add form */}
      <datalist id={hostAddListId}>
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
                list={hostAddListId}
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
            <div className="field" style={{ justifyContent: 'flex-end' }}>
              <button className="btn-primary" onClick={addItem} style={{ marginTop: 'auto' }}>Add</button>
            </div>
            <div className="field" style={{ justifyContent: 'flex-end' }}>
              <button className="btn-ghost" onClick={() => setShowAdd(false)} style={{ marginTop: 'auto' }}>Cancel</button>
            </div>
          </div>
        </div>
      ) : (
        <button className="btn-add" onClick={() => setShowAdd(true)}>+ Add Screen</button>
      )}

      {items.length > 0 && (
        <p className="hint" style={{ marginTop: 8 }}>
          Drag screens to arrange them. Adjacent screens from different hosts automatically become neighbours.
        </p>
      )}
    </div>
  )
}
