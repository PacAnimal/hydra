import { useRef } from 'react'
import './App.css'
import { useHydraConfig } from './hooks/useHydraConfig'
import { validate } from './utils/validation'
import { DropZone } from './components/DropZone'
import { ModeSelect } from './components/ModeSelect'
import { RootSettings } from './components/RootSettings'
import { GlobalSettings } from './components/GlobalSettings'
import { LayoutCanvas } from './components/LayoutCanvas'
import { ScreenDefinitions } from './components/ScreenDefinitions'
import { ConditionsEditor } from './components/ConditionsEditor'
import { ConfigFileSection } from './components/ConfigFileSection'

export default function App() {
  const {
    state, current,
    updateRoot,
    updateCurrent,
    updateLayoutItems,
    addScreen, removeScreen, updateScreen,
    updateConditions,
    addProfile, removeProfile, setActiveIndex,
    importJson, reset,
  } = useHydraConfig()

  const errorsRef = useRef<HTMLDivElement>(null)

  const { profiles, activeIndex } = state
  const isMaster = current.mode === 'Master'
  const errors = validate(profiles)
  const isValid = errors.length === 0

  return (
    <div className="app">
      <header className="app-header">
        <div className="header-inner">
          <div className="header-title">
            <h1>Hydra</h1>
            <span className="subtitle">Configuration editor</span>
          </div>
          <div className="header-actions">
            <DropZone onImport={importJson} />
            <button className="btn-ghost" onClick={reset}>Reset</button>
          </div>
        </div>
      </header>

      <div className="app-body">
        <aside className="config-panel">
          <ConfigFileSection
            state={state}
            isValid={isValid}
            onScrollToErrors={() => errorsRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' })}
          />
        </aside>

        <main className="app-main">
          <RootSettings state={state} onChange={updateRoot} />

          <div className="profiles-section">
            <div className="profiles-label">Profiles</div>
            <div className="config-tabs">
              {profiles.map((p, i) => (
                <button
                  key={i}
                  className={`tab-btn${i === activeIndex ? ' active' : ''}`}
                  onClick={() => setActiveIndex(i)}
                >
                  {p.profileName.trim() || `Profile ${i + 1}`}
                  {profiles.length > 1 && (
                    <span
                      className="tab-remove"
                      onClick={e => { e.stopPropagation(); removeProfile(i) }}
                      role="button"
                      aria-label={`remove profile ${i + 1}`}
                    >
                      ✕
                    </span>
                  )}
                </button>
              ))}
              <button className="tab-btn tab-add" onClick={addProfile}>+</button>
            </div>

            <div className="tab-panel">
              {/* profile identity */}
              <div className="section">
                <div className="profile-identity-row">
                  <div className="field flex-grow">
                    <label htmlFor="profileName" className="required">Profile Name</label>
                    <input
                      id="profileName"
                      type="text"
                      value={current.profileName}
                      placeholder="e.g. Home, Work, Office"
                      onChange={e => updateCurrent({ profileName: e.target.value })}
                    />
                  </div>
                  <div className="profile-mode-field">
                    <div className="field-label-sm">Mode</div>
                    <ModeSelect value={current.mode} onChange={mode => updateCurrent({ mode })} />
                  </div>
                </div>
                <p className="hint" style={{ marginTop: 8, marginBottom: 0 }}>
                  {isMaster
                    ? 'This machine controls keyboard and mouse, routing input to all connected hosts.'
                    : 'This machine receives keyboard and mouse input from the master.'}
                </p>
              </div>

              <GlobalSettings config={current} onChange={updateCurrent} />

              {/* master: screen layout editor */}
              {isMaster && (
                <div className="section">
                  <h2>Screen Layout</h2>
                  <LayoutCanvas
                    items={current.layoutItems ?? []}
                    onChange={updateLayoutItems}
                  />
                </div>
              )}

              {/* slave: screen definitions */}
              {!isMaster && (
                <ScreenDefinitions
                  screens={current.screenDefinitions ?? []}
                  onAdd={addScreen}
                  onRemove={removeScreen}
                  onUpdate={updateScreen}
                />
              )}

              <div className="section">
                <h2>Conditions</h2>
                <p className="hint">Activate this profile only when all conditions match — leave blank for always-active.</p>
                <ConditionsEditor
                  conditions={current.conditions ?? {}}
                  onChange={updateConditions}
                />
              </div>
            </div>
          </div>

          {!isValid && (
            <div className="section validation-errors" ref={errorsRef}>
              <h2>Validation Errors</h2>
              {errors.map((e, i) => (
                <div key={i} className="error">{e.message}</div>
              ))}
            </div>
          )}
        </main>
      </div>
    </div>
  )
}
