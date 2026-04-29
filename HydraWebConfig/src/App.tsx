import { useRef, useEffect } from 'react'
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
import { CanvasErrorBoundary } from './components/CanvasErrorBoundary'

export default function App() {
  const {
    state, current,
    canUndo, canRedo, undo, redo,
    updateRoot,
    updateCurrent,
    updateLayoutItems,
    addScreen, removeScreen, updateScreen,
    updateConditions,
    addProfile, removeProfile, duplicateProfile, moveProfile, setActiveIndex,
    importJson, reset,
  } = useHydraConfig()

  const errorsRef = useRef<HTMLDivElement>(null)

  const { profiles, activeIndex } = state
  const isMaster = current.mode === 'Master'
  const errors = validate(profiles)
  const isValid = errors.length === 0

  // undo/redo keyboard shortcuts
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (!(e.ctrlKey || e.metaKey)) return
      if (e.key === 'z') {
        e.preventDefault()
        if (e.shiftKey) redo()
        else undo()
      } else if (e.key === 'y') {
        e.preventDefault()
        redo()
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [undo, redo])

  function handleReset() {
    if (window.confirm('Reset all configuration? This cannot be undone.')) reset()
  }

  // per-profile field error lookup
  function fieldError(suffix: string): string | undefined {
    return errors.find(e => e.path === `profiles[${activeIndex}]${suffix}`)?.message
  }

  return (
    <div className="app">
      <header className="app-header">
        <div className="header-inner">
          <div className="header-title">
            <h1>Hydra</h1>
            <span className="subtitle">Configuration editor</span>
          </div>
          <div className="header-actions">
            <button className="btn-ghost btn-icon" onClick={undo} disabled={!canUndo} title="Undo (Ctrl+Z)">↩</button>
            <button className="btn-ghost btn-icon" onClick={redo} disabled={!canRedo} title="Redo (Ctrl+Shift+Z)">↪</button>
            <DropZone onImport={importJson} />
            <button className="btn-ghost" onClick={handleReset}>Reset</button>
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

            {errors.find(e => e.path === 'profiles') && (
              <div className="field-error-msg" style={{ marginBottom: 6 }}>
                {errors.find(e => e.path === 'profiles')!.message}
              </div>
            )}

            <div className="config-tabs-scroll">
              <div className="config-tabs">
                {profiles.map((p, i) => (
                  <button
                    key={i}
                    className={`tab-btn${i === activeIndex ? ' active' : ''}`}
                    onClick={() => setActiveIndex(i)}
                  >
                    <span className="tab-name">{p.profileName.trim() || `Profile ${i + 1}`}</span>
                    {i === activeIndex && i > 0 && (
                      <span
                        className="tab-action"
                        onClick={e => { e.stopPropagation(); moveProfile(i, 'left') }}
                        role="button"
                        title="Move left"
                        aria-label="move profile left"
                      >‹</span>
                    )}
                    {i === activeIndex && i < profiles.length - 1 && (
                      <span
                        className="tab-action"
                        onClick={e => { e.stopPropagation(); moveProfile(i, 'right') }}
                        role="button"
                        title="Move right"
                        aria-label="move profile right"
                      >›</span>
                    )}
                    <span
                      className="tab-action"
                      onClick={e => { e.stopPropagation(); duplicateProfile(i) }}
                      role="button"
                      title="Duplicate profile"
                      aria-label={`duplicate profile ${i + 1}`}
                    >⧉</span>
                    {profiles.length > 1 && (
                      <span
                        className="tab-remove"
                        onClick={e => { e.stopPropagation(); removeProfile(i) }}
                        role="button"
                        aria-label={`remove profile ${i + 1}`}
                      >✕</span>
                    )}
                  </button>
                ))}
                <button className="tab-btn tab-add" onClick={addProfile}>+</button>
              </div>
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
                      className={fieldError('.profileName') ? 'input-error' : undefined}
                      onChange={e => updateCurrent({ profileName: e.target.value })}
                    />
                    {fieldError('.profileName') && (
                      <span className="field-error-msg">{fieldError('.profileName')}</span>
                    )}
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
                  <CanvasErrorBoundary onReset={() => updateCurrent({ layoutItems: [] })}>
                    <LayoutCanvas
                      items={current.layoutItems ?? []}
                      onChange={updateLayoutItems}
                    />
                  </CanvasErrorBoundary>
                </div>
              )}

              {/* slave: screen definitions */}
              {!isMaster && (
                <ScreenDefinitions
                  screens={current.screenDefinitions ?? []}
                  errors={errors}
                  profileIndex={activeIndex}
                  onAdd={addScreen}
                  onRemove={removeScreen}
                  onUpdate={updateScreen}
                />
              )}

              <div className="section">
                <h2>Conditions</h2>
                <p className="hint">Activate this profile only when all conditions match — leave blank for always-active.</p>
                {fieldError('.conditions') && (
                  <div className="field-error-msg" style={{ marginBottom: 8 }}>{fieldError('.conditions')}</div>
                )}
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
