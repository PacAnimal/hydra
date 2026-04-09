import type { NeighbourConfig, Direction } from '../types'

const DIRECTIONS: Direction[] = ['Left', 'Right', 'Up', 'Down']

interface Props {
  neighbour: NeighbourConfig
  onChange: (patch: Partial<NeighbourConfig>) => void
  onRemove: () => void
}

export function NeighbourEditor({ neighbour, onChange, onRemove }: Props) {
  return (
    <div className="neighbour-card">
      <div className="neighbour-header">
        <span className="neighbour-label">Neighbour</span>
        <button className="btn-remove" onClick={onRemove} aria-label="remove neighbour">✕</button>
      </div>

      <div className="field-row">
        <div className="field">
          <label>Direction</label>
          <select
            value={neighbour.direction}
            onChange={e => onChange({ direction: e.target.value as Direction })}
          >
            {DIRECTIONS.map(d => <option key={d} value={d}>{d}</option>)}
          </select>
        </div>
        <div className="field flex-grow">
          <label className="required">Host Name</label>
          <input
            type="text"
            value={neighbour.name}
            placeholder="hostname"
            onChange={e => onChange({ name: e.target.value })}
          />
        </div>
      </div>

      <details className="advanced">
        <summary>Advanced</summary>
        <div className="field-row">
          <div className="field">
            <label>Source Screen</label>
            <input
              type="text"
              value={neighbour.sourceScreen ?? ''}
              placeholder="optional"
              onChange={e => onChange({ sourceScreen: e.target.value || undefined })}
            />
          </div>
          <div className="field">
            <label>Dest Screen</label>
            <input
              type="text"
              value={neighbour.destScreen ?? ''}
              placeholder="optional"
              onChange={e => onChange({ destScreen: e.target.value || undefined })}
            />
          </div>
        </div>
        <div className="field-row">
          <div className="field">
            <label>Source Start %</label>
            <input
              type="number" min="0" max="100"
              value={neighbour.sourceStart ?? 0}
              onChange={e => onChange({ sourceStart: Number(e.target.value) })}
            />
          </div>
          <div className="field">
            <label>Source End %</label>
            <input
              type="number" min="0" max="100"
              value={neighbour.sourceEnd ?? 100}
              onChange={e => onChange({ sourceEnd: Number(e.target.value) })}
            />
          </div>
          <div className="field">
            <label>Dest Start %</label>
            <input
              type="number" min="0" max="100"
              value={neighbour.destStart ?? 0}
              onChange={e => onChange({ destStart: Number(e.target.value) })}
            />
          </div>
          <div className="field">
            <label>Dest End %</label>
            <input
              type="number" min="0" max="100"
              value={neighbour.destEnd ?? 100}
              onChange={e => onChange({ destEnd: Number(e.target.value) })}
            />
          </div>
        </div>
        <label className="checkbox-label">
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
