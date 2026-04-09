import type { Mode } from '../types'

interface Props {
  value: Mode
  onChange: (mode: Mode) => void
}

export function ModeSelect({ value, onChange }: Props) {
  return (
    <div className="section">
      <h2>Mode</h2>
      <div className="toggle-group" role="group" aria-label="mode">
        <button
          className={`toggle-btn${value === 'Master' ? ' active' : ''}`}
          onClick={() => onChange('Master')}
          aria-pressed={value === 'Master'}
        >
          Master
        </button>
        <button
          className={`toggle-btn${value === 'Slave' ? ' active' : ''}`}
          onClick={() => onChange('Slave')}
          aria-pressed={value === 'Slave'}
        >
          Slave
        </button>
      </div>
      <p className="hint">
        {value === 'Master'
          ? 'This machine controls input routing for all hosts.'
          : 'This machine receives input from the master.'}
      </p>
    </div>
  )
}
