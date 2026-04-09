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
          <label>WiFi SSID</label>
          <input
            type="text"
            value={conditions.ssid ?? ''}
            placeholder="network name (case-insensitive)"
            onChange={e => onChange({ ssid: e.target.value || undefined })}
          />
        </div>
        <div className="field">
          <label>Screen Count</label>
          <input
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
