import type { HydraConfig } from '../types'

export interface ValidationError {
  path: string
  message: string
}

function isUnconditional(cfg: HydraConfig): boolean {
  if (!cfg.conditions) return true
  const { ssid, screenCount } = cfg.conditions
  return !ssid && screenCount === undefined
}

export function validate(configs: HydraConfig[], multiConfig: boolean): ValidationError[] {
  const errors: ValidationError[] = []

  if (multiConfig) {
    const defaults = configs.filter(isUnconditional)
    if (defaults.length > 1) {
      errors.push({ path: 'configs', message: 'at most one config can be unconditional (no conditions)' })
    }

    // check duplicate condition tuples
    const seen = new Set<string>()
    configs.forEach((cfg, i) => {
      if (isUnconditional(cfg)) return
      const key = `${cfg.conditions?.ssid ?? ''}|${cfg.conditions?.screenCount ?? ''}`
      if (seen.has(key)) {
        errors.push({ path: `configs[${i}].conditions`, message: 'duplicate condition combination' })
      }
      seen.add(key)
    })
  }

  configs.forEach((cfg, i) => {
    const prefix = multiConfig ? `configs[${i}]` : ''

    if (!cfg.mode) {
      errors.push({ path: `${prefix}.mode`, message: 'mode is required' })
    }

    if (cfg.remoteOnly && cfg.mode !== 'Master') {
      errors.push({ path: `${prefix}.remoteOnly`, message: 'remoteOnly requires Master mode' })
    }

    if (cfg.remoteOnly) {
      const localName = (cfg.name ?? '').trim().toLowerCase()
      const remoteHosts = (cfg.hosts ?? []).filter(
        h => h.name.trim().toLowerCase() !== localName
      )
      if (remoteHosts.length === 0) {
        errors.push({ path: `${prefix}.remoteOnly`, message: 'remoteOnly requires at least one remote host' })
      }
    }

    if (cfg.conditions?.screenCount !== undefined && cfg.conditions.screenCount < 1) {
      errors.push({ path: `${prefix}.conditions.screenCount`, message: 'screenCount must be at least 1' })
    }

    (cfg.screenDefinitions ?? []).forEach((s, si) => {
      if (!s.displayName && !s.outputName && !s.platformId) {
        errors.push({
          path: `${prefix}.screenDefinitions[${si}]`,
          message: 'at least one of displayName, outputName, or platformId is required',
        })
      }
    })
  })

  return errors
}
