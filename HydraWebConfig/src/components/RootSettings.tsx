import type { FormState, LogLevel } from '../types'

const LOG_LEVELS: LogLevel[] = ['trace', 'debug', 'info', 'warn', 'error', 'critical']

interface Props {
  state: FormState
  onChange: (patch: Partial<Pick<FormState, 'name' | 'autoUpdate' | 'logLevel' | 'lockFile'>>) => void
}

export function RootSettings({ state, onChange }: Props) {
  return (
    <div className="section">
      <h2>Global Settings</h2>
      <div className="field-row">
        <div className="field">
          <label htmlFor="name">Name</label>
          <input
            id="name"
            type="text"
            value={state.name ?? ''}
            placeholder="hostname"
            onChange={e => onChange({ name: e.target.value || undefined })}
          />
        </div>
        <div className="field">
          <label htmlFor="logLevel">Log Level</label>
          <select
            id="logLevel"
            value={state.logLevel ?? 'info'}
            onChange={e => onChange({ logLevel: e.target.value as LogLevel })}
          >
            {LOG_LEVELS.map(l => <option key={l} value={l}>{l}</option>)}
          </select>
        </div>
        <div className="field">
          <label htmlFor="lockFile">Lock File</label>
          <input
            id="lockFile"
            type="text"
            value={state.lockFile ?? ''}
            placeholder="hydra.lock"
            onChange={e => onChange({ lockFile: e.target.value || undefined })}
          />
        </div>
      </div>
      <div className="checkbox-group">
        <label className="checkbox-label">
          <input
            type="checkbox"
            checked={state.autoUpdate !== false}
            onChange={e => onChange({ autoUpdate: e.target.checked ? undefined : false })}
          />
          Auto Update
        </label>
      </div>
    </div>
  )
}
