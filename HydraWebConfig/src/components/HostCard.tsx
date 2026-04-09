import type { HostConfig, NeighbourConfig } from '../types'
import { NeighbourEditor } from './NeighbourEditor'

interface Props {
  host: HostConfig
  index: number
  onChange: (patch: Partial<HostConfig>) => void
  onRemove: () => void
  onAddNeighbour: () => void
  onRemoveNeighbour: (ni: number) => void
  onUpdateNeighbour: (ni: number, patch: Partial<NeighbourConfig>) => void
}

export function HostCard({ host, index, onChange, onRemove, onAddNeighbour, onRemoveNeighbour, onUpdateNeighbour }: Props) {
  return (
    <div className="host-card">
      <div className="host-header">
        <span className="host-label">Host {index + 1}</span>
        <button className="btn-remove" onClick={onRemove} aria-label="remove host">✕</button>
      </div>

      <div className="field-row">
        <div className="field flex-grow">
          <label>Host Name</label>
          <input
            type="text"
            value={host.name}
            placeholder="hostname"
            onChange={e => onChange({ name: e.target.value })}
          />
        </div>
        <div className="field">
          <label>Dead Corners (px)</label>
          <input
            type="number"
            min="0"
            value={host.deadCorners ?? ''}
            placeholder="inherit"
            onChange={e => onChange({ deadCorners: e.target.value ? Number(e.target.value) : undefined })}
          />
        </div>
      </div>

      <div className="neighbours-section">
        {(host.neighbours ?? []).map((n, ni) => (
          <NeighbourEditor
            key={ni}
            neighbour={n}
            onChange={patch => onUpdateNeighbour(ni, patch)}
            onRemove={() => onRemoveNeighbour(ni)}
          />
        ))}
        <button className="btn-add-small" onClick={onAddNeighbour}>+ Add Neighbour</button>
      </div>
    </div>
  )
}
