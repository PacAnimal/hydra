import type { FormState, LogLevel, RootSettingsPatch, UpdateChannel } from '../types'

const LOG_LEVELS: LogLevel[] = ['trace', 'debug', 'info', 'warn', 'error', 'critical']
const UPDATE_CHANNELS: UpdateChannel[] = ['stable', 'edge']

interface Props {
  state: FormState
  onChange: (patch: RootSettingsPatch) => void
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
        <div className="field">
          <label htmlFor="gs-updatechannel" title="edge follows prereleases built from the edge branch, and moves back onto a stable release once one overtakes them">Update Channel</label>
          <select
            id="gs-updatechannel"
            value={state.updateChannel ?? 'stable'}
            onChange={e => onChange({ updateChannel: e.target.value === 'stable' ? undefined : e.target.value as UpdateChannel })}
          >
            {UPDATE_CHANNELS.map(c => <option key={c} value={c}>{c}</option>)}
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
      <div className="field-row">
        <div className="field">
          <label htmlFor="gs-logmaxsize" title="How large a log file may grow before it is cut down, e.g. 10 MiB or 512 KB; 0 or none never cuts">Log Max Size</label>
          <input
            id="gs-logmaxsize"
            type="text"
            value={state.logMaxSize ?? ''}
            placeholder="10 MiB"
            onChange={e => onChange({ logMaxSize: e.target.value || undefined })}
          />
        </div>
        <div className="field">
          <label htmlFor="gs-loghistory" title="How many cut-off log files to keep beside it (log.1, log.2…); 0 trims the log in place instead">Log History</label>
          <input
            id="gs-loghistory"
            type="number"
            min="0"
            step="1"
            value={state.logHistory ?? ''}
            placeholder="1"
            onChange={e => onChange({ logHistory: e.target.value === '' ? undefined : Number(e.target.value) })}
          />
        </div>
        <div className="field">
          <label htmlFor="gs-logtrimpercent" title="With no log history: how much of a full log to cut from its start, 1 to 100">Log Trim %</label>
          <input
            id="gs-logtrimpercent"
            type="number"
            min="1"
            max="100"
            step="1"
            value={state.logTrimPercent ?? ''}
            placeholder="50"
            onChange={e => onChange({ logTrimPercent: e.target.value === '' ? undefined : Number(e.target.value) })}
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
        <label className="checkbox-label">
          <input
            type="checkbox"
            checked={state.managementListener !== false}
            onChange={e => onChange({ managementListener: e.target.checked ? undefined : false })}
          />
          TUI Listener
        </label>
      </div>
    </div>
  )
}
