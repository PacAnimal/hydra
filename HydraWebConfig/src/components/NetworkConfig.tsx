import type { HydraProfile, NetworkType, EmbeddedStyxConfig, EmbeddedStyxServerConfig } from '../types'

interface Props {
  config: HydraProfile
  onChange: (patch: Partial<HydraProfile>) => void
}

const TYPE_LABELS: Record<NetworkType, string> = {
  config: 'Styx relay (base64)',
  embeddedStyx: 'Embedded Styx client',
  embeddedStyxServer: 'Embedded Styx server',
}

export function NetworkConfig({ config, onChange }: Props) {
  const type = config.networkType ?? 'config'

  function setType(t: NetworkType) {
    onChange({ networkType: t })
  }

  function patchStyx(patch: Partial<EmbeddedStyxConfig>) {
    onChange({ embeddedStyx: { server: '', password: '', ...config.embeddedStyx, ...patch } })
  }

  function patchStyxServer(patch: Partial<EmbeddedStyxServerConfig>) {
    onChange({ embeddedStyxServer: { port: 5000, password: '', ...config.embeddedStyxServer, ...patch } })
  }

  return (
    <div className="network-config">
      <div className="network-type-group">
        {(Object.keys(TYPE_LABELS) as NetworkType[]).map(t => (
          <label key={t} className={`network-type-btn${type === t ? ' active' : ''}`}>
            <input
              type="radio"
              name="networkType"
              value={t}
              checked={type === t}
              onChange={() => setType(t)}
            />
            {TYPE_LABELS[t]}
          </label>
        ))}
      </div>

      {type === 'config' && (
        <div className="field mt-10">
          <textarea
            value={config.networkConfig ?? ''}
            placeholder="Base64-encoded config string from Styx"
            onChange={e => onChange({ networkConfig: e.target.value || undefined })}
            rows={3}
          />
        </div>
      )}

      {type === 'embeddedStyx' && (
        <div className="field-row mt-10">
          <div className="field flex-grow">
            <label>Server</label>
            <input
              type="text"
              value={config.embeddedStyx?.server ?? ''}
              placeholder="e.g. styx.example.com:8080 or 192.168.1.10"
              onChange={e => patchStyx({ server: e.target.value })}
            />
          </div>
          <div className="field flex-grow">
            <label>Password</label>
            <input
              type="password"
              value={config.embeddedStyx?.password ?? ''}
              placeholder="shared relay password"
              onChange={e => patchStyx({ password: e.target.value })}
            />
          </div>
        </div>
      )}

      {type === 'embeddedStyxServer' && (
        <div className="field-row mt-10">
          <div className="field">
            <label>Port</label>
            <input
              type="number"
              min="1"
              max="65535"
              value={config.embeddedStyxServer?.port ?? 5000}
              onChange={e => patchStyxServer({ port: Number(e.target.value) })}
            />
          </div>
          <div className="field flex-grow">
            <label>Password</label>
            <input
              type="password"
              value={config.embeddedStyxServer?.password ?? ''}
              placeholder="shared relay password"
              onChange={e => patchStyxServer({ password: e.target.value })}
            />
          </div>
        </div>
      )}
    </div>
  )
}
