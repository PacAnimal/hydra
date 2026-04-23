import type { FormState, LogLevel } from '../types'

const LOG_LEVELS: LogLevel[] = ['trace', 'debug', 'info', 'warn', 'error', 'critical']

interface Props {
  state: FormState
  onChange: (patch: Partial<Pick<FormState, 'name' | 'autoUpdate' | 'logLevel' | 'lockFile' | 'logFile' | 'sessionLogFile'>>) => void
}

export function RootSettings({ state, onChange }: Props) {
  return (
    <div className="section">
      <h2>Global Settings</h2>
      <div className="field-row">
        <div className="field flex-grow">
          <label htmlFor="gs-name">Name</label>
          <input
            id="gs-name"
            type="text"
            value={state.name ?? ''}
            placeholder="hostname (optional)"
            onChange={e => onChange({ name: e.target.value || undefined })}
          />
        </div>
        <div className="field">
          <label htmlFor="gs-loglevel">Log Level</label>
          <select
            id="gs-loglevel"
            value={state.logLevel ?? 'info'}
            onChange={e => onChange({ logLevel: e.target.value as LogLevel })}
          >
            {LOG_LEVELS.map(l => <option key={l} value={l}>{l}</option>)}
          </select>
        </div>
        <div className="field flex-grow">
          <label htmlFor="gs-lockfile">Lock File</label>
          <input
            id="gs-lockfile"
            type="text"
            value={state.lockFile ?? ''}
            placeholder="hydra.lock"
            onChange={e => onChange({ lockFile: e.target.value || undefined })}
          />
        </div>
      </div>
      <div className="field-row">
        <div className="field flex-grow">
          <label htmlFor="gs-logfile">Log File</label>
          <input
            id="gs-logfile"
            type="text"
            value={state.logFile ?? ''}
            placeholder="path to log file (optional)"
            onChange={e => onChange({ logFile: e.target.value || undefined })}
          />
        </div>
        <div className="field flex-grow">
          <label htmlFor="gs-sessionlog">Session Log File</label>
          <input
            id="gs-sessionlog"
            type="text"
            value={state.sessionLogFile ?? ''}
            placeholder="path for service-mode log (optional)"
            onChange={e => onChange({ sessionLogFile: e.target.value || undefined })}
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
