import type { ConfigConditions } from '../types'

interface Props {
  conditions: ConfigConditions
  onChange: (patch: Partial<ConfigConditions>) => void
}

export function ConditionsEditor({ conditions, onChange }: Props) {
  const pluggedInValue = conditions.isPluggedIn === true ? 'true' : conditions.isPluggedIn === false ? 'false' : ''

  return (
    <div className="conditions-editor">
      <div className="field-row">
        <div className="field flex-grow">
          <label htmlFor="ce-ssid">WiFi SSID</label>
          <input
            id="ce-ssid"
            type="text"
            value={conditions.ssid ?? ''}
            placeholder="network name (case-insensitive)"
            onChange={e => onChange({ ssid: e.target.value || undefined })}
          />
        </div>
        <div className="field">
          <label htmlFor="ce-screencount">Screen Count</label>
          <input
            id="ce-screencount"
            type="number"
            min="1"
            value={conditions.screenCount ?? ''}
            placeholder="any"
            onChange={e => onChange({ screenCount: e.target.value ? Number(e.target.value) : undefined })}
          />
        </div>
        <div className="field">
          <label htmlFor="ce-pluggedin">Power</label>
          <select
            id="ce-pluggedin"
            value={pluggedInValue}
            onChange={e => onChange({
              isPluggedIn: e.target.value === 'true' ? true : e.target.value === 'false' ? false : undefined,
            })}
          >
            <option value="">any</option>
            <option value="true">plugged in (AC)</option>
            <option value="false">on battery</option>
          </select>
        </div>
      </div>
    </div>
  )
}
