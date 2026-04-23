import type { NeighbourConfig, Direction } from '../types'

const DIRECTIONS: Direction[] = ['Left', 'Right', 'Up', 'Down']

const DIR_ICONS: Record<Direction, string> = {
  Left: '←', Right: '→', Up: '↑', Down: '↓',
}

interface Props {
  neighbour: NeighbourConfig
  onChange: (patch: Partial<NeighbourConfig>) => void
  onRemove: () => void
}

export function NeighbourEditor({ neighbour, onChange, onRemove }: Props) {
  const sourceStart = neighbour.sourceStart ?? 0
  const sourceEnd = neighbour.sourceEnd ?? 100
  const destStart = neighbour.destStart ?? 0
  const destEnd = neighbour.destEnd ?? 100
  const hasCustomRange = sourceStart !== 0 || sourceEnd !== 100 || destStart !== 0 || destEnd !== 100
  const hasScreenFilter = !!(neighbour.sourceScreen || neighbour.destScreen)

  return (
    <div className="neighbour-card">
      <div className="neighbour-header">
        <div className="neighbour-dir-badge" title={neighbour.direction}>
          {DIR_ICONS[neighbour.direction]}
        </div>
        <select
          className="neighbour-dir-select"
          value={neighbour.direction}
          onChange={e => onChange({ direction: e.target.value as Direction })}
        >
          {DIRECTIONS.map(d => <option key={d} value={d}>{d}</option>)}
        </select>
        <div className="field flex-grow neighbour-name-field">
          <input
            type="text"
            value={neighbour.name}
            placeholder="target hostname"
            onChange={e => onChange({ name: e.target.value })}
          />
        </div>
        <button className="btn-remove" onClick={onRemove} aria-label="remove neighbour">✕</button>
      </div>

      <details className="advanced" open={hasScreenFilter || hasCustomRange}>
        <summary>Advanced{(hasScreenFilter || hasCustomRange) ? ' (active)' : ''}</summary>

        <div className="field-row">
          <div className="field flex-grow">
            <label>Source Screen</label>
            <input
              type="text"
              value={neighbour.sourceScreen ?? ''}
              placeholder="any screen (optional)"
              onChange={e => onChange({ sourceScreen: e.target.value || undefined })}
            />
          </div>
          <div className="field flex-grow">
            <label>Dest Screen</label>
            <input
              type="text"
              value={neighbour.destScreen ?? ''}
              placeholder="any screen (optional)"
              onChange={e => onChange({ destScreen: e.target.value || undefined })}
            />
          </div>
        </div>

        <div className="range-editor">
          <div className="range-label">Source edge range (%)</div>
          <div className="range-inputs">
            <input
              type="number" min="0" max="100"
              value={sourceStart}
              onChange={e => onChange({ sourceStart: Number(e.target.value) })}
            />
            <span className="range-sep">–</span>
            <input
              type="number" min="0" max="100"
              value={sourceEnd}
              onChange={e => onChange({ sourceEnd: Number(e.target.value) })}
            />
          </div>
          <div className="range-bar-wrap">
            <div className="range-bar" style={{ left: `${sourceStart}%`, width: `${Math.max(0, sourceEnd - sourceStart)}%` }} />
          </div>
        </div>

        <div className="range-editor">
          <div className="range-label">Dest edge range (%)</div>
          <div className="range-inputs">
            <input
              type="number" min="0" max="100"
              value={destStart}
              onChange={e => onChange({ destStart: Number(e.target.value) })}
            />
            <span className="range-sep">–</span>
            <input
              type="number" min="0" max="100"
              value={destEnd}
              onChange={e => onChange({ destEnd: Number(e.target.value) })}
            />
          </div>
          <div className="range-bar-wrap">
            <div className="range-bar" style={{ left: `${destStart}%`, width: `${Math.max(0, destEnd - destStart)}%` }} />
          </div>
        </div>

        <label className="checkbox-label mt-8">
          <input
            type="checkbox"
            checked={neighbour.mirror !== false}
            onChange={e => onChange({ mirror: e.target.checked ? undefined : false })}
          />
          Auto-create reverse mapping (mirror)
        </label>
      </details>
    </div>
  )
}
