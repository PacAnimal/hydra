import type { HydraProfile } from '../types'
import { NetworkConfig } from './NetworkConfig'

interface Props {
  config: HydraProfile
  onChange: (patch: Partial<HydraProfile>) => void
}

export function GlobalSettings({ config, onChange }: Props) {
  const isMaster = config.mode === 'Master'

  return (
    <>
      <div className="section">
        <h2>Network</h2>
        <NetworkConfig config={config} onChange={onChange} />
      </div>

      <div className="section">
        <h2>Settings</h2>
        <div className="field-row">
          <div className="field">
            <label htmlFor="gs-deadcorners" title="Pixels from screen corners where cursor switching is suppressed — prevents accidental host switches when moving to a corner">Dead Corners (px)</label>
            <input
              id="gs-deadcorners"
              type="number"
              min="0"
              value={config.deadCorners ?? ''}
              placeholder="50"
              onChange={e => onChange({ deadCorners: e.target.value ? Number(e.target.value) : undefined })}
            />
          </div>
          {!isMaster && (
            <>
              <div className="field">
                <label htmlFor="gs-mousescale">Mouse Scale</label>
                <input
                  id="gs-mousescale"
                  type="number"
                  step="0.1"
                  min="0.1"
                  value={config.mouseScale ?? ''}
                  placeholder="1.0"
                  onChange={e => onChange({ mouseScale: e.target.value ? Number(e.target.value) : undefined })}
                />
              </div>
              <div className="field">
                <label htmlFor="gs-relmousescale">Relative Mouse Scale</label>
                <input
                  id="gs-relmousescale"
                  type="number"
                  step="0.1"
                  min="0.1"
                  value={config.relativeMouseScale ?? ''}
                  placeholder="1.0"
                  onChange={e => onChange({ relativeMouseScale: e.target.value ? Number(e.target.value) : undefined })}
                />
              </div>
            </>
          )}
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
            <>
              <label className="checkbox-label">
                <input
                  type="checkbox"
                  checked={config.remoteOnly === true}
                  onChange={e => onChange({ remoteOnly: e.target.checked ? true : undefined })}
                />
                Remote Only
              </label>
              <label className="checkbox-label">
                <input
                  type="checkbox"
                  checked={config.accelerateMouseWheel !== false}
                  onChange={e => onChange({ accelerateMouseWheel: e.target.checked ? true : undefined })}
                />
                Accelerate Mouse Wheel
              </label>
            </>
          )}
        </div>
      </div>
    </>
  )
}
