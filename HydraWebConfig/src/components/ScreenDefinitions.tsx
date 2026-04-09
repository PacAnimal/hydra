import type { ScreenDefinition } from '../types'

interface CardProps {
  screen: ScreenDefinition
  index: number
  onChange: (patch: Partial<ScreenDefinition>) => void
  onRemove: () => void
}

function ScreenDefinitionCard({ screen, index, onChange, onRemove }: CardProps) {
  return (
    <div className="screen-card">
      <div className="host-header">
        <span className="host-label">Screen {index + 1}</span>
        <button className="btn-remove" onClick={onRemove} aria-label="remove screen definition">✕</button>
      </div>
      <div className="field-row">
        <div className="field flex-grow">
          <label>Display Name</label>
          <input
            type="text"
            value={screen.displayName ?? ''}
            placeholder="e.g. DELL U2720Q"
            onChange={e => onChange({ displayName: e.target.value || undefined })}
          />
        </div>
        <div className="field flex-grow">
          <label>Output Name</label>
          <input
            type="text"
            value={screen.outputName ?? ''}
            placeholder="e.g. HDMI-1"
            onChange={e => onChange({ outputName: e.target.value || undefined })}
          />
        </div>
      </div>
      <div className="field-row">
        <div className="field flex-grow">
          <label>Platform ID</label>
          <input
            type="text"
            value={screen.platformId ?? ''}
            placeholder="optional"
            onChange={e => onChange({ platformId: e.target.value || undefined })}
          />
        </div>
        <div className="field">
          <label>Mouse Scale</label>
          <input
            type="number"
            step="0.1"
            min="0.1"
            value={screen.mouseScale ?? ''}
            placeholder="1.0"
            onChange={e => onChange({ mouseScale: e.target.value ? Number(e.target.value) : undefined })}
          />
        </div>
      </div>
    </div>
  )
}

interface Props {
  screens: ScreenDefinition[]
  onAdd: () => void
  onRemove: (si: number) => void
  onUpdate: (si: number, patch: Partial<ScreenDefinition>) => void
}

export function ScreenDefinitions({ screens, onAdd, onRemove, onUpdate }: Props) {
  return (
    <div className="section">
      <h2>Screen Definitions</h2>
      <p className="hint">At least one of Display Name, Output Name, or Platform ID is required per screen.</p>
      {screens.map((s, si) => (
        <ScreenDefinitionCard
          key={si}
          screen={s}
          index={si}
          onChange={patch => onUpdate(si, patch)}
          onRemove={() => onRemove(si)}
        />
      ))}
      <button className="btn-add" onClick={onAdd}>+ Add Screen Definition</button>
    </div>
  )
}
