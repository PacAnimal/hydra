import type { HydraProfile } from '../types'

export interface ValidationError {
  path: string
  message: string
}

function isUnconditional(p: HydraProfile): boolean {
  if (!p.conditions) return true
  const { ssid, screenCount } = p.conditions
  return !ssid && screenCount === undefined
}

export function validate(profiles: HydraProfile[]): ValidationError[] {
  const errors: ValidationError[] = []

  const defaults = profiles.filter(isUnconditional)
  if (defaults.length > 1) {
    errors.push({ path: 'profiles', message: 'at most one profile can be unconditional (no conditions)' })
  }

  // check duplicate condition tuples
  const seen = new Set<string>()
  profiles.forEach((p, i) => {
    if (isUnconditional(p)) return
    const key = `${p.conditions?.ssid ?? ''}|${p.conditions?.screenCount ?? ''}`
    if (seen.has(key)) {
      errors.push({ path: `profiles[${i}].conditions`, message: 'duplicate condition combination' })
    }
    seen.add(key)
  })

  // check duplicate profile names
  const seenNames = new Set<string>()
  profiles.forEach((p, i) => {
    const name = p.profileName.trim().toLowerCase()
    if (!name) return
    if (seenNames.has(name)) {
      errors.push({ path: `profiles[${i}].profileName`, message: 'duplicate profile name' })
    }
    seenNames.add(name)
  })

  profiles.forEach((p, i) => {
    if (!p.profileName.trim()) {
      errors.push({ path: `profiles[${i}].profileName`, message: 'profile name is required' })
    }

    if (!p.mode) {
      errors.push({ path: `profiles[${i}].mode`, message: 'mode is required' })
    }

    if (p.remoteOnly && p.mode !== 'Master') {
      errors.push({ path: `profiles[${i}].remoteOnly`, message: 'remoteOnly requires Master mode' })
    }

    if (p.remoteOnly && (p.hosts ?? []).filter(h => h.name.trim()).length === 0) {
      errors.push({ path: `profiles[${i}].remoteOnly`, message: 'remoteOnly requires at least one remote host' })
    }

    if (p.conditions?.screenCount !== undefined && p.conditions.screenCount < 1) {
      errors.push({ path: `profiles[${i}].conditions.screenCount`, message: 'screenCount must be at least 1' })
    }

    ;(p.screenDefinitions ?? []).forEach((s, si) => {
      if (!s.displayName && !s.outputName && !s.platformId) {
        errors.push({
          path: `profiles[${i}].screenDefinitions[${si}]`,
          message: 'at least one of displayName, outputName, or platformId is required',
        })
      }
    })
  })

  return errors
}
