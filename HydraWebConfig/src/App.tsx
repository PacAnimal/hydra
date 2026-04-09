import { useRef } from 'react'
import './App.css'
import { useHydraConfig } from './hooks/useHydraConfig'
import { validate } from './utils/validation'
import { DropZone } from './components/DropZone'
import { ModeSelect } from './components/ModeSelect'
import { RootSettings } from './components/RootSettings'
import { GlobalSettings } from './components/GlobalSettings'
import { HostsEditor } from './components/HostsEditor'
import { ScreenDefinitions } from './components/ScreenDefinitions'
import { ConditionsEditor } from './components/ConditionsEditor'
import { ConfigFileSection } from './components/ConfigFileSection'

export default function App() {
  const {
    state, current,
    updateRoot,
    updateCurrent,
    addHost, removeHost, updateHost,
    addNeighbour, removeNeighbour, updateNeighbour,
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

  const scrollToErrors = () => {
    errorsRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' })
  }

  return (
    <div className="app">
      <header className="app-header">
        <div className="header-inner">
          <div className="header-title">
            <h1>Hydra Config</h1>
            <span className="subtitle">Visual configuration editor</span>
          </div>
          <div className="header-actions">
            <DropZone onImport={importJson} />
            <button className="btn-ghost" onClick={reset}>Reset</button>
          </div>
        </div>
      </header>

      <div className="app-body">
        <aside className="config-panel">
          <ConfigFileSection state={state} isValid={isValid} onScrollToErrors={scrollToErrors} />
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
              <div className="section">
                <div className="field">
                  <label htmlFor="profileName" className="required">Profile Name</label>
                  <input
                    id="profileName"
                    type="text"
                    value={current.profileName}
                    placeholder="e.g. Home, Work, Office"
                    onChange={e => updateCurrent({ profileName: e.target.value })}
                  />
                </div>
              </div>

              <ModeSelect value={current.mode} onChange={mode => updateCurrent({ mode })} />

              <GlobalSettings config={current} onChange={updateCurrent} />

              {isMaster && (
                <HostsEditor
                  hosts={current.hosts ?? []}
                  onAdd={addHost}
                  onRemove={removeHost}
                  onUpdate={updateHost}
                  onAddNeighbour={addNeighbour}
                  onRemoveNeighbour={removeNeighbour}
                  onUpdateNeighbour={updateNeighbour}
                />
              )}

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
                <p className="hint">When should this profile be active?</p>
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
