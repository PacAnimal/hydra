import type { ConfigConditions } from '../types'

interface Props {
  conditions: ConfigConditions
  onChange: (patch: Partial<ConfigConditions>) => void
}

export function ConditionsEditor({ conditions, onChange }: Props) {
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
      </div>
    </div>
  )
}
