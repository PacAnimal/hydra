import './App.css'
import { useHydraConfig } from './hooks/useHydraConfig'
import { validate } from './utils/validation'
import { DropZone } from './components/DropZone'
import { ModeSelect } from './components/ModeSelect'
import { GlobalSettings } from './components/GlobalSettings'
import { HostsEditor } from './components/HostsEditor'
import { ScreenDefinitions } from './components/ScreenDefinitions'
import { ConditionsEditor } from './components/ConditionsEditor'
import { ConfigFileSection } from './components/ConfigFileSection'

export default function App() {
  const {
    state, current,
    updateCurrent,
    addHost, removeHost, updateHost,
    addNeighbour, removeNeighbour, updateNeighbour,
    addScreen, removeScreen, updateScreen,
    updateConditions,
    setMultiConfig, addConfigEntry, removeConfigEntry, setActiveIndex,
    importJson, reset,
  } = useHydraConfig()

  const { configs, multiConfig, activeIndex } = state
  const isMaster = current.mode === 'Master'
  const errors = validate(configs, multiConfig)

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
          <ConfigFileSection configs={configs} multiConfig={multiConfig} />
        </aside>

        <main className="app-main">
          <div className="section multi-toggle">
            <label className="checkbox-label">
              <input
                type="checkbox"
                checked={multiConfig}
                onChange={e => setMultiConfig(e.target.checked)}
              />
              Multi-Config Mode (array of configs with conditions)
            </label>
          </div>

          {multiConfig && (
            <div className="config-tabs">
              {configs.map((_, i) => (
                <button
                  key={i}
                  className={`tab-btn${i === activeIndex ? ' active' : ''}`}
                  onClick={() => setActiveIndex(i)}
                >
                  Config {i + 1}
                  {configs.length > 1 && (
                    <span
                      className="tab-remove"
                      onClick={e => { e.stopPropagation(); removeConfigEntry(i) }}
                      role="button"
                      aria-label={`remove config ${i + 1}`}
                    >
                      ✕
                    </span>
                  )}
                </button>
              ))}
              <button className="tab-btn tab-add" onClick={addConfigEntry}>+</button>
            </div>
          )}

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

          <ScreenDefinitions
            screens={current.screenDefinitions ?? []}
            onAdd={addScreen}
            onRemove={removeScreen}
            onUpdate={updateScreen}
          />

          {multiConfig && (
            <div className="section">
              <h2>Conditions</h2>
              <p className="hint">When should this config be active?</p>
              <ConditionsEditor
                conditions={current.conditions ?? {}}
                onChange={updateConditions}
              />
            </div>
          )}

          {errors.length > 0 && (
            <div className="section validation-errors">
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
