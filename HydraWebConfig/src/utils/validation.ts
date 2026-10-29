import type { FormState, HydraProfile, HostConfig } from '../types'
import { deriveHostsFromLayout } from './layout'

export interface ValidationError {
  path: string
  message: string
}

function isUnconditional(p: HydraProfile): boolean {
  if (!p.conditions) return true
  const { ssid, screenCount, isPluggedIn, address, gatewayMac, wired } = p.conditions
  return !ssid && screenCount === undefined && isPluggedIn === undefined && !address && !gatewayMac && wired === undefined
}

// six octets split by ':' or '-', each one or two hex digits, or twelve digits unsplit, as Hydra reads them; null if neither
export function normalizeMac(mac: string): string | null {
  const trimmed = mac.trim()
  const octets = /^[0-9a-f]{12}$/i.test(trimmed) ? trimmed.match(/../g)! : trimmed.split(/[:-]/)
  if (octets.length !== 6 || !octets.every(o => /^[0-9a-f]{1,2}$/i.test(o))) return null
  return octets.map(o => o.toLowerCase().padStart(2, '0')).join(':')
}

// an address (192.168.1.7) or a network (192.168.1.0/24) as one network string, a bare address its own /32 or /128;
// null where Hydra refuses it, which includes an IPv4 network with host bits set past the prefix
export function normalizeAddress(text: string): string | null {
  const [address, prefix, ...rest] = text.trim().split('/')
  if (rest.length > 0 || (prefix !== undefined && !/^\d{1,3}$/.test(prefix))) return null
  const v4 = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(address)
  if (v4) {
    const octets = v4.slice(1).map(Number)
    const bits = prefix === undefined ? 32 : Number(prefix)
    if (octets.some(o => o > 255) || bits > 32) return null
    const value = octets.reduce((n, o) => n * 256 + o, 0)
    if (value % 2 ** (32 - bits) !== 0) return null
    return `${octets.join('.')}/${bits}`
  }
  if (!address.includes(':') || !/^[0-9a-f:.]+$/i.test(address)) return null
  const bits = prefix === undefined ? 128 : Number(prefix)
  return bits > 128 ? null : `${address.toLowerCase()}/${bits}`
}

// returns the effective hosts for a profile (from layout or explicit)
function effectiveHosts(p: HydraProfile): HostConfig[] {
  if (p.layoutItems && p.layoutItems.length > 0) {
    return deriveHostsFromLayout(p.layoutItems)
  }
  return p.hosts ?? []
}

// screens are named <host>:<n>, so a ':' in a host name could collide with another host's monitor
function checkHostName(name: string, path: string, errors: ValidationError[]) {
  if (name.includes(':')) {
    errors.push({ path, message: `host name '${name}' cannot contain ':' (reserved for monitor numbers)` })
  }
}

// a size Hydra reads: bytes, or a number and a unit (KB is 1000, KiB 1024; a bare lowercase b is bits, which it refuses)
const LOG_SIZE = /^\s*(?:[Nn][Oo][Nn][Ee]|\d+|\d+(?:\.\d+)?\s*(?:B|[KkMmGgTtPp][Ii]?[Bb]))\s*$/

export type LogCap = Pick<FormState, 'logMaxSize' | 'logHistory' | 'logTrimPercent'>

function checkLogCap(log: LogCap, errors: ValidationError[]) {
  if (log.logMaxSize !== undefined && !LOG_SIZE.test(log.logMaxSize)) {
    errors.push({ path: 'logMaxSize', message: 'logMaxSize must be a size such as 10 MiB or 512 KB, a number of bytes, or 0 or none for no cap' })
  }
  if (log.logHistory !== undefined && (!Number.isInteger(log.logHistory) || log.logHistory < 0)) {
    errors.push({ path: 'logHistory', message: 'logHistory must be a whole number, 0 or more' })
  }
  if (log.logTrimPercent !== undefined && (!Number.isInteger(log.logTrimPercent) || log.logTrimPercent < 1 || log.logTrimPercent > 100)) {
    errors.push({ path: 'logTrimPercent', message: 'logTrimPercent must be a whole number from 1 to 100' })
  }
}

// name is the root name, which names this machine's screens
export function validate(profiles: HydraProfile[], name?: string, log: LogCap = {}): ValidationError[] {
  const errors: ValidationError[] = []

  if (name) checkHostName(name, 'name', errors)
  checkLogCap(log, errors)

  const defaults = profiles.filter(isUnconditional)
  if (defaults.length > 1) {
    errors.push({ path: 'profiles', message: 'at most one profile can be unconditional (no conditions)' })
  }

  // check duplicate condition tuples
  const seen = new Set<string>()
  profiles.forEach((p, i) => {
    if (isUnconditional(p)) return
    const c = p.conditions
    if (c?.address && normalizeAddress(c.address) === null) {
      errors.push({ path: `profiles[${i}].conditions.address`, message: 'address must be an IP address or a network such as 192.168.1.0/24, with no bits set past the prefix' })
    }
    if (c?.gatewayMac && normalizeMac(c.gatewayMac) === null) {
      errors.push({ path: `profiles[${i}].conditions.gatewayMac`, message: 'gateway MAC must be a MAC address such as a4:91:b1:0c:22:7e' })
    }
    const mac = c?.gatewayMac ? normalizeMac(c.gatewayMac) ?? c.gatewayMac : ''
    const address = c?.address ? normalizeAddress(c.address) ?? c.address : ''
    const key = `${c?.ssid?.toLowerCase() ?? ''}|${c?.screenCount ?? ''}|${c?.isPluggedIn ?? ''}|${address}|${mac}|${c?.wired ?? ''}`
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

    if (p.mode === 'Master' && p.maxMouseHz !== undefined && (!Number.isInteger(p.maxMouseHz) || p.maxMouseHz < 1 || p.maxMouseHz > 1000)) {
      errors.push({ path: `profiles[${i}].maxMouseHz`, message: 'maxMouseHz must be a whole number from 1 to 1000' })
    }

    if (p.syncScreensaver === false && p.mode !== 'Master') {
      errors.push({ path: `profiles[${i}].syncScreensaver`, message: 'syncScreensaver requires Master mode' })
    }

    const hosts = effectiveHosts(p)
    if (p.remoteOnly && hosts.filter(h => h.name.trim()).length === 0) {
      errors.push({ path: `profiles[${i}].remoteOnly`, message: 'remoteOnly requires at least one remote host' })
    }

    // a mirrored neighbour becomes a host too
    hosts.forEach((h, hi) => {
      checkHostName(h.name, `profiles[${i}].hosts[${hi}].name`, errors)
      ;(h.neighbours ?? []).forEach((n, ni) => {
        if (n.mirror !== false) checkHostName(n.name, `profiles[${i}].hosts[${hi}].neighbours[${ni}].name`, errors)
      })
    })

    // the embedded relay only speaks TLS, so Hydra refuses an http:// server
    if (p.networkType === 'embeddedStyx' && p.embeddedStyx?.server.trim().toLowerCase().startsWith('http://')) {
      errors.push({ path: `profiles[${i}].embeddedStyx.server`, message: 'embeddedStyx server must be https:// (or have no scheme)' })
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
