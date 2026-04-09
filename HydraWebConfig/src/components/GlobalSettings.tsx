import type { HydraConfig, LogLevel } from '../types'

const LOG_LEVELS: LogLevel[] = ['trace', 'debug', 'info', 'warn', 'error', 'critical']

interface Props {
  config: HydraConfig
  onChange: (patch: Partial<HydraConfig>) => void
}

export function GlobalSettings({ config, onChange }: Props) {
  const isMaster = config.mode === 'Master'

  return (
    <div className="section">
      <h2>Identity</h2>
      <div className="field-row">
        <div className="field">
          <label htmlFor="name">Name</label>
          <input
            id="name"
            type="text"
            value={config.name ?? ''}
            placeholder="hostname"
            onChange={e => onChange({ name: e.target.value || undefined })}
          />
        </div>
        <div className="field">
          <label htmlFor="lockFile">Lock File</label>
          <input
            id="lockFile"
            type="text"
            value={config.lockFile ?? ''}
            placeholder="hydra.lock"
            onChange={e => onChange({ lockFile: e.target.value || undefined })}
          />
        </div>
      </div>

      <h2 style={{ marginTop: '1.5rem' }}>Network</h2>
      <div className="field">
        <label htmlFor="networkConfig">Network Config</label>
        <textarea
          id="networkConfig"
          value={config.networkConfig ?? ''}
          placeholder="Base64-encoded network config string from Styx"
          onChange={e => onChange({ networkConfig: e.target.value || undefined })}
          rows={3}
        />
      </div>

      <h2 style={{ marginTop: '1.5rem' }}>Settings</h2>
      <div className="field-row">
        <div className="field">
          <label htmlFor="logLevel">Log Level</label>
          <select
            id="logLevel"
            value={config.logLevel ?? 'info'}
            onChange={e => onChange({ logLevel: e.target.value as LogLevel })}
          >
            {LOG_LEVELS.map(l => <option key={l} value={l}>{l}</option>)}
          </select>
        </div>
        <div className="field">
          <label htmlFor="mouseScale">Mouse Scale</label>
          <input
            id="mouseScale"
            type="number"
            step="0.1"
            min="0.1"
            value={config.mouseScale ?? ''}
            placeholder="1.0"
            onChange={e => onChange({ mouseScale: e.target.value ? Number(e.target.value) : undefined })}
          />
        </div>
        <div className="field">
          <label htmlFor="deadCorners">Dead Corners (px)</label>
          <input
            id="deadCorners"
            type="number"
            min="0"
            value={config.deadCorners ?? ''}
            placeholder="0"
            onChange={e => onChange({ deadCorners: e.target.value ? Number(e.target.value) : undefined })}
          />
        </div>
      </div>

      <div className="checkbox-group">
        <label className="checkbox-label">
          <input
            type="checkbox"
            checked={config.autoUpdate !== false}
            onChange={e => onChange({ autoUpdate: e.target.checked ? undefined : false })}
          />
          Auto Update
        </label>
        <label className="checkbox-label">
          <input
            type="checkbox"
            checked={config.syncScreensaver !== false}
            onChange={e => onChange({ syncScreensaver: e.target.checked ? undefined : false })}
          />
          Sync Screensaver
        </label>
        <label className="checkbox-label">
          <input
            type="checkbox"
            checked={config.debugShield === true}
            onChange={e => onChange({ debugShield: e.target.checked ? true : undefined })}
          />
          Debug Shield
        </label>
        {isMaster && (
          <label className="checkbox-label">
            <input
              type="checkbox"
              checked={config.remoteOnly === true}
              onChange={e => onChange({ remoteOnly: e.target.checked ? true : undefined })}
            />
            Remote Only
          </label>
        )}
      </div>

    </div>
  )
}
