import type { HydraProfile } from '../types'

interface Props {
  config: HydraProfile
  onChange: (patch: Partial<HydraProfile>) => void
}

export function GlobalSettings({ config, onChange }: Props) {
  const isMaster = config.mode === 'Master'

  return (
    <div className="section">
      <h2>Network</h2>
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
        {!isMaster && (
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
        )}
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
            checked={config.syncScreensaver !== false}
            onChange={e => onChange({ syncScreensaver: e.target.checked ? undefined : false })}
          />
          Sync Screensaver
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
