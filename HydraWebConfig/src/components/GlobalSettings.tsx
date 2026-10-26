import { HOTKEY_ACTIONS, type HydraProfile } from '../types'
import { NetworkConfig } from './NetworkConfig'

interface Props {
  config: HydraProfile
  serverError?: string
  onChange: (patch: Partial<HydraProfile>) => void
}

export function GlobalSettings({ config, serverError, onChange }: Props) {
  const isMaster = config.mode === 'Master'

  return (
    <>
      <div className="section">
        <h2>Network</h2>
        <NetworkConfig config={config} serverError={serverError} onChange={onChange} />
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
          {isMaster && (
            <>
              <div className="field">
                <label htmlFor="gs-maxmousehz" title="How many mouse updates a second the master sends to a slave (1–1000). Higher costs master CPU">Max Mouse Hz</label>
                <input
                  id="gs-maxmousehz"
                  type="number"
                  min="1"
                  max="1000"
                  value={config.maxMouseHz ?? ''}
                  placeholder="125"
                  onChange={e => onChange({ maxMouseHz: e.target.value ? Number(e.target.value) : undefined })}
                />
              </div>
              <div className="field">
                <label htmlFor="gs-modifiernumlock" title="NumLock for a keypad key pressed with Super, Ctrl or Alt, whatever the keyboard's own NumLock says — for keypad shortcuts on a slave">Keypad NumLock</label>
                <select
                  id="gs-modifiernumlock"
                  value={config.modifierNumLock === true ? 'true' : config.modifierNumLock === false ? 'false' : ''}
                  onChange={e => onChange({
                    modifierNumLock: e.target.value === 'true' ? true : e.target.value === 'false' ? false : undefined,
                  })}
                >
                  <option value="">as pressed</option>
                  <option value="true">digits with Super/Ctrl/Alt</option>
                  <option value="false">navigation with Super/Ctrl/Alt</option>
                </select>
              </div>
            </>
          )}
          <div className="field">
            <label htmlFor="gs-clipboardsync" title="Hydra syncs clipboards itself. System lets a macOS master leave Mac peers to Universal Clipboard. Off shares no clipboard or files with any peer">Clipboard</label>
            <select
              id="gs-clipboardsync"
              value={config.clipboardSync ?? 'Hydra'}
              onChange={e => onChange({ clipboardSync: e.target.value === 'System' || e.target.value === 'Off' ? e.target.value : undefined })}
            >
              <option value="Hydra">Hydra's own sync</option>
              <option value="System">System (Universal Clipboard)</option>
              <option value="Off">Off (no clipboard or file transfer)</option>
            </select>
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
          <label className="checkbox-label" title="Let the operating system's idle policy sleep this machine; Hydra leaves the relay before it suspends">
            <input
              type="checkbox"
              checked={config.allowSystemSleep === true}
              onChange={e => onChange({ allowSystemSleep: e.target.checked ? true : undefined })}
            />
            Allow System Sleep
          </label>
          {isMaster && (
            <>
              <label className="checkbox-label" title="Hide the master's local cursor while it is inactive or routed remotely">
                <input
                  type="checkbox"
                  checked={config.hideCursor === true}
                  onChange={e => onChange({ hideCursor: e.target.checked ? true : undefined })}
                />
                Hide Cursor
              </label>
              <label className="checkbox-label" title="Send activity pings to slaves to prevent their screensavers from activating, and re-broadcast pings from slaves to other slaves">
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
              <label className="checkbox-label" title="Propagate machine lock to connected Mac and Windows slaves (Mac: ctrl+cmd+q, Windows: LockWorkStation)">
                <input
                  type="checkbox"
                  checked={config.screenLockPropagation === true}
                  onChange={e => onChange({ screenLockPropagation: e.target.checked ? true : undefined })}
                />
                Screen Lock Propagation
              </label>
            </>
          )}
        </div>
      </div>

      {isMaster && (
        <div className="section">
          <h2>Hotkeys</h2>
          <div className="field-row">
            {HOTKEY_ACTIONS.map(({ action, label, defaultChord }) => (
              <div className="field" key={action}>
                <label htmlFor={`gs-hotkey-${action}`} title="Chords joined with + (Ctrl+Alt+Super+L), one per line. Listing any replaces the default; empty keeps it">{label}</label>
                <textarea
                  id={`gs-hotkey-${action}`}
                  rows={2}
                  value={config.hotkeys?.[action] ?? ''}
                  placeholder={defaultChord}
                  onChange={e => onChange({ hotkeys: { ...config.hotkeys, [action]: e.target.value } })}
                />
              </div>
            ))}
          </div>
        </div>
      )}
    </>
  )
}
